using System.Net;
using System.Text;
using Fyers.Core.Fyers;
using Microsoft.Extensions.Logging;
using Xunit;

// NB: the RateLimiter type is referenced unqualified — `using Fyers.Core.RateLimiter;`
// is CS0138 (the type shadows its own namespace name).

namespace Fyers.Core.Tests;

/// <summary>
/// Parity tests for the port of src/fyers_client.py + src/collector.py:_quote_row_from_v.
/// All HTTP is served by a fake HttpMessageHandler that captures the request line and
/// headers; the limiter is the real RateLimiter driven by a fake clock.
/// </summary>
public sealed class ClientTests
{
    /// <summary>2026-09-15 05:30Z == 11:00 IST — a live trading minute.</summary>
    private static readonly DateTime Base = new(2026, 9, 15, 5, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime BaseIst = new(2026, 9, 15, 11, 0, 0, DateTimeKind.Unspecified);

    // ------------------------------------------------------------------ fakes

    private sealed record Captured(string Url, string? Auth, string? Ua);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

        // FyersClient issues synchronous calls, so capture and reply on the sync path.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var auth = request.Headers.TryGetValues("Authorization", out var a) ? string.Join("", a) : null;
            // NB: the typed User-Agent header parses "Mozilla/5.0 (…)" into product + comment
            // tokens; re-joining with a space reproduces the exact wire value.
            var ua = request.Headers.TryGetValues("User-Agent", out var u) ? string.Join(" ", u) : null;
            Requests.Add(new Captured(request.RequestUri?.ToString() ?? "", auth, ua));
            return Responder?.Invoke(request) ?? Json();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        public string Query(int index) => Requests[index].Url[(Requests[index].Url.IndexOf('?') + 1)..];

        /// <summary>Decodes <c>k=v&amp;k2=v2</c> pairs (each value %-decoded) into a list.</summary>
        public List<(string Key, string Value)> Pairs(int index)
            => Query(index).Split('&').Select(p =>
            {
                var eq = p.IndexOf('=');
                return (p[..eq], Uri.UnescapeDataString(p[(eq + 1)..]));
            }).ToList();
    }

    private static HttpResponseMessage Json(string body = """{"s":"ok","code":200,"message":"done","d":[]}""",
        HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>ILogger that keeps every formatted line so tests can assert on them.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool Has(LogLevel level, string text)
            => Entries.Any(e => e.Level == level && string.Equals(e.Message, text, StringComparison.Ordinal));

        public bool HasContaining(LogLevel level, string text)
            => Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.Ordinal));
    }

    private static (FyersClient Client, FakeHandler Handler, CapturingLogger Log) Build(
        RateLimiter? lim = null, Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        string? accessToken = "TOKEN123", string? appId = "APPID-1", DateTime? clock = null)
    {
        var handler = new FakeHandler { Responder = responder };
        var log = new CapturingLogger();
        Func<string?> tokenFn = accessToken is null ? (Func<string?>)(() => null) : () => accessToken;
        var now = clock ?? Base;
        var client = new FyersClient(new HttpClient(handler), tokenFn,
            lim ?? new RateLimiter(10_000, 10_000, () => Base), log, appId, () => now);
        return (client, handler, log);
    }

    // --------------------------------------------------------------- chunking

    [Fact]
    public void Quotes_120_symbols_chunk_50_sends_three_gets_with_comma_joined_symbols()
    {
        var (client, handler, _) = Build();
        var symbols = Enumerable.Range(0, 120).Select(i => $"NSE:S{i:D3}-EQ").ToList();

        var rows = client.Quotes(symbols, chunk: 50);

        Assert.Equal(3, handler.Requests.Count);
        // chunk boundaries + comma-joined payload, in order
        var expected = new[]
        {
            string.Join(",", Enumerable.Range(0, 50).Select(i => $"NSE:S{i:D3}-EQ")),
            string.Join(",", Enumerable.Range(50, 50).Select(i => $"NSE:S{i:D3}-EQ")),
            string.Join(",", Enumerable.Range(100, 20).Select(i => $"NSE:S{i:D3}-EQ")),
        };
        for (var i = 0; i < 3; i++)
        {
            Assert.StartsWith(FyersClient.BaseUrl + "/quotes?symbols=", handler.Requests[i].Url);
            var pairs = handler.Pairs(i);
            var sym = pairs.Single(p => p.Key == "symbols");
            Assert.Equal(expected[i], Uri.UnescapeDataString(sym.Value));
        }
        Assert.Empty(rows);                                 // fixture carries no d[] items
    }

    [Fact]
    public void Quotes_sends_python_auth_header_and_user_agent()
    {
        var (client, handler, _) = Build(accessToken: "tok-abc", appId: "XYZ123-100");
        client.Quotes(new[] { "NSE:SBIN-EQ" });

        var req = Assert.Single(handler.Requests);
        Assert.Equal("XYZ123-100:tok-abc", req.Auth);       // python: f"{app_id}:{access_token}"
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", req.Ua);
        Assert.StartsWith("https://api-t1.fyers.in/data/quotes", req.Url);
    }

    [Fact]
    public void Quotes_without_app_id_uses_precomposed_credential_verbatim()
    {
        var (client, handler, _) = Build(accessToken: "XYZ123-100:tok-abc", appId: null);
        client.Quotes(new[] { "NSE:SBIN-EQ" });
        Assert.Equal("XYZ123-100:tok-abc", Assert.Single(handler.Requests).Auth);
    }

    [Fact]
    public void Quotes_escapes_ampersand_but_keeps_colon_readable()
    {
        var (client, handler, _) = Build();
        client.Quotes(new[] { "NSE:M&M-EQ", "NSE:RELIANCE-EQ" });
        var raw = handler.Query(0);
        Assert.Contains("symbols=NSE:M%26M-EQ,NSE:RELIANCE-EQ", raw);
    }

    // ---------------------------------------------------------------- mapping

    private const string CanonicalQuotes = """
        {"s":"ok","code":200,"message":"done","d":[
          {"n":"NSE:RELIANCE-EQ","s":"ok","v":{"ch":-5.10,"chp":-0.41,"lp":1234.50,"spread":0.05,
           "prev_close_price":1239.60,"open_price":1238.00,"high_price":1241.00,"low_price":1230.10,
           "volume":1234567,"atp":1235.40,"bid":1234.40,"ask":1234.60,"tt":1765512600}},
          {"n":"NSE:NIFTY26SEPFUT","s":"ok","v":{"symbol":"NSE:NIFTY26SEPFUT","ltp":25999.5,
           "prev_close_price":25900,"open_price":25910,"high_price":26010,"low_price":25880,
           "volume":98765,"spread":0.5}}
        ]}
        """;

    [Fact]
    public void Quotes_maps_the_v_dict_field_for_field()
    {
        var (client, handler, _) = Build(responder: _ => Json(CanonicalQuotes));

        var rows = client.Quotes(new[] { "NSE:RELIANCE-EQ", "NSE:NIFTY26SEPFUT" });

        Assert.Equal(2, rows.Count);
        var r = rows[0];
        Assert.Equal("NSE:RELIANCE-EQ", r.Symbol);          // from item.n
        Assert.Equal(1234.50m, r.Ltp);                      // lp
        Assert.Equal(1238.00m, r.Open);                     // open_price
        Assert.Equal(1241.00m, r.High);                     // high_price
        Assert.Equal(1230.10m, r.Low);                      // low_price
        Assert.Equal(1239.60m, r.PrevClose);                // prev_close_price
        Assert.Equal(1234567m, r.Volume);                   // volume
        Assert.Equal(0.05m, r.Spread);                      // spread
        Assert.Null(r.Underlying);                          // caller tags these
        Assert.Null(r.ExpiryEpoch);
        Assert.Equal(string.Empty, r.Type);
        Assert.Equal(string.Empty, r.InstrumentType);
        Assert.Equal(Base, r.TsUtc);
        Assert.Equal(BaseIst, r.IstMinute);
        // v.symbol wins over item.n (python: v.get("symbol") or item.get("n"))
        Assert.Equal("NSE:NIFTY26SEPFUT", rows[1].Symbol);
    }

    [Fact]
    public void Quotes_falls_back_to_the_legacy_fyers_keys_like_python()
    {
        const string body = """
            {"s":"ok","code":200,"d":[{"n":"NSE:FOO-EQ","v":{"ltp":101.5,"tv":500,"pdc":100.0,
             "o":100.5,"h":102.0,"l":99.75,"spread":0.10,"oi":1234}}]}
            """;
        var (client, _, _) = Build(responder: _ => Json(body));

        var row = Assert.Single(client.Quotes(new[] { "NSE:FOO-EQ" }));

        Assert.Equal(101.5m, row.Ltp);                      // lp -> ltp
        Assert.Equal(500m, row.Volume);                     // volume -> tv
        Assert.Equal(100.0m, row.PrevClose);                // prev_close_price -> pdc
        Assert.Equal(100.5m, row.Open);                     // open_price -> o
        Assert.Equal(102.0m, row.High);                     // high_price -> h
        Assert.Equal(99.75m, row.Low);                      // low_price -> l
        Assert.Equal(0.10m, row.Spread);
    }

    [Fact]
    public void Quotes_skips_rows_without_any_symbol()
    {
        const string body = """{"s":"ok","code":200,"d":[{"v":{"lp":1.0}},{"n":"NSE:OK-EQ","v":{"lp":2.0}}]}""";
        var (client, _, _) = Build(responder: _ => Json(body));
        Assert.Equal("NSE:OK-EQ", Assert.Single(client.Quotes(new[] { "NSE:OK-EQ" })).Symbol);
    }

    [Fact]
    public void Quotes_returns_empty_for_an_empty_symbol_list_and_makes_no_calls()
    {
        var (client, handler, _) = Build();
        Assert.Empty(client.Quotes(Array.Empty<string>()));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------- leg mapper

    private const string ChainFixture = """
        {"s":"ok","code":200,"data":{
          "expiry":1790676600,
          "optionsChain":[
            {"symbol":"NSE:NIFTY26SEP24800CE","option_type":"CE","strike_price":24800,
             "expiry":1790676600,"ltp":182.35,"ltpch":4.10,"ltpchp":2.30,
             "bid":181.90,"ask":182.80,"oi":12345678,"oich":98765,"prev_oi":12246913,
             "volume":654321,"greeks":{"iv":13.42,"delta":0.5123,"gamma":0.000412,
             "theta":-4.21,"vega":6.13}},
            {"symbol":"NSE:NIFTY26SEP24800PE","option_type":"PE","strike_price":24800,
             "expiry":1790676600,"ltp":140.05,"ltpch":-3.55,"ltpchp":-2.47,
             "bid":139.60,"ask":140.50,"oi":20112233,"oich":-152000,"prev_oi":20264233,
             "volume":789654,"greeks":{"iv":14.08,"delta":-0.4871,"gamma":0.000421,
             "theta":-4.35,"vega":6.21}},
            {"option_type":"XX","strike_price":-1,"expiry":1790676600,
             "symbol":"NSE:NIFTY-INDEX","ltp":25123.40},
            {"option_type":"CE","strike_price":"not-a-number","ltp":1.0},
            {"option_type":"PE","strike_price":24900,"expiry":1793095800,
             "oi":500,"oich":10,"volume":25,"ltp":99.5,"ltpch":-1,"ltpchp":-0.99}
          ]}}
        """;

    [Fact]
    public void ParseLegs_maps_the_chain_leg_fields_including_ltp()
    {
        var (client, handler, _) = Build(responder: _ => Json("""{"s":"ok","code":200,"data":{}}"""));
        var legs = client.ParseLegs(ChainFixture, "NIFTY", Base, BaseIst);

        Assert.Equal(3, legs.Count);                        // index/spot + bad-strike legs skipped
        var ce = legs[0];
        Assert.Equal("NSE:NIFTY26SEP24800CE", ce.Symbol);
        Assert.Equal("NIFTY", ce.Underlying);
        Assert.Equal(24800m, ce.Strike);
        Assert.Equal("CE", ce.OptionType);
        Assert.Equal(1790676600L, ce.ExpiryEpoch);
        Assert.Equal(12345678m, ce.Oi);
        Assert.Equal(98765m, ce.OiChg);
        Assert.Equal(12246913m, ce.PrevOi);
        Assert.Equal(654321m, ce.Volume);
        Assert.Equal(182.35m, ce.Ltp);                      // ltp
        Assert.Equal(4.10m, ce.LtpCh);                      // ltpch
        Assert.Equal(2.30m, ce.LtpChp);                     // ltpchp
        Assert.Equal(181.90m, ce.Bid);
        Assert.Equal(182.80m, ce.Ask);
        Assert.Equal(13.42m, ce.Iv);
        Assert.Equal(0.5123m, ce.Delta);
        Assert.Equal(0.000412m, ce.Gamma);
        Assert.Equal(-4.21m, ce.Theta);
        Assert.Equal(6.13m, ce.Vega);
        Assert.Equal(Base, ce.TsUtc);
        Assert.Equal(BaseIst, ce.IstMinute);

        var pe = legs[1];
        Assert.Equal("PE", pe.OptionType);
        Assert.Equal(-0.4871m, pe.Delta);
        Assert.Equal(-152000m, pe.OiChg);
    }

    [Fact]
    public void ParseLegs_falls_back_to_the_chain_level_expiry_and_defaults_missing_oi()
    {
        const string body = """
            {"data":{"expiry":1790676600,"optionsChain":[
                {"option_type":"PE","strike_price":24900,"ltp":99.5}]}}
            """;
        var (client, _, _) = Build(responder: _ => Json(body));

        var leg = Assert.Single(client.ParseLegs(body, "NIFTY", Base, BaseIst));

        Assert.Equal(1790676600L, leg.ExpiryEpoch);         // data.expiry fallback
        Assert.Equal(0m, leg.Oi);                           // absent oi -> 0 (non-nullable column)
        Assert.Equal(0m, leg.OiChg);
        Assert.Null(leg.PrevOi);
        Assert.Null(leg.Volume);
        Assert.Equal(99.5m, leg.Ltp);                       // fixture carries ltp
        Assert.Null(leg.LtpCh);
        Assert.Null(leg.LtpChp);
        Assert.Null(leg.Bid);
        Assert.Null(leg.Ask);
        Assert.Null(leg.Iv);
        Assert.Null(leg.Delta);
    }

    [Fact]
    public void ParseLegs_returns_empty_for_a_body_without_a_chain()
    {
        var (client, _, _) = Build(responder: _ => Json(body: """{"s":"ok","data":{}}"""));
        Assert.Empty(client.ParseLegs("""{"s":"ok","code":200}""", "NIFTY", Base, BaseIst));
        Assert.Empty(client.ParseLegs("""{"data":{"optionsChain":[]}}""", "NIFTY", Base, BaseIst));
    }

    // ------------------------------------------------------------- spec join

    [Fact]
    public void Quotes_by_spec_tags_rows_with_type_underlying_and_expiry()
    {
        const string body = """
            {"s":"ok","code":200,"d":[{"n":"NSE:NIFTY26SEPFUT","v":{"lp":26000.25,"volume":42}},
                                       {"n":"NSE:INDIAVIX","v":{"lp":13.11}}]}
            """;
        var (client, handler, _) = Build(responder: _ => Json(body));
        var specs = new List<QuoteSpec>
        {
            new("NSE:NIFTY26SEPFUT", "FUTURE", "NIFTY", 1790676600),
            new("NSE:INDIAVIX", "VIX", null, null),
        };

        var rows = client.Quotes(specs);

        Assert.Equal(2, rows.Count);
        Assert.Equal("FUTURE", rows[0].Type);
        Assert.Equal("FUTURE", rows[0].InstrumentType);
        Assert.Equal("NIFTY", rows[0].Underlying);
        Assert.Equal(1790676600L, rows[0].ExpiryEpoch);
        Assert.Equal("VIX", rows[1].Type);
        Assert.Null(rows[1].Underlying);
        Assert.Null(rows[1].ExpiryEpoch);
        // dedup: duplicate spec symbols are requested once
        var specs2 = new List<QuoteSpec> { new("NSE:DUP-EQ", "CASH", "DUP", null), new("NSE:DUP-EQ", "CASH", "DUP", null) };
        client.Quotes(specs2);
        Assert.Single(handler.Requests.Skip(1));            // 1 request for the deduped symbol
        Assert.Contains("symbols=NSE:DUP-EQ", handler.Query(1));
    }

    // ------------------------------------------------------------ limiter skip

    [Fact]
    public void Quotes_skipped_chunk_logs_warning_and_returns_partial()
    {
        // minute budget of 1 call: chunk #1 goes out, chunk #2 is skipped, chunk #3 skipped
        var lim = new RateLimiter(perMinute: 1, perSecond: 100, () => Base);
        const string body = """{"s":"ok","code":200,"d":[{"n":"NSE:A-EQ","v":{"lp":10.0}}]}""";
        var (client, handler, log) = Build(lim: lim, responder: _ => Json(body));

        var rows = client.Quotes(Enumerable.Range(0, 100).Select(i => $"NSE:{i}-EQ").ToList(), chunk: 50);

        Assert.Single(handler.Requests);                    // only the first chunk hit the wire
        var row = Assert.Single(rows);
        Assert.Equal("NSE:A-EQ", row.Symbol);
        Assert.Equal(10.0m, row.Ltp);
        Assert.True(log.Has(LogLevel.Warning, "quotes chunk 50-100 skipped by limiter"));
        Assert.False(log.HasContaining(LogLevel.Error, "quotes chunk"));  // never throws on skip
    }

    [Fact]
    public void Quotes_after_a_429_drain_every_chunk_skips()
    {
        var lim = new RateLimiter(200, 8, () => Base);
        lim.DrainMinute("test");
        var (client, handler, log) = Build(lim: lim);

        var rows = client.Quotes(new[] { "NSE:A-EQ", "NSE:B-EQ" }, chunk: 1);

        Assert.Empty(handler.Requests);
        Assert.Empty(rows);
        Assert.True(log.Has(LogLevel.Warning, "quotes chunk 0-1 skipped by limiter"));
        Assert.True(log.Has(LogLevel.Warning, "quotes chunk 1-2 skipped by limiter"));
    }

    // ------------------------------------------------------------ option chain

    [Fact]
    public void OptionsChain_builds_symbol_strikecount_greeks_url_and_returns_raw_body()
    {
        const string body = """{"s":"ok","code":200,"data":{"optionsChain":[],"expiryData":[]}}""";
        var (client, handler, _) = Build(responder: _ => Json(body));

        var raw = client.OptionsChain("NSE:NIFTY-INDEX", 50, greeks: true);

        Assert.Equal(body, raw);                            // raw json string, caller parses
        var req = Assert.Single(handler.Requests);
        Assert.StartsWith(FyersClient.BaseUrl + "/options-chain-v3?", req.Url);
        var pairs = handler.Pairs(0);
        Assert.Equal(("symbol", "NSE:NIFTY-INDEX"), pairs[0]);
        Assert.Contains(("strikecount", "50"), pairs);
        Assert.Contains(("greeks", "1"), pairs);
        Assert.DoesNotContain(pairs, p => p.Key == "timestamp");
    }

    [Fact]
    public void OptionsChain_greeks_false_emits_zero_and_timestamp_is_honoured()
    {
        var (client, handler, _) = Build(responder: _ => Json("""{"s":"ok","code":200,"data":{}}"""));

        client.OptionsChain("NSE:RELIANCE-EQ", 12, greeks: false, timestamp: 1793095800);

        var pairs = handler.Pairs(0);
        Assert.Contains(("greeks", "0"), pairs);
        Assert.Contains(("strikecount", "12"), pairs);
        Assert.Contains(("timestamp", "1793095800"), pairs);
        Assert.Contains(("symbol", "NSE:RELIANCE-EQ"), pairs);
    }

    [Fact]
    public void OptionsChain_non_ok_body_throws()
    {
        var (client, _, _) = Build(responder: _ => Json("""{"s":"error","code":405,"message":"bad symbol"}"""));
        Assert.Throws<InvalidOperationException>(() => client.OptionsChain("NSE:NOPE-EQ", 5, true));
    }

    // ------------------------------------------------------------ auth mapping

    [Theory]
    [InlineData(-8)]
    [InlineData(-15)]
    [InlineData(-16)]
    [InlineData(-17)]
    [InlineData(416)]
    [InlineData(403)]
    public void Quotes_fyers_auth_codes_throw_auth_expired(int code)
    {
        var (client, _, _) = Build(responder: _ => Json($$"""{"s":"error","code":{{code}},"message":"..."}"""));
        var ex = Assert.Throws<AuthExpiredException>(() => client.Quotes(new[] { "NSE:A-EQ" }));
        Assert.Contains("\"code\":" + code, ex.Message);    // the offending body is surfaced
    }

    [Fact]
    public void Quotes_error_body_mentioning_token_throws_auth_expired()
    {
        var (client, _, _) = Build(responder: _ => Json(
            """{"s":"error","code":405,"message":"Invalid or expired access token"}"""));
        Assert.Throws<AuthExpiredException>(() => client.Quotes(new[] { "NSE:A-EQ" }));
    }

    [Fact]
    public void Quotes_error_body_without_token_is_not_auth()
    {
        var (client, _, _) = Build(responder: _ => Json(
            """{"s":"error","code":405,"message":"unknown symbol"}"""));
        Assert.Throws<InvalidOperationException>(() => client.Quotes(new[] { "NSE:A-EQ" }));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Quotes_http_401_403_throw_auth_expired(HttpStatusCode status)
    {
        var (client, _, _) = Build(responder: _ => Json("""{"s":"error"}""", status));
        var ex = Assert.Throws<AuthExpiredException>(() => client.Quotes(new[] { "NSE:A-EQ" }));
        Assert.Contains("HTTP", ex.Message);
    }

    [Fact]
    public void Quotes_with_no_available_token_throws_auth_expired()
    {
        var (client, handler, _) = Build(accessToken: null);
        Assert.Throws<AuthExpiredException>(() => client.Quotes(new[] { "NSE:A-EQ" }));
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------- clock

    [Fact]
    public void Quotes_timestamps_floor_to_the_snapshot_minute()
    {
        // 11:00:41.730 IST -> rows must carry the 11:00 minute, not the wall instant.
        var clock = new DateTime(2026, 9, 15, 5, 30, 41, DateTimeKind.Utc).AddMilliseconds(730);
        var (client, _, _) = Build(clock: clock, responder: _ => Json(
            """{"s":"ok","code":200,"d":[{"n":"NSE:A-EQ","v":{"lp":7.25}}]}"""));

        var row = Assert.Single(client.Quotes(new[] { "NSE:A-EQ" }));

        Assert.Equal(new DateTime(2026, 9, 15, 5, 30, 0, DateTimeKind.Utc), row.TsUtc);
        Assert.Equal(new DateTime(2026, 9, 15, 11, 0, 0, DateTimeKind.Unspecified), row.IstMinute);
        Assert.Equal(7.25m, row.Ltp);
    }
}
