using System.Diagnostics;
using Fyers.Backfill.Config;
using Fyers.Backfill.Failures;
using Fyers.Backfill.Instruments;
using Fyers.Backfill.Ledger;
using Fyers.Backfill.Parquet;
using Fyers.Backfill.Work;
using Fyers.Core.Fyers;
using Microsoft.Extensions.Logging;

namespace Fyers.Backfill;

/// <summary>What a single run should do.</summary>
public enum RunMode
{
    /// <summary>Fetch every planned window, resuming from the ledger.</summary>
    Backfill,

    /// <summary>Fetch only each instrument's newest window (the daily increment).</summary>
    Update,

    /// <summary>Validate end-to-end on a handful of instruments before a sweep.</summary>
    Pilot,
}

/// <summary>Outcome of one run (for tests and the exit code).</summary>
public sealed record RunReport(
    int Considered,
    int Completed,
    int Skipped,
    int Failed,
    int Requests,
    long Rows,
    bool AuthExpired,
    bool BudgetExhausted,
    TimeSpan Elapsed);

/// <summary>
/// The backfill driver: walks the ordered work list, fetches each window through
/// the rate-limited client, merge-writes the Parquet partition and checkpoints the
/// ledger. It is deliberately a bounded batch — a request budget and an optional
/// wall-clock cap stop the run cleanly (the Fyers quota is global to the account
/// and shared with the live collector), and everything left unvisited is simply
/// picked up by the next run.
/// </summary>
public sealed class BackfillRunner(
    BackfillConfig cfg,
    FyersClient client,
    ResumeLedger ledger,
    CandleStore store,
    FailuresManifest failures,
    ILogger log,
    Func<DateTime>? clock = null)
{
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(2);
    private const int MaxRateLimitRetries = 40;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public async Task<RunReport> RunAsync(RunMode mode, int pilotCount, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var builder = new UniverseBuilder(cfg, client, log);
        var instruments = await builder.BuildAsync(ct);

        if (mode == RunMode.Pilot && instruments.Count > pilotCount)
            instruments = instruments.Take(pilotCount).ToList();

        var planned = WorkPlanner.Plan(instruments, cfg);
        var work = mode == RunMode.Update ? NewestPerInstrument(planned) : planned;
        log.LogInformation(
            "backfill: mode={Mode} instruments={Instruments} units={Units} from={From} to={To} root={Root}",
            mode, instruments.Count, work.Count, cfg.From, cfg.EndDate, cfg.Root);

        var budgetMs = cfg.MaxMinutes > 0 ? cfg.MaxMinutes * 60_000L : 0;
        var started = _clock();
        int requests = 0, completed = 0, skipped = 0, failed = 0;
        long rows = 0;
        var authExpired = false;
        var budgetExhausted = false;

        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();

            if (ledger.IsDone(item.Key))
            {
                skipped++;
                continue;
            }

            if (requests >= cfg.DailyBudget ||
                (budgetMs > 0 && (_clock() - started).TotalMilliseconds >= budgetMs))
            {
                budgetExhausted = true;
                log.LogWarning("backfill: budget reached ({Requests} requests) — stopping cleanly", requests);
                break;
            }

            try
            {
                var (bars, used, effective) = await FetchAsync(item, ct);
                requests += used;
                var total = cfg.PartFiles
                    ? await store.WritePartAsync(effective, item.Resolution, item.From, item.To, bars, ct)
                    : await store.MergeWriteAsync(effective, item.Resolution, bars, ct);
                ledger.MarkDone(item, bars.Count, used);
                completed++;
                rows += bars.Count;
                log.LogInformation(
                    "backfill: {Symbol} {Res} {From}..{To} -> {Bars} bars ({Total} in partition)",
                    effective.Symbol, item.Resolution, item.From, item.To, bars.Count, total);
            }
            catch (AuthExpiredException e)
            {
                authExpired = true;
                log.LogError("backfill: AUTH EXPIRED — run parked, re-login then re-run ({Message})", e.Message);
                ledger.SetMeta("parked_at", DateTimeOffset.UtcNow.ToString("O"));
                ledger.SetMeta("parked_symbol", item.Instrument.Symbol);
                break;
            }
            catch (Exception e)
            {
                failed++;
                ledger.MarkFailed(item, 0, e.Message);
                failures.Record(item.Instrument.Symbol, item.Resolution, item.From, item.To,
                    item.Instrument.Kind, e.Message);
                log.LogError("backfill: {Symbol} {From}..{To} failed: {Message}",
                    item.Instrument.Symbol, item.From, item.To, e.Message);
            }
        }

        sw.Stop();
        ledger.SetMeta("last_run_at", DateTimeOffset.UtcNow.ToString("O"));
        ledger.SetMeta("last_run_mode", mode.ToString());
        log.LogInformation(
            "backfill done: completed={Completed} skipped={Skipped} failed={Failed} requests={Requests} rows={Rows} ({Elapsed:F0}s)",
            completed, skipped, failed, requests, rows, sw.Elapsed.TotalSeconds);

        return new RunReport(work.Count, completed, skipped, failed, requests, rows,
            authExpired, budgetExhausted, sw.Elapsed);
    }

    /// <summary>
    /// Fetch one window, retrying while the limiter drains the minute. Expired
    /// contracts go through the expired endpoints; everything else through
    /// <c>/data/history</c>. When an equity is not served in the <c>-EQ</c> series
    /// (Fyers returns "Invalid symbol" — the ticker trades trade-for-trade), it
    /// falls back to <c>-BE</c> and returns the effective instrument so the row
    /// lands under the real symbol. Returns bars, requests consumed, instrument.
    /// </summary>
    private async Task<(IReadOnlyList<CandleRow> Bars, int Requests, Instrument Instrument)> FetchAsync(
        WorkItem item, CancellationToken ct)
    {
        var inst = item.Instrument;
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                var bars = inst.Expired
                    ? client.FnoHistoricalData(inst.Symbol, item.From, item.To, item.Resolution,
                        includeOi: cfg.IncludeOi && inst.WantsOi)
                    : client.History(inst.Symbol, item.From, item.To,
                        oiFlag: cfg.IncludeOi && inst.WantsOi, contFlag: inst.WantsCont,
                        resolution: item.Resolution, ct);
                return (bars, attempts, inst);
            }
            catch (RateLimitedException e) when (attempts < MaxRateLimitRetries)
            {
                log.LogDebug("backfill: limiter drain, retrying {Symbol} in {Sec}s ({Message})",
                    inst.Symbol, RateLimitBackoff.TotalSeconds, e.Message);
                await Task.Delay(RateLimitBackoff, ct);
            }
            catch (InvalidOperationException e)
                when (inst.Kind == Instrument.KindEquity
                      && inst.Symbol.EndsWith("-EQ", StringComparison.Ordinal)
                      && e.Message.Contains("Invalid symbol", StringComparison.OrdinalIgnoreCase))
            {
                // EQ series not served for this ticker (it trades in BE / T2T).
                var be = inst with { Symbol = inst.Symbol[..^"-EQ".Length] + "-BE" };
                log.LogInformation("backfill: {Eq} not served as EQ — retrying as {Be}", inst.Symbol, be.Symbol);
                var bars = client.History(be.Symbol, item.From, item.To,
                    oiFlag: false, contFlag: false, resolution: item.Resolution, ct);
                return (bars, attempts + 1, be);
            }
        }
    }

    /// <summary>Keep only the newest planned window per (instrument, resolution).</summary>
    internal static IReadOnlyList<WorkItem> NewestPerInstrument(IReadOnlyList<WorkItem> planned)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newest = new List<WorkItem>();
        foreach (var item in planned)                 // already recency-sorted
        {
            var key = $"{item.Instrument.Symbol}|{item.Resolution}";
            if (seen.Add(key))
                newest.Add(item);
        }
        return newest;
    }
}
