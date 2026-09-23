using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fyers.Token;

/// <summary>Raised when the validate-authcode call itself fails (network,
/// non-200, unparseable body) — mirrors the exceptions src/auth.py:_exchange
/// lets escape into token_service.py's "validate-authcode raised ..." text.</summary>
public sealed class FyersExchangeException : Exception
{
    public FyersExchangeException(string message) : base(message) { }
}

/// <summary>auth_code -> access_token, seam for tests (src/token_service.py
/// passes `exchange` the same way).</summary>
public delegate Task<JsonObject?> ExchangeAuthCodeDelegate(
    string appId, string secretId, string authCode);

/// <summary>Token validity probe seam (True / False / null = unknown).</summary>
public delegate Task<bool?> ProfileValidDelegate(string appId, string accessToken);

/// <summary>1:1 port of the Fyers auth pieces the token service needs:
/// src/auth.py (build_auth_url, _app_id_hash, _exchange, token_is_stale) and
/// src/token_service.py (_profile_valid, _jwt_claims, _next_death_ist,
/// describe_token).</summary>
public static class FyersAuth
{
    public const string Base = "https://api-t1.fyers.in/api/v3";
    public const string ProfileUrl = Base + "/profile";

    /// <summary>The plain browser UA. Parity note (2026-09-09): the profile
    /// endpoint sits behind Cloudflare, which 403s the bare-urllib signature
    /// ("error code: 1010"); requests' own UA passes, and so does this.</summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    // --- login URL -----------------------------------------------------

    /// <summary>build_auth_url: https://api-t1.fyers.in/api/v3/generate-authcode
    /// with client_id, redirect_uri, response_type, state in exactly that order
    /// (Fyers does not care about order; byte-parity with Python does). A fresh
    /// state per URL is the rotation: an old Telegram link then carries a stale
    /// state and is rejected.</summary>
    public static (string Url, string State) BuildAuthUrl(
        string appId, string redirectUri, string? state = null)
    {
        state ??= NewState();
        var url = $"{Base}/generate-authcode?"
                  + $"client_id={UrlEncode(appId)}"
                  + $"&redirect_uri={UrlEncode(redirectUri)}"
                  + "&response_type=code"
                  + $"&state={UrlEncode(state)}";
        return (url, state);
    }

    /// <summary>secrets.token_urlsafe(8): 8 random bytes, unpadded base64url
    /// (11 chars) — same entropy approach as Python.</summary>
    public static string NewState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(8));

    /// <summary>urllib.parse.urlencode / quote_plus semantics (space -> '+',
    /// everything unreserved percent-encoded).</summary>
    public static string UrlEncode(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+");

    // --- staleness ------------------------------------------------------

    /// <summary>token_is_stale: the one freshness rule every reader shares —
    /// the token file's saved_at IST date must equal today's IST date. The JWT
    /// exp claim is informational only (Fyers' real reset is ~06:00 IST).</summary>
    public static bool TokenIsStale(long savedAtEpoch, string? todayIso = null)
    {
        todayIso ??= Ist.DateStr(Ist.Now());
        var saved = Ist.FromEpoch(savedAtEpoch);
        return Ist.DateStr(saved) != todayIso;
    }

    /// <summary>_next_death_ist: Fyers resets access at ~06:00 IST daily —
    /// the REAL death of a token.</summary>
    public static DateTimeOffset NextDeathIst(DateTimeOffset nowIst)
    {
        var death = new DateTimeOffset(
            nowIst.Year, nowIst.Month, nowIst.Day, 6, 0, 0, nowIst.Offset);
        if (nowIst >= death) death = death.AddDays(1);
        return death;
    }

    // --- exchange -------------------------------------------------------

    /// <summary>_app_id_hash: lowercase hex sha256 of "appId:secret".</summary>
    public static string AppIdHash(string appId, string secretId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{appId}:{secretId}")))
            .ToLowerInvariant();

    /// <summary>POST /validate-authcode with the exact body/headers Python
    /// sends. urllib raises on a non-2xx, so a non-success status is thrown
    /// too; the parsed JSON body (or null for an empty one) comes back.</summary>
    public static async Task<JsonObject?> ExchangeAuthCodeAsync(
        HttpClient http, string appId, string secretId, string authCode)
    {
        var body = "{\"grant_type\": \"authorization_code\", \"appIdHash\": \""
                   + AppIdHash(appId, secretId) + "\", \"code\": \""
                   + JsonEncodedText.Encode(authCode) + "\"}";
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/validate-authcode");
        var content = new StringContent(body, new UTF8Encoding(false));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Content = content;
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, timeout.Token);
        }
        catch (Exception e)
        {
            throw new FyersExchangeException($"{e.GetType().Name}: {e.Message}");
        }
        if (!resp.IsSuccessStatusCode)
        {
            throw new FyersExchangeException(
                $"HTTPError: HTTP Error {(int)resp.StatusCode}: {resp.ReasonPhrase}");
        }
        var text = await resp.Content.ReadAsStringAsync(timeout.Token);
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new FyersExchangeException($"{e.GetType().Name}: {e.Message}");
        }
    }

    // --- profile / describe ----------------------------------------------

    /// <summary>_profile_valid: one cheap /profile call. True = Fyers accepts
    /// the token, False = rejected (HTTP 401, or a 200 whose body says
    /// s=error), null = unknown. A bot-block / outage is not a verdict:
    /// anything that isn't Fyers speaking is null (warn, never "not valid").</summary>
    public static async Task<bool?> ProfileValidAsync(
        HttpClient http, string appId, string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"{appId}:{accessToken}");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await http.SendAsync(req, timeout.Token);
            if ((int)resp.StatusCode == 401) return false;
            if ((int)resp.StatusCode != 200) return null;
            var text = await resp.Content.ReadAsStringAsync(timeout.Token);
            JsonObject? body;
            try { body = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { return null; }
            if (body is null) return null;
            var s = Json.Str(body, "s");
            if (s == "ok") return true;
            return s == "error";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>describe_token: (ok, detail) for the post-login Telegram.
    /// ok is true/false/null (null = Fyers unreachable — validity unknown,
    /// never claimed).</summary>
    public static async Task<(bool? Ok, string Detail)> DescribeTokenAsync(
        string appId, string accessToken, ProfileValidDelegate validate, DateTimeOffset? now = null)
    {
        var t = now ?? Ist.Now();
        var claims = JwtClaims(accessToken);
        var expTxt = "";
        if (Json.Long(claims, "exp") is { } exp && exp != 0)
        {
            expTxt = $"JWT exp claim {Ist.FromEpoch(exp):yyyy-MM-dd HH:mm} IST; ";
        }
        var mins = (long)Math.Floor((NextDeathIst(t) - t).TotalSeconds / 60.0);
        var deathTxt = "stops working at the ~06:00 IST Fyers reset "
                       + $"(~{mins / 60}h{mins % 60:D2}m from now)";
        var ok = await validate(appId, accessToken);
        if (ok == true)
        {
            return (true, $"VALID — {deathTxt}. {expTxt}Collector picks it up automatically.");
        }
        if (ok == false)
        {
            return (false,
                $"NOT VALID — Fyers already rejects it (click the next login link). {expTxt}");
        }
        return (null, $"saved; could not reach Fyers to validate. {deathTxt}.");
    }

    /// <summary>_jwt_claims: decoded JWT payload claims, or {} for non-JWT
    /// tokens (never throws).</summary>
    public static JsonObject JwtClaims(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return new JsonObject();
            var payload = Base64Url.DecodeFromChars(parts[1]);
            return JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject ?? new JsonObject();
        }
        catch (Exception)
        {
            return new JsonObject();
        }
    }
}

/// <summary>Tiny JSON accessors over the parsed Fyers/token-file bodies
/// (mirrors Python's dict.get tolerance of missing keys).</summary>
public static class Json
{
    public static string? Str(JsonObject? obj, string key)
    {
        if (obj is null || obj[key] is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
    }

    public static int? Int(JsonObject? obj, string key)
    {
        if (obj is null || obj[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Truncate(d)
            && d is >= int.MinValue and <= int.MaxValue)
        {
            return (int)d;
        }
        return int.TryParse(v.ToJsonString().Trim('"'), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static long? Long(JsonObject? obj, string key)
    {
        if (obj is null || obj[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d) && d == Math.Truncate(d)) return (long)d;
        return long.TryParse(v.ToJsonString().Trim('"'), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
