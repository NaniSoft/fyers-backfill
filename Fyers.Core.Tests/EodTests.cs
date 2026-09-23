using System.Globalization;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fyers.Core.Tests;

// NB: inside namespace Fyers.Core.Tests a bare `Fyers.Collector...` binds `Fyers`
// to the sibling namespace Fyers.Core.Fyers (CS0234), so qualify globally.
// Both aliases live INSIDE the namespace declaration on purpose: at file scope
// the enclosing Fyers.Core namespace member `Storage` (the NAMESPACE) would win
// over the alias and CS0118 again (same unblock as StorageTests).
using EodService = global::Fyers.Collector.Services.EodService;
using Storage = global::Fyers.Core.Storage.Storage;

/// <summary>
/// Parity tests for the .NET EOD daily-summary rollup
/// (<c>src/eod.py:compute_daily_summary</c> + <c>src/storage.py</c>
/// <c>day_quotes</c> / <c>option_eod_facts</c> / <c>upsert_daily_summary</c>).
///
/// Each fixture is a real day file carrying <c>Ddl.DailySchema</c>, seeded with
/// a few minutes of SPOT/VIX quotes and option legs across TWO expiries at the
/// last captured minute; the expected values below are hand-computed off the
/// Python source (open/close = first/last non-NULL ltp, OI from the last
/// minute's NEAREST expiry only, duplicate strike = value replaced, NULL oi = 0).
/// </summary>
public class EodTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 15);   // Tuesday, trading day
    private const string DateStr = "2026-09-15";

    // session 09:00..16:00 (config.yaml) => expected_minutes_today = 420
    private const int ExpectedMinutes = 420;

    // two "expiries" for the same underlying; only the NEAREST one may feed the
    // EOD aggregates (option_eod_facts' MIN(expiry_epoch) restriction).
    private const long NearExpiry = 1790000000L;
    private const long FarExpiry = 1798000000L;

    private static readonly DateTime M0900 = new(2026, 9, 15, 9, 0, 0);
    private static readonly DateTime M1000 = new(2026, 9, 15, 10, 0, 0);
    private static readonly DateTime M1200 = new(2026, 9, 15, 12, 0, 0);
    private static readonly DateTime M1529 = new(2026, 9, 15, 15, 29, 0);
    private static readonly DateTime M1530 = new(2026, 9, 15, 15, 30, 0);   // last captured minute

    private readonly string _dir;
    private readonly Storage _storage;

    public EodTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fyers-eod-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _storage = new Storage(_dir, Path.Combine(_dir, "summary.db"));
    }

    public void Dispose()
    {
        _storage.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    // ------------------------------------------------------------------
    // fixtures
    // ------------------------------------------------------------------

    private const string TwoIndexYaml = """
        symbols:
          - underlying: NIFTY
            chain_symbol: NSE:NIFTY50-INDEX
            weekly: true
          - underlying: BANKNIFTY
            chain_symbol: NSE:NIFTYBANK-INDEX
            weekly: false
        options:
          strikecount: 50
        futures:
          n_months: 3
        session:
          tz: Asia/Kolkata
          quotes_start: "08:30"
          start: "09:00"
          end: "16:00"
          quotes_end: "16:00"
          premarket_times: ["08:45"]
          eod_time: "15:31"
        """;

    private const string NiftyOnlyYaml = """
        symbols:
          - underlying: NIFTY
            chain_symbol: NSE:NIFTY50-INDEX
            weekly: true
        options:
          strikecount: 50
        futures:
          n_months: 3
        session:
          tz: Asia/Kolkata
          quotes_start: "08:30"
          start: "09:00"
          end: "16:00"
          quotes_end: "16:00"
          premarket_times: ["08:45"]
          eod_time: "15:31"
        """;

    private static AppConfig Config(string yaml) => AppConfig.Parse(yaml);

    private static MarketCalendar Calendar(AppConfig cfg)
        => new(cfg, Day, MarketCalendar.HolidaysFromConfig(cfg));

    /// <summary>The day file with the daily schema, plus the seeded quotes and
    /// option legs shared by the aggregation tests.</summary>
    private void SeedDayDb()
    {
        CreateDayDb();

        // ---- quotes: NIFTY spot OHLC = 25000.25 / 25100.0 / 24950.5 / 25100.0
        Quote(M0900, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25000.25);
        Quote(M1000, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 24950.5);
        Quote(M1200, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", null);     // NULL ltp: skipped
        Quote(M1530, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25100.0);
        // distractors that must NOT feed the SPOT series
        Quote(M0900, "NSE:NIFTY50-INDEX", "FUTURE", "NIFTY", 25050.0);
        Quote(M1000, "RELIANCE", "CASH", "NIFTY", 24960.0);
        // BANKNIFTY spot: a single print
        Quote(M1530, "NSE:NIFTYBANK-INDEX", "SPOT", "BANKNIFTY", 51000.0);
        // an underlying that is NOT in cfg.symbols -> no summary row
        Quote(M1200, "SENSEX", "SPOT", "SENSEX", 80000.0);
        // INDIA VIX carries underlying = NULL and is shared by both indices:
        // OHLC = 13.4 / 14.2 / 13.4 / 13.9
        Quote(M0900, "NSE:INDIAVIX", "VIX", null, 13.4);
        Quote(M1000, "NSE:INDIAVIX", "VIX", null, 14.2);
        Quote(M1200, "NSE:INDIAVIX", "VIX", null, null);              // NULL ltp: skipped
        Quote(M1530, "NSE:INDIAVIX", "VIX", null, 13.9);

        // ---- NIFTY option legs
        // an EARLIER minute: counts toward n_minutes_captured, never toward OI
        Leg(M1529, "NIFTY", NearExpiry, 25100, "CE", 100);
        // the LAST captured minute, nearest expiry
        Leg(M1530, "NIFTY", NearExpiry, 25000, "CE", 1200);
        Leg(M1530, "NIFTY", NearExpiry, 25100, "CE", 5000);           // max CE
        Leg(M1530, "NIFTY", NearExpiry, 25200, "CE", 300);
        Leg(M1530, "NIFTY", NearExpiry, 24900, "PE", 900);
        Leg(M1530, "NIFTY", NearExpiry, 25000, "PE", 8000);           // max PE
        Leg(M1530, "NIFTY", NearExpiry, 25100, "PE", 100);
        // same minute, FAR expiry: illiquid wing that must be EXCLUDED
        Leg(M1530, "NIFTY", FarExpiry, 25000, "CE", 999999);
        Leg(M1530, "NIFTY", FarExpiry, 25000, "PE", 888888);
        // legs of another underlying at the last minute (per-underlying filter)
        Leg(M1530, "BANKNIFTY", FarExpiry, 51000, "CE", 4242);        // its only expiry = nearest
        Leg(M1530, "BANKNIFTY", FarExpiry, 50900, "PE", 3000);
    }

    private string DayPath(DateOnly? day = null) => _storage.DayPath(day ?? Day);

    private void CreateDayDb(DateOnly? day = null)
    {
        using var c = Open(DayPath(day));
        foreach (var statement in Ddl.SplitStatements(Ddl.DailySchema))
            Exec(c, statement);
    }

    private void Quote(DateTime istMinute, string symbol, string type, string? underlying,
        double? ltp, DateOnly? day = null)
    {
        using var c = Open(DayPath(day));
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO quotes (ts_utc, ist_minute, symbol, instrument_type, underlying,
                                expiry_epoch, ltp)
            VALUES (@ts, @minute, @symbol, @type, @underlying, @expiry, @ltp)
            """;
        cmd.Parameters.AddWithValue("@ts", Epoch(istMinute));
        cmd.Parameters.AddWithValue("@minute", Minute(istMinute));
        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@underlying", (object?)underlying ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expiry", DBNull.Value);
        cmd.Parameters.AddWithValue("@ltp", (object?)ltp ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Leg(DateTime istMinute, string underlying, long expiry, double strike,
        string optionType, long? oi, DateOnly? day = null)
    {
        using var c = Open(DayPath(day));
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO option_chain (ts_utc, ist_minute, symbol, underlying, expiry_epoch,
                                      strike_price, option_type, oi)
            VALUES (@ts, @minute, @symbol, @underlying, @expiry, @strike, @type, @oi)
            """;
        cmd.Parameters.AddWithValue("@ts", Epoch(istMinute));
        cmd.Parameters.AddWithValue("@minute", Minute(istMinute));
        cmd.Parameters.AddWithValue("@symbol", $"{underlying}{strike:0}{optionType}");
        cmd.Parameters.AddWithValue("@underlying", underlying);
        cmd.Parameters.AddWithValue("@expiry", expiry);
        cmd.Parameters.AddWithValue("@strike", strike);
        cmd.Parameters.AddWithValue("@type", optionType);
        cmd.Parameters.AddWithValue("@oi", (object?)oi ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // Storage.ToUnixMinute parity: IST wall clock -> epoch minute.
    private static long Epoch(DateTime istMinute) => new DateTimeOffset(
        DateTime.SpecifyKind(istMinute, DateTimeKind.Utc).AddHours(-5).AddMinutes(-30))
        .ToUnixTimeSeconds();

    private static string Minute(DateTime istMinute)
        => istMinute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // result helpers
    // ------------------------------------------------------------------

    /// <summary><paramref name="mustExist"/> is false for the no-run tests,
    /// where summary.db never comes into existence.</summary>
    private SqliteConnection OpenSummary(bool mustExist = true)
    {
        Assert.Equal(!mustExist, !File.Exists(_storage.SummaryDbPath));
        return Open(_storage.SummaryDbPath);
    }

    private static Dictionary<string, object?> SummaryRow(SqliteConnection c, string date, string underlying)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM daily_summary WHERE date=@d AND underlying=@u";
        cmd.Parameters.AddWithValue("@d", date);
        cmd.Parameters.AddWithValue("@u", underlying);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), $"no daily_summary row for {date} / {underlying}");
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < r.FieldCount; i++)
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        return row;
    }

    private static int RowCount(SqliteConnection c, string date)
    {
        if (!TableExists(c, "daily_summary")) return 0;   // no EOD run ever opened it
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM daily_summary WHERE date=@d";
        cmd.Parameters.AddWithValue("@d", date);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static double? D(Dictionary<string, object?> row, string col)
        => row[col] is null ? null : Convert.ToDouble(row[col], CultureInfo.InvariantCulture);

    private static long? L(Dictionary<string, object?> row, string col)
        => row[col] is null ? null : Convert.ToInt64(row[col], CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // aggregation parity
    // ------------------------------------------------------------------

    [Fact]
    public void Compute_WritesNiftyRow_LastMinuteNearestExpiryFacts()
    {
        SeedDayDb();
        var svc = new EodService(Config(TwoIndexYaml), _storage, Calendar(Config(TwoIndexYaml)),
            new ListLogger());
        var written = svc.ComputeDailySummary(Day);

        Assert.Equal(2, written);
        using var summary = OpenSummary();
        var row = SummaryRow(summary, DateStr, "NIFTY");

        Assert.Equal(DateStr, row["date"]);
        Assert.Equal("NIFTY", row["underlying"]);

        // spot OHLC: first/last NON-NULL ltp of the ts-ordered SPOT rows
        // (the NULL-ltp print and the FUTURE/CASH rows are ignored)
        Assert.Equal(25000.25, D(row, "spot_open"));
        Assert.Equal(25100.0, D(row, "spot_high"));
        Assert.Equal(24950.5, D(row, "spot_low"));
        Assert.Equal(25100.0, D(row, "spot_close"));

        // VIX OHLC is shared (underlying = NULL on VIX rows)
        Assert.Equal(13.4, D(row, "vix_open"));
        Assert.Equal(14.2, D(row, "vix_high"));
        Assert.Equal(13.4, D(row, "vix_low"));
        Assert.Equal(13.9, D(row, "vix_close"));

        // last captured minute (15:30) NEAREST expiry only:
        //   CE {25000:1200, 25100:5000, 25200:300} = 6500
        //   PE {24900:900, 25000:8000, 25100:100} = 9000
        Assert.Equal(6500L, L(row, "total_ce_oi"));
        Assert.Equal(9000L, L(row, "total_pe_oi"));
        Assert.Equal(25100L, L(row, "max_oi_strike_ce"));
        Assert.Equal(5000L, L(row, "max_oi_ce"));
        Assert.Equal(25000L, L(row, "max_oi_strike_pe"));
        Assert.Equal(8000L, L(row, "max_oi_pe"));
        Assert.Equal(9000.0 / 6500.0, D(row, "pcr_close")!.Value, 12);

        // distinct option minutes for NIFTY (15:29 + 15:30), not the quote count
        Assert.Equal(2L, L(row, "n_minutes_captured"));
        Assert.Equal((long)ExpectedMinutes, L(row, "n_minutes_expected"));
    }

    [Fact]
    public void Compute_WritesBankniftyRow_IsolatedFromNiftyAndFarOnlyExpiry()
    {
        SeedDayDb();
        var svc = new EodService(Config(TwoIndexYaml), _storage, Calendar(Config(TwoIndexYaml)),
            new ListLogger());
        svc.ComputeDailySummary(Day);

        using var summary = OpenSummary();
        var row = SummaryRow(summary, DateStr, "BANKNIFTY");

        Assert.Equal(51000.0, D(row, "spot_open"));
        Assert.Equal(51000.0, D(row, "spot_high"));
        Assert.Equal(51000.0, D(row, "spot_low"));
        Assert.Equal(51000.0, D(row, "spot_close"));
        Assert.Equal(13.9, D(row, "vix_close"));

        // BANKNIFTY's only expiry at the last minute IS its nearest -> both legs count
        Assert.Equal(4242L, L(row, "total_ce_oi"));
        Assert.Equal(3000L, L(row, "total_pe_oi"));
        Assert.Equal(51000L, L(row, "max_oi_strike_ce"));
        Assert.Equal(50900L, L(row, "max_oi_strike_pe"));
        Assert.Equal(3000.0 / 4242.0, D(row, "pcr_close")!.Value, 12);
        Assert.Equal(1L, L(row, "n_minutes_captured"));
        Assert.Equal((long)ExpectedMinutes, L(row, "n_minutes_expected"));

        // the NIFTY row kept its own aggregates (its FAR-expiry wing of
        // 999999/888888 OI reached neither row), and SENSEX got no row at all.
        var nifty = SummaryRow(summary, DateStr, "NIFTY");
        Assert.Equal(6500L, L(nifty, "total_ce_oi"));
        Assert.Equal(9000L, L(nifty, "total_pe_oi"));
        Assert.Equal(2, RowCount(summary, DateStr));   // SENSEX is not in cfg.symbols
    }

    [Fact]
    public void Compute_DuplicateStrikeReplacesOi_AndTieGoesToFirstInsertedStrike()
    {
        CreateDayDb();
        Quote(M1530, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25000.0);
        Quote(M1530, "NSE:INDIAVIX", "VIX", null, 13.0);
        // id order = dict insertion order
        Leg(M1530, "NIFTY", NearExpiry, 25100, "CE", 500);
        Leg(M1530, "NIFTY", NearExpiry, 25000, "CE", 500);   // tie: first inserted wins
        Leg(M1530, "NIFTY", NearExpiry, 25000, "PE", 10);
        Leg(M1530, "NIFTY", NearExpiry, 25000, "PE", 99);    // duplicate: replaces, not sums

        var svc = new EodService(Config(NiftyOnlyYaml), _storage, Calendar(Config(NiftyOnlyYaml)),
            new ListLogger());
        svc.ComputeDailySummary(Day);

        using var summary = OpenSummary();
        var row = SummaryRow(summary, DateStr, "NIFTY");
        Assert.Equal(1000L, L(row, "total_ce_oi"));          // 500 + 500
        Assert.Equal(25100L, L(row, "max_oi_strike_ce"));    // max(ce_oi, key=...) -> first max
        Assert.Equal(99L, L(row, "total_pe_oi"));            // 99 replaced 10
        Assert.Equal(25000L, L(row, "max_oi_strike_pe"));
        Assert.Equal(99L, L(row, "max_oi_pe"));
        Assert.Equal(99.0 / 1000.0, D(row, "pcr_close")!.Value, 12);
        Assert.Equal(1L, L(row, "n_minutes_captured"));
    }

    [Fact]
    public void Compute_NullOiCountsAsZero_AndPcrIsNullWhenTotalCeIsZero()
    {
        CreateDayDb();
        Quote(M1530, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25000.0);
        Quote(M1530, "NSE:INDIAVIX", "VIX", null, 13.0);
        Leg(M1530, "NIFTY", NearExpiry, 25000, "CE", null);  // python: (oi or 0)
        Leg(M1530, "NIFTY", NearExpiry, 25100, "PE", 5);

        var svc = new EodService(Config(NiftyOnlyYaml), _storage, Calendar(Config(NiftyOnlyYaml)),
            new ListLogger());
        svc.ComputeDailySummary(Day);

        using var summary = OpenSummary();
        var row = SummaryRow(summary, DateStr, "NIFTY");
        Assert.Equal(0L, L(row, "total_ce_oi"));
        Assert.Equal(25000L, L(row, "max_oi_strike_ce"));
        Assert.Equal(0L, L(row, "max_oi_ce"));
        Assert.Equal(5L, L(row, "total_pe_oi"));
        Assert.Null(D(row, "pcr_close"));                    // total_ce == 0 -> pcr None
        Assert.Null(row["pcr_close"]);
    }

    [Fact]
    public void Compute_NoOptionRows_YieldsNullOptionColumnsButRealOhlc()
    {
        CreateDayDb();
        Quote(M0900, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25000.0);
        Quote(M1530, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25100.0);
        Quote(M0900, "NSE:INDIAVIX", "VIX", null, 13.4);

        var svc = new EodService(Config(NiftyOnlyYaml), _storage, Calendar(Config(NiftyOnlyYaml)),
            new ListLogger());
        svc.ComputeDailySummary(Day);

        using var summary = OpenSummary();
        var row = SummaryRow(summary, DateStr, "NIFTY");
        Assert.Equal(25000.0, D(row, "spot_open"));
        Assert.Equal(25100.0, D(row, "spot_close"));
        Assert.Equal(13.4, D(row, "vix_close"));
        Assert.Null(row["pcr_close"]);
        Assert.Null(row["max_oi_strike_ce"]);
        Assert.Null(row["max_oi_strike_pe"]);
        Assert.Null(row["max_oi_ce"]);
        Assert.Null(row["max_oi_pe"]);
        Assert.Equal(0L, L(row, "total_ce_oi"));
        Assert.Equal(0L, L(row, "total_pe_oi"));
        Assert.Equal(0L, L(row, "n_minutes_captured"));
    }

    [Fact]
    public void Compute_EmptyDayFile_NoTables_NoCrash_AllNulls()
    {
        // no day db at all -> the read connection creates an empty file whose
        // missing tables read as empty (python's OperationalError -> [])
        var svc = new EodService(Config(NiftyOnlyYaml), _storage, Calendar(Config(NiftyOnlyYaml)),
            new ListLogger());
        svc.ComputeDailySummary(Day);

        using var summary = OpenSummary();
        var row = SummaryRow(summary, DateStr, "NIFTY");
        Assert.Null(D(row, "spot_close"));
        Assert.Null(D(row, "vix_close"));
        Assert.Null(D(row, "pcr_close"));
        Assert.Equal(0L, L(row, "n_minutes_captured"));
        Assert.Equal((long)ExpectedMinutes, L(row, "n_minutes_expected"));
    }

    [Fact]
    public void Compute_TwiceForSameDay_UpertsOneRowPerUnderlying()
    {
        SeedDayDb();
        var svc = new EodService(Config(TwoIndexYaml), _storage, Calendar(Config(TwoIndexYaml)),
            new ListLogger());
        svc.ComputeDailySummary(Day);
        svc.ComputeDailySummary(Day);

        using var summary = OpenSummary();
        Assert.Equal(2, RowCount(summary, DateStr));
        var row = SummaryRow(summary, DateStr, "NIFTY");
        Assert.Equal(6500L, L(row, "total_ce_oi"));
    }

    [Fact]
    public void ExpectedMinutes_SessionLengthOnTradingDay_ZeroOnHoliday()
    {
        var cfg = Config(TwoIndexYaml);
        var cal = Calendar(cfg);
        var svc = new EodService(cfg, _storage, cal, new ListLogger());

        Assert.Equal(ExpectedMinutes, svc.ExpectedMinutes(Day));   // 09:00..16:00 -> 420
        Assert.Equal(0, svc.ExpectedMinutes(new DateOnly(2026, 10, 2)));  // BUILTIN holiday
        Assert.Equal(0, svc.ExpectedMinutes(new DateOnly(2026, 9, 12)));  // Saturday
    }

    // ------------------------------------------------------------------
    // the hosted loop
    // ------------------------------------------------------------------

    [Fact]
    public async Task Service_RunsOnceAtOrAfterEodTime_OnTradingDay()
    {
        SeedDayDb();
        var cfg = Config(TwoIndexYaml);
        var now = new DateTime(2026, 9, 15, 15, 30, 0);   // 1 minute BEFORE the 15:31 slot
        var logger = new ListLogger();
        var svc = new EodService(cfg, _storage, Calendar(cfg), logger, clock: () => now);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        try
        {
            // advance the clock across 15:31 and on into the evening: the slot
            // fires exactly once, the done-mark blocks a re-run.
            for (var i = 0; i < 20 && logger.Messages.Count(m => m.EndsWith("complete")) == 0; i++)
            {
                now = now.AddMinutes(15);
                await Task.Delay(20);
            }
            now = now.AddHours(3);                        // keep going past the slot
            await Task.Delay(120);

            Assert.Equal(1, logger.Messages.Count(m => m == $"EOD {DateStr} complete"));
            // the python log line, verbatim (src/eod.py:76)
            Assert.Contains(logger.Messages, m => m ==
                "EOD 2026-09-15 NIFTY: spot_close=25100 vix_close=13.9 PCR=1.385 " +
                "maxCE=5000@25100 maxPE=8000@25000 min 2/420");
            Assert.Contains(logger.Messages, m => m ==
                "EOD 2026-09-15: 12 quote rows, expected 420 minutes");
            Assert.Equal(DateStr, svc.LastRunDate?.ToString("yyyy-MM-dd"));

            using var summary = OpenSummary();
            var row = SummaryRow(summary, DateStr, "NIFTY");
            Assert.Equal(6500L, L(row, "total_ce_oi"));
        }
        finally
        {
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Service_DoesNotRunOnHoliday()
    {
        // 2026-10-02 is in MarketCalendar.BuiltinHolidays (a Friday)
        var holidayDate = new DateOnly(2026, 10, 2);
        const string holiday = "2026-10-02";
        var cfg = Config(NiftyOnlyYaml);
        Assert.True(MarketCalendar.HolidaysFromConfig(cfg).Contains(holidayDate),
            "2026-10-02 must be a builtin holiday for this test to mean anything");

        // a fully-populated day file: if the gate leaked, EOD would write a row
        var holidayIst = new DateTime(2026, 10, 2, 15, 30, 0);
        CreateDayDb(holidayDate);
        Quote(holidayIst, "NSE:NIFTY50-INDEX", "SPOT", "NIFTY", 25000.0, holidayDate);
        Quote(holidayIst, "NSE:INDIAVIX", "VIX", null, 13.0, holidayDate);
        Leg(holidayIst, "NIFTY", NearExpiry, 25000, "CE", 10, holidayDate);

        var now = new DateTime(2026, 10, 2, 15, 35, 0);   // past the slot, market shut
        var logger = new ListLogger();
        var svc = new EodService(cfg, _storage, Calendar(cfg), logger, clock: () => now);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        try
        {
            await Task.Delay(400);                        // many poll ticks, still no run
            Assert.DoesNotContain(logger.Messages, m => m.EndsWith("complete"));
            Assert.Null(svc.LastRunDate);
        }
        finally
        {
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);
        }

        using var summary = OpenSummary(mustExist: false);   // no run -> no db
        Assert.Equal(0, RowCount(summary, holiday));
    }

    [Fact]
    public async Task Service_DoesNotRunBeforeEodTime()
    {
        SeedDayDb();
        var cfg = Config(TwoIndexYaml);
        var now = new DateTime(2026, 9, 15, 15, 30, 59);
        var logger = new ListLogger();
        var svc = new EodService(cfg, _storage, Calendar(cfg), logger, clock: () => now);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        try
        {
            await Task.Delay(400);
            Assert.DoesNotContain(logger.Messages, m => m.EndsWith("complete"));
        }
        finally
        {
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);
        }

        using var summary = OpenSummary(mustExist: false);   // no run -> no db
        Assert.Equal(0, RowCount(summary, DateStr));
    }

    // ------------------------------------------------------------------
    // test doubles
    // ------------------------------------------------------------------

    /// <summary>Captures the FORMATTED log lines, so the python-parity text of
    /// the EOD lines is asserted verbatim and runs can be counted.</summary>
    private sealed class ListLogger : ILogger<EodService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
