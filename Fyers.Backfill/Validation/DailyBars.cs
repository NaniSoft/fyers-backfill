using Fyers.Backfill.Parquet;

namespace Fyers.Backfill.Validation;

/// <summary>One ISIN's aggregated trading day, derived from 1-minute bars.</summary>
public sealed record DailyBar(
    DateOnly Date, double Open, double High, double Low, double Close,
    long Volume, int Bars, string FirstMinute, string LastMinute);

/// <summary>Minute → daily aggregation. Day boundary is IST (+19800s), matching
/// tools/build_our_eod.py. Duplicate minutes collapse keeping the FIRST row per
/// <c>ts_utc</c>, so a re-downloaded minute cannot double-count volume or push
/// the day's high/low around. (CandleStore's own ts_utc merge is LAST-wins, a
/// different rule — do not rely on parity with it.)</summary>
public static class DailyAggregator
{
    public const int IstOffsetSeconds = 19_800;

    public static List<DailyBar> Aggregate(IEnumerable<CandleRowDto> minutes)
    {
        var byDay = new SortedDictionary<int, List<CandleRowDto>>();
        foreach (var m in minutes)
        {
            var day = (int)((m.ts_utc + IstOffsetSeconds) / 86_400);
            if (!byDay.TryGetValue(day, out var list))
                byDay[day] = list = [];
            list.Add(m);
        }

        var bars = new List<DailyBar>(byDay.Count);
        foreach (var (day, list) in byDay)
        {
            var ordered = list
                .GroupBy(m => m.ts_utc)                    // dedup, first wins
                .Select(g => g.First())
                .OrderBy(m => m.ts_utc)
                .ToList();

            var first = ordered[0];
            var last = ordered[^1];
            bars.Add(new DailyBar(
                Date: DateFromDayNumber(day),
                Open: first.open,
                High: ordered.Max(m => m.high),
                Low: ordered.Min(m => m.low),
                Close: last.close,
                Volume: ordered.Sum(m => m.volume),
                Bars: ordered.Count,
                FirstMinute: first.ist_minute,
                LastMinute: last.ist_minute));
        }
        return bars;
    }

    /// <summary>Days since (1970-01-01) shifted by the IST offset → DateOnly.</summary>
    internal static DateOnly DateFromDayNumber(int day) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)day * 86_400 - IstOffsetSeconds)
            .UtcDateTime.AddSeconds(IstOffsetSeconds));

    /// <summary>Merges one ISIN's per-file daily bars into one bar per IST day
    /// (a ticker can trade EQ and BE series the same day): volume and bars sum,
    /// high max, low min. The open comes from the bar with the earliest
    /// <c>FirstMinute</c>, a tie keeping the first file's value (discover orders
    /// the EQ file first); the close comes from the last file in the given order
    /// that traded the day — the fixture's <c>LastMinute</c> values are not
    /// comparable, so "latest last-minute" is expressed as file order. Files are
    /// consumed in the order given, so the result is deterministic.</summary>
    public static List<DailyBar> MergeFiles(IEnumerable<IEnumerable<DailyBar>> files)
    {
        var byDate = new Dictionary<DateOnly, DailyBar>();
        foreach (var file in files)
        {
            foreach (var bar in file)
            {
                if (!byDate.TryGetValue(bar.Date, out var acc))
                {
                    byDate[bar.Date] = bar;
                    continue;
                }
                var open = string.CompareOrdinal(bar.FirstMinute, acc.FirstMinute) < 0 ? bar : acc;
                byDate[bar.Date] = acc with
                {
                    Open = open.Open,
                    FirstMinute = open.FirstMinute,
                    Close = bar.Close,                  // last file wins
                    LastMinute = bar.LastMinute,
                    High = Math.Max(acc.High, bar.High),
                    Low = Math.Min(acc.Low, bar.Low),
                    Volume = acc.Volume + bar.Volume,
                    Bars = acc.Bars + bar.Bars,
                };
            }
        }
        return byDate.Values.OrderBy(b => b.Date).ToList();
    }
}
