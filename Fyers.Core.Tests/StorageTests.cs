using System.Globalization;
using System.Text.RegularExpressions;
using Fyers.Core.Fyers;
using Fyers.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Fyers.Core.Tests;

// NB (Task 5 agent, compile unblock): the `Storage` TYPE shadows its own namespace name
// (namespace Fyers.Core.Storage + class Storage), so a bare `Storage` field type in
// namespace Fyers.Core.Tests resolves to the NAMESPACE (CS0118). Alias it globally.
using Storage = global::Fyers.Core.Storage.Storage;

/// <summary>
/// Parity tests for the .NET port of the day-file half of src/storage.py.
///
/// The golden schema test pastes the DDL statements VERBATIM from
/// src/storage.py and asserts the <c>sqlite_master</c> text of a db created by
/// <see cref="Storage"/> equals it (whitespace collapsed + the IF NOT EXISTS
/// clause SQLite strips from the stored text), so the two implementations
/// cannot drift apart.
/// </summary>
public class StorageTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 15);

    // 2026-09-15 10:54 IST == 2026-09-15T05:24:00Z == 1789449840
    // (verified against src/market_calendar.py to_utc_epoch)
    private static readonly DateTime IstMinute = new(2026, 9, 15, 10, 54, 0);
    private static readonly DateTime TsUtc = new(2026, 9, 15, 5, 24, 0, DateTimeKind.Utc);
    private const long ExpectedTsUtc = 1789449840L;

    private readonly string _dir;
    private readonly Storage _storage;

    public StorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fyers-storage-tests-" + Guid.NewGuid().ToString("N"));
        _storage = new Storage(_dir, Path.Combine(_dir, "summary.db"));
    }

    public void Dispose()
    {
        _storage.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _storage.DayPath(Day),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static string? NString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ------------------------------------------------------------------
    // paths / lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public void DayPath_IsSnapshotsPrefixWithIsoDate()
    {
        Assert.Equal(
            Path.Combine(_dir, "snapshots-2026-09-15.db"),
            _storage.DayPath(Day));
        Assert.Equal(
            Path.Combine(_dir, "snapshots-2026-01-02.db"),
            _storage.DayPath(new DateOnly(2026, 1, 2)));
    }

    [Fact]
    public void Ctor_CreatesMissingDailyDir()
    {
        var nested = Path.Combine(_dir, "a", "b", "c");
        using var s = new Storage(nested, Path.Combine(_dir, "summary.db"));
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void DayFile_IsCreatedLazily_AndIsWal()
    {
        Assert.False(File.Exists(_storage.DayPath(Day)));   // nothing written yet

        _storage.MarkSnapshot(Day, IstMinute, "full", legs: 0, quotes: 0, ms: 1, errors: 0);

        Assert.True(File.Exists(_storage.DayPath(Day)));

        using var conn = OpenRead();
        // journal_mode=WAL is persistent: a fresh connection must see it
        using var mode = conn.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", Convert.ToString(mode.ExecuteScalar(), CultureInfo.InvariantCulture)?.ToLowerInvariant());
        // (synchronous=NORMAL is per-connection, so it cannot be asserted from
        //  this separate handle; Storage.ApplyWal sets it on every open.)
    }

    // ------------------------------------------------------------------
    // GOLDEN SCHEMA — src/storage.py DAILY_SCHEMA, verbatim
    // ------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> PythonDailyDdl =
        new Dictionary<string, string>
        {
            // ---- pasted from src/storage.py, untouched ----
            ["option_chain"] = """
                CREATE TABLE IF NOT EXISTS option_chain (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc INTEGER NOT NULL,
                    ist_minute TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    underlying TEXT NOT NULL,
                    expiry_epoch INTEGER NOT NULL,
                    strike_price REAL NOT NULL,
                    option_type TEXT NOT NULL,           -- CE / PE
                    ltp REAL, ltpch REAL, ltpchp REAL,
                    bid REAL, ask REAL,
                    oi INTEGER, oich INTEGER, oichp REAL, prev_oi INTEGER, volume INTEGER,
                    iv REAL, delta REAL, gamma REAL, theta REAL, vega REAL
                )
                """,
            ["idx_oc_ts"] = "CREATE INDEX IF NOT EXISTS idx_oc_ts ON option_chain(ts_utc)",
            ["idx_oc_lookup"] = "CREATE INDEX IF NOT EXISTS idx_oc_lookup ON option_chain(underlying, expiry_epoch, ts_utc, strike_price, option_type)",
            ["quotes"] = """
                CREATE TABLE IF NOT EXISTS quotes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc INTEGER NOT NULL,
                    ist_minute TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    instrument_type TEXT NOT NULL,       -- FUTURE / SPOT / VIX / CASH
                    underlying TEXT,
                    expiry_epoch INTEGER,
                    ltp REAL, bid REAL, ask REAL, bid_size INTEGER, ask_size INTEGER,
                    oi INTEGER, volume INTEGER,
                    ch REAL, chp REAL, prev_close REAL, open REAL, high REAL, low REAL,
                    spread REAL, atp REAL
                )
                """,
            ["idx_q_ts"] = "CREATE INDEX IF NOT EXISTS idx_q_ts ON quotes(ts_utc)",
            ["idx_q_sym"] = "CREATE INDEX IF NOT EXISTS idx_q_sym ON quotes(symbol, ts_utc)",
            ["snapshot_runs"] = """
                CREATE TABLE IF NOT EXISTS snapshot_runs (
                    ts_utc INTEGER PRIMARY KEY,
                    ist_minute TEXT NOT NULL,
                    n_legs_options INTEGER,
                    n_quotes INTEGER,
                    fetch_ms INTEGER,
                    errors INTEGER,
                    token_valid INTEGER,
                    note TEXT
                )
                """,
            ["candles_1m"] = """
                CREATE TABLE IF NOT EXISTS candles_1m (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc INTEGER NOT NULL,
                    ist_minute TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    instrument_type TEXT,                -- OPT / FUT / SPOT / VIX / CASH
                    underlying TEXT,
                    expiry_epoch INTEGER,
                    strike_price REAL,
                    option_type TEXT,                    -- CE / PE (OPT only)
                    open REAL, high REAL, low REAL, close REAL,
                    volume INTEGER, oi INTEGER
                )
                """,
            ["idx_candles_sym_ts"] = "CREATE UNIQUE INDEX IF NOT EXISTS idx_candles_sym_ts ON candles_1m(symbol, ts_utc)",
            ["idx_candles_ts"] = "CREATE INDEX IF NOT EXISTS idx_candles_ts ON candles_1m(ts_utc)",
        };

    /// <summary>Collapse whitespace; drop the IF NOT EXISTS clause SQLite does
    /// not store in sqlite_master.</summary>
    private static string NormalizeSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return string.Empty;
        var collapsed = Regex.Replace(sql.Trim(), @"\s+", " ");
        return collapsed
            .Replace("CREATE TABLE IF NOT EXISTS ", "CREATE TABLE ")
            .Replace("CREATE UNIQUE INDEX IF NOT EXISTS ", "CREATE UNIQUE INDEX ")
            .Replace("CREATE INDEX IF NOT EXISTS ", "CREATE INDEX ");
    }

    [Fact]
    public void GoldenSchema_DdlMatchesPythonVerbatim()
    {
        _storage.WriteQuotes(Day, Array.Empty<QuoteRow>());   // lazily create the day db

        var actual = new Dictionary<string, string>();
        using (var conn = OpenRead())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name, type, sql FROM sqlite_master " +
                "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                actual[r.GetString(0)] = NormalizeSql(r.IsDBNull(2) ? null : r.GetString(2));
        }

        var expectedNames = string.Join(",", PythonDailyDdl.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var actualNames = string.Join(",", actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(expectedNames == actualNames,
            $"schema objects differ\nexpected: {expectedNames}\nactual:   {actualNames}");

        foreach (var (name, expectedSql) in PythonDailyDdl.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Assert.True(actual[name] == NormalizeSql(expectedSql),
                $"{name} DDL drifted from src/storage.py\nexpected: {NormalizeSql(expectedSql)}\nactual:   {actual[name]}");
    }

    // ------------------------------------------------------------------
    // quotes
    // ------------------------------------------------------------------

    private static QuoteRow CashQuote() => new(
        Symbol: "NSE:TCS-EQ",
        Type: "CASH",
        Underlying: null,
        ExpiryEpoch: null,
        Ltp: 3891.50m,
        Open: 3880.25m,
        High: 3902.00m,
        Low: 3875.10m,
        PrevClose: 3885.40m,
        Volume: 1250000m,
        Spread: 0.05m,
        TsUtc: TsUtc,
        IstMinute: IstMinute,
        InstrumentType: "CASH");

    private static QuoteRow FutQuote() => new(
        Symbol: "NSE:NIFTY25SEP25FUT",
        Type: "FUTURE",
        Underlying: "NIFTY",
        ExpiryEpoch: 1789449840,
        Ltp: 25412.35m,
        Open: 25300.00m,
        High: 25450.00m,
        Low: 25290.10m,
        PrevClose: 25321.65m,
        Volume: 231400m,
        Spread: 0.20m,
        TsUtc: TsUtc,
        IstMinute: IstMinute,
        InstrumentType: "FUTURE");

    [Fact]
    public void WriteQuotes_Roundtrip_AllColumns()
    {
        var rows = new List<QuoteRow> { CashQuote(), FutQuote() };
        _storage.WriteQuotes(Day, rows);

        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts_utc, ist_minute, symbol, instrument_type, underlying, expiry_epoch,
                   ltp, bid, ask, bid_size, ask_size, oi, volume,
                   ch, chp, prev_close, open, high, low, spread, atp
            FROM quotes ORDER BY id
            """;
        using var r = cmd.ExecuteReader();

        // ---- row 1: cash (underlying/expiry NULL) ----
        Assert.True(r.Read());
        Assert.Equal(ExpectedTsUtc, r.GetInt64(0));
        Assert.Equal("2026-09-15 10:54", r.GetString(1));
        Assert.Equal("NSE:TCS-EQ", r.GetString(2));
        Assert.Equal("CASH", r.GetString(3));
        Assert.True(r.IsDBNull(4), "underlying");
        Assert.True(r.IsDBNull(5), "expiry_epoch");
        Assert.Equal(3891.50, r.GetDouble(6));
        Assert.True(r.IsDBNull(7), "bid");                  // not carried on QuoteRow
        Assert.True(r.IsDBNull(8), "ask");
        Assert.True(r.IsDBNull(9), "bid_size");
        Assert.True(r.IsDBNull(10), "ask_size");
        Assert.True(r.IsDBNull(11), "oi");
        Assert.Equal(1250000L, r.GetInt64(12));
        Assert.True(r.IsDBNull(13), "ch");
        Assert.True(r.IsDBNull(14), "chp");
        Assert.Equal(3885.40, r.GetDouble(15));
        Assert.Equal(3880.25, r.GetDouble(16));
        Assert.Equal(3902.00, r.GetDouble(17));
        Assert.Equal(3875.10, r.GetDouble(18));
        Assert.Equal(0.05, r.GetDouble(19));
        Assert.True(r.IsDBNull(20), "atp");

        // ---- row 2: future (underlying/expiry set) ----
        Assert.True(r.Read());
        Assert.Equal("NSE:NIFTY25SEP25FUT", r.GetString(2));
        Assert.Equal("FUTURE", r.GetString(3));
        Assert.Equal("NIFTY", r.GetString(4));
        Assert.Equal(1789449840L, r.GetInt64(5));
        Assert.Equal(25412.35, r.GetDouble(6));
        Assert.Equal(231400L, r.GetInt64(12));
        Assert.Equal(25321.65, r.GetDouble(15));

        Assert.False(r.Read(), "exactly 2 rows");
    }

    [Fact]
    public void WriteQuotes_Appends_PlainInsertLikePython()
    {
        // Python add_quote_rows is a bare INSERT (no conflict clause): a
        // re-written minute appends a second set of rows. Parity — do NOT
        // silently upgrade to INSERT OR REPLACE.
        _storage.WriteQuotes(Day, new List<QuoteRow> { CashQuote() });
        _storage.WriteQuotes(Day, new List<QuoteRow> { CashQuote() });

        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM quotes";
        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // option_chain legs
    // ------------------------------------------------------------------

    private static LegRow CeLeg() => new(
        Symbol: "NIFTY25S2525500CE",
        Underlying: "NIFTY",
        Strike: 25500.00m,
        OptionType: "CE",
        ExpiryEpoch: 1789449840,
        Oi: 1234567m,
        OiChg: -45678m,
        PrevOi: 1280245m,
        Volume: 890123m,
        Iv: 12.34m,
        Ltp: 211.50m,
        LtpCh: 1.25m,
        LtpChp: 0.59m,
        Bid: 210.35m,
        Ask: 212.80m,
        Delta: 0.42m,
        Gamma: 0.0012m,
        Theta: -8.50m,
        Vega: 3.25m,
        TsUtc: TsUtc,
        IstMinute: IstMinute);

    private static LegRow PeLegSparse() => new(
        Symbol: "NIFTY25S2525500PE",
        Underlying: "NIFTY",
        Strike: 25500.00m,
        OptionType: "PE",
        ExpiryEpoch: 1789449840,
        Oi: 99m,
        OiChg: 3m,
        PrevOi: null,
        Volume: null,
        Iv: null,
        Ltp: null,
        LtpCh: null,
        LtpChp: null,
        Bid: null,
        Ask: null,
        Delta: null,
        Gamma: null,
        Theta: null,
        Vega: null,
        TsUtc: TsUtc,
        IstMinute: IstMinute);

    [Fact]
    public void WriteLegs_Roundtrip_AllColumns()
    {
        _storage.WriteLegs(Day, new List<LegRow> { CeLeg(), PeLegSparse() });

        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts_utc, ist_minute, symbol, underlying, expiry_epoch, strike_price,
                   option_type, ltp, ltpch, ltpchp, bid, ask, oi, oich, oichp, prev_oi,
                   volume, iv, delta, gamma, theta, vega
            FROM option_chain ORDER BY id
            """;
        using var r = cmd.ExecuteReader();

        // ---- row 1: full CE leg ----
        Assert.True(r.Read());
        Assert.Equal(ExpectedTsUtc, r.GetInt64(0));
        Assert.Equal("2026-09-15 10:54", r.GetString(1));
        Assert.Equal("NIFTY25S2525500CE", r.GetString(2));
        Assert.Equal("NIFTY", r.GetString(3));
        Assert.Equal(1789449840L, r.GetInt64(4));
        Assert.Equal(25500.00, r.GetDouble(5));
        Assert.Equal("CE", r.GetString(6));
        Assert.True(r.IsDBNull(7), "ltp");                  // not carried on LegRow
        Assert.True(r.IsDBNull(8), "ltpch");
        Assert.True(r.IsDBNull(9), "ltpchp");
        Assert.Equal(210.35, r.GetDouble(10));
        Assert.Equal(212.80, r.GetDouble(11));
        Assert.Equal(1234567L, r.GetInt64(12));
        Assert.Equal(-45678L, r.GetInt64(13));
        Assert.True(r.IsDBNull(14), "oichp");
        Assert.Equal(1280245L, r.GetInt64(15));
        Assert.Equal(890123L, r.GetInt64(16));
        Assert.Equal(12.34, r.GetDouble(17));
        Assert.Equal(0.42, r.GetDouble(18));
        Assert.Equal(0.0012, r.GetDouble(19));
        Assert.Equal(-8.50, r.GetDouble(20));
        Assert.Equal(3.25, r.GetDouble(21));

        // ---- row 2: sparse PE leg -> optional columns bind NULL ----
        Assert.True(r.Read());
        Assert.Equal("NIFTY25S2525500PE", r.GetString(2));
        Assert.Equal("PE", r.GetString(6));
        Assert.True(r.IsDBNull(10), "bid");
        Assert.True(r.IsDBNull(11), "ask");
        Assert.Equal(99L, r.GetInt64(12));
        Assert.Equal(3L, r.GetInt64(13));
        Assert.True(r.IsDBNull(15), "prev_oi");
        Assert.True(r.IsDBNull(16), "volume");
        Assert.True(r.IsDBNull(17), "iv");
        Assert.True(r.IsDBNull(18), "delta");
        Assert.True(r.IsDBNull(19), "gamma");
        Assert.True(r.IsDBNull(20), "theta");
        Assert.True(r.IsDBNull(21), "vega");

        Assert.False(r.Read(), "exactly 2 rows");
    }

    // ------------------------------------------------------------------
    // snapshot_runs
    // ------------------------------------------------------------------

    [Fact]
    public void MarkSnapshot_IsVisibleInSnapshotRuns()
    {
        _storage.MarkSnapshot(Day, IstMinute, mode: "full", legs: 123, quotes: 562, ms: 845, errors: 0);

        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts_utc, ist_minute, n_legs_options, n_quotes, fetch_ms, errors,
                   token_valid, note
            FROM snapshot_runs
            """;
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(ExpectedTsUtc, r.GetInt64(0));
        Assert.Equal("2026-09-15 10:54", r.GetString(1));
        Assert.Equal(123L, r.GetInt64(2));
        Assert.Equal(562L, r.GetInt64(3));
        Assert.Equal(845L, r.GetInt64(4));
        Assert.Equal(0L, r.GetInt64(5));
        Assert.Equal(1L, r.GetInt64(6));
        Assert.Equal("full", NString(r, 7));
        Assert.False(r.Read());
    }

    [Fact]
    public void MarkSnapshot_WithErrors_NotesPartial_AndReplacesSameMinute()
    {
        // INSERT OR REPLACE on ts_utc (the PRIMARY KEY), like python add_run.
        _storage.MarkSnapshot(Day, IstMinute, mode: "quotes_only", legs: 0, quotes: 562, ms: 400, errors: 0);
        _storage.MarkSnapshot(Day, IstMinute, mode: "quotes_only", legs: 0, quotes: 561, ms: 401, errors: 3);

        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n_quotes, fetch_ms, errors, note FROM snapshot_runs
            """;
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(561L, r.GetInt64(0));
        Assert.Equal(401L, r.GetInt64(1));
        Assert.Equal(3L, r.GetInt64(2));
        Assert.Equal("quotes_only:partial:3", r.GetString(3));
        Assert.False(r.Read(), "one row per minute, replaced");
    }

    // ------------------------------------------------------------------
    // transaction atomicity
    // ------------------------------------------------------------------

    [Fact]
    public void WriteQuotes_BadRow_RollsBackWholeBatch()
    {
        // A row with a null symbol violates quotes.symbol NOT NULL mid-batch;
        // the earlier row of the same transaction must not survive.
        var rows = new List<QuoteRow> { CashQuote() };
        _storage.WriteQuotes(Day, rows);          // 1 row in

        Assert.ThrowsAny<Exception>(() =>
            _storage.WriteQuotes(Day, new List<QuoteRow> { FutQuote(), CashQuote() with { Symbol = null! } }));

        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM quotes";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
    }
}
