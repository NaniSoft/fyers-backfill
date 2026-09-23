using System.Net;
using System.Text;
using Fyers.Collector.Services;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

// Fyers.Collector is referenced for OiService, a BackgroundService that needs
// Microsoft.Extensions.Hosting (same shape as Fyers.Token.Tests -> Fyers.Token).

namespace Fyers.Core.Tests;

// NB (same trap as StorageTests.cs / CandlesTests.cs): inside namespace
// Fyers.Core.Tests the bare name `Storage` resolves to the NAMESPACE
// Fyers.Core.Storage (CS0118) — alias the type, INSIDE the namespace declaration.
using Storage = global::Fyers.Core.Storage.Storage;

/// <summary>
/// Tests for the live-OI capture: <see cref="FyersClient.Depth(string, CancellationToken)"/>
/// (endpoint + wire-shape mapping) and <see cref="OiService"/> (the depth set
/// filter, the minute grid, and the enriched <c>quotes</c> rows it writes).
///
/// All HTTP goes through a fake <see cref="HttpMessageHandler"/> answering the
/// shape verified live on 2026-09-15: <c>{"s":"ok","d":{"&lt;symbol&gt;":{...}}}</c>
/// — an object keyed by symbol, with Fyers' string-typed numbers where it sends
/// them. The OiService tests run a REAL FyersClient over the fake, so the depth
/// request the service produces is asserted end to end.
/// </summary>
public sealed class OiTests
{
    /// <summary>The parity day: Tuesday 2026-09-15, not a builtin holiday.</summary>
    private static readonly DateOnly Day = new(2026, 9, 15);

    /// <summary>2026-09-15 03:45Z == 09:15 IST — a Full-mode minute.</summary>
    private static readonly DateTime TsUtc = new(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime IstMinute = new(2026, 9, 15, 9, 15, 0);
    private static readonly DateTime Boundary = new(2026, 9, 15, 9, 15, 0);

    private const long Exp1 = 1790676600L;
    private const long Exp2 = 1793182200L;
    private const long Exp3 = 1795687800L;

    // ---- the 9-contract depth set: currency (current + 2 monthlies) + MIDCPNIFTY
    private const string Usd1 = "NSE:USDINR26SEPFUT";
    private const string Usd2 = "NSE:USDINR26OCTFUT";
    private const string Usd3 = "NSE:USDINR26NOVFUT";
    private const string Eur1 = "NSE:EURINR26SEPFUT";
    private const string Eur2 = "NSE:EURINR26OCTFUT";
    private const string Eur3 = "NSE:EURINR26NOVFUT";
    private const string Mid1 = "NSE:MIDCPNIFTY26SEPFUT";
    private const string Mid2 = "NSE:MIDCPNIFTY26OCTFUT";
    private const string Mid3 = "NSE:MIDCPNIFTY26NOVFUT";

    private static readonly string[] Nine = [Usd1, Usd2, Usd3, Eur1, Eur2, Eur3, Mid1, Mid2, Mid3];

    /// <summary>The spec array the orchestrator hands over: the 9 depth contracts
    /// PLUS commodity/cash/index/VIX specs that must be ignored.</summary>
    private static QuoteSpec[] Specs() =>
    [
        new(Usd1, "FUTURE", "USDINR", Exp1),
        new(Usd2, "FUTURE", "USDINR", Exp2),
        new(Usd3, "FUTURE", "USDINR", Exp3),
        new(Eur1, "FUTURE", "EURINR", Exp1),
        new(Eur2, "FUTURE", "EURINR", Exp2),
        new(Eur3, "FUTURE", "EURINR", Exp3),
        new(Mid1, "FUTURE", "MIDCPNIFTY", Exp1),
        new(Mid2, "FUTURE", "MIDCPNIFTY", Exp2),
        new(Mid3, "FUTURE", "MIDCPNIFTY", Exp3),
        new("NSE:NIFTY-INDEX", "SPOT", "NIFTY", null),          // index spot: no depth
        new("NSE:INDIAVIX", "VIX", null, null),                 // VIX: no depth
        new("NSE:RELIANCE-EQ", "CASH", "RELIANCE", null),       // cash equity: no depth
        new("NSE:GOLDM26SEPFUT", "FUTURE", "GOLDM", Exp1),      // commodity: oi is always 0
        new("MCX:SILVER26SEPFUT", "FUTURE", "SILVER", Exp1),    // commodity, other exchange
    ];

    // ------------------------------------------------------------------ fakes

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _requests = [];

        /// <summary>Symbol -> body; a symbol with no entry answers HTTP 200 s=error.</summary>
        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

        /// <summary>Requested symbol -> status code override (401 = token dead).</summary>
        public Dictionary<string, HttpStatusCode> Status { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Requested
        {
            get { lock (_gate) return [.. _requests]; }
        }

        public List<string> Urls
        {
            get { lock (_gate) return [.. _requests]; }
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            lock (_gate) { _requests.Add(url); }
            var symbol = SymbolOf(url);
            if (Status.TryGetValue(symbol, out var code))
                return new HttpResponseMessage(code) { Content = new StringContent("", Encoding.UTF8, "application/json") };
            return Json(Answers.TryGetValue(symbol, out var body)
                ? body
                : """{"s":"error","code":405,"message":"unexpected symbol"}""");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Send(request, ct));

        public IReadOnlyList<string> DepthUrls()
            => Requested.Where(u => u.Contains("/depth?", StringComparison.Ordinal)).ToList();
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string SymbolOf(string url)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var p in query.Split('&'))
        {
            var eq = p.IndexOf('=');
            if (p[..eq] == "symbol")
                return Uri.UnescapeDataString(p[(eq + 1)..]);
        }
        return "";
    }

    /// <summary>
    /// The live <c>/data/depth</c> body (verified 2026-09-15) for one symbol.
    /// <paramref name="oi"/>/<paramref name="pdoi"/>/<paramref name="v"/> are raw JSON
    /// so a test can send Fyers' string-typed numbers; <paramref name="book"/>=false
    /// drops bids/asks (empty book).
    /// </summary>
    private static string DepthBody(string symbol, string oi, string pdoi, string v, bool book = true)
    {
        // Hand-built (not an interpolated raw string): the body ends in three
        // closing braces, which a $$""" literal cannot carry.
        var sb = new StringBuilder();
        sb.Append("{\"s\":\"ok\",\"code\":200,\"message\":\"done\",\"d\":{\"")
          .Append(symbol).Append("\":{");
        sb.Append("\"ltp\":86.4825,\"oi\":").Append(oi)
          .Append(",\"pdoi\":").Append(pdoi)
          .Append(",\"v\":").Append(v)
          .Append(",\"atp\":86.3100,");
        if (book)
            sb.Append("\"bids\":[{\"price\":86.4800,\"volume\":2,\"orders\":1}],")
              .Append("\"asks\":[{\"price\":86.4850,\"volume\":5,\"orders\":2}],");
        sb.Append("\"lower_ckt\":77.83,\"upper_ckt\":95.13,")
          .Append("\"o\":86.2000,\"h\":86.5500,\"l\":86.1000,\"c\":86.4825,\"chp\":0.35}}}");
        return sb.ToString();
    }

    /// <summary>ILogger that keeps every formatted line (message text assertions).</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool HasContaining(LogLevel level, string text)
            => Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.Ordinal));
    }

    private sealed class CaptureLogger<T>(CapturingLogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>Mutable IST wall clock — the service's injectable clock.</summary>
    private sealed class IstClock
    {
        public DateTime Now { get; set; } = new(2026, 9, 15, 9, 15, 2);
    }

    /// <summary>Everything one test needs: temp storage, fake http, config, clock.</summary>
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fyers-oi-tests-" + Guid.NewGuid().ToString("N"));
        public Storage Store { get; }
        public FakeHandler Handler { get; } = new();
        public CapturingLogger Log { get; } = new();
        public IstClock Clock { get; } = new();
        public AppConfig Cfg { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            Store = new Storage(Dir, Path.Combine(Dir, "summary.db"));
            Cfg = new AppConfig
            {
                StrikeCount = 50,
                FuturesNMonths = 3,
                Symbols = [],
                // 08:30-09:00 = quotes_only shoulder, 09:00-16:00 = full
                Session = new SessionCfg("Asia/Kolkata",
                    new TimeOnly(8, 30), "08:30",
                    new TimeOnly(9, 0), "09:00",
                    new TimeOnly(16, 0), "16:00",
                    new TimeOnly(16, 0), "16:00",
                    new TimeOnly(15, 31), "15:31",
                    []),
            };
            foreach (var sym in Nine)
                Handler.Answers[sym] = DepthBody(sym, oi: "19170000", pdoi: "19000000", v: "123456");
            Handler.Answers[Mid1] = DepthBody(Mid1, oi: "105120", pdoi: "104000", v: "98765");
        }

        public MarketCalendar Cal => new(Cfg, Day, MarketCalendar.HolidaysFromConfig(Cfg));

        /// <summary>The client's clock shares the service clock's minute (IST -> UTC).</summary>
        public FyersClient BuildClient()
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(Clock.Now, OiService.IstZone);
            return new FyersClient(new HttpClient(Handler), () => "TOKEN",
                new RateLimiter(10_000, 10_000, () => utc), Log, appId: "APPID-1", clock: () => utc);
        }

        public OiService BuildService(QuoteSpec[]? specs = null, Func<QuoteSpec[]>? specFn = null)
        {
            var fn = specFn ?? new Func<QuoteSpec[]>(() => specs ?? Specs());
            return new OiService(Cfg, BuildClient(), Cal, fn, Store,
                new CaptureLogger<OiService>(Log), clock: () => Clock.Now);
        }

        public SqliteConnection Open(string path)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn;
        }

        public SqliteConnection OpenDay() => Open(Store.DayPath(Day));

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Dir, recursive: true); }
            catch { /* temp dir cleanup is best-effort */ }
        }
    }

    /// <summary>A full quotes row, read back from the day db (column-for-column).</summary>
    private sealed record QRow(
        long TsUtc, string IstMinute, string Symbol, string InstrumentType, string? Underlying,
        long? ExpiryEpoch, double? Ltp, double? Bid, double? Ask, long? BidSize, long? AskSize,
        long? Oi, long? Volume, double? Ch, double? Chp, double? PrevClose,
        double? Open, double? High, double? Low, double? Spread, double? Atp);

    private static List<QRow> ReadQuotes(SqliteConnection conn)
    {
        // A minute that writes nothing never creates the day file, so the reader
        // must not demand the schema (EodTests' legacy-day-file parity).
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='quotes'";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0) return [];
        }

        var rows = new List<QRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts_utc, ist_minute, symbol, instrument_type, underlying, expiry_epoch,
                   ltp, bid, ask, bid_size, ask_size, oi, volume,
                   ch, chp, prev_close, open, high, low, spread, atp
            FROM quotes ORDER BY symbol, ts_utc
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new QRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetInt64(10),
                r.IsDBNull(11) ? null : r.GetInt64(11),
                r.IsDBNull(12) ? null : r.GetInt64(12),
                r.IsDBNull(13) ? null : r.GetDouble(13),
                r.IsDBNull(14) ? null : r.GetDouble(14),
                r.IsDBNull(15) ? null : r.GetDouble(15),
                r.IsDBNull(16) ? null : r.GetDouble(16),
                r.IsDBNull(17) ? null : r.GetDouble(17),
                r.IsDBNull(18) ? null : r.GetDouble(18),
                r.IsDBNull(19) ? null : r.GetDouble(19),
                r.IsDBNull(20) ? null : r.GetDouble(20)));
        }
        return rows;
    }

    // ========================================================= Depth (client)

    [Fact]
    public void Depth_sends_one_symbol_per_call_with_ohlcv_flag_and_maps_the_row()
    {
        using var f = new Fixture();
        f.Handler.Answers[Usd1] = DepthBody(Usd1, oi: "19170000", pdoi: "19000000", v: "123456");
        var client = f.BuildClient();

        var rows = client.Depth(Usd1);

        var urls = f.Handler.DepthUrls();
        var url = Assert.Single(urls);
        // single symbol (NOT the comma-joined quotes form) + the OHLCV flag
        Assert.Equal(FyersClient.BaseUrl + "/depth?symbol=" + Usd1 + "&ohlcv_flag=1", url);

        var row = Assert.Single(rows);
        Assert.Equal(Usd1, row.Symbol);
        Assert.Equal(86.4825m, row.Ltp);
        Assert.Equal(19170000L, row.Oi);
        Assert.Equal(19000000L, row.PrevDayOi);
        Assert.Equal(123456m, row.Volume);
        Assert.Equal(86.3100m, row.Atp);
        Assert.Equal(86.2000m, row.Open);
        Assert.Equal(86.5500m, row.High);
        Assert.Equal(86.1000m, row.Low);
        Assert.Equal(86.4825m, row.Close);
        Assert.Equal(86.4800m, row.Bid);
        Assert.Equal(86.4850m, row.Ask);
        Assert.Equal(2m, row.BidSize);
        Assert.Equal(5m, row.AskSize);
        Assert.Equal(0.35m, row.Chp);
        // the client's clock stamps the snapshot minute (03:45Z == 09:15 IST)
        Assert.Equal(TsUtc, row.TsUtc);
        Assert.Equal(IstMinute, row.IstMinute);
    }

    [Fact]
    public void Depth_parses_string_typed_oi_pdoi_and_volume()
    {
        using var f = new Fixture();
        // MIDCPNIFTY answers oi as a NUMBER and the currency leg as a STRING —
        // both must land in the long column.
        f.Handler.Answers[Usd1] = DepthBody(Usd1, oi: """ "19170000" """, pdoi: """ "19000000" """, v: """ "123456" """);
        f.Handler.Answers[Mid1] = DepthBody(Mid1, oi: "105120", pdoi: "104000", v: "98765");
        var client = f.BuildClient();

        var usd = Assert.Single(client.Depth(Usd1));
        Assert.Equal(19170000L, usd.Oi);
        Assert.Equal(19000000L, usd.PrevDayOi);
        Assert.Equal(123456m, usd.Volume);

        var mid = Assert.Single(client.Depth(Mid1));
        Assert.Equal(105120L, mid.Oi);
        Assert.Equal(104000L, mid.PrevDayOi);
    }

    [Fact]
    public void Depth_missing_keys_and_empty_book_yield_nulls_not_throws()
    {
        using var f = new Fixture();
        f.Handler.Answers[Usd1] = """{"s":"ok","code":200,"d":{"NSE:USDINR26SEPFUT":{"ltp":86.4825,"oi":19170000}}}""";
        var client = f.BuildClient();

        var row = Assert.Single(client.Depth(Usd1));

        Assert.Equal(19170000L, row.Oi);
        Assert.Equal(86.4825m, row.Ltp);
        Assert.Null(row.PrevDayOi);
        Assert.Null(row.Volume);
        Assert.Null(row.Atp);
        Assert.Null(row.Open);
        Assert.Null(row.High);
        Assert.Null(row.Low);
        Assert.Null(row.Close);
        Assert.Null(row.Bid);
        Assert.Null(row.Ask);
        Assert.Null(row.BidSize);
        Assert.Null(row.AskSize);
        Assert.Null(row.Chp);
    }

    [Fact]
    public void Depth_with_no_d_or_an_unmatched_key_returns_zero_rows()
    {
        using var f = new Fixture();
        f.Handler.Answers[Usd1] = """{"s":"ok","code":200,"message":"done"}""";          // no d
        f.Handler.Answers[Usd2] = """{"s":"ok","code":200,"d":{}}""";                    // empty d
        f.Handler.Answers[Usd3] = DepthBody("NSE:USDINR26SEPFUT", "1", "1", "1");        // alien key
        var client = f.BuildClient();

        Assert.Empty(client.Depth(Usd1));
        Assert.Empty(client.Depth(Usd2));
        // one symbol per call: a single alien-keyed entry is still THIS symbol's answer
        Assert.Equal(Usd3, Assert.Single(client.Depth(Usd3)).Symbol);
    }

    [Fact]
    public void Depth_throws_when_s_is_not_ok()
    {
        using var f = new Fixture();
        f.Handler.Answers[Usd1] = """{"s":"error","code":405,"message":"unknown symbol"}""";
        var client = f.BuildClient();

        var ex = Assert.Throws<InvalidOperationException>(() => client.Depth(Usd1));
        Assert.Contains("/depth -> error", ex.Message);
        Assert.Contains("unknown symbol", ex.Message);
    }

    // ============================================================ the depth set

    [Fact]
    public void Depth_set_keeps_only_currency_and_midcpnifty_futures()
    {
        var set = OiService.DepthSet(Specs());

        // exactly the 9 contracts, in spec order — no commodity, cash, spot or VIX
        Assert.Equal(Nine, set.Select(s => s.Symbol).ToArray());
        Assert.All(set, s => Assert.Equal("FUTURE", s.Type));
        Assert.All(set, s => Assert.Contains(s.Underlying!, new[] { "USDINR", "EURINR", "MIDCPNIFTY" }));
    }

    [Fact]
    public void Depth_set_is_case_insensitive_and_dedupes_symbols()
    {
        QuoteSpec[] specs =
        [
            new("NSE:USDINR26SEPFUT", "future", "usdinr", Exp1),     // lower-cased both
            new("NSE:USDINR26SEPFUT", "FUTURE", "USDINR", Exp1),     // duplicate symbol
            new("NSE:MIDCPNIFTY26SEPFUT", "FUTURE", "MIDCPNIFTY", Exp1),
            new("NSE:GOLDM26SEPFUT", "FUTURE", "GOLDM", Exp1),       // commodity underlying
            new("NSE:RELIANCE26SEPFUT", "FUTURE", "RELIANCE", Exp1), // equity future
            new("NSE:RELIANCE-EQ", "CASH", "RELIANCE", null),        // cash
            new("NSE:NIFTY-INDEX", "SPOT", "NIFTY", null),           // index
            new("NSE:INDIAVIX", "VIX", null, null),                  // vix
        ];

        var set = OiService.DepthSet(specs);

        Assert.Equal(new[] { "NSE:USDINR26SEPFUT", "NSE:MIDCPNIFTY26SEPFUT" },
            set.Select(s => s.Symbol).ToArray());
    }

    [Fact]
    public void Depth_set_of_null_or_untyped_specs_is_empty()
    {
        Assert.Empty(OiService.DepthSet(null));
        Assert.Empty(OiService.DepthSet([new QuoteSpec("NSE:RELIANCE-EQ", "CASH", "RELIANCE", null)]));
    }

    // ============================================================== OiService

    [Fact]
    public void Writes_enriched_rows_for_the_nine_contract_set_only()
    {
        using var f = new Fixture();
        var svc = f.BuildService();

        var summary = svc.RunMinute(f.Clock.Now, Boundary);

        Assert.NotNull(summary);
        Assert.Equal(9, summary!.Total);
        Assert.Equal(9, summary.Written);
        Assert.Equal(0, summary.Errors);
        Assert.False(summary.AuthExpired);
        Assert.Equal(IstMinute, summary.IstMinute);
        Assert.Equal(Day, summary.Day);

        // 9 depth GETs, one symbol each — no commodity/cash/spot/VIX traffic
        var urls = f.Handler.DepthUrls();
        Assert.Equal(9, urls.Count);
        Assert.All(urls, u => Assert.EndsWith("&ohlcv_flag=1", u));
        Assert.Equal(Nine.OrderBy(s => s, StringComparer.Ordinal),
            urls.Select(SymbolOf).OrderBy(s => s, StringComparer.Ordinal).ToArray());

        using var conn = f.OpenDay();
        var rows = ReadQuotes(conn);
        Assert.Equal(9, rows.Count);
        Assert.Equal(Nine.OrderBy(s => s, StringComparer.Ordinal),
            rows.Select(r => r.Symbol).OrderBy(s => s, StringComparer.Ordinal).ToArray());

        var usd = rows.Single(r => r.Symbol == Usd1);
        Assert.Equal(TsUtc, DateTimeOffset.FromUnixTimeSeconds(usd.TsUtc).UtcDateTime);
        Assert.Equal("2026-09-15 09:15", usd.IstMinute);
        Assert.Equal("FUTURE", usd.InstrumentType);
        Assert.Equal("USDINR", usd.Underlying);
        Assert.Equal(Exp1, usd.ExpiryEpoch);
        Assert.Equal(19170000L, usd.Oi);               // the point of the exercise
        // (pdoi has no quotes column, so it is read and dropped here)
        Assert.Equal(86.4825, usd.Ltp);
        Assert.Equal(86.31, usd.Atp);
        Assert.Equal(86.48, usd.Bid);
        Assert.Equal(86.485, usd.Ask);
        Assert.Equal(2L, usd.BidSize);
        Assert.Equal(5L, usd.AskSize);
        Assert.Equal(123456L, usd.Volume);
        Assert.Equal(0.35, usd.Chp);
        Assert.Equal(86.2, usd.Open);
        Assert.Equal(86.55, usd.High);
        Assert.Equal(86.1, usd.Low);
        Assert.Null(usd.Ch);                            // depth carries chp, not ch

        var mid = rows.Single(r => r.Symbol == Mid1);
        Assert.Equal("MIDCPNIFTY", mid.Underlying);
        Assert.Equal(105120L, mid.Oi);

        Assert.True(f.Log.HasContaining(LogLevel.Information, "oi 2026-09-15 09:15 IST: 9/9 depth rows,"));
    }

    [Fact]
    public void Quotes_only_and_off_minutes_get_no_depth_traffic()
    {
        using var f = new Fixture();
        var svc = f.BuildService();

        // 08:45 IST: inside the quotes envelope, before the chains window
        f.Clock.Now = new DateTime(2026, 9, 15, 8, 45, 2);
        var shoulder = svc.RunMinute(f.Clock.Now, new DateTime(2026, 9, 15, 8, 45, 0));
        Assert.Null(shoulder);

        // 20:00 IST: off session
        f.Clock.Now = new DateTime(2026, 9, 15, 20, 0, 2);
        var off = svc.RunMinute(f.Clock.Now, new DateTime(2026, 9, 15, 20, 0, 0));
        Assert.Null(off);

        Assert.Empty(f.Handler.DepthUrls());
        using var conn = f.OpenDay();
        Assert.Empty(ReadQuotes(conn));
    }

    [Fact]
    public void Skips_the_minute_when_it_wakes_more_than_five_seconds_late()
    {
        using var f = new Fixture();
        var svc = f.BuildService();

        var late = svc.RunMinute(Boundary.AddSeconds(6), Boundary);
        Assert.Null(late);
        Assert.Empty(f.Handler.DepthUrls());

        // exactly 5s late is still inside the guard (mirror CaptureLoopService: > 5)
        f.Clock.Now = IstMinute.AddSeconds(5);
        var onTime = svc.RunMinute(Boundary.AddSeconds(5), Boundary);
        Assert.NotNull(onTime);
        Assert.Equal(9, onTime!.Written);
    }

    [Fact]
    public void Replaces_the_same_symbol_minute_row_instead_of_duplicating_it()
    {
        using var f = new Fixture();
        // the quotes batch already wrote this symbol/minute: prev_close + spread,
        // with bid/ask/oi/atp NULL (what Storage.WriteQuotes stores)
        f.Store.WriteQuotes(Day,
        [
            new QuoteRow(Usd1, "FUTURE", "USDINR", Exp1, Ltp: 86.4000m, Open: 86.2m, High: 86.55m,
                Low: 86.1m, PrevClose: 85.75m, Volume: 100000m, Spread: 0.01m,
                TsUtc: TsUtc, IstMinute: IstMinute, InstrumentType: "FUTURE"),
        ]);

        var svc = f.BuildService(specs: [new QuoteSpec(Usd1, "FUTURE", "USDINR", Exp1)]);
        svc.RunMinute(f.Clock.Now, Boundary);
        svc.RunMinute(f.Clock.Now, Boundary);        // same minute again

        using var conn = f.OpenDay();
        var rows = ReadQuotes(conn);
        var row = Assert.Single(rows);                          // replaced, not duplicated
        Assert.Equal(Usd1, row.Symbol);
        Assert.Equal(19170000L, row.Oi);                        // enriched
        Assert.Equal(86.4825, row.Ltp);
        Assert.Equal(86.31, row.Atp);
        Assert.Equal(86.48, row.Bid);
        Assert.Equal(86.485, row.Ask);
        Assert.Equal(85.75, row.PrevClose);                     // carried over from the batch row
        Assert.Equal(0.01, row.Spread);                         // carried over from the batch row
        Assert.Equal(86.2, row.Open);
        Assert.Equal(86.55, row.High);
        Assert.Equal(86.1, row.Low);
        Assert.Equal("2026-09-15 09:15", row.IstMinute);
    }

    [Fact]
    public void Auth_expired_ends_the_minute_and_writes_nothing()
    {
        using var f = new Fixture();
        f.Handler.Status[Usd1] = HttpStatusCode.Unauthorized;    // dead token
        var svc = f.BuildService();

        var summary = svc.RunMinute(f.Clock.Now, Boundary);

        Assert.NotNull(summary);
        Assert.True(summary!.AuthExpired);
        Assert.Equal(0, summary.Written);
        Assert.Equal(0, summary.Errors);
        // stopped polling after the 401 — the capture loop owns reauth
        Assert.Single(f.Handler.DepthUrls());
        Assert.True(f.Log.HasContaining(LogLevel.Error, "AUTH EXPIRED"));
        using var conn = f.OpenDay();
        Assert.Empty(ReadQuotes(conn));
    }

    [Fact]
    public void Per_symbol_errors_are_counted_and_the_rest_is_still_written()
    {
        using var f = new Fixture();
        f.Handler.Answers[Usd2] = """{"s":"error","code":405,"message":"bad symbol"}""";
        f.Handler.Answers[Eur3] = """{"s":"ok","code":200,"d":{}}""";   // empty answer = a miss
        var svc = f.BuildService();

        var summary = svc.RunMinute(f.Clock.Now, Boundary);

        Assert.NotNull(summary);
        Assert.Equal(9, summary!.Total);
        Assert.Equal(7, summary.Written);
        Assert.Equal(2, summary.Errors);
        Assert.False(summary.AuthExpired);
        Assert.Equal(9, f.Handler.DepthUrls().Count);           // a bad symbol does not stop the pass

        using var conn = f.OpenDay();
        var rows = ReadQuotes(conn);
        Assert.Equal(7, rows.Count);
        Assert.DoesNotContain(rows, r => r.Symbol == Usd2);
        Assert.DoesNotContain(rows, r => r.Symbol == Eur3);
        Assert.True(f.Log.HasContaining(LogLevel.Warning, "oi 2026-09-15 09:15 IST: 2/9 depth poll(s) failed"));
        Assert.True(f.Log.HasContaining(LogLevel.Information, "oi 2026-09-15 09:15 IST: 7/9 depth rows,"));
    }

    [Fact]
    public void Null_or_empty_depth_specs_are_a_quiet_no_op()
    {
        using var f = new Fixture();
        var svc = f.BuildService(specFn: static () => null!);

        Assert.Null(svc.RunMinute(f.Clock.Now, Boundary));
        Assert.Empty(f.Handler.DepthUrls());

        // a Full minute with no matching specs at all is the same quiet skip
        var none = f.BuildService(specs: [new QuoteSpec("NSE:RELIANCE-EQ", "CASH", "RELIANCE", null)]);
        Assert.Null(none.RunMinute(f.Clock.Now, Boundary));
        Assert.Empty(f.Handler.DepthUrls());
    }

    [Fact]
    public void Depth_specs_are_read_fresh_each_minute()
    {
        using var f = new Fixture();
        QuoteSpec[] specs = [new QuoteSpec(Usd1, "FUTURE", "USDINR", Exp1)];
        var svc = f.BuildService(specFn: () => specs);

        svc.RunMinute(f.Clock.Now, Boundary);

        specs = [new QuoteSpec(Eur1, "FUTURE", "EURINR", Exp1)];
        svc.RunMinute(f.Clock.Now, Boundary.AddMinutes(1));

        var urls = f.Handler.DepthUrls();
        Assert.Equal(2, urls.Count);
        Assert.Equal(Usd1, SymbolOf(urls[0]));
        Assert.Equal(Eur1, SymbolOf(urls[1]));
    }
}
