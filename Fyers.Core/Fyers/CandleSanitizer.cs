namespace Fyers.Core.Fyers;

/// <summary>
/// Cleans raw <c>/data/history</c> candles before anything stores them.
///
/// Fyers' 1-minute feed is not internally consistent. Live-verified against the
/// BhavDesk EOD reference over 3.75M aggregated days (2026-09-28):
/// <list type="bullet">
/// <item><b>Impossible bars</b> — <c>JISLDVREQS 2017-11-10 09:15</c> came back as
/// <c>open=73.00, high=72.00, low=71.15, close=72.00</c>: the high is below the open,
/// so the day bar ends up with a high below its open. 10,424 days / 1,541 ISINs.</item>
/// <item><b>Volume sentinels</b> — a negative volume arrives wrapped and multiplied
/// (<c>429496726000</c> == <c>2^32 * 100 - 3600</c>), which destroys the daily sum;
/// GOLDBEES reached 2.1e12 in one day. 3,234 days / 1,124 ISINs.</item>
/// <item><b>Non-positive prices</b> — 954 days / 517 ISINs carry a zero or negative
/// open/high/low/close.</item>
/// </list>
///
/// Rules, applied per candle:
/// <list type="number">
/// <item>any of O/H/L/C &lt;= 0 -> the bar is dropped (the price is unusable and a
/// synthetic value would be worse than a gap);</item>
/// <item>otherwise <c>high = max(o,h,l,c)</c> and <c>low = min(o,h,l,c)</c>;</item>
/// <item><c>volume &lt; 0 || volume &gt; 1e9</c> -> 0 (the true figure is unrecoverable;
/// zero is the honest "unknown" for a minute with no volume).</item>
/// </list>
/// Counters are process-wide and surfaced by <see cref="Stats"/> so a run can report
/// how much it had to repair.
/// </summary>
public static class CandleSanitizer
{
    /// <summary>Above this many shares in one minute the value is a sentinel, not a trade.</summary>
    public const decimal MaxSaneVolume = 1_000_000_000m;

    private static long _dropped;
    private static long _clamped;
    private static long _volumeFixed;

    /// <summary>Non-positive price -> not a real bar.</summary>
    public static bool HasSanePrice(decimal open, decimal high, decimal low, decimal close)
        => open > 0m && high > 0m && low > 0m && close > 0m;

    public static bool HasSaneVolume(decimal volume)
        => volume >= 0m && volume <= MaxSaneVolume;

    /// <summary>
    /// Sanitize one candle. Returns false when the bar must be dropped; otherwise
    /// writes the cleaned candle into <paramref name="cleaned"/>.
    /// </summary>
    public static bool TryClean(in CandleRow row, out CandleRow cleaned)
    {
        cleaned = row;
        if (!HasSanePrice(row.Open, row.High, row.Low, row.Close))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        var high = decimal.Max(decimal.Max(row.High, row.Open), decimal.Max(row.Low, row.Close));
        var low = decimal.Min(decimal.Min(row.Low, row.Open), decimal.Min(row.High, row.Close));
        var volume = row.Volume;
        if (!HasSaneVolume(volume))
        {
            volume = 0m;
            Interlocked.Increment(ref _volumeFixed);
        }
        if (high != row.High || low != row.Low)
        {
            Interlocked.Increment(ref _clamped);
            cleaned = row with { High = high, Low = low, Volume = volume };
            return true;
        }
        if (volume != row.Volume)
            cleaned = row with { Volume = volume };
        return true;
    }

    /// <summary>
    /// Sanitize a freshly parsed candle list in place, dropping unsalvageable bars.
    /// </summary>
    public static void Clean(List<CandleRow> rows)
    {
        var write = 0;
        for (var read = 0; read < rows.Count; read++)
        {
            if (!TryClean(rows[read], out var cleaned))
                continue;
            rows[write++] = cleaned;
        }
        if (write != rows.Count)
            rows.RemoveRange(write, rows.Count - write);
    }

    /// <summary>One-line summary of everything repaired in this process.</summary>
    public static string Stats()
        => $"sanitizer: dropped={_dropped} clamped={_clamped} volume_fixed={_volumeFixed}";

    public static (long Dropped, long Clamped, long VolumeFixed) Counters
        => (Interlocked.Read(ref _dropped), Interlocked.Read(ref _clamped),
            Interlocked.Read(ref _volumeFixed));

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _clamped, 0);
        Interlocked.Exchange(ref _volumeFixed, 0);
    }
}
