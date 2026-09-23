using Fyers.Collector.Capture;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Fyers;

namespace Fyers.Collector.Services;

/// <summary>Minute-grid capture loop — port of the <c>src/scheduler.py</c> run
/// loop: align to the next IST minute boundary, pick the snapshot mode for the
/// window, skip-on-overrun (a run that lands >5s late skips its minute), park
/// on AuthExpired after touching <c>fyers_reauth.request</c>.</summary>
public sealed class CaptureLoopService(
    AppConfig cfg,
    MarketCalendar cal,
    SnapshotRunner runner,
    TokenFileReader tokens,
    ILogger<CaptureLoopService> log) : BackgroundService
{
    internal static readonly TimeZoneInfo IstZone = SnapshotRunner.IstZone;
    private static DateTime IstNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstZone);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("scheduler started. quotes {Qs}-{Qe} IST, chains {S}-{E}, tz=Asia/Kolkata",
            cfg.Session.QuotesStart, cfg.Session.QuotesEnd, cfg.Session.Start, cfg.Session.End);
        while (!ct.IsCancellationRequested)
        {
            var now = IstNow();
            var boundary = now.AddMinutes(1).AddSeconds(-now.Second)
                .AddMilliseconds(-now.Millisecond);
            var delay = boundary - now;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (TaskCanceledException) { break; }
            }

            var istNow = IstNow();
            // overrun guard: we wanted to wake ON the boundary; >5s late = the
            // previous minute's work bled over — skip this minute (grid parity).
            if ((istNow - boundary).TotalSeconds > 5)
            {
                log.LogWarning("overran the minute boundary by {S:F0}s — skipping {Minute}",
                    (istNow - boundary).TotalSeconds, boundary);
                continue;
            }

            var mode = cal.ModeAt(TimeOnly.FromDateTime(istNow));
            if (mode == SnapshotMode.Off) continue;

            try
            {
                await runner.RunAsync(istNow, mode == SnapshotMode.Full ? "full" : "quotes_only", ct);
            }
            catch (AuthExpiredException e)
            {
                log.LogError("AUTH EXPIRED — token service owns login; parking 60s ({Message})", e.Message);
                try { File.WriteAllText(tokens.ReauthRequestPath, DateTime.UtcNow.ToString("O")); }
                catch (Exception fe) { log.LogWarning("could not write reauth signal: {Message}", fe.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
                catch (TaskCanceledException) { break; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                log.LogError("snapshot tick failed (supervised, continuing): {Message}", e.Message);
            }
        }
        log.LogInformation("capture loop stopped");
    }
}

/// <summary>Collector-side token file reader — the same file the token service
/// writes (<c>{dataDir}/fyers_access_token.json</c>). Stale when the saved_at
/// IST date is before today (the ~06:00 Fyers daily reset).</summary>
public sealed class TokenFileReader
{
    public string TokenPath { get; }
    public string ReauthRequestPath => Path.Combine(Path.GetDirectoryName(TokenPath)!, "fyers_reauth.request");

    public TokenFileReader(string tokenPath) => TokenPath = tokenPath;

    public string? AccessToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TokenPath));
            var tok = doc.RootElement.GetProperty("access_token").GetString();
            var savedAt = doc.RootElement.TryGetProperty("saved_at", out var sa)
                ? sa.GetInt64() : 0;
            var ist = SnapshotRunner.IstZone;
            var savedIst = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeSeconds(savedAt).UtcDateTime, ist);
            var todayIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist).Date;
            if (savedIst.Date < todayIst) return null; // stale: pre-reset token
            return string.IsNullOrEmpty(tok) ? null : tok;
        }
        catch
        {
            return null;
        }
    }
}
