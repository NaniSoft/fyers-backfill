using System.Text;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fyers.Token;

/// <summary>The OAuth callback handler + expiry watcher — 1:1 port of
/// src/token_service.py:TokenService. The HTTP surface is a thin adapter over
/// HandleCallbackAsync, the watcher loop over WatchOnceAsync; tests drive
/// those directly — no sockets, no Fyers, no Telegram.</summary>
public sealed class TokenService
{
    public const string CallbackPath = "/callback";

    /// <summary>The exact prompt text Python sends (one click/day).</summary>
    public const string PromptTemplate =
        "Fyers token missing/expired — daily login needed (one click/day):\n{url}\n"
        + "Log in on the Fyers page; the token lands automatically and the "
        + "collector resumes on its own.";

    public const string StateMismatchText =
        "This link is from an older login message — use the latest Telegram link.";

    public const string NoAuthCodeText =
        "No auth_code in the redirect — start again from the Telegram link.";

    private readonly TokenConfig _cfg;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly ExchangeAuthCodeDelegate _exchange;
    private readonly ProfileValidDelegate _validator;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _oauthLock = new(1, 1);

    private INotifier? _notifier;
    private string? _pendingState;      // rotated per prompt
    private string? _consumedState;     // the state of the last accepted click
    private DateTimeOffset? _lastPromptAt;   // IST time of the last Telegram prompt
    private string? _lastValidDate;     // last IST date the token looked healthy

    public TokenService(
        TokenConfig cfg,
        ILogger? log = null,
        INotifier? notifier = null,
        ExchangeAuthCodeDelegate? exchange = null,
        ProfileValidDelegate? validator = null,
        Func<DateTimeOffset>? now = null,
        HttpClient? http = null)
    {
        _cfg = cfg;
        _log = log ?? NullLogger.Instance;
        _http = http ?? new HttpClient();
        _notifier = notifier;
        _exchange = exchange ?? ((appId, secretId, code) =>
            FyersAuth.ExchangeAuthCodeAsync(_http, appId, secretId, code));
        _validator = validator ?? ((appId, token) =>
            FyersAuth.ProfileValidAsync(_http, appId, token));
        _now = now ?? Ist.Now;
        // Fail loud at startup if config and the registered URI disagree: a
        // mismatch bounces the browser silently back to the login page.
        RedirectUri();
    }

    public TokenConfig Config => _cfg;

    // --- redirect / login URL -------------------------------------------

    /// <summary>The byte-exact registered redirect URI. callback_base_url,
    /// when set, must agree with FYERS_REDIRECT_URI — that agreement is
    /// checked here (a mismatch bounces silently to the login page).</summary>
    public string RedirectUri()
    {
        var baseUrl = _cfg.CallbackBaseUrl;
        if (baseUrl.Length == 0) return _cfg.RedirectUri;
        var derived = baseUrl.TrimEnd('/') + CallbackPath;
        if (derived != _cfg.RedirectUri)
        {
            throw new InvalidOperationException(
                $"auth.callback_base_url ({baseUrl}) + {CallbackPath} != "
                + $"FYERS_REDIRECT_URI ({_cfg.RedirectUri}) — the registered URI is "
                + "byte-exact; fix one of them");
        }
        return derived;
    }

    public int CallbackPort
    {
        get
        {
            var p = new Uri(RedirectUri()).Port;
            return p > 0 ? p : 80;
        }
    }

    /// <summary>Fresh login URL (fresh state each time — an old Telegram link
    /// from a previous prompt carries a stale state and is rejected).</summary>
    public string NewLoginUrl()
    {
        var (url, state) = FyersAuth.BuildAuthUrl(_cfg.AppId, RedirectUri());
        _pendingState = state;
        return url;
    }

    public string? PendingState => _pendingState;

    // --- HTTP surface -----------------------------------------------------

    /// <summary>Route one GET; returns (status, body, content_type).</summary>
    public async Task<(int Status, string Body, string ContentType)> HandleCallbackAsync(
        string path, string query, CancellationToken ct = default)
    {
        if (path == "/healthz") return (200, "ok", "text/plain");
        if (path != CallbackPath) return (404, "not found", "text/plain");
        await _oauthLock.WaitAsync(ct);   // one click at a time; auth_code is single-use
        try
        {
            // No cancellation downstream: Python completes the exchange even
            // if the browser gives up on the redirect, and so do we.
            return await HandleOauthAsync(query);
        }
        finally
        {
            _oauthLock.Release();
        }
    }

    private async Task<(int Status, string Body, string ContentType)> HandleOauthAsync(
        string query)
    {
        var code = QueryValue(query, "auth_code");
        if (string.IsNullOrEmpty(code))
        {
            return (400, Page(NoAuthCodeText, ok: false), "text/html");
        }
        var state = QueryValue(query, "state");
        // Fyers' session auto-redirect omits state entirely; only a MISMATCHED
        // state (an older login flow's redirect) is rejected. A state that was
        // already consumed by an accepted click is rejected the same way: its
        // auth_code is single-use, so a fresh exchange can never succeed.
        if (state is not null
            && ((_pendingState is not null && state != _pendingState)
                || state == _consumedState))
        {
            return (400, Page(StateMismatchText, ok: false), "text/html");
        }
        // auth_code TTL ≈ 2 min: exchange synchronously, right here.
        JsonObject? resp;
        try
        {
            resp = await _exchange(_cfg.AppId, _cfg.SecretId, code);
        }
        catch (Exception e)
        {
            return await OauthFailedAsync(
                $"validate-authcode raised {e.GetType().Name}: {e.Message}");
        }
        var s = Json.Str(resp, "s");
        int? respCode = Json.Int(resp, "code");
        if (s != "ok" && respCode is not (null or 200))
        {
            return await OauthFailedAsync(
                $"validate-authcode failed: {respCode} {Json.Str(resp, "message") ?? ""} ({s})");
        }

        var accessToken = Json.Str(resp, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return await OauthFailedAsync(
                "validate-authcode failed: response carried no access_token");
        }
        try
        {
            SaveToken(accessToken);
        }
        catch (Exception e)
        {
            return await OauthFailedAsync($"saving the token file failed: {e.Message}");
        }
        // success: consume the collector's reauth signal, retire this prompt
        ClearSignal();
        _consumedState = state ?? _consumedState;
        _pendingState = null;
        _lastPromptAt = null;
        _log.LogInformation("access token refreshed via callback");

        var (ok, detail) = await FyersAuth.DescribeTokenAsync(
            _cfg.AppId, accessToken, _validator, _now());
        var (level, mark) = ok switch
        {
            true => ("info", "✅"),
            false => ("error", "❌"),
            _ => ("warn", "•"),
        };
        await NotifyAsync($"{mark} Fyers token saved — {detail}", level);
        return (200, Page("Token saved. You can close this tab — the collector picks "
                          + "it up automatically."), "text/html");
    }

    private async Task<(int Status, string Body, string ContentType)> OauthFailedAsync(
        string detail)
    {
        _log.LogError("token exchange failed: {Detail}", detail);
        await NotifyAsync($"Token exchange FAILED: {detail} — click the login link again.",
            "error");
        return (502, Page($"Login failed: {detail}. Close this tab and click the "
                          + "Telegram link again.", ok: false), "text/html");
    }

    /// <summary>The redirect page Python serves (same html, same tone words,
    /// detail interpolated raw — parity with _page()).</summary>
    public static string Page(string detail, bool ok = true)
    {
        var tone = ok ? "ok" : "failed";
        return "<html><body style='font-family:sans-serif'>"
               + $"<h2>Fyers login {tone}.</h2><p>{detail}</p></body></html>";
    }

    /// <summary>Atomic write (tmp + replace) so the collector's mtime watcher
    /// never reads a half-written file. Same keys/order as Python's json.dump:
    /// {"access_token": ..., "app_id": ..., "saved_at": epoch}</summary>
    public void SaveToken(string token)
    {
        var path = _cfg.TokenPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, TokenJson(token, _cfg.AppId, Ist.NowEpoch()),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);   // os.replace
    }

    /// <summary>The token file's exact JSON shape: Python json.dump with its
    /// default separators (", " between members, ": " after keys) and the key
    /// order access_token, app_id, saved_at.</summary>
    public static string TokenJson(string accessToken, string appId, long savedAtEpoch) =>
        "{\"access_token\": \"" + JavaScriptEncoder.Default.Encode(accessToken)
        + "\", \"app_id\": \"" + JavaScriptEncoder.Default.Encode(appId)
        + "\", \"saved_at\": " + savedAtEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "}";

    // --- expiry watcher ---------------------------------------------------

    /// <summary>One token-health check. Sends ONE Telegram prompt when the
    /// token is missing/expired — at/after auth.prompt_time for the daily
    /// routine (token death ≈ 06:00 IST; never a 00:00–06:00 wake-up),
    /// immediately for incidents (the collector's reauth signal, or a same-day
    /// death). Re-minds at remind_interval_min while unresolved. Returns true
    /// iff a prompt went out.</summary>
    public async Task<bool> WatchOnceAsync(CancellationToken ct = default)
    {
        var now = _now();
        var today = Ist.DateStr(now);
        var tok = LoadTokenFrom(_cfg.TokenPath);
        var savedAt = Json.Long(tok, "saved_at") ?? 0;
        if (Str(tok, "access_token") is { Length: > 0 } && Str(tok, "app_id") is { Length: > 0 }
            && !FyersAuth.TokenIsStale(savedAt, today))
        {
            _lastValidDate = today;
            ClearSignal();
            return false;
        }

        var signal = File.Exists(_cfg.ReauthRequestPath);
        var incident = signal || _lastValidDate == today;
        var (ph, pm) = Ist.ParseHhmm(_cfg.PromptTime);
        var due = new DateTimeOffset(now.Year, now.Month, now.Day, ph, pm, 0, now.Offset);
        if (!incident && now < due) return false;   // overnight quiet window
        if (signal) ClearSignal();   // consumed: the prompt below replaces it
        if (_lastPromptAt is not null
            && (now - _lastPromptAt.Value).TotalSeconds < _cfg.RemindIntervalMin * 60)
        {
            return false;   // already waiting on the user — no spam
        }
        var url = NewLoginUrl();
        var sent = await NotifyAsync(PromptTemplate.Replace("{url}", url), "warn");
        if (sent) _lastPromptAt = now;
        return sent;
    }

    private void ClearSignal()
    {
        try
        {
            if (File.Exists(_cfg.ReauthRequestPath)) File.Delete(_cfg.ReauthRequestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("could not remove reauth signal");
        }
    }

    /// <summary>auth._load_token_from: null when missing or unparsable.</summary>
    public static JsonObject? LoadTokenFrom(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? obj, string key) => Json.Str(obj, key);

    private async Task<bool> NotifyAsync(string text, string level)
    {
        _notifier ??= new TelegramNotifier(_cfg, _log, _http);
        return await _notifier.NotifyAsync(text, level);
    }

    /// <summary>Query-string lookup, first value wins (parse_qs semantics),
    /// tolerant of a leading '?'.</summary>
    public static string? QueryValue(string query, string key)
    {
        if (query.StartsWith('?')) query = query[1..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = eq < 0 ? pair : pair[..eq];
            if (!Uri.UnescapeDataString(k.Replace("+", "%20")).Equals(key, StringComparison.Ordinal))
            {
                continue;
            }
            if (eq < 0) return "";
            var v = pair[(eq + 1)..];
            return Uri.UnescapeDataString(v.Replace("+", "%20"));
        }
        return null;
    }
}
