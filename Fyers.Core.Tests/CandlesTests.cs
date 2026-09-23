using System.Globalization;
using System.Net;
using System.Text;
using Fyers.Collector.Services;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

// Fyers.Collector is referenced for CandlesService, a BackgroundService that needs
// Microsoft.Extensions.Hosting (same shape as Fyers.Token.Tests -> Fyers.Token).
// See the ProjectReference in Fyers.Core.Tests.csproj.

namespace Fyers.Core.Tests;

// NB (same trap as StorageTests.cs): inside namespace Fyers.Core.Tests the bare name
// `Storage` resolves to the NAMESPACE Fyers.Core.Storage (CS0118) — alias the type.
// The alias must sit INSIDE the namespace declaration to win that lookup.
using Storage = global::Fyers.Core.Storage.Storage;

/// <summary>
/// Tests for the today-only candles port: <see cref="FyersClient.History(string, DateOnly, CancellationToken)"/>
/// (endpoint parity) and <see cref="CandlesService"/> (loop, symbol list, budget,
/// candles_1m writes, candles_runs marker).
///
/// All HTTP goes through a fake HttpMessageHandler; the CandlesService tests run a REAL
/// FyersClient over it, so the request URL the service produces is asserted end to end.
/// </summary>
public sealed class CandlesTests
{
    /// <summary>The parity day: Tuesday 2026-09-15, not a builtin holiday.</summary>
    private static readonly DateOnly Day = new(2026, 9, 15);
    private const string DayStr = "2026-09-15";
    private const long Expiry = 1790676600L;

    /// <summary>2026-09-15 03:45Z == 09:15 IST — the day's first captured minute.</summary>
    private static readonly DateTime TsUtc1 = new(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime TsUtc2 = new(2026, 9, 15, 3, 46, 0, DateTimeKind.Utc);

    private const string Index = "NSE:NIFTY-INDEX";
    private const string Fut = "NSE:NIFTY26SEPFUT";
    private const string Ce24800 = "NSE:NIFTY26SEP24800CE";
    private const string Pe24800 = "NSE:NIFTY26SEP24800PE";
    private const string Ce24900 = "NSE:NIFTY26SEP24900CE";

    /// <summary>The two bar epochs: 2026-09-15 03:45Z/03:46Z == 09:15/09:16 IST.</summary>
    private static readonly long Bar1 = new DateTimeOffset(2026, 9, 15, 3, 45, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly long Bar2 = new DateTimeOffset(2026, 9, 15, 3, 46, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>python <c>candles</c> payload: a full 6-tuple, a 7-tuple (oi) and a
    /// truncated 3-tuple (python would IndexError there; the port skips it).</summary>
    private static readonly string BarsPayload = $$"""
        {"s":"ok","code":200,"candles":[
          [{{Bar1}},100.50,101.00,99.75,100.25,1234],
          [{{Bar2}},100.25,102.00,100.00,101.50,0,98765],
          [{{Bar2 + 60}},1.00,1.00,1.00]
        ]}
        """;

    // ---------------------------------------------------------------- fakes

    private sealed record Captured(string Url);

    private sealed class FakeHandler : HttpMessageHandler
    {
        // Snapshot-on-read: the pass under test appends from its own continuation while
        // the test enumerates, so every read must copy under the lock.
        private readonly object _gate = new();
        private readonly List<Captured> _requests = [];

        public IReadOnlyList<Captured> Requests { get { lock (_gate) return [.. _requests]; } }

        public Func<string, HttpResponseMessage> Responder { get; set; } =
            _ => Json("""{"s":"ok","code":200,"candles":[]}""");

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            lock (_gate) { _requests.Add(new Captured(url)); }
            return Responder(url);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Send(request, ct));

        public List<string> HistoryUrls()
            => Requests.Select(r => r.Url).Where(u => u.Contains("/history?", StringComparison.Ordinal)).ToList();

        /// <summary>The decoded <c>key=value</c> pairs of the nth /history request.</summary>
        public List<(string Key, string Value)> Pairs(int historyIndex)
        {
            var url = HistoryUrls()[historyIndex];
            var query = url[(url.IndexOf('?') + 1)..];
            return query.Split('&').Select(p =>
            {
                var eq = p.IndexOf('=');
                return (p[..eq], Uri.UnescapeDataString(p[(eq + 1)..]));
            }).ToList();
        }
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
        public DateTime Now { get; set; } = new(2026, 9, 15, 20, 59, 0);   // just before the 21:00 slot
        public DateTime At(DateOnly day, int hour, int minute) => new(day.Year, day.Month, day.Day, hour, minute, 0);
    }

    // ---------------------------------------------------------------- harness

    /// <summary>Everything one test needs: temp storage, fake http, config, clock.</summary>
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fyers-candles-tests-" + Guid.NewGuid().ToString("N"));
        public Storage Store { get; }
        public FakeHandler Handler { get; } = new();
        public CapturingLogger Log { get; } = new();
        public IstClock Clock { get; } = new();
        public AppConfig Cfg { get; private set; } = null!;

        public Fixture()
        {
            Store = new Storage(Dir, Path.Combine(Dir, "summary.db"));
            Directory.CreateDirectory(Dir);
        }

        public AppConfig BuildCfg(int budgetMin = 120, TimeOnly? slot = null) => new()
        {
            Candles = new CandlesCfg(Enabled: true, slot ?? new TimeOnly(21, 0), (slot ?? new TimeOnly(21, 0)).ToString("HH:mm"),
                LookbackDays: 0, BudgetMin: budgetMin, ProbeRetryMin: 0),
        };

        public FyersClient BuildClient()
            => new(new HttpClient(Handler), () => "TOKEN", new RateLimiter(10_000, 10_000, () => UtcNow),
                Log, appId: "APPID-1", clock: () => UtcNow);

        public CandlesService BuildService(int budgetMin = 120, TimeOnly? slot = null)
        {
            Cfg = BuildCfg(budgetMin, slot);
            return new CandlesService(Cfg, BuildClient(), Store,
                new MarketCalendar(Cfg, Day, MarketCalendar.HolidaysFromConfig(Cfg)),
                new CaptureLogger<CandlesService>(Log), clock: () => Clock.Now);
        }

        /// <summary>2026-09-15 15:30Z == 21:00 IST (the slot).</summary>
        private static DateTime UtcNow => new(2026, 9, 15, 15, 30, 0, DateTimeKind.Utc);

        /// <summary>The default canned history responses: two bars for every symbol,
        /// except one leg that answers no_data and one that answers error.</summary>
        public void AnswerBars()
        {
            Handler.Responder = url => SymbolOf(url) switch
            {
                Pe24800 => Json("""{"s":"no_data","candles":null,"message":""}"""),
                Ce24900 => Json("""{"s":"error","code":405,"message":"unknown symbol"}"""),
                _ => Json(BarsPayload),
            };
        }

        public void SeedCaptures()
        {
            var istMinute = new DateTime(2026, 9, 15, 9, 15, 0);
            Store.WriteQuotes(Day,
            [
                new QuoteRow(Index, "SPOT", null, null, 24800.10m, null, null, null, null, null, null, TsUtc1, istMinute, "SPOT"),
                new QuoteRow(Fut, "FUTURE", "NIFTY", Expiry, 24850.00m, null, null, null, null, null, null, TsUtc1, istMinute, "FUTURE"),
            ]);
            Store.WriteLegs(Day,
            [
                Leg(Ce24800, 24800, "CE"),
                Leg(Pe24800, 24800, "PE"),
                Leg(Ce24900, 24900, "CE"),
            ]);
        }

        private static LegRow Leg(string symbol, decimal strike, string optionType) => new(
            Symbol: symbol, Underlying: "NIFTY", Strike: strike, OptionType: optionType, ExpiryEpoch: Expiry,
            Oi: 1000, OiChg: 10, PrevOi: 990, Volume: 5, Iv: 13m, Ltp: 182m, LtpCh: 1m, LtpChp: 0.5m,
            Bid: 181m, Ask: 183m, Delta: 0.5m, Gamma: 0.001m, Theta: -4m, Vega: 6m, TsUtc: TsUtc1,
            IstMinute: new DateTime(2026, 9, 15, 9, 15, 0));

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

        public SqliteConnection OpenDay(DateOnly? day = null) => Open(Store.DayPath(day ?? Day));

        public SqliteConnection OpenSummary() => Open(Store.SummaryDbPath);

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Dir, recursive: true); }
            catch { /* temp dir cleanup is best-effort */ }
        }
    }

    // =========================================================== History (client)

    [Fact]
    public void History_sends_symbol_resolution_1_and_the_day_range()
    {
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json("""{"s":"ok","code":200,"candles":[]}""");

        client.History("NSE:SBIN-EQ", Day);

        var url = Assert.Single(f.Handler.HistoryUrls());
        Assert.StartsWith(FyersClient.BaseUrl + "/history?", url);
        var pairs = f.Handler.Pairs(0);
        Assert.Equal(("symbol", "NSE:SBIN-EQ"), pairs[0]);       // python's param order
        Assert.Contains(("resolution", "1"), pairs);             // 1-minute
        Assert.Contains(("date_format", "1"), pairs);            // yyyy-MM-dd ranges
        Assert.Contains(("range_from", DayStr), pairs);
        Assert.Contains(("range_to", DayStr), pairs);            // python: history(sym, date_s, date_s)
        Assert.DoesNotContain(pairs, p => p.Key == "oi_flag");
        Assert.DoesNotContain(pairs, p => p.Key == "cont_flag");
    }

    [Fact]
    public void History_flags_add_oi_flag_and_cont_flag()
    {
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json("""{"s":"ok","code":200,"candles":[]}""");

        client.History(Fut, Day, oiFlag: true, contFlag: true);

        var pairs = f.Handler.Pairs(0);
        Assert.Contains(("oi_flag", "1"), pairs);
        Assert.Contains(("cont_flag", "1"), pairs);
        Assert.DoesNotContain(pairs, p => p.Key == "timestamp");
    }

    [Fact]
    public void History_maps_a_candles_payload_to_candle_rows()
    {
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json(BarsPayload);

        var rows = client.History(Ce24800, Day);

        Assert.Equal(2, rows.Count);                             // the truncated 3-tuple is skipped
        var b0 = rows[0];
        Assert.Equal(Ce24800, b0.Symbol);                        // attached by the client
        Assert.Equal(Bar1, b0.TsUtc);                            // c[0], python int(c[0])
        Assert.Equal(100.50m, b0.Open);
        Assert.Equal(101.00m, b0.High);
        Assert.Equal(99.75m, b0.Low);
        Assert.Equal(100.25m, b0.Close);
        Assert.Equal(1234m, b0.Volume);
        // 7-tuple: volume is still c[5]; the 7th (oi) is not carried on CandleRow
        Assert.Equal(Bar2, rows[1].TsUtc);
        Assert.Equal(101.50m, rows[1].Close);
        Assert.Equal(0m, rows[1].Volume);
    }

    [Fact]
    public void History_no_data_is_empty_and_not_an_error()
    {
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json("""{"s":"no_data","candles":null,"message":""}""");

        Assert.Empty(client.History(Pe24800, Day));
    }

    [Fact]
    public void History_ok_body_without_candles_is_empty_too()
    {
        // python: body.get("candles") or [] — the third-state reading of s=ok + no array
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json("""{"s":"ok","code":200,"message":"done"}""");

        Assert.Empty(client.History(Index, Day));
    }

    [Fact]
    public void History_error_body_throws_invalid_operation()
    {
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json("""{"s":"error","code":405,"message":"unknown symbol"}""");

        var ex = Assert.Throws<InvalidOperationException>(() => client.History(Ce24900, Day));
        Assert.Contains("history " + Ce24900, ex.Message);       // parity with python's RuntimeError text
        Assert.Contains("unknown symbol", ex.Message);
    }

    [Fact]
    public void History_token_error_still_raises_auth_expired()
    {
        using var f = new Fixture();
        var client = f.BuildClient();
        f.Handler.Responder = _ => Json("""{"s":"error","code":-15,"message":"token expired"}""");

        Assert.Throws<AuthExpiredException>(() => client.History(Ce24800, Day));
    }

    // =========================================================== CandlesService

    [Fact]
    public async Task RunOnce_fetches_each_captured_symbol_writes_bars_and_marks_the_run()
    {
        using var f = new Fixture();
        f.SeedCaptures();
        f.AnswerBars();
        var svc = f.BuildService();

        var summary = await svc.RunOnceAsync(Day, CancellationToken.None);

        // 5 captured symbols, 5 one-per-symbol history calls
        Assert.Equal(5, summary.Symbols);
        Assert.Equal(5, f.Handler.HistoryUrls().Count);

        // flags follow python's oi_for/cont_for off the day's own metadata
        var urls = f.Handler.HistoryUrls();
        var spot = urls.Single(u => SymbolOf(u) == Index);
        var fut = urls.Single(u => SymbolOf(u) == Fut);
        var leg = urls.Single(u => SymbolOf(u) == Ce24800);
        Assert.DoesNotContain("oi_flag", spot);
        Assert.DoesNotContain("cont_flag", spot);
        Assert.Contains("oi_flag=1", fut);
        Assert.Contains("cont_flag=1", fut);
        Assert.Contains("oi_flag=1", leg);
        Assert.DoesNotContain("cont_flag", leg);
        Assert.Contains("range_from=" + DayStr, leg);
        Assert.Contains("resolution=1", leg);

        // 3 symbols wrote bars (2 each); PE answered no_data (0 bars, not a failure)
        // and 24900CE answered error (counted, run continued)
        Assert.Equal(6, summary.Candles);
        Assert.Equal(1, summary.Errored);
        Assert.Equal(1, summary.Failed);                         // the erroring symbol
        Assert.False(summary.BudgetExpired);
        Assert.True(f.Log.HasContaining(LogLevel.Error,
            $"candles {DayStr}: {Ce24900} failed: history {Ce24900} -> error: unknown symbol"));

        using (var conn = f.OpenDay())
        {
            using var all = conn.CreateCommand();
            all.CommandText = "SELECT COUNT(*) FROM candles_1m";
            Assert.Equal(6L, (long)all.ExecuteScalar()!);

            using var legRow = conn.CreateCommand();
            legRow.CommandText = """
                SELECT ts_utc, ist_minute, instrument_type, underlying, expiry_epoch,
                       strike_price, option_type, open, high, low, close, volume, oi
                FROM candles_1m WHERE symbol=@s ORDER BY ts_utc
                """;
            legRow.Parameters.Add(new SqliteParameter("@s", Ce24800));
            using var r = legRow.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(Bar1, r.GetInt64(0));
            Assert.Equal("2026-09-15 09:15", r.GetString(1));    // IST conversion of the epoch
            Assert.Equal("OPT", r.GetString(2));
            Assert.Equal("NIFTY", r.GetString(3));
            Assert.Equal(Expiry, r.GetInt64(4));
            Assert.Equal(24800.0, r.GetDouble(5));
            Assert.Equal("CE", r.GetString(6));
            Assert.Equal(100.50, r.GetDouble(7));
            Assert.Equal(101.00, r.GetDouble(8));
            Assert.Equal(99.75, r.GetDouble(9));
            Assert.Equal(100.25, r.GetDouble(10));
            Assert.Equal(1234L, r.GetInt64(11));
            Assert.True(r.IsDBNull(12));                         // oi: not carried on CandleRow
            Assert.True(r.Read());
            Assert.Equal("2026-09-15 09:16", r.GetString(1));
            Assert.False(r.Read());

            // the quote-descriptor symbols carry their own instrument metadata
            using var spotRow = conn.CreateCommand();
            spotRow.CommandText = "SELECT instrument_type, underlying, strike_price, option_type FROM candles_1m WHERE symbol=@s";
            spotRow.Parameters.Add(new SqliteParameter("@s", Index));
            using var sr = spotRow.ExecuteReader();
            Assert.True(sr.Read());
            Assert.Equal("SPOT", sr.GetString(0));
            Assert.True(sr.IsDBNull(1));
            Assert.True(sr.IsDBNull(2));
            Assert.True(sr.IsDBNull(3));

            using var futRow = conn.CreateCommand();
            futRow.CommandText = "SELECT instrument_type, underlying, expiry_epoch FROM candles_1m WHERE symbol=@s";
            futRow.Parameters.Add(new SqliteParameter("@s", Fut));
            using var fr = futRow.ExecuteReader();
            Assert.True(fr.Read());
            Assert.Equal("FUTURE", fr.GetString(0));
            Assert.Equal("NIFTY", fr.GetString(1));
            Assert.Equal(Expiry, fr.GetInt64(2));
        }

        // the completion marker — a status record, written with the failed symbol
        AssertMarker(f, Day, nSymbols: 5, nCandles: 6, nFailed: 1);
    }

    [Fact]
    public async Task RunOnce_is_idempotent_and_never_duplicates_bars_or_marker_rows()
    {
        using var f = new Fixture();
        f.SeedCaptures();
        f.AnswerBars();
        var svc = f.BuildService();

        await svc.RunOnceAsync(Day, CancellationToken.None);
        await svc.RunOnceAsync(Day, CancellationToken.None);       // same day again: INSERT OR REPLACE

        using var conn = f.OpenDay();
        using var bars = conn.CreateCommand();
        bars.CommandText = "SELECT COUNT(*), COUNT(DISTINCT symbol) FROM candles_1m";
        using var r = bars.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(6L, r.GetInt64(0));
        Assert.Equal(3L, r.GetInt64(1));
        AssertMarker(f, Day, nSymbols: 5, nCandles: 6, nFailed: 1, expectOneMarkerRow: true);
    }

    [Fact]
    public async Task RunOnce_with_a_zero_budget_stops_immediately_and_marks_the_day_incomplete()
    {
        using var f = new Fixture();
        f.SeedCaptures();
        f.AnswerBars();
        var svc = f.BuildService(budgetMin: 0);

        var summary = await svc.RunOnceAsync(Day, CancellationToken.None);

        Assert.Empty(f.Handler.Requests);                        // not one call left the wire
        Assert.Equal(0, summary.Candles);
        Assert.Equal(0, summary.Errored);
        Assert.Equal(5, summary.Failed);                         // every symbol unfetched
        Assert.True(summary.BudgetExpired);
        Assert.True(f.Log.HasContaining(LogLevel.Warning, $"candles {DayStr}: budget expired with 5 symbols unfetched"));
        Assert.True(f.Log.HasContaining(LogLevel.Information, $"candles {DayStr}: done 0 bars, 5 failed symbols"));

        // complete=0 in the python contract: n_failed != 0
        AssertMarker(f, Day, nSymbols: 5, nCandles: 0, nFailed: 5);
    }

    [Fact]
    public async Task RunOnce_with_no_captured_symbols_logs_skips_and_leaves_the_day_file_absent()
    {
        using var f = new Fixture();
        var svc = f.BuildService();

        var summary = await svc.RunOnceAsync(Day, CancellationToken.None);

        Assert.Equal(0, summary.Symbols);
        Assert.Equal(0, summary.Candles);
        Assert.Empty(f.Handler.Requests);
        Assert.True(f.Log.HasContaining(LogLevel.Warning, $"candles {DayStr}: no captured symbols — skipping"));
        Assert.False(File.Exists(f.Store.DayPath(Day)));         // nothing was created either
        AssertMarker(f, Day, nSymbols: 0, nCandles: 0, nFailed: 0);
    }

    /// <summary>The hosted-service loop: pass at the slot, one per IST date, off days
    /// (weekends/holidays) skipped.</summary>
    [Fact]
    public async Task Service_runs_one_pass_at_the_slot_then_idles_until_the_date_rolls()
    {
        using var f = new Fixture();
        f.SeedCaptures();
        f.AnswerBars();
        var svc = f.BuildService();                              // clock starts 2026-09-15 20:59 IST
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        try
        {
            // 1. the slot: one pass for 2026-09-15
            f.Clock.Now = f.Clock.At(Day, 21, 0);
            await WaitUntil(() => f.Handler.HistoryUrls().Count >= 1);
            await WaitUntil(() => MarkerCount(f, Day) == 1);
            Assert.Equal(6L, BarCount(f, Day));
            Assert.Equal(5, f.Handler.HistoryUrls().Count);      // one call per captured symbol

            // 2. same day, later: never a second pass
            f.Clock.Now = f.Clock.At(Day, 23, 0);
            await Task.Delay(600);
            Assert.Equal(5, f.Handler.HistoryUrls().Count);      // still just the one pass

            // 3. next trading day at the slot: a new pass (no captures -> zero symbols)
            var next = new DateOnly(2026, 9, 16);
            f.Clock.Now = f.Clock.At(next, 21, 5);
            await WaitUntil(() => MarkerCount(f, next) == 1);
            Assert.Equal(5, f.Handler.HistoryUrls().Count);      // no day-2 captures: no new calls
            Assert.Equal(0L, MarkerLong(f, next, "n_symbols"));   // no day-2 captures
            Assert.False(File.Exists(f.Store.DayPath(next)));     // and no empty day file either

            // 4. Saturday (2026-09-19) at the slot: not a trading day, nothing runs
            var saturday = new DateOnly(2026, 9, 19);
            f.Clock.Now = f.Clock.At(saturday, 21, 0);
            await Task.Delay(600);
            Assert.Equal(0, MarkerCount(f, saturday));
            Assert.Equal(5, f.Handler.HistoryUrls().Count);

            // 5. before the slot on a trading day: still nothing
            var monday = new DateOnly(2026, 9, 21);
            f.Clock.Now = f.Clock.At(monday, 12, 0);
            await Task.Delay(600);
            Assert.Equal(0, MarkerCount(f, monday));
        }
        finally
        {
            cts.Cancel();
            try { await svc.StopAsync(CancellationToken.None); }
            catch { /* the loop may already be gone */ }
        }
    }

    // ---------------------------------------------------------------- asserts

    private static long Scalar(Fixture f, string sql, params SqliteParameter[] ps)
    {
        using var conn = f.OpenSummary();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.Add(p);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>0 while the pass is still running (the marker is written last, and a
    /// reader-created empty summary db has no candles_runs table yet).</summary>
    private static long MarkerCount(Fixture f, DateOnly day)
    {
        if (!MarkerTableExists(f)) return 0;
        return Scalar(f, "SELECT COUNT(*) FROM candles_runs WHERE date=@d",
            new SqliteParameter("@d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    private static bool MarkerTableExists(Fixture f)
    {
        using var conn = f.OpenSummary();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='candles_runs'";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) > 0;
    }

    private static long MarkerLong(Fixture f, DateOnly day, string column) => Scalar(f,
        $"SELECT {column} FROM candles_runs WHERE date=@d",
        new SqliteParameter("@d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    private static long BarCount(Fixture f, DateOnly day)
    {
        using var conn = f.OpenDay(day);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM candles_1m";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>The exact <c>candles_runs</c> row the port writes (python parity:
    /// date / n_symbols / n_candles / n_failed / legs_ok / completed_at).</summary>
    private static void AssertMarker(Fixture f, DateOnly day, long nSymbols, long nCandles, long nFailed,
        bool expectOneMarkerRow = false)
    {
        using var conn = f.OpenSummary();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date, n_symbols, n_candles, n_failed, legs_ok, completed_at FROM candles_runs";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(DayStr, r.GetString(0));
        Assert.Equal(nSymbols, r.GetInt64(1));
        Assert.Equal(nCandles, r.GetInt64(2));
        Assert.Equal(nFailed, r.GetInt64(3));
        Assert.True(r.IsDBNull(4));                              // no derivative probe in this port
        var completedAt = r.GetString(5);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+05:30$", completedAt);  // now_ist().isoformat()
        if (!expectOneMarkerRow) return;
        Assert.False(r.Read());                                  // INSERT OR REPLACE, never a second row
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), $"condition not met within {timeoutMs}ms");
    }
}
