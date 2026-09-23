using System.Globalization;
using System.Runtime.CompilerServices;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The parity tests for this service live in the Fyers.Core.Tests assembly
// (the plan pins EOD tests there), so the internal compute entry points below
// are visible to it. Declared here — not in the csproj — to keep this the only
// file this task touches.
[assembly: InternalsVisibleTo("Fyers.Core.Tests")]

namespace Fyers.Collector.Services;

/// <summary>
/// 15:31 IST daily-summary rollup — port of <c>src/eod.py:compute_daily_summary</c>
/// on the src/scheduler.py EOD slot. One <c>daily_summary</c> row per
/// (trading day, underlying) in the live summary db:
/// spot/VIX OHLC from the day's <c>quotes</c> table, PCR + max-OI strikes from
/// the SQL-side last-captured-minute nearest-expiry facts, minutes captured vs
/// expected. No API call.
///
/// PARITY NOTES (read off the Python, 2026-09-16):
///  * <c>src/storage.py:day_quotes</c> — <c>SELECT * FROM quotes WHERE
///    substr(ist_minute,1,10)=? ORDER BY ts_utc, id</c>; open/close are the
///    first/last NON-NULL ltp. VIX rows carry <c>underlying</c> = NULL and are
///    shared by every underlying (the Python filters <c>instrument_type ==
///    'VIX'</c> with no underlying term).
///  * <c>src/storage.py:option_eod_facts</c> — the last minute and the
///    nearest-expiry restriction are computed IN SQL; the day's option rows
///    never leave the db. <c>n_minutes_captured</c> is the DISTINCT-minute
///    count for that underlying (not the quote count).
///  * <c>src/eod.py</c> — a row is upserted even when a side is empty (NULL
///    OHLC / NULL PCR / 0 totals / 0 minutes), and <c>max(ce_oi, key=...)</c>
///    keeps the FIRST strike reaching the max, where a strike repeated inside
///    the last minute keeps its original slot but takes the newer oi (dict
///    assignment) and a NULL oi counts as 0.
///  * <c>src/scheduler.py:354</c> — the slot fires once per day; the done mark
///    is set even when the run throws ("avoid retry storm") and clears when
///    the IST date rolls.
/// </summary>
public sealed class EodService(
    Fyers.Core.Config.AppConfig cfg,
    Fyers.Core.Storage.Storage storage,
    Fyers.Core.Calendar.MarketCalendar cal,
    ILogger<EodService> log,
    Func<DateTime>? clock = null) : BackgroundService
{
    /// <summary>IST wall clock. Mirrors
    /// Fyers.Collector.Capture.SnapshotRunner.IstZone (same value, kept local
    /// so this service does not depend on the capture half). Windows names it
    /// "India Standard Time", everything else "Asia/Kolkata" — IST has no DST,
    /// so both resolve to a fixed +05:30 zone.</summary>
    internal static readonly TimeZoneInfo IstZone =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    /// <summary>How often the loop re-checks the clock (python polls the slot
    /// once a minute; 30s is well inside the 15:31 slot).</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // src/storage.py day_quotes — verbatim text (named parameters instead of ?).
    private const string DayQuotesSql = """
        SELECT * FROM quotes WHERE substr(ist_minute,1,10)=@date ORDER BY ts_utc, id
        """;

    // src/storage.py option_eod_facts — all three statements verbatim.
    private const string LastMinuteSql = """
        SELECT MAX(ts_utc), COUNT(DISTINCT ts_utc)
        FROM option_chain WHERE substr(ist_minute,1,10)=@date
        AND underlying=@underlying
        """;

    private const string NearestExpirySql = """
        SELECT MIN(expiry_epoch) FROM option_chain
        WHERE ts_utc=@ts_utc AND underlying=@underlying
        """;

    private const string LastLegsSql = """
        SELECT * FROM option_chain WHERE ts_utc=@ts_utc AND underlying=@underlying
        AND expiry_epoch=@expiry ORDER BY id
        """;

    // src/storage.py upsert_daily_summary — verbatim text.
    private const string UpsertSummarySql = """
        INSERT OR REPLACE INTO daily_summary
        (date, underlying, spot_open, spot_high, spot_low, spot_close,
         vix_open, vix_high, vix_low, vix_close, pcr_close,
         max_oi_strike_ce, max_oi_strike_pe, max_oi_ce, max_oi_pe,
         total_ce_oi, total_pe_oi, n_minutes_captured, n_minutes_expected)
        VALUES (@date,@underlying,@spot_open,@spot_high,@spot_low,@spot_close,
         @vix_open,@vix_high,@vix_low,@vix_close,@pcr_close,
         @max_oi_strike_ce,@max_oi_strike_pe,@max_oi_ce,@max_oi_pe,
         @total_ce_oi,@total_pe_oi,@n_minutes_captured,@n_minutes_expected)
        """;

    /// <summary>sqlite3.OperationalError the Python swallows: a db without the
    /// table (legacy/pre-extension day file) yields empty results.</summary>
    private const int SqliteErrorNoTable = 1;   // SQLITE_ERROR

    /// <summary>The IST date this instance last computed — the scheduler's
    /// <c>done_eod_date</c> mark (null until the first run).</summary>
    internal DateOnly? LastRunDate { get; private set; }

    /// <summary>IST now — the injected test clock returns IST wall clock.</summary>
    private DateTime Now() => clock is { } f
        ? f()
        : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstZone);

    /// <summary>
    /// Real deployments sleep the poll interval; an injected clock means a test
    /// is driving time, so sleep only a whisker and let the test advance it.
    /// </summary>
    private Task WaitAsync(CancellationToken ct) => clock is null
        ? Task.Delay(PollInterval, ct)
        : Task.Delay(TimeSpan.FromMilliseconds(20), ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("eod service started: slot {Eod} IST, {Underlyings} underlyings",
            cfg.Session.EodTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            cfg.Symbols.Select(s => s.Underlying).Distinct().Count());

        DateOnly? doneDate = null;
        DateOnly? closedLogged = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = Now();
                var today = DateOnly.FromDateTime(now);
                if (doneDate is { } done && done != today)
                    doneDate = null;                      // IST date rolled (scheduler.py:307)

                if (!cal.IsTradingDay(today))
                {
                    if (closedLogged != today)
                    {
                        log.LogInformation(
                            "EOD {Date}: market closed (weekend/holiday) — no summary",
                            Iso(today));
                        closedLogged = today;
                    }
                    await WaitAsync(stoppingToken);
                    continue;
                }

                var slot = today.ToDateTime(cfg.Session.EodTime);
                if (doneDate == today || now < slot)
                {
                    await WaitAsync(stoppingToken);
                    continue;
                }

                // One attempt per day, success or failure — a throwing run must
                // not retry-storm at 30s intervals for the rest of the day.
                try
                {
                    ComputeDailySummary(today);
                }
                catch (Exception e)
                {
                    log.LogError(e, "EOD summary failed: {Message}", e.Message);
                }
                doneDate = today;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // never take the host down (supervised loop, like the capture loop)
                log.LogError("eod loop failed (supervised, continuing): {Message}", e.Message);
                try { await WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        log.LogInformation("eod service stopped");
    }

    // ----- the port of src/eod.py:compute_daily_summary -----

    /// <summary>
    /// Computes and upserts today's <c>daily_summary</c> rows (one per
    /// configured underlying). Returns the number of rows written.
    /// </summary>
    internal int ComputeDailySummary(DateOnly targetDate)
    {
        var dateStr = Iso(targetDate);
        var expected = ExpectedMinutes(targetDate);
        LastRunDate = targetDate;

        // One read connection per run for the day file, one for the summary db
        // (python opens/closes per call; a single conn is the same result).
        using var day = OpenDayDb(storage.DayPath(targetDate));
        var quotes = ReadDayQuotes(day, dateStr);
        log.LogInformation("EOD {Date}: {Count} quote rows, expected {Expected} minutes",
            dateStr, quotes.Count, expected);

        using var summary = OpenSummaryDb(storage.SummaryDbPath);
        var written = 0;
        foreach (var underlying in Underlyings())
        {
            var facts = OptionEodFacts(day, dateStr, underlying);
            var row = BuildRow(dateStr, underlying, quotes, facts, expected);
            UpsertDailySummary(summary, row);
            written++;
            log.LogInformation(
                "EOD {Date} {Underlying}: spot_close={SpotClose} vix_close={VixClose} " +
                "PCR={Pcr} maxCE={MaxCeOi}@{MaxCeStrike} maxPE={MaxPeOi}@{MaxPeStrike} " +
                "min {Captured}/{Expected}",
                dateStr, underlying,
                Fmt(row.SpotClose), Fmt(row.VixClose), FmtPcr(row.PcrClose),
                Fmt(row.MaxOiCe), Fmt(row.MaxOiStrikeCe),
                Fmt(row.MaxOiPe), Fmt(row.MaxOiStrikePe),
                row.NMinutesCaptured, row.NMinutesExpected);
        }
        log.LogInformation("EOD {Date} complete", dateStr);
        return written;
    }

    /// <summary>Python: <c>sorted({s["underlying"] for s in cfg.symbols})</c> —
    /// ordinal (code-point) order, one row per distinct underlying.</summary>
    private IReadOnlyList<string> Underlyings() => cfg.Symbols
        .Select(s => s.Underlying)
        .Distinct()
        .OrderBy(u => u, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Port of <c>MarketCalendar.expected_minutes_today</c>: 0 off-session,
    /// else (session_end - session_start) whole minutes — 09:00..16:00 = 420,
    /// so the last captured minute is 15:59.
    /// </summary>
    internal int ExpectedMinutes(DateOnly d) => cal.IsTradingDay(d)
        ? (int)Math.Floor((cal.End - cal.Start).TotalMinutes)
        : 0;

    /// <summary>The summary row — python's <c>row</c> dict, typed.</summary>
    internal sealed record SummaryRow(
        string Date, string Underlying,
        double? SpotOpen, double? SpotHigh, double? SpotLow, double? SpotClose,
        double? VixOpen, double? VixHigh, double? VixLow, double? VixClose,
        double? PcrClose,
        long? MaxOiStrikeCe, long? MaxOiStrikePe, long? MaxOiCe, long? MaxOiPe,
        long TotalCeOi, long TotalPeOi,
        int NMinutesCaptured, int NMinutesExpected);

    private SummaryRow BuildRow(string dateStr, string underlying,
        IReadOnlyList<DayQuote> quotes, (long? LastTs, List<Leg> Legs, int NMinutes) facts,
        int expected)
    {
        // quotes have an explicit underlying for SPOT/FUTURE; VIX has
        // underlying=NULL and is shared by every index (src/eod.py:29-31).
        var spotLtps = quotes.Where(q => q.InstrumentType == "SPOT" && q.Underlying == underlying)
            .Select(q => q.Ltp).OfType<double>().ToList();
        var vixLtps = quotes.Where(q => q.InstrumentType == "VIX")
            .Select(q => q.Ltp).OfType<double>().ToList();

        // {strike: oi} per option_type over the last captured minute's
        // nearest-expiry legs only (src/eod.py:44-47).
        var ceOi = new OiByStrike();
        var peOi = new OiByStrike();
        foreach (var leg in facts.Legs)
        {
            if (leg.OptionType == "CE") ceOi.Set(leg.Strike, leg.Oi ?? 0);
            else if (leg.OptionType == "PE") peOi.Set(leg.Strike, leg.Oi ?? 0);
        }

        var totalCe = ceOi.Total;
        var totalPe = peOi.Total;
        var maxCeStrike = ceOi.MaxStrike;
        var maxPeStrike = peOi.MaxStrike;
        double? pcr = totalCe != 0 ? (double)totalPe / totalCe : null;

        var spot = Ohlc(spotLtps);
        var vix = Ohlc(vixLtps);
        return new SummaryRow(
            dateStr, underlying,
            spot.Open, spot.High, spot.Low, spot.Close,
            vix.Open, vix.High, vix.Low, vix.Close,
            pcr,
            maxCeStrike, maxPeStrike,
            maxCeStrike is null ? null : ceOi.OiOf(maxCeStrike.Value),
            maxPeStrike is null ? null : peOi.OiOf(maxPeStrike.Value),
            totalCe, totalPe,
            facts.NMinutes, expected);
    }

    /// <summary>first ltp = open, last ltp = close, min/max in between; NULLs
    /// already dropped by the caller (python <c>q["ltp"] is not None</c>).</summary>
    private static (double? Open, double? High, double? Low, double? Close) Ohlc(
        IReadOnlyList<double> ltps)
    {
        if (ltps.Count == 0) return (null, null, null, null);
        double high = ltps[0], low = ltps[0];
        foreach (var v in ltps)
        {
            if (v > high) high = v;
            if (v < low) low = v;
        }
        return (ltps[0], high, low, ltps[^1]);
    }

    /// <summary>
    /// Python dict <c>{strike: oi}</c> semantics: inserting a new strike
    /// appends, re-assigning an existing strike updates its value IN PLACE —
    /// so <c>max(d, key=d.get)</c> resolves ties to the first-inserted strike.
    /// </summary>
    private sealed class OiByStrike
    {
        private readonly Dictionary<long, int> _index = new();
        private readonly List<long> _strikes = [];
        private readonly List<long> _oi = [];

        public void Set(long strike, long oi)
        {
            if (_index.TryGetValue(strike, out var at))
            {
                _oi[at] = oi;
                return;
            }
            _index[strike] = _strikes.Count;
            _strikes.Add(strike);
            _oi.Add(oi);
        }

        public long Total
        {
            get { long t = 0; foreach (var v in _oi) t += v; return t; }
        }

        /// <summary>First strike holding the maximum oi (python max(key=...)),
        /// or null when there are no legs.</summary>
        public long? MaxStrike
        {
            get
            {
                if (_strikes.Count == 0) return null;
                var best = 0;
                for (var i = 1; i < _oi.Count; i++)
                    if (_oi[i] > _oi[best]) best = i;
                return _strikes[best];
            }
        }

        public long OiOf(long strike) => _oi[_index[strike]];
    }

    // ----- connections -----

    /// <summary>
    /// The day file may already be WAL (written live by the collector), so the
    /// read connection uses the default mode — a read-only open cannot create
    /// the -shm/-wal sidecars — plus python's 30s busy timeout.
    /// </summary>
    private static SqliteConnection OpenDayDb(string path) => OpenDb(path);

    /// <summary>Python <c>_summary_conn</c> + <c>_init_summary</c>: WAL and the
    /// summary schema (all CREATE ... IF NOT EXISTS, idempotent — the summary
    /// tables are owned by this task in the port).</summary>
    private static SqliteConnection OpenSummaryDb(string path)
    {
        var conn = OpenDb(path);
        try
        {
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "PRAGMA synchronous=NORMAL");
            foreach (var statement in Ddl.SplitStatements(Ddl.SummarySchema))
                Exec(conn, statement);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenDb(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,   // python _conn_for_day(writable=False)
            Pooling = false,                         // python closes each connection for real
            DefaultTimeout = 30,                     // sqlite3.connect(..., timeout=30)
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ----- src/storage.py day_quotes -----

    private readonly record struct DayQuote(string InstrumentType, string? Underlying, double? Ltp);

    /// <summary>The day's quote rows, ts-ordered. A day file without a
    /// <c>quotes</c> table (legacy) reads as empty — the Python's
    /// <c>except sqlite3.OperationalError: return []</c>.</summary>
    private static List<DayQuote> ReadDayQuotes(SqliteConnection conn, string dateStr)
    {
        var rows = new List<DayQuote>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = DayQuotesSql;
            cmd.Parameters.AddWithValue("@date", dateStr);
            using var r = cmd.ExecuteReader();
            var it = r.GetOrdinal("instrument_type");
            var un = r.GetOrdinal("underlying");
            var ltp = r.GetOrdinal("ltp");
            while (r.Read())
            {
                rows.Add(new DayQuote(
                    r.GetString(it),
                    r.IsDBNull(un) ? null : r.GetString(un),
                    r.IsDBNull(ltp) ? null : r.GetDouble(ltp)));
            }
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteErrorNoTable)
        {
            return [];
        }
        return rows;
    }

    // ----- src/storage.py option_eod_facts -----

    private readonly record struct Leg(long Strike, string OptionType, long? Oi);

    /// <summary>
    /// (last_ts, legs, n_minutes) for one underlying: the last captured
    /// minute's nearest-expiry legs (one minute — small) plus the
    /// distinct-minute count. (null, [], 0) when the day has no option rows.
    /// </summary>
    private static (long? LastTs, List<Leg> Legs, int NMinutes) OptionEodFacts(
        SqliteConnection conn, string dateStr, string underlying)
    {
        long? lastTs;
        int n;
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = LastMinuteSql;
                cmd.Parameters.AddWithValue("@date", dateStr);
                cmd.Parameters.AddWithValue("@underlying", underlying);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return (null, [], 0);
                lastTs = r.IsDBNull(0) ? null : r.GetInt64(0);
                n = r.GetInt64(1) > int.MaxValue ? int.MaxValue : (int)r.GetInt64(1);
            }
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteErrorNoTable)
        {
            return (null, [], 0);   // legacy db: no option_chain
        }
        if (lastTs is null) return (null, [], 0);

        long near;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = NearestExpirySql;
            cmd.Parameters.AddWithValue("@ts_utc", lastTs.Value);
            cmd.Parameters.AddWithValue("@underlying", underlying);
            using var r = cmd.ExecuteReader();
            near = r.Read() && !r.IsDBNull(0) ? r.GetInt64(0) : 0;
        }

        var legs = new List<Leg>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = LastLegsSql;
            cmd.Parameters.AddWithValue("@ts_utc", lastTs.Value);
            cmd.Parameters.AddWithValue("@underlying", underlying);
            cmd.Parameters.AddWithValue("@expiry", near);
            using var r = cmd.ExecuteReader();
            var strike = r.GetOrdinal("strike_price");
            var type = r.GetOrdinal("option_type");
            var oi = r.GetOrdinal("oi");
            while (r.Read())
            {
                // strike_price is REAL; NSE strikes are whole numbers, so the
                // long cast matches SQLite's INTEGER-affinity storage of the
                // same value into max_oi_strike_ce.
                legs.Add(new Leg(
                    (long)r.GetDouble(strike),
                    r.GetString(type),
                    r.IsDBNull(oi) ? null : r.GetInt64(oi)));
            }
        }
        return (lastTs, legs, n);
    }

    // ----- src/storage.py upsert_daily_summary -----

    private static void UpsertDailySummary(SqliteConnection conn, SummaryRow row)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = UpsertSummarySql;
        cmd.Parameters.AddWithValue("@date", row.Date);
        cmd.Parameters.AddWithValue("@underlying", row.Underlying);
        cmd.Parameters.AddWithValue("@spot_open", Val(row.SpotOpen));
        cmd.Parameters.AddWithValue("@spot_high", Val(row.SpotHigh));
        cmd.Parameters.AddWithValue("@spot_low", Val(row.SpotLow));
        cmd.Parameters.AddWithValue("@spot_close", Val(row.SpotClose));
        cmd.Parameters.AddWithValue("@vix_open", Val(row.VixOpen));
        cmd.Parameters.AddWithValue("@vix_high", Val(row.VixHigh));
        cmd.Parameters.AddWithValue("@vix_low", Val(row.VixLow));
        cmd.Parameters.AddWithValue("@vix_close", Val(row.VixClose));
        cmd.Parameters.AddWithValue("@pcr_close", Val(row.PcrClose));
        cmd.Parameters.AddWithValue("@max_oi_strike_ce", Val(row.MaxOiStrikeCe));
        cmd.Parameters.AddWithValue("@max_oi_strike_pe", Val(row.MaxOiStrikePe));
        cmd.Parameters.AddWithValue("@max_oi_ce", Val(row.MaxOiCe));
        cmd.Parameters.AddWithValue("@max_oi_pe", Val(row.MaxOiPe));
        cmd.Parameters.AddWithValue("@total_ce_oi", row.TotalCeOi);
        cmd.Parameters.AddWithValue("@total_pe_oi", row.TotalPeOi);
        cmd.Parameters.AddWithValue("@n_minutes_captured", row.NMinutesCaptured);
        cmd.Parameters.AddWithValue("@n_minutes_expected", row.NMinutesExpected);
        cmd.ExecuteNonQuery();
    }

    private static object Val(object? v) => v ?? DBNull.Value;

    // ----- log formatting (python %-formats) -----

    /// <summary>Python <c>%s</c> of a number/None: invariant digits, "None".</summary>
    private static string Fmt(object? v) => v switch
    {
        null => "None",
        double d => d.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "None",
    };

    /// <summary>Python <c>%.3f</c> of <c>pcr if pcr is not None else 0</c>.</summary>
    private static string FmtPcr(double? pcr)
        => (pcr ?? 0).ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Python <c>date_str</c> / the <c>substr(ist_minute,1,10)</c> key.</summary>
    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
