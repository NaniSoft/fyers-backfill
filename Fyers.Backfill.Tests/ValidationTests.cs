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
            // Fingerprint a FILE, not the directory itself: WriteIsin creates
            // cache/ + state.json inside dir, which bumps a directory's mtime
            // and would flake the IsCurrent assert (~1 in 3 on NTFS).
            var watch = Path.Combine(dir, "watch.txt");
            File.WriteAllText(watch, "x");
            var cache = new AggregationCache(dir);
            var src = IsinSource.Of("INE000TEST000", watch);
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

public sealed class Eod2ReferenceTests
{
    private static string TempEod2()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-eod2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "daily"));
        return dir;
    }

    [Fact]
    public void Maps_stems_preferring_the_eq_series_row()
    {
        var dir = TempEod2();
        try
        {
            File.WriteAllText(Path.Combine(dir, "isin.csv"),
                "ISIN,SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES\n" +
                "INE000A00001,SBIN,EQ,1,1,1,1,1,1,1,1,22-JUN-2011,1,\n" +
                "INE000A00001,SBIN,BE,1,1,1,1,1,1,1,1,22-JUN-2011,1,\n");
            File.WriteAllText(Path.Combine(dir, "meta.json"),
                """{"lastUpdate": "2026-10-02T00:00:00+05:30"}""");

            var reference = new Eod2Reference(dir);

            Assert.Equal("INE000A00001", reference.StemToIsin["sbin"]);
            Assert.Equal(new DateTime(2026, 10, 1, 18, 30, 0, DateTimeKind.Utc),
                reference.LastUpdateUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parses_daily_rows_and_skips_garbage_lines()
    {
        var dir = TempEod2();
        try
        {
            File.WriteAllText(Path.Combine(dir, "daily", "sbin.csv"),
                "Date,Open,High,Low,Close,Volume,Series,TOTAL_TRADES,QTY_PER_TRADE,DLV_QTY\n" +
                "2026-09-28,810.0,815.5,808.2,812.3,4500000,EQ,,,\n" +
                "not-a-date,1,2,3,4,5,EQ,,,\n" +
                "2026-09-29,811.0,816.0,809.0,815.0,0,BE,,,\n");
            File.WriteAllText(Path.Combine(dir, "isin.csv"),
                "ISIN,SYMBOL,SERIES\nINE000A00001,SBIN,EQ\n");

            var days = new Eod2Reference(dir).Days("sbin");

            Assert.Equal(2, days.Count);
            Assert.Equal(new DateOnly(2026, 9, 28), days[0].Date);
            Assert.Equal(810.0, days[0].Open);
            Assert.Equal(4_500_000, days[0].Volume);
            Assert.Equal("EQ", days[0].Series);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_files_are_tolerated()
    {
        var dir = TempEod2();
        try
        {
            var reference = new Eod2Reference(dir);
            Assert.Empty(reference.StemToIsin);
            Assert.Empty(reference.Stems);
            Assert.Empty(reference.Days("absent"));
            Assert.Null(reference.LastUpdateUtc);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class ValidationRulesTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);

    private static DailyBar Ours(double o, double h, double l, double c, long v) =>
        new(Day, o, h, l, c, v, 375, "09:15", "15:29");

    private static ReferenceDay Ref(double o, double h, double l, double c, double v) =>
        new(Day, o, h, l, c, v, "EQ");

    [Fact]
    public void Matching_day_produces_no_anomaly()
    {
        var anomalies = ValidationRules.CompareDay("I1", Day,
            Ours(810.0, 815.5, 808.2, 812.3, 4_470_000), Ref(810.0, 815.5, 808.2, 812.0, 4_500_000));
        Assert.Empty(anomalies);   // close 812.3 vs 812.0 = 0.037%, volume −0.67%
    }

    [Fact]
    public void Tick_rounding_applies_to_open_high_low()
    {
        // ours 810.005 rounds to 810.00/810.01 — the tick test treats 810.0 vs 810.004 as equal
        Assert.True(ValidationRules.SameTick(810.004, 810.0));
        Assert.False(ValidationRules.SameTick(810.004, 810.01));
    }

    [Fact]
    public void One_tick_off_high_is_an_anomaly()
    {
        var anomalies = ValidationRules.CompareDay("I1", Day,
            Ours(810.0, 815.51, 808.2, 812.3, 4_500_000), Ref(810.0, 815.5, 808.2, 812.0, 4_500_000));
        var a = Assert.Single(anomalies);
        Assert.Equal("ohlc_high", a.Kind);
        Assert.Equal(815.51, a.Ours);
        Assert.Equal(815.5, a.Reference);
    }

    [Fact]
    public void Close_exactly_at_tolerance_passes_above_fails()
    {
        // 0.5% of 800 = 4.0 → close 804.0 passes, 804.01 fails
        Assert.Empty(ValidationRules.CompareDay("I1", Day,
            Ours(800, 801, 799, 804.0, 100), Ref(800, 801, 799, 800, 100)));
        Assert.Equal("close", Assert.Single(ValidationRules.CompareDay("I1", Day,
            Ours(800, 801, 799, 804.01, 100), Ref(800, 801, 799, 800, 100))).Kind);
    }

    [Fact]
    public void Volume_exactly_at_tolerance_passes_above_fails()
    {
        // 1% of 1000 = 10 → 990 passes, 989 fails
        Assert.Empty(ValidationRules.CompareDay("I1", Day,
            Ours(800, 801, 799, 800, 990), Ref(800, 801, 799, 800, 1_000)));
        Assert.Equal("volume", Assert.Single(ValidationRules.CompareDay("I1", Day,
            Ours(800, 801, 799, 800, 989), Ref(800, 801, 799, 800, 1_000))).Kind);
    }

    [Fact]
    public void All_four_prices_can_fail_at_once()
    {
        var anomalies = ValidationRules.CompareDay("I1", Day,
            Ours(10, 20, 5, 15, 1_000), Ref(11, 21, 6, 16, 2_000));
        Assert.Equal(["ohlc_open", "ohlc_high", "ohlc_low", "close", "volume"],
            anomalies.Select(a => a.Kind).ToArray());
    }
}
