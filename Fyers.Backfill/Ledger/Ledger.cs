using System.Globalization;
using Fyers.Backfill.Work;
using Microsoft.Data.Sqlite;

namespace Fyers.Backfill.Ledger;

/// <summary>Aggregate progress counters for the <c>status</c> command.</summary>
public sealed record LedgerCounts(long Done, long Failed, long Pending, long Rows);

/// <summary>
/// The resume ResumeLedger — a small SQLite database beside the dataset that records
/// which <c>(symbol, resolution, window)</c> units have been fetched. A run
/// consults it to skip completed work, so the backfill can be killed and resumed
/// at any point (across days, across token re-logins) without refetching or
/// duplicating data. Failed units are recorded with their error and retried on a
/// later run.
/// </summary>
public sealed class ResumeLedger : IDisposable
{
    private readonly SqliteConnection _conn;

    public ResumeLedger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("""
            CREATE TABLE IF NOT EXISTS progress (
                key        TEXT PRIMARY KEY,
                symbol     TEXT NOT NULL,
                resolution TEXT NOT NULL,
                from_date  TEXT NOT NULL,
                to_date    TEXT NOT NULL,
                status     TEXT NOT NULL,
                rows       INTEGER NOT NULL DEFAULT 0,
                requests   INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                error      TEXT
            )
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_progress_symbol ON progress(symbol)");
        Exec("CREATE INDEX IF NOT EXISTS idx_progress_status ON progress(status)");
        Exec("CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT)");
    }

    public bool IsDone(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM progress WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        var status = cmd.ExecuteScalar() as string;
        return status == "done";
    }

    public void MarkDone(WorkItem item, long rows, int requests)
        => Upsert(item, "done", rows, requests, null);

    public void MarkFailed(WorkItem item, int requests, string error)
        => Upsert(item, "failed", 0, requests, Truncate(error, 500));

    private void Upsert(WorkItem item, string status, long rows, int requests, string? error)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO progress (key, symbol, resolution, from_date, to_date, status, rows, requests, updated_at, error)
            VALUES (@key, @symbol, @res, @from, @to, @status, @rows, @req, @at, @err)
            ON CONFLICT(key) DO UPDATE SET
                status = excluded.status,
                rows = excluded.rows,
                requests = excluded.requests,
                updated_at = excluded.updated_at,
                error = excluded.error
            """;
        cmd.Parameters.AddWithValue("@key", item.Key);
        cmd.Parameters.AddWithValue("@symbol", item.Instrument.Symbol);
        cmd.Parameters.AddWithValue("@res", item.Resolution);
        cmd.Parameters.AddWithValue("@from", item.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@to", item.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@rows", rows);
        cmd.Parameters.AddWithValue("@req", requests);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public LedgerCounts Counts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(status = 'done'), 0),
                COALESCE(SUM(status = 'failed'), 0),
                COALESCE(SUM(rows), 0)
            FROM progress
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        var done = r.GetInt64(0);
        var failed = r.GetInt64(1);
        var rows = r.GetInt64(2);
        return new LedgerCounts(done, failed, 0, rows);
    }

    /// <summary>Per-symbol done/failed counts, newest activity first (status command).</summary>
    public IReadOnlyList<(string Symbol, long Done, long Failed, long Rows, string? LastError)>
        SymbolSummary(int limit = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol,
                   COALESCE(SUM(status = 'done'), 0) AS done,
                   COALESCE(SUM(status = 'failed'), 0) AS failed,
                   COALESCE(SUM(rows), 0) AS rows,
                   MAX(CASE WHEN status = 'failed' THEN error END) AS last_error
            FROM progress
            GROUP BY symbol
            ORDER BY MAX(updated_at) DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        var list = new List<(string, long, long, long, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (k, v) VALUES (@k, @v) " +
                          "ON CONFLICT(k) DO UPDATE SET v = excluded.v";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _conn.Dispose();
}
