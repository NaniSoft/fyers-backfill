using System.Globalization;
using Fyers.Core.Fyers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Fyers.Core.Storage;

/// <summary>
/// SQLite storage — daily-file writer. Port of the day-file half of
/// <c>src/storage.py</c> (the live <c>summary.db</c> tables are deferred to the
/// EOD task).
///
/// Each IST trading day gets its own WAL database,
/// <c>{dailyDir}/snapshots-yyyy-MM-dd.db</c>, holding the per-minute firehose:
/// <c>option_chain</c> + <c>quotes</c> + <c>snapshot_runs</c> (+ <c>candles_1m</c>
/// for the post-close backfill). The schema is applied with IF NOT EXISTS on
/// every open (see <see cref="Ddl.DailySchema"/>), so a file written by the
/// Python collector and one written by this port are interchangeable.
///
/// Parity notes, read off the Python source:
///  * <c>option_chain</c> and <c>quotes</c> are plain <c>INSERT</c> (append-only —
///    a re-captured minute appends a second set of rows, exactly like
///    <c>add_option_rows</c>/<c>add_quote_rows</c>); only <c>snapshot_runs</c> is
///    <c>INSERT OR REPLACE</c> (its <c>ts_utc</c> is the PRIMARY KEY, so
///    re-marking a minute overwrites instead of duplicating).
///  * One transaction per batch: a partial minute rolls back, never
///    half-written.
///  * <c>PRAGMA journal_mode=WAL</c> + <c>PRAGMA synchronous=NORMAL</c> on every
///    open, 30s busy timeout.
///  * Columns absent from <see cref="QuoteRow"/>/<see cref="LegRow"/> (a quote's
///    bid_size/ask_size/oi/ch/chp, a leg's ltp/ltpch/ltpchp/oichp) bind NULL, same as
///    the Python dict's <c>.get()</c> misses. The quote's bid/ask/atp ARE written
///    (QuoteRow carries them since the macro-universe/VWAP work).
/// </summary>
public sealed class Storage : IDisposable
{
    /// <summary>Python <c>ist_minute_str</c>: <c>strftime("%Y-%m-%d %H:%M")</c>.</summary>
    public const string IstMinuteFormat = "yyyy-MM-dd HH:mm";

    private const int IndiaUtcOffsetSeconds = 5 * 3600 + 30 * 60;

    // Column lists and order copied from src/storage.py _opt_row / _quote_row /
    // add_run. Only the VALUES placeholders are named.
    private const string InsertOptionSql = """
        INSERT INTO option_chain
        (ts_utc, ist_minute, symbol, underlying, expiry_epoch, strike_price,
         option_type, ltp, ltpch, ltpchp, bid, ask, oi, oich, oichp, prev_oi,
         volume, iv, delta, gamma, theta, vega)
        VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19,@20,@21)
        """;

    private const string InsertQuoteSql = """
        INSERT INTO quotes
        (ts_utc, ist_minute, symbol, instrument_type, underlying, expiry_epoch,
         ltp, bid, ask, bid_size, ask_size, oi, volume,
         ch, chp, prev_close, open, high, low, spread, atp)
        VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19,@20)
        """;

    private const string InsertRunSql = """
        INSERT OR REPLACE INTO snapshot_runs
        (ts_utc, ist_minute, n_legs_options, n_quotes, fetch_ms, errors,
         token_valid, note) VALUES (@0,@1,@2,@3,@4,@5,@6,@7)
        """;

    private readonly string _dailyDir;
    private readonly string _summaryDbPath;
    private readonly ILogger? _log;
    private bool _disposed;

    public Storage(string dailyDir, string summaryDbPath, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dailyDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryDbPath);
        _dailyDir = dailyDir;
        _summaryDbPath = summaryDbPath;
        _log = log;
        Directory.CreateDirectory(_dailyDir);   // os.makedirs(daily_dir, exist_ok=True)
    }

    /// <summary>Directory the per-day raw databases live in.</summary>
    public string DailyDir => _dailyDir;

    /// <summary>Path of the live summary db (summary tables are applied by the
    /// EOD task, not here — parity with python's deferred summary work).</summary>
    public string SummaryDbPath => _summaryDbPath;

    /// <summary>Parity with <c>Storage.daily_path</c>: snapshots-yyyy-MM-dd.db.</summary>
    public string DayPath(DateOnly d)
        => Path.Combine(_dailyDir, $"snapshots-{d:yyyy-MM-dd}.db");

    /// <summary>
    /// Append one minute's quote rows to the day's <c>quotes</c> table in a
    /// single transaction (python <c>add_quote_rows</c> inside
    /// <c>begin_minute</c>/<c>commit_minute</c>). Creates the day file if absent.
    /// </summary>
    public void WriteQuotes(DateOnly d, IReadOnlyList<QuoteRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var conn = OpenDay(d);
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = InsertQuoteSql;
                for (var i = 0; i < 21; i++)
                    cmd.Parameters.Add(new SqliteParameter($"@{i}", DBNull.Value));

                foreach (var r in rows)
                {
                    var p = cmd.Parameters;
                    p[0].Value = Val(ToUnixMinute(r.TsUtc));
                    p[1].Value = Val(IstMinuteStr(r.IstMinute));
                    p[2].Value = Val(r.Symbol);
                    p[3].Value = Val(r.InstrumentType);
                    p[4].Value = Val(r.Underlying);
                    p[5].Value = Val(r.ExpiryEpoch);
                    p[6].Value = Val((double)r.Ltp);
                    // bid, ask, atp ARE carried on QuoteRow now (VWAP metric);
                    // bid_size, ask_size, oi still are not.
                    p[7].Value = Val(ToDouble(r.Bid));
                    p[8].Value = Val(ToDouble(r.Ask));
                    p[9].Value = DBNull.Value;
                    p[10].Value = DBNull.Value;
                    p[11].Value = DBNull.Value;
                    p[12].Value = Val(ToLong(r.Volume));
                    p[13].Value = DBNull.Value;             // ch
                    p[14].Value = DBNull.Value;             // chp
                    p[15].Value = Val(ToDouble(r.PrevClose));
                    p[16].Value = Val(ToDouble(r.Open));
                    p[17].Value = Val(ToDouble(r.High));
                    p[18].Value = Val(ToDouble(r.Low));
                    p[19].Value = Val(ToDouble(r.Spread));
                    p[20].Value = Val(ToDouble(r.Atp));
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            LogError(ex, "quotes write rolled back for {Date} ({Count} rows)", d, rows.Count);
            throw;
        }
    }

    /// <summary>
    /// Append one minute's option legs to the day's <c>option_chain</c> table in
    /// a single transaction (python <c>add_option_rows</c>). Creates the day
    /// file if absent.
    /// </summary>
    public void WriteLegs(DateOnly d, IReadOnlyList<LegRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var conn = OpenDay(d);
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = InsertOptionSql;
                for (var i = 0; i < 22; i++)
                    cmd.Parameters.Add(new SqliteParameter($"@{i}", DBNull.Value));

                foreach (var r in rows)
                {
                    var p = cmd.Parameters;
                    p[0].Value = Val(ToUnixMinute(r.TsUtc));
                    p[1].Value = Val(IstMinuteStr(r.IstMinute));
                    p[2].Value = Val(r.Symbol);
                    p[3].Value = Val(r.Underlying);
                    p[4].Value = Val(r.ExpiryEpoch);
                    p[5].Value = Val((double)r.Strike);
                    p[6].Value = Val(r.OptionType);
                    p[7].Value = DBNull.Value;              // ltp
                    p[8].Value = DBNull.Value;              // ltpch
                    p[9].Value = DBNull.Value;              // ltpchp
                    p[10].Value = Val(ToDouble(r.Bid));
                    p[11].Value = Val(ToDouble(r.Ask));
                    p[12].Value = Val(ToLong(r.Oi));
                    p[13].Value = Val(ToLong(r.OiChg));
                    p[14].Value = DBNull.Value;             // oichp
                    p[15].Value = Val(ToLong(r.PrevOi));
                    p[16].Value = Val(ToLong(r.Volume));
                    p[17].Value = Val(ToDouble(r.Iv));
                    p[18].Value = Val(ToDouble(r.Delta));
                    p[19].Value = Val(ToDouble(r.Gamma));
                    p[20].Value = Val(ToDouble(r.Theta));
                    p[21].Value = Val(ToDouble(r.Vega));
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            LogError(ex, "option legs write rolled back for {Date} ({Count} rows)", d, rows.Count);
            throw;
        }
    }

    /// <summary>
    /// Record one captured minute in <c>snapshot_runs</c> (python
    /// <c>add_run</c>). INSERT OR REPLACE on <c>ts_utc</c>, so re-marking a
    /// minute overwrites the row. <paramref name="mode"/> becomes the
    /// <c>note</c> column, suffixed <c>:partial:&lt;errors&gt;</c> when errors
    /// were seen — same as <c>src/collector.py:snapshot_once</c>.
    /// <paramref name="istMinute"/> is IST wall-clock (a UTC <c>DateTime</c> is
    /// also accepted and used as-is).
    /// </summary>
    public void MarkSnapshot(DateOnly d, DateTime istMinute, string mode, int legs, int quotes, int ms, int errors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        var note = errors > 0 ? $"{mode}:partial:{errors}" : mode;
        using var conn = OpenDay(d);
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = InsertRunSql;
                cmd.Parameters.Add(new SqliteParameter("@0", Val(ToUnixMinute(istMinute, ist: true))));
                cmd.Parameters.Add(new SqliteParameter("@1", Val(IstMinuteStr(istMinute))));
                cmd.Parameters.Add(new SqliteParameter("@2", Val((long)legs)));
                cmd.Parameters.Add(new SqliteParameter("@3", Val((long)quotes)));
                cmd.Parameters.Add(new SqliteParameter("@4", Val((long)ms)));
                cmd.Parameters.Add(new SqliteParameter("@5", Val((long)errors)));
                cmd.Parameters.Add(new SqliteParameter("@6", 1L));   // token_valid
                cmd.Parameters.Add(new SqliteParameter("@7", Val(note)));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            LogError(ex, "snapshot_runs write rolled back for {Date} {Minute}", d, istMinute);
            throw;
        }
    }

    /// <summary>No pooled or long-lived connection is held: each batch opens,
    /// writes, commits and closes. Nothing to release.</summary>
    public void Dispose() => _disposed = true;

    // ----- connections -----

    /// <summary>
    /// Writable connection to the day's raw db, creating it (and applying
    /// <see cref="Ddl.DailySchema"/>) on first use — python <c>_daily_conn</c>.
    /// </summary>
    private SqliteConnection OpenDay(DateOnly d)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = DayPath(d);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,             // python closes each connection for real
            DefaultTimeout = 30,         // sqlite3.connect(..., timeout=30)
        }.ToString());
        conn.Open();
        try
        {
            ApplyWal(conn);
            foreach (var statement in Ddl.SplitStatements(Ddl.DailySchema))
                Exec(conn, statement);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void ApplyWal(SqliteConnection conn)
    {
        // _apply_wal(): journal_mode=WAL, synchronous=NORMAL
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA synchronous=NORMAL");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ----- value helpers -----

    /// <summary>Python <c>ist_minute_str</c>: "%Y-%m-%d %H:%M".</summary>
    private static string IstMinuteStr(DateTime istMinute) => istMinute.ToString(IstMinuteFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Epoch seconds truncated to the minute (python passes
    /// <c>at_time.replace(second=0, microsecond=0)</c>). A UTC or local
    /// <c>DateTime</c> is converted; an unspecified-kind value is taken as UTC.
    /// </summary>
    private static long ToUnixMinute(DateTime utc, bool ist = false)
    {
        var dto = utc.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(utc, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(utc.ToUniversalTime()),
            // ist: unspecified kind means IST wall clock, i.e. UTC-05:30
            _ => new DateTimeOffset(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                    .AddSeconds(ist ? -IndiaUtcOffsetSeconds : 0), TimeSpan.Zero),
        };
        var epoch = dto.ToUnixTimeSeconds();
        return epoch - (epoch % 60);
    }

    private static long? ToLong(decimal? v) => v.HasValue ? (long)v.Value : null;
    private static long ToLong(decimal v) => (long)v;
    private static double? ToDouble(decimal? v) => v.HasValue ? (double)v.Value : null;
    private static double ToDouble(decimal v) => (double)v;

    /// <summary>Box for binding: null becomes DBNull (python's `.get()` miss).</summary>
    private static object Val(object? v) => v ?? DBNull.Value;

    private void LogError(Exception ex, string message, params object?[] args)
    {
        if (_log is not null)
            _log.LogError(ex, message, args);
    }
}
