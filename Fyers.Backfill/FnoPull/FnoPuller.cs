using System.Diagnostics;
using Fyers.Core;
using Fyers.Core.Fyers;
using Microsoft.Extensions.Logging;

namespace Fyers.Backfill.FnoPull;

/// <summary>
/// Pulls the expired F&amp;O queue through <see cref="FyersClient"/> + the real
/// <see cref="RateLimiter"/>, writing into the existing dataset layout and
/// checkpointing the inherited python ledger.
///
/// This replaces <c>tools/pull_expired.py</c>, which reimplemented the client and
/// its own limiter. That limiter slept <c>30 * attempt</c> seconds on any 429
/// <b>without logging</b>, so the run spent its life in invisible 30-150s stalls
/// and achieved ~12 contracts/min against an API that serves a valid request in
/// 130-800ms. Here a 429 drains the minute and says so
/// (<see cref="RateLimiter.DrainMinute"/>), and the limiter runs at Fyers' documented
/// ceilings (8/s, 190/min vs 10/s, 200/min).
///
/// Windowing per contract, mirroring the python puller so a contract's data does
/// not change shape depending on which puller fetched it:
/// <list type="bullet">
/// <item>one <c>[expiry-99d, expiry]</c> window (Fyers caps minute data at 100 days);</item>
/// <item>futures get a second, older window <c>[expiry-200d, expiry-100d]</c>,
/// because futures are listed months ahead and trade well before that window;</item>
/// <item>windows clipped to the documented NSE expired-data floor, 2018-10-10.</item>
/// </list>
/// </summary>
public sealed class FnoPuller(
    FnoQueue queue,
    FnoStore store,
    FyersClient client,
    ILogger log,
    int dailyBudget = 90_000,
    int maxContracts = 0)
{
    /// <summary>Documented NSE expired-data floor (the python puller's FLOOR).</summary>
    public static readonly DateOnly Floor = new(2018, 10, 10);

    private const int Window = 100;
    private const int FuturesExtraDays = 200;

    /// <summary>
    /// Longest a single contract will be held waiting for the rate window to reopen.
    /// Past this the run gives up on the item rather than looping forever, and the
    /// contract stays unledgered so the next run picks it up.
    /// </summary>
    private const int MaxRateLimitHoldSeconds = 90;

    public async Task<FnoRunReport> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var todo = queue.Remaining();
        var (ok, nodata, err) = queue.StatusCounts();

        log.LogInformation(
            "fno: queue={Queue} ledgered={Ledgered} (ok={Ok} no_data={NoData} err={Err}) remaining={Remaining}",
            queue.Items.Count, queue.Ledger.Count, ok, nodata, err, todo.Count);

        if (todo.Count == 0)
        {
            log.LogInformation("fno: nothing remaining — the queue is fully ledgered");
            return new FnoRunReport(0, 0, 0, 0, 0, 0, false, false, TimeSpan.Zero, null);
        }

        log.LogInformation(
            "fno: starting at expiry {First} ({FirstSym}), ending at {Last} ({LastSym}) — chronological",
            todo[0].Expiry, todo[0].Sym, todo[^1].Expiry, todo[^1].Sym);

        if (maxContracts > 0 && todo.Count > maxContracts)
        {
            log.LogInformation("fno: --max {Max} limits this run to the first {N} items", maxContracts, maxContracts);
            todo = todo.Take(maxContracts).ToList();
        }

        int done = 0, newOk = 0, newNoData = 0, newErr = 0, requests = 0;
        var rateLimitHolds = 0;      // not an error: a contract held for the rate window
        var samples = new Queue<(double Min, int Done)>();   // trailing-window ETA samples
        long rows = 0;
        var authExpired = false;
        var budgetHit = false;
        var stopReason = (string?)null;
        var currentExpiry = todo[0].Expiry;

        foreach (var item in todo)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Expiry != currentExpiry)
            {
                // Crossing an expiry month is the progress marker that makes the
                // chronological order legible in the log.
                currentExpiry = item.Expiry;
                log.LogInformation("fno: -> now at expiry {Expiry} ({Done}/{Total} contracts done)",
                    currentExpiry, done, todo.Count);
            }

            if (requests >= dailyBudget)
            {
                budgetHit = true;
                stopReason = $"daily budget of {dailyBudget} requests reached";
                log.LogWarning("fno: {Reason} — stopping cleanly, re-run to continue", stopReason);
                break;
            }

            try
            {
                var (bars, used) = await FetchAsync(item, ct);
                requests += used;
                done++;

                if (bars.Count == 0)
                {
                    // Fyers says "no valid data here" — terminal, matching the python
                    // ledger's semantics so it is never retried.
                    queue.Ledger[item.Sym] = "no_data";
                    newNoData++;
                }
                else
                {
                    if (store.DirFor(item.Sym, item.Underlying) is null)
                    {
                        // No ISIN and not an index stem: nowhere to write it. Counted
                        // as an error so it stays visible instead of vanishing.
                        queue.Ledger[item.Sym] = "error:no_isin";
                        newErr++;
                    }
                    else
                    {
                        store.MergeWrite(item.Sym, item.Underlying, bars);
                        queue.Ledger[item.Sym] = "ok";
                        newOk++;
                        rows += bars.Count;
                    }
                }
            }
            catch (AuthExpiredException e)
            {
                authExpired = true;
                stopReason = "auth expired";
                log.LogError("fno: AUTH EXPIRED — parked ({Message}); re-login then re-run", e.Message);
                break;
            }
            catch (RateLimitedException e)
            {
                // The limiter refused the call without it going on the wire, so this
                // contract is NOT failed and must NOT be ledgered: it has never been
                // tried. Two distinct causes arrive here, and both are "wait for the
                // window to slide", not "this contract is bad":
                //   * minute budget spent for the current window  -> a few seconds
                //   * a 429 breach, which drained the whole minute  -> up to 60s
                // Polling short and re-deciding on the SAME item lets the retry happen
                // naturally, and costs no request. (The python puller slept a blind
                // 30*n seconds on 429 with no logging at all; this is the same
                // throttle, made visible and without collateral damage.)
                var held = 0;
                while (held < MaxRateLimitHoldSeconds)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    held += 2;
                    if (held % 10 == 0)
                        log.LogInformation("fno: holding for the rate window ({Held}s held on {Sym})",
                            held, item.Sym);
                }
                rateLimitHolds++;
                log.LogWarning("fno: rate window unavailable for {Sym} ({Message}) — held {Held}s, will retry",
                    item.Sym, e.Message, held);
                continue;                       // same item, no request consumed, no ledger write
            }
            catch (Exception e)
            {
                requests++;
                newErr++;
                // Keep the message, not just the type: the type alone ("OperationCanceled
                // Exception") cannot distinguish a 15s timeout from a Fyers server error,
                // which would force a post-mortem back into the run log.
                queue.Ledger[item.Sym] = "error:" + e.GetType().Name + ": " + Truncate(e.Message, 120);
                log.LogError("fno: {Sym} failed: {Message}", item.Sym, e.Message);
            }

            // Checkpoint every 25 contracts (the python cadence) but through the
            // atomic save, and report throughput so a stall is visible in the log.
            if (done % 25 == 0)
            {
                queue.SaveLedger();

                // Rate over the last ~5 minutes, not the cumulative average. The
                // cumulative figure is dragged down by the run's opening ramp and
                // goes stale the moment throughput changes, so its ETA drifts. A
                // short trailing window tracks the current reality.
                samples.Enqueue((sw.Elapsed.TotalMinutes, done));
                while (samples.Count > 2 && sw.Elapsed.TotalMinutes - samples.Peek().Min > 5)
                    samples.Dequeue();
                var oldest = samples.Peek();
                var windowMin = sw.Elapsed.TotalMinutes - oldest.Min;
                var rate = windowMin > 0.2
                    ? (done - oldest.Done) / windowMin
                    : done / Math.Max(sw.Elapsed.TotalMinutes, 0.01);

                var remaining = todo.Count - done;
                var eta = rate > 0.01
                    ? FormatEta(TimeSpan.FromMinutes(remaining / rate))
                    : "--:--";
                log.LogInformation(
                    "fno: {Done}/{Total} (ok={Ok} no_data={NoData} err={Err}) req={Req} {Rate:F0}/min eta={Eta} expiry={Expiry}",
                    done, todo.Count, newOk, newNoData, newErr, requests, rate, eta, currentExpiry);
            }
        }

        queue.SaveLedger();
        sw.Stop();

        log.LogInformation(
            "fno done: contracts={Done} ok={Ok} no_data={NoData} err={Err} rate_holds={Holds} requests={Req} rows={Rows} in {Elapsed:F0}s",
            done, newOk, newNoData, newErr, rateLimitHolds, requests, rows, sw.Elapsed.TotalSeconds);

        var (fOk, fNoData, fErr) = queue.StatusCounts();
        log.LogInformation("fno ledger now: ok={Ok} no_data={NoData} err={Err}", fOk, fNoData, fErr);

        var remainingNow = queue.Remaining().Count;
        log.LogInformation("fno: {Remaining} contracts still outstanding", remainingNow);

        return new FnoRunReport(done, newOk, newNoData, newErr, requests, rows,
            authExpired, budgetHit, sw.Elapsed, stopReason);
    }

    /// <summary>
    /// Fetch one contract across its window(s). Returns the bars and the number of
    /// wire requests consumed. A window that answers <c>no_data</c> contributes
    /// nothing and is not an error; an <c>error</c> body is rethrown to the caller.
    /// </summary>
    private async Task<(List<FnoCandle> Bars, int Requests)> FetchAsync(FnoQueueItem item, CancellationToken ct)
    {
        var expiry = item.ExpiryDate;
        var from = Max(Floor, expiry.AddDays(-(Window - 1)));
        var windows = new List<(DateOnly From, DateOnly To)> { (from, expiry) };

        if (item.Sym.EndsWith("FUT", StringComparison.Ordinal))
        {
            var olderFrom = Max(Floor, expiry.AddDays(-FuturesExtraDays));
            var olderTo = expiry.AddDays(-Window);
            if (olderTo >= olderFrom && olderTo > Floor)
                windows.Insert(0, (olderFrom, olderTo));
        }

        var bars = new List<FnoCandle>();
        var requests = 0;
        foreach (var (wf, wt) in windows)
        {
            var rows = client.FnoHistoricalData(item.Sym, wf, wt,
                FyersClient.HistoryResolution1Min, includeOi: true, ct: ct);
            requests++;
            foreach (var r in rows)
                bars.Add(new FnoCandle(r.TsUtc, (double)r.Open, (double)r.High,
                    (double)r.Low, (double)r.Close, (long)r.Volume, r.Oi));
        }

        // De-duplicate across windows by ts_utc, keeping the last write (a later
        // window is the more recent fetch of the same minute).
        var byTs = new Dictionary<long, FnoCandle>(bars.Count);
        foreach (var b in bars)
            byTs[b.TsUtc] = b;

        return (byTs.Values.OrderBy(b => b.TsUtc).ToList(), requests);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a >= b ? a : b;

    /// <summary>
    /// ETA as <c>d.hh:mm</c> when days are involved, else <c>hh:mm</c>.
    ///
    /// The bug this replaces used <c>TimeSpan.ToString("hh\:mm")</c>, which prints
    /// only the hour COMPONENT (0-23) and silently drops whole days — so a genuine
    /// 8-day ETA rendered as "01:59" and read as two hours. Every multi-day figure
    /// this puller reported was therefore wrong by a factor of ~24/day.
    /// </summary>
    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalDays >= 1)
            return $"{(int)eta.TotalDays}d{eta.Hours:00}:{eta.Minutes:00}";
        return $"{eta.Hours:00}:{eta.Minutes:00}";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Outcome of one <see cref="FnoPuller"/> run.</summary>
public sealed record FnoRunReport(
    int Contracts,
    int Ok,
    int NoData,
    int Errors,
    int Requests,
    long Rows,
    bool AuthExpired,
    bool BudgetExhausted,
    TimeSpan Elapsed,
    string? StopReason);
