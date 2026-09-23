using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// NB: no `using Fyers.Core.RateLimiter;` here — the RateLimiter *type* shadows its own
// namespace name (CS0138). The type is visible from Fyers.Core.Fyers via the enclosing
// namespace lookup.

namespace Fyers.Core.Fyers;

/// <summary>
/// Fyers token invalid/expired (HTTP 401/403, Fyers body code -8/-15/-16/-17 or
/// 416/403, or an "error" body mentioning "token"). Port of <c>src/auth.py:AuthExpired</c>
/// — the scheduler must stop and ask for a re-login instead of retrying.
/// </summary>
public sealed class AuthExpiredException : Exception
{
    public AuthExpiredException(string message) : base(message) { }
}

/// <summary>
/// The rate limiter (or an HTTP 429 drain) says SKIP this call — port of
/// <c>src/rate_limiter.py:RateLimited</c>. Callers drop the chunk/chain and
/// continue; they never retry it inside the same minute (decision 04).
/// </summary>
public sealed class RateLimitedException : Exception
{
    public RateLimitedException(string message) : base(message) { }
}

/// <summary>
/// Fyers v3 REST data client — .NET port of <c>src/fyers_client.py</c> (decision 09,
/// REST-only). Wraps the two endpoints the collector needs:
///   /data/quotes           — batched LTP/OHLC for futures, spot, VIX, cash
///   /data/options-chain-v3 — full option chain per (underlying, expiry), greeks
///
/// Note the response shapes differ: option-chain-v3 carries its payload under
/// <c>body.data</c>, quotes carries a list under <c>body.d</c>.
///
/// Auth: every request sends <c>Authorization: {appId}:{accessToken}</c> exactly as
/// the Python client does (no "Bearer"). The token is re-read on every call through
/// the <c>accessToken</c> delegate so a refreshed token file is picked up without
/// rebuilding the client: pass the raw token plus <paramref name="appId"/>, or pass
/// an already-composed <c>"appId:token"</c> string with <paramref name="appId"/> null.
/// </summary>
// `partial` (only change to this file): the /data/history endpoint + CandleRow live in
// Fyers/History.cs — same class, separate file, mirroring the python layout where
// src/fyers_client.py:history is consumed by src/candles.py.
public sealed partial class FyersClient
{
    /// <summary>Fyers data API root (parity: python <c>DATA</c>).</summary>
    public const string BaseUrl = "https://api-t1.fyers.in/data";

    /// <summary>Fyers rejects the default HttpClient UA; mirror the python one.</summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    /// <summary>Per-call network timeout (python <c>timeout=15</c>).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Fyers business-level auth failure codes (reauth: -8/-15/-16/-17; plus 416/403).</summary>
    private static readonly int[] AuthCodes = { -8, -15, -16, -17, 416, 403 };

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };

    private static readonly JsonDocumentOptions JsonOpts = new()
    {
        AllowTrailingCommas = true,
    };

    /// <summary>IST zone (plan global: Asia/Kolkata with a Windows-id fallback).</summary>
    internal static readonly TimeZoneInfo Ist = ResolveIst();

    private static TimeZoneInfo ResolveIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    private readonly HttpClient _http;
    private readonly Func<string?> _accessToken;
    private readonly RateLimiter _lim;
    private readonly ILogger _log;
    private readonly string? _appId;
    private readonly Func<DateTime> _clock;

    public FyersClient(HttpClient http, Func<string?> accessToken, RateLimiter lim, ILogger? log,
        string? appId = null, Func<DateTime>? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _lim = lim ?? throw new ArgumentNullException(nameof(lim));
        _log = log ?? NullLogger.Instance;          // tolerates a quiet run (Storage parity)
        _appId = appId;
        _clock = clock ?? (static () => DateTime.UtcNow);
    }

    // ------------------------------------------------------------------ quotes

    /// <summary>
    /// Batched quotes — port of <c>FyersClient.quotes</c>. Large lists are split into
    /// URL-safe chunks of <paramref name="chunk"/> symbols; each chunk is its own
    /// rate-limited GET. A chunk skipped by the limiter is dropped — its symbols just
    /// don't appear in the result (warning logged, never thrown on a skip).
    /// Rows carry the snapshot minute in <see cref="QuoteRow.TsUtc"/> /
    /// <see cref="QuoteRow.IstMinute"/>; the caller tags
    /// <see cref="QuoteRow.Type"/>/<see cref="QuoteRow.Underlying"/>/
    /// <see cref="QuoteRow.ExpiryEpoch"/> from its quote specs (or use the
    /// <see cref="Quotes(IReadOnlyList{QuoteSpec}, int, CancellationToken)"/> overload).
    /// </summary>
    public IReadOnlyList<QuoteRow> Quotes(IReadOnlyList<string> symbols, int chunk = 50, CancellationToken ct = default)
    {
        var rows = new List<QuoteRow>();
        if (symbols is null || symbols.Count == 0)
            return rows;

        var (tsUtc, istMinute) = SnapshotMinute();
        var step = Math.Max(1, chunk);
        for (var i = 0; i < symbols.Count; i += step)
        {
            var take = Math.Min(step, symbols.Count - i);
            var batch = new string[take];
            for (var k = 0; k < take; k++)
                batch[k] = symbols[i + k];

            JsonDocument doc;
            try
            {
                doc = GetJson("/quotes", "symbols=" + JoinSymbols(batch), ct);
            }
            catch (RateLimitedException)
            {
                _log.LogWarning("quotes chunk {ChunkStart}-{ChunkEnd} skipped by limiter", i, i + take);
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var s = Str(root, "s");
                if (!string.Equals(s, "ok", StringComparison.Ordinal))
                    throw new InvalidOperationException($"quotes -> {s}: {Str(root, "message") ?? ""}");
                if (root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in d.EnumerateArray())
                    {
                        var row = QuoteRowFromItem(item, tsUtc, istMinute);
                        if (row is not null)
                            rows.Add(row);
                    }
                }
            }
        }
        return rows;
    }

    /// <summary>
    /// Collector convenience: requests the spec symbols (deduped, order kept) and
    /// tags every returned row with its spec's type/underlying/expiry — the join
    /// <c>src/collector.py:snapshot_once</c> does after <c>client.quotes</c>. Specs
    /// whose symbol did not come back are simply absent (the caller counts them as
    /// errors, as python does).
    /// </summary>
    public IReadOnlyList<QuoteRow> Quotes(IReadOnlyList<QuoteSpec> specs, int chunk = 50, CancellationToken ct = default)
    {
        if (specs is null || specs.Count == 0)
            return Array.Empty<QuoteRow>();
        var ordered = new List<string>(specs.Count);
        var bySym = new Dictionary<string, QuoteSpec>(specs.Count, StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (string.IsNullOrEmpty(spec.Symbol) || !bySym.TryAdd(spec.Symbol, spec))
                continue;
            ordered.Add(spec.Symbol);
        }

        var raw = Quotes(ordered, chunk, ct);
        var rows = new List<QuoteRow>(raw.Count);
        foreach (var r in raw)
        {
            if (!bySym.TryGetValue(r.Symbol, out var spec))
                continue;
            rows.Add(r with
            {
                Type = spec.Type,
                InstrumentType = spec.Type,
                Underlying = spec.Underlying,
                ExpiryEpoch = spec.ExpiryEpoch,
            });
        }
        return rows;
    }

    /// <summary>
    /// One <c>/data/quotes</c> item (<c>{"n": ...,"v": {...}}</c>) -> <see cref="QuoteRow"/>.
    /// Port of <c>_quote_row_from_v</c>: only the fields python keeps, with the same
    /// tolerant key fallbacks — ltp: <c>lp|ltp</c>, open: <c>open_price|open|o</c>,
    /// high: <c>high_price|high|h</c>, low: <c>low_price|low|l</c>,
    /// prev_close: <c>prev_close_price|prev_close|pdc</c>, volume: <c>volume|tv</c>,
    /// spread: <c>spread</c>. Plus the quote-level <c>atp|bid|ask</c> columns the
    /// quotes table already has (bid: <c>bid|bid_price</c>, ask: <c>ask|ask_price</c>),
    /// same <c>_q</c> first-present-key semantics. (oi/ch/chp/tt are still NOT kept.)
    /// </summary>
    private QuoteRow? QuoteRowFromItem(JsonElement item, DateTime tsUtc, DateTime istMinute)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var v = item.TryGetProperty("v", out var vv) && vv.ValueKind == JsonValueKind.Object ? vv : item;
        var sym = FirstNonEmpty(Str(v, "symbol"), Str(item, "n"), Str(item, "symbol"));
        if (sym is null)
            return null;
        return new QuoteRow(
            Symbol: sym,
            Type: "",
            Underlying: null,
            ExpiryEpoch: null,
            Ltp: Q(v, "lp", "ltp") ?? 0m,
            Open: Q(v, "open_price", "open", "o"),
            High: Q(v, "high_price", "high", "h"),
            Low: Q(v, "low_price", "low", "l"),
            PrevClose: Q(v, "prev_close_price", "prev_close", "pdc"),
            Volume: Q(v, "volume", "tv"),
            Spread: Q(v, "spread"),
            TsUtc: tsUtc,
            IstMinute: istMinute,
            InstrumentType: "",
            Atp: Q(v, "atp"),
            Bid: Q(v, "bid", "bid_price"),
            Ask: Q(v, "ask", "ask_price"));
    }

    // ---------------------------------------------------------- option chains

    /// <summary>
    /// Raw JSON body of <c>/data/options-chain-v3</c> — port of
    /// <c>FyersClient.option_chain</c>, returned unparsed so the caller owns the leg
    /// mapping (python returns <c>body["data"]</c>; here the caller reads the
    /// <c>data</c> property off this string). Payload keys: optionsChain, callOi,
    /// putOi, expiryData, indiavixData (nearest expiry only carries VIX).
    /// Throws <see cref="InvalidOperationException"/> when <c>s != "ok"</c> (parity).
    /// </summary>
    /// <param name="timestamp">Expiry epoch to fetch a specific chain (python's
    /// <c>timestamp</c> param); null = nearest expiry.</param>
    public string OptionsChain(string chainSymbol, int strikeCount, bool greeks, long? timestamp = null,
        CancellationToken ct = default)
    {
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(chainSymbol))
            .Append("&strikecount=").Append(strikeCount.ToString(CultureInfo.InvariantCulture))
            .Append("&greeks=").Append(greeks ? "1" : "0");
        if (timestamp is not null)
            qs.Append("&timestamp=").Append(timestamp.Value.ToString(CultureInfo.InvariantCulture));

        try
        {
            var body = GetRaw("/options-chain-v3", qs.ToString(), ct);
            using var doc = JsonDocument.Parse(body, JsonOpts);
            EnsureOk("/options-chain-v3", doc);
            return body;
        }
        catch (RateLimitedException)
        {
            _log.LogWarning("{ChainSymbol} options-chain skipped by limiter", chainSymbol);
            throw;
        }
    }

    // ------------------------------------------------------------- leg mapper

    /// <summary>
    /// Maps an <see cref="OptionsChain"/> body to <see cref="LegRow"/>s — port of
    /// <c>src/collector.py:_parse_option_legs</c>. Skips the index/spot leg (no
    /// CE/PE <c>option_type</c> or non-numeric <c>strike_price</c>), and takes each
    /// leg's expiry from <c>leg.expiry</c> with the chain-level <c>data.expiry</c>
    /// fallback (python: <c>int(leg.get("expiry") or data.get("expiry") or 0)</c>).
    /// Field names read verbatim: symbol|fyToken, strike_price, option_type,
    /// oi, oich, prev_oi, volume, ltp, ltpch, ltpchp, bid, ask, greeks.{iv,delta,gamma,theta,vega}.
    /// </summary>
    public IReadOnlyList<LegRow> ParseLegs(string optionsChainJson, string underlying, DateTime tsUtc, DateTime istMinute)
    {
        var rows = new List<LegRow>();
        using var doc = JsonDocument.Parse(optionsChainJson, JsonOpts);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return rows;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
        var chainExpiry = LongOrNull(data, "expiry");
        if (!data.TryGetProperty("optionsChain", out var legs) || legs.ValueKind != JsonValueKind.Array)
            return rows;

        foreach (var leg in legs.EnumerateArray())
        {
            if (leg.ValueKind != JsonValueKind.Object)
                continue;
            var otype = Str(leg, "option_type");
            if (otype is not ("CE" or "PE"))
                continue;
            var strike = NumberOrNull(leg, "strike_price");
            if (strike is null)
                continue;
            var g = leg.TryGetProperty("greeks", out var gg) && gg.ValueKind == JsonValueKind.Object ? gg : default;
            rows.Add(new LegRow(
                Symbol: FirstNonEmpty(Str(leg, "symbol"), Str(leg, "fyToken")) ?? "",
                Underlying: underlying,
                Strike: strike.Value,
                OptionType: otype,
                ExpiryEpoch: LongOrNull(leg, "expiry") ?? chainExpiry ?? 0L,
                Oi: Q(leg, "oi") ?? 0m,
                OiChg: Q(leg, "oich") ?? 0m,
                PrevOi: Q(leg, "prev_oi"),
                Volume: Q(leg, "volume"),
                Iv: Q(g, "iv"),
                Ltp: Q(leg, "ltp"),
                LtpCh: Q(leg, "ltpch"),
                LtpChp: Q(leg, "ltpchp"),
                Bid: Q(leg, "bid"),
                Ask: Q(leg, "ask"),
                Delta: Q(g, "delta"),
                Gamma: Q(g, "gamma"),
                Theta: Q(g, "theta"),
                Vega: Q(g, "vega"),
                TsUtc: tsUtc,
                IstMinute: istMinute));
        }
        return rows;
    }

    /// <summary>A JSON *number* (python's <c>isinstance(strike, (int, float))</c>).</summary>
    private static decimal? NumberOrNull(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    /// <summary>Long from a JSON number or numeric string (leg/chain <c>expiry</c>).</summary>
    private static long? LongOrNull(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i))
            return i;
        if (v.ValueKind == JsonValueKind.String
            && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            return p;
        return null;
    }

    // -------------------------------------------------------------- transport

    /// <summary>Snapshot minute: UTC instant + IST wall clock, both floor(seconds).</summary>
    private (DateTime TsUtc, DateTime IstMinute) SnapshotMinute()
    {
        var now = _clock();
        var utc = now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
        var floored = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        var ist = TimeZoneInfo.ConvertTimeFromUtc(floored, Ist);
        return (floored, DateTime.SpecifyKind(ist, DateTimeKind.Unspecified));
    }

    /// <summary>Rate-limited GET -> parsed body (caller disposes). Port of <c>_get</c>.</summary>
    private JsonDocument GetJson(string path, string query, CancellationToken ct)
    {
        var body = GetRaw(path, query, ct);
        var doc = JsonDocument.Parse(body, JsonOpts);
        EnsureOk(path, doc);
        return doc;
    }

    /// <summary>Throws (parity: python raises RuntimeError) when Fyers says s != "ok".</summary>
    private void EnsureOk(string path, JsonDocument doc)
    {
        var root = doc.RootElement;
        var s = Str(root, "s");
        if (string.Equals(s, "ok", StringComparison.Ordinal))
            return;
        throw new InvalidOperationException($"{path} -> {s}: {Str(root, "message") ?? ""}");
    }

    private string GetRaw(string path, string query, CancellationToken ct)
    {
        // Every call goes through the limiter — no bypass. Minute budget /
        // 429 drain => skip (python parity); second window => pace (block).
        if (!_lim.TryAcquirePaced())
            throw new RateLimitedException($"skip {path} (bucket empty / 429 drain)");

        var url = $"{BaseUrl}{path}?{query}";
        var cred = Credential();
        if (cred is null)
            throw new AuthExpiredException($"{path} -> no access token available (re-login required)");

        for (var attempt = 1; ; attempt++)
        {
            var t0 = _clock();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", cred);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                using var resp = _http.Send(req, timeout.Token);
                var status = (int)resp.StatusCode;
                var body = resp.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();

                // Auth / token problems surface immediately — never retried blindly.
                if (status == 401 || status == 403)
                    throw new AuthExpiredException($"{path} -> HTTP {status}: token invalid/expired");
                if (status == 429)
                {
                    // decision 04: NO in-minute retry. Log one breach, skip this call
                    // for the rest of the minute, resume next minute.
                    _log.LogWarning("429 on {Path} — bucket drained", path);
                    throw new RateLimitedException($"429 on {path} — bucket drained");
                }

                var lat = (long)(_clock() - t0).TotalMilliseconds;
                using var probe = JsonDocument.Parse(body, JsonOpts);
                var root = probe.RootElement;
                _log.LogDebug("{Path} s={S} code={Code} lat={Latency}ms", path, Str(root, "s"), IntOrNull(root, "code"), lat);

                if (IsAuthFailure(root))
                    throw new AuthExpiredException($"{path} -> {body}");

                if (attempt > 1)
                    _log.LogInformation("GET {Path} succeeded on attempt {Attempt}", path, attempt);
                return body;
            }
            catch (Exception e) when (e is AuthExpiredException or RateLimitedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;                                   // caller is shutting down
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TimeoutException
                                        or IOException or OperationCanceledException)
            {
                // network / malformed-body failure: back off and retry, parity with
                // python's `wait = min(2 ** attempt, 8)`.
                if (attempt >= MaxRetries)
                    throw new InvalidOperationException(
                        $"GET {path} failed after {attempt} attempts: {e.Message}", e);
                var wait = Backoff[Math.Min(attempt - 1, Backoff.Length - 1)];
                _log.LogWarning("GET {Path} attempt {Attempt} failed: {Message} (retry in {Seconds:0.#}s)",
                    path, attempt, e.Message, wait.TotalSeconds);
                Thread.Sleep(wait);
            }
        }
    }

    /// <summary>Authorization header value: appId:accessToken (python f-string parity).</summary>
    private string? Credential()
    {
        var token = _accessToken();
        if (string.IsNullOrEmpty(token))
            return null;
        return _appId is null ? token : $"{_appId}:{token}";
    }

    /// <summary>Fyers business-level auth failure on the parsed body.</summary>
    private static bool IsAuthFailure(JsonElement root)
    {
        var code = IntOrNull(root, "code");
        if (code is not null && Array.IndexOf(AuthCodes, code.Value) >= 0)
            return true;
        if (string.Equals(Str(root, "s"), "error", StringComparison.Ordinal))
        {
            var msg = Str(root, "message");
            if (msg is not null && msg.Contains("token", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- helpers

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? IntOrNull(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i;
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            return p;
        return null;
    }

    /// <summary>python <c>_q</c>: first present, non-null key — coerced to decimal.</summary>
    private static decimal? Q(JsonElement v, params string[] keys)
    {
        if (v.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var k in keys)
        {
            if (!v.TryGetProperty(k, out var e))
                continue;
            if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (e.ValueKind == JsonValueKind.Number)
                return e.GetDecimal();
            if (e.ValueKind == JsonValueKind.String
                && decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
                return v;
        }
        return null;
    }

    /// <summary>Comma-joined chunk (literal commas — Fyers' documented form); each symbol escaped.</summary>
    private static string JoinSymbols(string[] symbols)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < symbols.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(EscapeSymbol(symbols[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escape a symbol for the query string while leaving the documented
    /// <c>NSE:SYMBOL-EQ</c> colon readable; real tickers' <c>&amp;</c> (M&amp;M) IS escaped.
    /// </summary>
    private static string EscapeSymbol(string? symbol)
        => Uri.EscapeDataString(symbol ?? "").Replace("%3A", ":", StringComparison.Ordinal);
}
