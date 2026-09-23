using Fyers.Backfill.Config;
using Fyers.Backfill.Instruments;

namespace Fyers.Backfill.Work;

/// <summary>
/// Turns a universe of instruments into an ordered work list.
///
/// Ordering is <b>breadth-first by recency</b>: every instrument's newest window
/// comes first, then the next window back, and so on. A partial run therefore
/// still yields coherent recent data across the whole universe rather than a
/// complete history for a handful of symbols.
///
/// Window rules:
/// <list type="bullet">
/// <item>Cash equities: fixed <c>chunk_days</c> windows from <c>from</c> to the end date.</item>
/// <item>Derivatives (live or expired): a contract only trades in the run-up to its
/// own expiry, so one window bounded by <c>[expiry - chunk_days, expiry]</c> (clipped
/// to the global range) — never crossing an expiry, which Fyers would reject.</item>
/// </list>
/// </summary>
public static class WorkPlanner
{
    /// <summary>Fyers caps minute resolutions at 100 days/request, daily at 366.</summary>
    public static int ChunkDaysFor(string resolution, int configured)
        => resolution is "D" or "1D" or "1W" or "1M"
            ? Math.Max(1, Math.Min(configured <= 0 ? 366 : configured, 366))
            : Math.Max(1, Math.Min(configured <= 0 ? 100 : configured, 100));

    public static IReadOnlyList<WorkItem> Plan(
        IEnumerable<Instrument> instruments, BackfillConfig cfg)
    {
        var end = cfg.EndDate;
        var items = new List<WorkItem>();

        foreach (var inst in instruments)
        {
            foreach (var resolution in cfg.Resolutions)
            {
                var chunk = ChunkDaysFor(resolution, cfg.ChunkDays);
                if (inst.ExpiryDate is { } expiry)
                {
                    // Contract life window, clipped to the global range.
                    var from = Max(cfg.From, expiry.AddDays(-chunk));
                    var to = Min(end, expiry);
                    if (from <= to)
                        items.Add(new WorkItem(inst, resolution, from, to));
                }
                else
                {
                    for (var start = cfg.From; start <= end; start = start.AddDays(chunk))
                    {
                        var to = Min(end, start.AddDays(chunk - 1));
                        items.Add(new WorkItem(inst, resolution, start, to));
                    }
                }
            }
        }

        // Breadth-first by recency, then deterministic by symbol/window.
        items.Sort(static (a, b) =>
        {
            var c = b.Recency.CompareTo(a.Recency);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Instrument.Symbol, b.Instrument.Symbol);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Resolution, b.Resolution);
        });
        return items;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a >= b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
}
