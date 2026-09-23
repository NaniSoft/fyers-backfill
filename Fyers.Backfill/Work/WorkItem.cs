using Fyers.Backfill.Instruments;

namespace Fyers.Backfill.Work;

/// <summary>One unit of fetch work: a single instrument over an inclusive date window.</summary>
/// <param name="Instrument">The instrument to fetch.</param>
/// <param name="Resolution">Fyers resolution string ("1", "D", ...).</param>
/// <param name="From">Inclusive start date.</param>
/// <param name="To">Inclusive end date.</param>
public sealed record WorkItem(Instrument Instrument, string Resolution, DateOnly From, DateOnly To)
{
    /// <summary>Stable ledger key (symbol + resolution + window).</summary>
    public string Key =>
        $"{Instrument.Symbol}|{Resolution}|{From:yyyy-MM-dd}|{To:yyyy-MM-dd}";

    /// <summary>Recency key: later windows first (breadth-first by recency).</summary>
    public long Recency => To.DayNumber * 100_000L + From.DayNumber;
}
