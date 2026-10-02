using Fyers.Backfill.Parquet;
using Fyers.Backfill.Validation;
using Xunit;

namespace Fyers.Backfill.Tests;

/// <summary>Local smoke test for the validation pipeline's parquet interop.
/// The cash files were REWRITTEN by pyarrow (tools/compact_parts2.py), not by
/// CandleStore, so Parquet.Net must be proven to read them. No-ops when the
/// dataset is absent so CI stays green.</summary>
public sealed class ValidationInteropTests
{
    public static string? FirstCashFile()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.parquet", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Contains($"{Path.DirectorySeparatorChar}cash{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public async Task CandleStore_reads_a_pyarrow_rewritten_cash_file()
    {
        var path = FirstCashFile();
        if (path is null) return;   // dataset not present (CI) — nothing to prove

        var rows = await CandleStore.ReadAsync(path, CancellationToken.None);

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.ts_utc > 0));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.ist_minute)));
    }
}

public sealed class DailyAggregatorTests
{
    private static CandleRowDto Minute(long tsUtc, double o, double h, double l, double c, long v) => new()
    {
        ts_utc = tsUtc, ist_minute = "", open = o, high = h, low = l, close = c, volume = v,
        symbol = "NSE:X-EQ", instrument_type = "EQ", resolution = "1",
    };

    [Fact]
    public void Splits_days_on_the_ist_boundary()
    {
        // 2026-09-28 09:15 IST = 2026-09-28 03:45 UTC = 1790567100
        // 2026-09-28 15:29 IST = 2026-09-28 09:59 UTC = 1790589540
        // 2026-09-29 09:15 IST = 2026-09-29 03:45 UTC = 1790653500 (crosses UTC midnight)
        var bars = DailyAggregator.Aggregate(new[]
        {
            Minute(1790567100, 100, 101, 99, 100.5, 10),
            Minute(1790589540, 101, 102, 100, 101.5, 20),
            Minute(1790653500, 102, 103, 101, 102.5, 30),
        });

        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateOnly(2026, 9, 28), bars[0].Date);
        Assert.Equal(new DateOnly(2026, 9, 29), bars[1].Date);
    }

    [Fact]
    public void Dedupes_by_ts_utc_keeping_first_and_orders_output()
    {
        var bars = DailyAggregator.Aggregate(new[]
        {
            Minute(1790589540, 90, 95, 89, 91, 5),        // later ts first in input
            Minute(1790567100, 100, 101, 99, 100.5, 10),
            Minute(1790567100, 111, 112, 110, 111, 99),   // duplicate ts — must be ignored
        });

        var day = Assert.Single(bars);
        Assert.Equal(100, day.Open);        // earliest minute's open
        Assert.Equal(91, day.Close);        // latest minute's close — the last-minute rule
        Assert.Equal(101, day.High);        // dup row dropped, so max(101, 95)
        Assert.Equal(89, day.Low);
        Assert.Equal(15, day.Volume);
        Assert.Equal(2, day.Bars);
    }

    [Fact]
    public void Merges_minute_extremes_across_the_day()
    {
        var bars = DailyAggregator.Aggregate(new[]
        {
            Minute(1790567100, 100, 101, 99.5, 100.5, 10),
            Minute(1790567160, 100.6, 105, 99, 100.6, 20),  // day high 105, day low 99 here
        });

        var day = Assert.Single(bars);
        Assert.Equal(105, day.High);
        Assert.Equal(99, day.Low);
        Assert.Equal(30, day.Volume);
    }

    [Fact]
    public void Empty_input_yields_no_bars()
    {
        Assert.Empty(DailyAggregator.Aggregate(Array.Empty<CandleRowDto>()));
    }
}

public sealed class AggregationCacheTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IsinSource Source(string path) => IsinSource.Of("INE000TEST000", path);

    private static List<DailyBar> Bars() =>
    [
        new(new DateOnly(2026, 9, 28), 100, 101, 99, 100.5, 30, 2, "09:15", "15:29"),
        new(new DateOnly(2026, 9, 29), 102, 103, 101, 102.5, 30, 1, "09:15", "09:15"),
    ];

    [Fact]
    public void Roundtrips_bars_through_the_cache()
    {
        var dir = TempDir();
        try
        {
            var cache = new AggregationCache(dir);
            var src = Source(dir);
            cache.WriteIsin("INE000TEST000", Bars(), src);

            Assert.Equal(Bars(), cache.ReadIsin("INE000TEST000"));
            Assert.True(cache.IsCurrent(src));   // same file fingerprint
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Fingerprint_change_marks_cache_stale()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "INE000TEST000.parquet");
            File.WriteAllBytes(file, [1, 2, 3]);
            var cache = new AggregationCache(dir);
            cache.WriteIsin("INE000TEST000", Bars(), IsinSource.Of("INE000TEST000", file));

            Assert.True(cache.IsCurrent(IsinSource.Of("INE000TEST000", file)));

            File.WriteAllBytes(file, [1, 2, 3, 4]);   // any change → stale
            Assert.False(cache.IsCurrent(IsinSource.Of("INE000TEST000", file)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unknown_isin_reads_empty_and_is_not_current()
    {
        var dir = TempDir();
        try
        {
            var cache = new AggregationCache(dir);
            Assert.Empty(cache.ReadIsin("INE000ABSENT000"));
            Assert.False(cache.IsCurrent(Source(dir)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
