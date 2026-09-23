using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Fyers.Collector.Capture;
using Fyers.Core.Config;
using Fyers.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Fyers.Collector.Services;

/// <summary>Outcome of one <see cref="BackupService"/> tick. Public because
/// the tests drive the tick loop directly.</summary>
public enum BackupOutcome
{
    /// <summary>Uploaded, verified on Drive, PV cleaned for a fresh slate.</summary>
    BackedUp,

    /// <summary>No day db for the date (non-trading day / nothing captured).</summary>
    SkippedNoDayDb,

    /// <summary>Failed at a step — nothing was deleted, retry after a backoff.</summary>
    Failed,

    /// <summary>This date already ran (success or skip); nothing to do.</summary>
    AlreadyDone,

    /// <summary>Before <c>cfg.Gdrive.Time</c> IST.</summary>
    NotYetTime,

    /// <summary>A previous attempt failed; backing off before retrying.</summary>
    BackingOff,
}

/// <summary>
/// Nightly Drive backup + PV reset — the .NET replacement for the flat-layout
/// <c>src/gdrive.py</c> uploader (design approved 2026-09-15, NOT a 1:1 port).
///
/// At <c>cfg.Gdrive.Time</c> (23:15 IST, after the candles job's 21:00+120min
/// hard budget) it archives THE DAY, then empties the PV for a fresh slate:
///
///  1. <c>PRAGMA wal_checkpoint(TRUNCATE)</c> on the day db, so the zip holds a
///     consistent db with no sidecars.
///  2. Zip <b>three</b> files — the day's <c>snapshots-yyyy-MM-dd.db</c>,
///     <c>summary.db</c> and <c>sym_master.json</c> — into
///     <c>{dailyDir}/fyers-yyyy-MM-dd.zip</c> (System.IO.Compression, Optimal;
///     native BCL, no external binary).
///  3. <c>rclone copyto</c> it to
///     <c>{remote}:{path}/yyyy-MM-dd/fyers-yyyy-MM-dd.zip</c> — the per-day
///     Drive layout replaces the old flat one.
///  4. VERIFY-THEN-DELETE: <c>rclone lsjson</c> that folder and compare the
///     remote size against the local zip. Only a verified match lets step 5 run.
///  5. Delete exactly: the day db + its <c>-wal</c>/<c>-shm</c>,
///     <c>sym_master.json</c>, <c>fyers_access_token.json</c>,
///     <c>fyers_reauth.request</c>. <c>summary.db</c> is cumulative
///     (daily_summary history + reference tables) and the next day reads it —
///     it stays, locally and untouched.
///
/// Any failure at any step logs <c>FAILED at {step} — no files deleted</c>,
/// leaves the PV exactly as it was and retries after a 10-minute backoff
/// (transient Drive/network blips self-heal instead of losing the day's backup;
/// the PV only grows until a run succeeds). Every tick is wrapped: this service
/// never throws out of <c>ExecuteAsync</c>.
/// </summary>
public sealed class BackupService(
    AppConfig cfg,
    Storage storage,
    ILogger<BackupService> log,
    Func<DateTime>? clock = null,
    Func<string, IReadOnlyList<string>, (int ExitCode, string StdOut, string StdErr)>? runner = null,
    TimeSpan? poll = null) : BackgroundService
{
    /// <summary>IST wall clock, shared with the capture side.</summary>
    internal static readonly TimeZoneInfo IstZone = SnapshotRunner.IstZone;

    // ---- the exact filenames this service owns (delete ONLY these) ----
    internal const string SummaryFile = "summary.db";
    // Per-segment masters introduced in multi-segment ticket 03; the F&O master
    // is sym_master_fo.json, and CDS/COM are sym_master_cds.json / sym_master_com.json.
    // The backup now looks for all three and includes whatever exists.
    internal const string MasterFile = "sym_master_fo.json";
    internal const string CdsMasterFile = "sym_master_cds.json";
    internal const string ComMasterFile = "sym_master_com.json";
    internal const string TokenFile = "fyers_access_token.json";
    internal const string ReauthFile = "fyers_reauth.request";

    internal const int UploadTimeoutSec = 3600;   // first run can carry a big day
    internal const int LsTimeoutSec = 120;
    internal const int RetryAfterMin = 10;        // failed-run backoff, same date

    private readonly Func<DateTime> _clock = clock ??
        (() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstZone));

    /// <summary>Null = the real rclone; a test injects a fake. The delegate is
    /// sync by shape (a process run is naturally blocking); the built-in path
    /// is async underneath and the fake is invoked off the loop.</summary>
    private readonly Func<string, IReadOnlyList<string>, (int ExitCode, string StdOut, string StdErr)>? _run = runner;

    private readonly TimeSpan _poll = poll ?? TimeSpan.FromSeconds(30);

    private DateOnly? _doneDate;                  // ran (success or skip) for this date
    private DateTime _nextAttemptAt = DateTime.MinValue;   // failed-run backoff

    private DateTime Now() => _clock();

    private static string ZipName(DateOnly date) => $"fyers-{date:yyyy-MM-dd}.zip";

    private string DayFolder(DateOnly date) =>
        $"{cfg.Gdrive.Remote}:{cfg.Gdrive.Path.TrimEnd('/')}/{date:yyyy-MM-dd}";

    // ------------------------------------------------------------------
    // hosted loop
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!cfg.Gdrive.Enabled)
        {
            log.LogInformation("backup: disabled (gdrive.enabled=false) — idle");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (TaskCanceledException) { }
            return;
        }

        log.LogInformation(
            "backup: nightly at {Time} IST -> {Remote}:{Path}/<date>/fyers-<date>.zip, then the PV is reset",
            cfg.Gdrive.Time, cfg.Gdrive.Remote, cfg.Gdrive.Path);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(Now(), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // a tick must never take the hosted service down
                log.LogError("backup: tick failed (supervised, continuing): {Message}", e.Message);
            }

            try { await Task.Delay(_poll, ct); }
            catch (TaskCanceledException) { break; }
        }
        log.LogInformation("backup: stopped");
    }

    /// <summary>
    /// One decision tick. Runs at most once per IST date (a successful run and
    /// a "no day db" skip both claim the date — only a failure retries, after
    /// <see cref="RetryAfterMin"/> minutes). <paramref name="istNow"/> is IST
    /// wall clock (same contract as <see cref="SnapshotRunner.RunAsync"/>).
    /// </summary>
    public async Task<BackupOutcome> TickAsync(DateTime istNow, CancellationToken ct = default)
    {
        var date = DateOnly.FromDateTime(istNow);
        if (_doneDate == date) return BackupOutcome.AlreadyDone;
        if (TimeOnly.FromDateTime(istNow) < cfg.Gdrive.Time) return BackupOutcome.NotYetTime;
        if (istNow < _nextAttemptAt) return BackupOutcome.BackingOff;

        var dayDb = storage.DayPath(date);
        if (!File.Exists(dayDb))
        {
            log.LogInformation("backup {Date}: no day db — skipping", date);
            _doneDate = date;                    // nothing will appear later today
            return BackupOutcome.SkippedNoDayDb;
        }

        var outcome = await BackupAsync(date, ct);
        if (outcome == BackupOutcome.BackedUp)
        {
            _doneDate = date;
        }
        else
        {
            // failure: retry this date after a backoff, never a hot rclone loop
            _nextAttemptAt = istNow.AddMinutes(RetryAfterMin);
        }
        return outcome;
    }

    // ------------------------------------------------------------------
    // the pipeline
    // ------------------------------------------------------------------

    private async Task<BackupOutcome> BackupAsync(DateOnly date, CancellationToken ct)
    {
        var dir = storage.DailyDir;
        var dayDb = storage.DayPath(date);
        var summary = storage.SummaryDbPath;
        var masterFo = Path.Combine(dir, MasterFile);
        var masterCds = Path.Combine(dir, CdsMasterFile);
        var masterCom = Path.Combine(dir, ComMasterFile);
        var token = Path.Combine(dir, TokenFile);
        var reauth = Path.Combine(dir, ReauthFile);
        var zip = Path.Combine(dir, ZipName(date));
        var remote = $"{DayFolder(date)}/{ZipName(date)}";

        // 1. checkpoint: the zip must hold a consistent db with no sidecars.
        if (!CheckpointWal(dayDb, date, fatal: true)) return BackupOutcome.Failed;
        // summary.db is copied too — flush its wal as well so the zip's copy is
        // complete. It is written by the candles job, so a lock here must not
        // sink the backup: warn and continue (a stale copy is still a copy).
        CheckpointWal(summary, date, fatal: false);

        // 2. zip — day db, summary.db, and any master files that exist (FO, CDS, COM).
        // The collector typically downloads the FO master to the path resolved from --master
        // (sym_master.json in the default k8s deployment), while LoadMulti also writes
        // per-segment caches (sym_master_fo.json, sym_master_cds.json, sym_master_com.json).
        // We look for each possible location in priority order.
        var inputs = new List<(string Path, string Entry)>
        {
            (dayDb, Path.GetFileName(dayDb)),
            (summary, SummaryFile),
        };
        string masterPath = masterFo; // fallback
        if (File.Exists(masterFo)) masterPath = masterFo;
        else if (File.Exists(masterCds)) masterPath = masterCds;
        else if (File.Exists(masterCom)) masterPath = masterCom;
        else if (File.Exists(Path.Combine(dir, "sym_master.json"))) masterPath = Path.Combine(dir, "sym_master.json");
        if (string.IsNullOrEmpty(masterPath) || !File.Exists(masterPath))
        {
            LogFailed(date, "zip", "missing required files: need at least day db + summary + one master");
            return BackupOutcome.Failed;
        }
        inputs.Add((masterPath, Path.GetFileName(masterPath)));
        log.LogInformation("backup {Date}: zipping {Files}", date,
            string.Join(", ", inputs.Select(i => i.Entry)));
        try
        {
            await Task.Run(() => WriteZip(zip, inputs), ct);
        }
        catch (Exception e)
        {
            LogFailed(date, "zip", e.Message);
            return BackupOutcome.Failed;
        }
        var localBytes = new FileInfo(zip).Length;
        log.LogInformation("backup {Date}: uploading {Zip} ({Mb} MB)", date, ZipName(date),
            (localBytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture));

        // 3. upload
        var (code, _, err) = await RcloneAsync("rclone",
            new[] { "copyto", zip, remote }, UploadTimeoutSec, ct);
        if (code != 0)
        {
            LogFailed(date, "upload", $"rclone copyto exit {code}: {Blank(err)}");
            return BackupOutcome.Failed;
        }
        NoteStderr(date, "upload", err);

        // 4. VERIFY-THEN-DELETE: the remote file must exist at the local size.
        var (lsCode, lsOut, lsErr) = await RcloneAsync("rclone",
            new[] { "lsjson", $"{DayFolder(date)}/" }, LsTimeoutSec, ct);
        if (lsCode != 0)
        {
            LogFailed(date, "verify", $"rclone lsjson exit {lsCode}: {Blank(lsErr)}");
            return BackupOutcome.Failed;
        }
        NoteStderr(date, "verify", lsErr);
        var remoteBytes = RemoteSize(lsOut, ZipName(date));
        if (remoteBytes is null)
        {
            LogFailed(date, "verify", $"{ZipName(date)} not found in {DayFolder(date)}/");
            return BackupOutcome.Failed;
        }
        if (remoteBytes.Value != localBytes)
        {
            LogFailed(date, "verify", $"size mismatch: local {localBytes} vs remote {remoteBytes.Value}");
            return BackupOutcome.Failed;
        }
        log.LogInformation("backup {Date}: verified {Size} bytes on Drive", date, remoteBytes.Value);

        // 5. clean — only after a verified upload, and ONLY these filenames.
        // The local zip goes too: it is redundant once verified on Drive, and
        // leaving it would grow the PV ~500 MB/day against the fresh-slate goal.
        var doomed = new List<string>
        {
            dayDb, dayDb + "-wal", dayDb + "-shm", token, reauth, zip,
        };
        if (File.Exists(masterFo)) doomed.Add(masterFo);
        if (File.Exists(masterCds)) doomed.Add(masterCds);
        if (File.Exists(masterCom)) doomed.Add(masterCom);
        foreach (var path in doomed)
        {
            if (!File.Exists(path)) continue;
            try { File.Delete(path); }
            catch (Exception e)
            {
                // the backup IS on Drive, so this is a cleanup miss, not data
                // loss — but say so, and let the backoff retry finish the job
                log.LogError("backup {Date}: could not delete {Path}: {Message}", date, path, e.Message);
                return BackupOutcome.Failed;
            }
        }
        log.LogInformation("backup {Date}: cleaned day db, master(s), token (summary.db preserved)", date);
        return BackupOutcome.BackedUp;
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private async Task<(int ExitCode, string StdOut, string StdErr)> RcloneAsync(
        string exe, IReadOnlyList<string> args, int timeoutSec, CancellationToken ct)
        => _run is not null
            ? await Task.Run(() => _run(exe, args), ct)
            : await Rclone.RunAsync(exe, args, TimeSpan.FromSeconds(timeoutSec), ct);

    /// <summary><c>PRAGMA wal_checkpoint(TRUNCATE)</c> on its own short-lived
    /// connection — folds the wal back into the db and truncates the sidecar to
    /// zero, so the zipped db file is self-contained.</summary>
    private bool CheckpointWal(string dbPath, DateOnly date, bool fatal)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,     // it exists; never create here
                Pooling = false,
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception e)
        {
            if (fatal) LogFailed(date, "checkpoint", e.Message);
            else log.LogWarning("backup {Date}: could not checkpoint {Path}: {Message}",
                date, Path.GetFileName(dbPath), e.Message);
            return false;
        }
    }

    private static void WriteZip(string zipPath, IReadOnlyList<(string Path, string Entry)> files)
    {
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (path, entry) in files)
        {
            var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
            using var dst = e.Open();
            using var src = File.OpenRead(path);
            src.CopyTo(dst);
        }
    }

    /// <summary>Size of <paramref name="fileName"/> in an
    /// <c>rclone lsjson</c> array, or null when the file is not listed.</summary>
    public static long? RemoteSize(string lsjson, string fileName)
    {
        try
        {
            using var doc = JsonDocument.Parse(lsjson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("Name", out var name) ||
                    !string.Equals(name.GetString(), fileName, StringComparison.Ordinal))
                    continue;
                if (el.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True)
                    continue;
                return el.TryGetProperty("Size", out var size) &&
                       size.ValueKind == JsonValueKind.Number
                    ? size.GetInt64()
                    : null;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>rclone on a read-only rootfs still writes
    /// <c>Failed to save config ... read-only file system</c> on success —
    /// noise, not failure (the exit code decides). Anything else is logged as a
    /// warning for visibility but still non-fatal.</summary>
    private void NoteStderr(DateOnly date, string step, string stderr)
    {
        var text = stderr.Trim();
        if (text.Length == 0) return;
        if (text.Contains("Failed to save config") && text.Contains("read-only file system"))
            log.LogInformation("backup {Date}: rclone {Step} stderr (benign): {Stderr}", date, step, text);
        else
            log.LogWarning("backup {Date}: rclone {Step} stderr: {Stderr}", date, step, text);
    }

    private void LogFailed(DateOnly date, string step, string detail) =>
        log.LogError("backup {Date}: FAILED at {Step} — no files deleted ({Detail})",
            date, step, detail);

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "<no stderr>" : s.Trim();

    /// <summary>The real rclone. On PATH in the container image;
    /// RCLONE_CONFIG comes from the deployment.</summary>
    internal static class Rclone
    {
        public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
            string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);   // no re-quoting hazards
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            Process? proc;
            try { proc = Process.Start(psi); }
            catch (Exception e) { return (-1, string.Empty, $"{exe}: {e.Message}"); }
            if (proc is null) return (-1, string.Empty, $"{exe}: could not start");

            using (proc)
            using (var timed = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timed.CancelAfter(timeout);
                try
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync(timed.Token);
                    var stderr = proc.StandardError.ReadToEndAsync(timed.Token);
                    await proc.WaitForExitAsync(timed.Token);
                    return (proc.ExitCode, await stdout, await stderr);
                }
                catch (OperationCanceledException)
                {
                    TryKill(proc);
                    var why = ct.IsCancellationRequested ? "cancelled" : $"timed out after {timeout.TotalSeconds:F0}s";
                    return (-1, string.Empty, $"{exe} {why}");
                }
            }
        }

        private static void TryKill(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }
}
