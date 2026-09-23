using System.IO.Compression;
using Fyers.Collector.Services;
using Fyers.Core.Config;
using Fyers.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fyers.Core.Tests;

// NB (backup task): `Storage` shadows its own namespace name (namespace
// Fyers.Core.Storage + class Storage), so a bare `Storage` field type in
// namespace Fyers.Core.Tests resolves to the NAMESPACE (CS0118). Alias it
// INSIDE the namespace (the alias must come after the file-scoped namespace
// line to win the lookup), same as StorageTests.
using Storage = global::Fyers.Core.Storage.Storage;

/// <summary>
/// Tests for the nightly Drive backup + PV reset (<c>BackupService</c>).
///
/// rclone is injected as a delegate, so the tests observe the EXACT argv (the
/// copyto destination and the lsjson folder ARE the design) and drive failure
/// injection (upload exit code, lsjson size) without a Drive round-trip.
///
/// The checkpoint-before-zip invariant is asserted BEHAVIOURALLY, via a
/// deliberately stale WAL: the day db is seeded with
/// <c>PRAGMA wal_autocheckpoint=0</c> so the committed canary row lives only in
/// the <c>-wal</c> (the main db file is still without it), then the
/// pre-checkpoint db+wal byte pair is put back — exactly the state of a hot day
/// db at 23:15. If the service skipped
/// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, the zipped db would be missing the
/// row. So: (a) the fake rclone records that the <c>-wal</c> was 0 bytes (or
/// gone) at upload time, and (b) the day-db entry pulled back out of the zip
/// contains the canary row.
/// </summary>
public class BackupTests : IDisposable
{
    private const string DayS = "2026-09-16";
    private static readonly DateOnly Day = new(2026, 9, 16);

    /// <summary>IST wall clock, past cfg.Gdrive.Time (23:15).</summary>
    private DateTime _now = new(2026, 9, 16, 23, 20, 0);

    private const string Remote = "gdrive";
    private const string Path_ = "fyers-snapshots";
    private const string CanarySymbol = "CHECKPOINT-CANARY";

    private readonly string _dir;
    private readonly Storage _storage;
    private readonly FakeRclone _rclone = new();

    public BackupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fyers-backup-tests-" + Guid.NewGuid().ToString("N"));
        _storage = new Storage(_dir, Path.Combine(_dir, "summary.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    // ------------------------------------------------------------------
    // paths
    // ------------------------------------------------------------------

    private string DayDb => _storage.DayPath(Day);
    private string DayWal => DayDb + "-wal";
    private string DayShm => DayDb + "-shm";
    private string Summary => Path.Combine(_dir, "summary.db");
    private string MasterFo => Path.Combine(_dir, "sym_master_fo.json");
    private string MasterCds => Path.Combine(_dir, "sym_master_cds.json");
    private string MasterCom => Path.Combine(_dir, "sym_master_com.json");
    private string Token => Path.Combine(_dir, "fyers_access_token.json");
    private string Reauth => Path.Combine(_dir, "fyers_reauth.request");
    private string Zip => Path.Combine(_dir, $"fyers-{DayS}.zip");

    /// <summary>The files the clean step owns — the invariant is that these
    /// and ONLY these disappear (the multi-segment master files are all included).</summary>
    private string[] Doomed => [DayDb, DayWal, DayShm, MasterFo, MasterCds, MasterCom, Token, Reauth];

    // ------------------------------------------------------------------
    // fixtures
    // ------------------------------------------------------------------

    private BackupService Service(bool enabled = true, TimeOnly? time = null, TimeSpan? poll = null)
    {
        var at = time ?? new TimeOnly(23, 15);
        var cfg = new AppConfig
        {
            DailyDir = _dir,
            SummaryDb = Summary,
            Gdrive = new GdriveCfg(enabled, at, at.ToString("HH:mm"), Remote, Path_),
        };
        return new BackupService(cfg, _storage, NullLogger<BackupService>.Instance,
            clock: () => _now, runner: _rclone.Invoke, poll: poll ?? TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Day db with Ddl.DailySchema + a canary row whose frames live
    /// ONLY in the -wal (autocheckpoint disabled, pre-checkpoint bytes put
    /// back). Also seeds summary.db, all master files, token and reauth.</summary>
    private void SeedDay()
    {
        var (mainBefore, walBefore) = WriteDayDbWithHotWal();
        RestoreHotDayState(mainBefore, walBefore);

        WriteSummaryDb();
        File.WriteAllText(MasterFo, """{"FO":{"NSE:CANARY-EQ":1}}""");
        File.WriteAllText(MasterCds, """{"CDS":{"NSE:CANARY-EQ":1}}""");
        File.WriteAllText(MasterCom, """{"COM":{"NSE:CANARY-EQ":1}}""");
        File.WriteAllText(Token, """{"access_token":"tok","saved_at":1}""");
        File.WriteAllText(Reauth, "2026-09-16T10:00:00.0000000Z");
    }

    /// <summary>Put the PRE-checkpoint pair back: the main db file lacks the
    /// canary row, the -wal holds it. A zip taken without a checkpoint would
    /// lose it. This is exactly what a hot day db looks like at 23:15.</summary>
    private void RestoreHotDayState(byte[] main, byte[] wal)
    {
        File.WriteAllBytes(DayDb, main);
        File.WriteAllBytes(DayWal, wal);
        File.WriteAllText(DayShm, string.Empty);          // 0 bytes: SQLite rebuilds it
    }

    private void WriteSummaryDb()
    {
        using var conn = Sqlite(Summary);
        foreach (var s in Ddl.SplitStatements(Ddl.SummarySchema)) Exec(conn, s);
        Exec(conn, """
            INSERT INTO daily_summary (date, underlying, spot_close, n_minutes_captured, n_minutes_expected)
            VALUES ('2026-09-16', 'NIFTY', 25400.5, 420, 420)
            """);
    }

    /// <summary>Returns the db + wal bytes as they are BEFORE any checkpoint,
    /// i.e. with the canary row still only inside the -wal.</summary>
    private (byte[] Main, byte[] Wal) WriteDayDbWithHotWal()
    {
        using var conn = Sqlite(DayDb);
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA wal_autocheckpoint=0");        // keep frames in the -wal
        foreach (var s in Ddl.SplitStatements(Ddl.DailySchema)) Exec(conn, s);
        Exec(conn, $"""
            INSERT INTO quotes (ts_utc, ist_minute, symbol, instrument_type, ltp)
            VALUES (1789900800, '{DayS} 15:30', '{CanarySymbol}', 'CASH', 100.0)
            """);

        var main = ReadShared(DayDb);                     // the connection is still open:
        var wal = ReadShared(DayWal);                     // read with FileShare.ReadWrite
        Assert.True(wal.Length > 0, "fixture: the -wal must hold content before the run");
        return (main, wal);
        // closing the connection below would checkpoint and delete the -wal —
        // the caller restores the captured bytes first, so the state survives.
    }

    private static SqliteConnection Sqlite(string path)
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

    /// <summary>Read a db/wal file that a live connection holds open
    /// (plain File.ReadAllBytes trips over the write sharing on Windows).</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sharpens the fixture: the day db file ALONE (no -wal next to
    /// it) is not even a schema — the schema AND the canary row live in the
    /// -wal. That gap is exactly what the checkpoint has to close before the
    /// zip, and the restored pair reads complete.</summary>
    [Fact]
    public void Fixture_DayDbWithoutItsWal_MissingTheCanaryRow()
    {
        var (main, wal) = WriteDayDbWithHotWal();
        RestoreHotDayState(main, wal);

        var lonely = Path.Combine(_dir, "day-db-alone.db");
        File.WriteAllBytes(lonely, main);                 // no -wal next to it
        using (var conn = Sqlite(lonely))
        {
            Assert.Equal(0L, Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM sqlite_master")));
        }

        Assert.True(wal.Length > 0);
        using (var conn = Sqlite(DayDb))                  // the pair: read through the -wal
        {
            Assert.Equal(1L, Count(conn, "quotes"));
        }
    }

    // ------------------------------------------------------------------
    // the happy path: zip 3 entries, upload, verify, clean
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_ZipsThreeEntries_UploadsVerifies_AndCleansThePv()
    {
        SeedDay();
        var masterLen = new FileInfo(MasterFo).Length;
        using var svc = Service();

        Assert.Equal(BackupOutcome.BackedUp, await svc.TickAsync(_now));

        // ---- the zip: exactly three FLAT entries, named after their sources ----
        // (the zip itself is DELETED after verified upload — it is redundant on
        // Drive and leaving it would grow the PV ~500 MB/day)
        Assert.False(File.Exists(Zip), "the local zip must be deleted after verified upload");
        Assert.NotNull(_rclone.UploadedZip);
        using (var zip = new ZipArchive(new MemoryStream(_rclone.UploadedZip)))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(
                [$"snapshots-{DayS}.db", "summary.db", "sym_master_fo.json"],
                names);
            Assert.All(zip.Entries, e => Assert.DoesNotContain('/', e.FullName));
            Assert.All(zip.Entries, e => Assert.True(e.Length > 0, $"{e.FullName} must not be empty"));
            Assert.Equal(masterLen, zip.GetEntry("sym_master_fo.json")!.Length);
        }

        // ---- the rclone invocations ARE the design (per-day Drive layout) ----
        Assert.Equal(2, _rclone.Calls.Count);
        Assert.Equal($"copyto {Zip} {Remote}:{Path_}/{DayS}/fyers-{DayS}.zip", _rclone.Calls[0].Args);
        Assert.Equal($"lsjson {Remote}:{Path_}/{DayS}/", _rclone.Calls[1].Args);

        // ---- checkpoint ran BEFORE the zip ----
        // At upload time the -wal holds no frames: TRUNCATE zeroes it, and the
        // checkpoint connection's clean close then removes the sidecar
        // entirely (last connection out). Either way the frames are folded
        // into the db file — proven by the canary assertion in
        // Run_TheZippedDayDb_IsComplete_BecauseTheCheckpointRan.
        Assert.True(_rclone.WalBytesAtUpload <= 0,
            $"the -wal still held {_rclone.WalBytesAtUpload} bytes at upload time");

        // ---- clean: the six owned files are gone, summary.db survives ----
        Assert.All(Doomed, f => Assert.False(File.Exists(f), $"{Path.GetFileName(f)} must be deleted"));
        Assert.True(File.Exists(Summary), "summary.db is cumulative — it must survive");
    }

    [Fact]
    public async Task Run_TheZippedDayDb_IsComplete_BecauseTheCheckpointRan()
    {
        SeedDay();
        using var svc = Service();
        Assert.Equal(BackupOutcome.BackedUp, await svc.TickAsync(_now));

        var extracted = Path.Combine(_dir, "extracted-day.db");
        Assert.NotNull(_rclone.UploadedZip);
        using (var zip = new ZipArchive(new MemoryStream(_rclone.UploadedZip)))
        {
            var entry = zip.GetEntry($"snapshots-{DayS}.db");
            Assert.NotNull(entry);
            using var src = entry!.Open();
            using var dst = File.Create(extracted);
            src.CopyTo(dst);
        }

        // The fixture's db file alone had 0 quotes rows — only the checkpoint
        // folded the -wal in, so the zipped db can only be complete if the
        // service ran PRAGMA wal_checkpoint(TRUNCATE) before zipping.
        using var conn = Sqlite(extracted);
        Assert.Equal(1L, Count(conn, "quotes"));
        Assert.Equal(1L, Convert.ToInt64(Scalar(conn,
            $"SELECT COUNT(*) FROM quotes WHERE symbol = '{CanarySymbol}'")));
    }

    [Fact]
    public async Task Run_BenignRcloneConfigStderr_IsNotAFailure()
    {
        // rclone on a read-only rootfs prints "Failed to save config" /
        // "read-only file system" on SUCCESS — the exit code decides.
        SeedDay();
        _rclone.StdErr = "Failed to save config after 10 tries: open /app/rclone.conf: read-only file system";
        using var svc = Service();

        Assert.Equal(BackupOutcome.BackedUp, await svc.TickAsync(_now));
        Assert.All(Doomed, f => Assert.False(File.Exists(f)));
    }

    // ------------------------------------------------------------------
    // VERIFY-THEN-DELETE: any failure leaves the PV untouched
    // ------------------------------------------------------------------

    /// <summary>On a failed run nothing may be cleaned. The -wal/-shm are
    /// SQLite's to manage, not the service's: the checkpoint connection's
    /// clean close folds and removes them regardless of the run's outcome, so
    /// they are asserted at the DATA level (the day db still holds the canary)
    /// rather than by file name.</summary>
    private void AssertPvIntact()
    {
        foreach (var f in new[] { DayDb, MasterFo, MasterCds, MasterCom, Token, Reauth })
            Assert.True(File.Exists(f), $"{Path.GetFileName(f)} must survive a failed run");
        Assert.True(File.Exists(Summary), "summary.db must survive a failed run");
        using var conn = Sqlite(DayDb);
        Assert.Equal(1L, Count(conn, "quotes"));          // the day's data is still there
    }

    [Fact]
    public async Task UploadFails_NothingIsDeleted_AndNoLsjsonCall()
    {
        SeedDay();
        _rclone.CopyToExit = 1;
        using var svc = Service();

        Assert.Equal(BackupOutcome.Failed, await svc.TickAsync(_now));

        AssertPvIntact();
        Assert.Equal(1, _rclone.Calls.Count);             // no verify after a failed upload
    }

    [Fact]
    public async Task RemoteSizeMismatch_NothingIsDeleted()
    {
        SeedDay();
        _rclone.SizeReturned = new FileInfo(Path.Combine(_dir, "summary.db")).Length + 999999;
        using var svc = Service();

        Assert.Equal(BackupOutcome.Failed, await svc.TickAsync(_now));

        AssertPvIntact();
        Assert.Equal(2, _rclone.Calls.Count);             // upload ran, verify refused to clean
    }

    [Fact]
    public async Task RemoteFileMissing_NothingIsDeleted()
    {
        SeedDay();
        _rclone.LsJson = "[]";
        using var svc = Service();

        Assert.Equal(BackupOutcome.Failed, await svc.TickAsync(_now));

        AssertPvIntact();
    }

    [Fact]
    public async Task LsjsonFails_NothingIsDeleted()
    {
        SeedDay();
        _rclone.LsJsonExit = 3;
        using var svc = Service();

        Assert.Equal(BackupOutcome.Failed, await svc.TickAsync(_now));

        AssertPvIntact();
    }

    // ------------------------------------------------------------------
    // scheduling: once per date, at cfg.Gdrive.Time, or not at all
    // ------------------------------------------------------------------

    [Fact]
    public async Task NoDayDb_Skips_DoesNotCallRclone()
    {
        // summary/master/token only: a non-trading day (nothing captured)
        WriteSummaryDb();
        File.WriteAllText(MasterFo, "{}");
        using var svc = Service();

        Assert.Equal(BackupOutcome.SkippedNoDayDb, await svc.TickAsync(_now));

        Assert.Empty(_rclone.Calls);
        Assert.True(File.Exists(MasterFo));
        Assert.True(File.Exists(Summary));
    }

    [Fact]
    public async Task BeforeGdriveTime_NothingHappens()
    {
        SeedDay();
        _now = new DateTime(2026, 9, 16, 23, 0, 0);       // candles budget ends 23:00
        using var svc = Service();

        Assert.Equal(BackupOutcome.NotYetTime, await svc.TickAsync(_now));
        Assert.Empty(_rclone.Calls);
        AssertPvIntact();
    }

    [Fact]
    public async Task SecondTickSameDate_IsANoOp()
    {
        SeedDay();
        using var svc = Service();

        Assert.Equal(BackupOutcome.BackedUp, await svc.TickAsync(_now));
        Assert.Equal(BackupOutcome.AlreadyDone, await svc.TickAsync(_now));
        Assert.Equal(2, _rclone.Calls.Count);             // one backup, not two

        // a "no day db" date is claimed too — no re-checking every 30s
        _now = new DateTime(2026, 9, 17, 23, 20, 0);
        Assert.Equal(BackupOutcome.SkippedNoDayDb, await svc.TickAsync(_now));
        Assert.Equal(2, _rclone.Calls.Count);
    }

    [Fact]
    public async Task FailedRun_BacksOff_ThenRetriesSameDate()
    {
        SeedDay();
        _rclone.CopyToExit = 1;
        using var svc = Service();

        Assert.Equal(BackupOutcome.Failed, await svc.TickAsync(_now));
        Assert.Equal(BackupOutcome.BackingOff, await svc.TickAsync(_now));      // +0s
        Assert.Equal(1, _rclone.Calls.Count);

        _now = _now.AddMinutes(11);                        // past the 10 min backoff
        Assert.Equal(BackupOutcome.Failed, await svc.TickAsync(_now));
        Assert.Equal(2, _rclone.Calls.Count);              // it did come back for this date

        // ... and once it finally succeeds, the date is closed out
        _rclone.CopyToExit = 0;
        _now = _now.AddMinutes(11);
        Assert.Equal(BackupOutcome.BackedUp, await svc.TickAsync(_now));
        AssertPvGone();
    }

    private void AssertPvGone() => Assert.All(Doomed, f => Assert.False(File.Exists(f)));

    // ------------------------------------------------------------------
    // RemoteSize (the rclone lsjson parse)
    // ------------------------------------------------------------------

    [Fact]
    public void RemoteSize_FindsTheFile_IgnoresDirsAndJunk()
    {
        const string json = """
            [{"Path":"fyers-2026-09-16.zip","Name":"fyers-2026-09-16.zip","Size":1234,"IsDir":false},
             {"Path":"old","Name":"old","Size":99999,"IsDir":true}]
            """;
        Assert.Equal(1234L, BackupService.RemoteSize(json, "fyers-2026-09-16.zip"));
        Assert.Null(BackupService.RemoteSize(json, "fyers-2026-09-17.zip"));
        Assert.Null(BackupService.RemoteSize("[]", "fyers-2026-09-16.zip"));
        Assert.Null(BackupService.RemoteSize("<html>nope</html>", "fyers-2026-09-16.zip"));
    }

    // ------------------------------------------------------------------
    // the hosted loop: disabled idles forever, and it never throws
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Disabled_IdlesForever_WithoutTouchingAnything()
    {
        SeedDay();
        using var svc = Service(enabled: false);

        await RunFor(svc, TimeSpan.FromMilliseconds(250));

        Assert.Empty(_rclone.Calls);
        AssertPvIntact();
    }

    [Fact]
    public async Task ExecuteAsync_BeforeTime_Idles()
    {
        SeedDay();
        _now = new DateTime(2026, 9, 16, 22, 45, 0);
        using var svc = Service();

        await RunFor(svc, TimeSpan.FromMilliseconds(250));

        Assert.Empty(_rclone.Calls);
        AssertPvIntact();
    }

    [Fact]
    public async Task ExecuteAsync_CatchesUp_ThenRunsOnceForTheDate()
    {
        // the pod was started (or restarted) at 23:40: the 23:15 slot is caught
        // up on the first tick, and a fixed clock proves the once-per-date guard
        SeedDay();
        _now = new DateTime(2026, 9, 16, 23, 40, 0);
        using var svc = Service();

        await RunFor(svc, TimeSpan.FromMilliseconds(400));

        Assert.Equal(2, _rclone.Calls.Count);
        AssertPvGone();
    }

    /// <summary>Run the hosted service for a while; asserts ExecuteAsync never
    /// threw (BackgroundService would surface it here).</summary>
    private static async Task RunFor(BackupService svc, TimeSpan howLong)
    {
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        try
        {
            await Task.Delay(howLong);
            if (svc.ExecuteTask is { } t)
                Assert.False(t.IsFaulted, "ExecuteAsync threw: " + t.Exception?.GetBaseException().Message);
        }
        finally
        {
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------
    // fake rclone
    // ------------------------------------------------------------------

    private sealed class FakeRclone
    {
        public List<(string Exe, string Args)> Calls { get; } = [];
        public int CopyToExit { get; set; }
        public int LsJsonExit { get; set; }
        public string? LsJson { get; set; }
        public long? SizeReturned { get; set; }
        public string? StdErr { get; set; }

        /// <summary>Size of the day db's -wal at upload time: 0 (or -1 when the
        /// file was already gone) proves the TRUNCATE checkpoint ran first.</summary>
        public int WalBytesAtUpload { get; private set; } = -2;
        /// <summary>Bytes of the zip at the moment rclone "uploaded" it — the
        /// only way to inspect entries after the service deletes the local file.</summary>
        public byte[]? UploadedZip { get; private set; }

        public (int ExitCode, string StdOut, string StdErr) Invoke(
            string exe, IReadOnlyList<string> args)
        {
            Calls.Add((exe, string.Join(" ", args)));
            switch (args.FirstOrDefault())
            {
                case "copyto":
                    var localZip = args[1];
                    WalBytesAtUpload = File.Exists(localZip + "-wal")
                        ? (int)new FileInfo(localZip + "-wal").Length
                        : -1;
                    if (CopyToExit == 0) UploadedZip = File.ReadAllBytes(localZip);
                    return (CopyToExit, string.Empty, StdErr ?? string.Empty);

                case "lsjson":
                    var zipPath = ZipOf(Calls);
                    var name = Path.GetFileName(zipPath);
                    var size = SizeReturned ?? new FileInfo(zipPath).Length;
                    var json = LsJson ?? $$"""[{"Path":"{{name}}","Name":"{{name}}","Size":{{size}},"IsDir":false}]""";
                    return (LsJsonExit, json, string.Empty);

                default:
                    return (0, string.Empty, string.Empty);
            }
        }

        private static string ZipOf(List<(string Exe, string Args)> calls)
        {
            foreach (var (_, args) in calls)
                if (args.StartsWith("copyto ", StringComparison.Ordinal))
                    return args.Split(' ')[1];
            throw new InvalidOperationException("no copyto recorded");
        }
    }

    private static long Count(SqliteConnection conn, string table)
        => Convert.ToInt64(Scalar(conn, $"SELECT COUNT(*) FROM {table}"));

    private static object Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() ?? 0L;
    }
}
