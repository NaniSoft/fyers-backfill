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

    [Fact]
    public void Co_traded_dates_take_the_eq_files_bar()
    {
        var day = new DateOnly(2026, 9, 28);
        var merged = DailyAggregator.MergeFiles(new[]
        {
            new[] { new DailyBar(day, 100, 101, 99, 100.5, 1_000, 375, "09:15", "15:29") },
            new[] { new DailyBar(day, 100.6, 102, 100, 101.0, 500, 60, "09:15", "15:29") },
            new[] { new DailyBar(day.AddDays(1), 200, 201, 199, 200.5, 2_000, 300, "09:15", "15:29") },
        });

        Assert.Equal(2, merged.Count);
        Assert.Equal(100, merged[0].Open);       // the EQ file's bar wins the co-traded date
        Assert.Equal(100.5, merged[0].Close);
        Assert.Equal(1_000, merged[0].Volume);   // NOT the sum — the BE row duplicates the same session
        Assert.Equal(375, merged[0].Bars);
        Assert.Equal(200.5, merged[1].Close);    // a date only the BE file carries passes through
        Assert.Equal(2_000, merged[1].Volume);
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
            cache.WriteIsin(src, Bars());

            Assert.Equal(Bars(), cache.ReadIsin(src));
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
            cache.WriteIsin(IsinSource.Of("INE000TEST000", file), Bars());

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
            Assert.Empty(cache.ReadIsin(Source(Path.Combine(dir, "absent.parquet"))));
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

public sealed class KnownIssuesTests
{
    private static Anomaly MissingDay(string isin, string date) => new(
        "missing_day", isin, DateOnly.Parse(date), null, null, "");

    [Fact]
    public void Missing_file_loads_empty()
    {
        Assert.Equal(0, KnownIssues.Load(Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString("N")).Replace('-', 'a')).Count);
    }

    [Fact]
    public void Accept_then_contains_roundtrips_through_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "fb-ki-" + Guid.NewGuid().ToString("N")[..8] + ".csv");
        try
        {
            var issues = new KnownIssues();
            issues.Accept([MissingDay("INE1", "2020-01-27"),
                           new Anomaly("split_factor", "INE2", null, 9.97, 1.0, "ratio")]);
            issues.Save(path);

            var reloaded = KnownIssues.Load(path);
            Assert.Equal(2, reloaded.Count);
            Assert.True(reloaded.Contains(MissingDay("INE1", "2020-01-27")));
            Assert.True(reloaded.Contains(new Anomaly("split_factor", "INE2", null, 0, 0, "")));
            Assert.False(reloaded.Contains(MissingDay("INE1", "2020-01-28")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Accept_deduplicates()
    {
        var issues = new KnownIssues();
        issues.Accept([MissingDay("INE1", "2020-01-27")]);
        issues.Accept([MissingDay("INE1", "2020-01-27")]);
        Assert.Equal(1, issues.Count);
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

public sealed class ValidationEngineTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);

    private static DailyBar Bar(DateOnly date, double c, long v = 1_000) =>
        new(date, c, c + 1, c - 1, c, v, 375, "09:15", "15:29");

    private static Eod2Reference RefWith(
        Dictionary<string, string> stemToIsin,
        Dictionary<string, List<ReferenceDay>> days)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-ve-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "daily"));
        File.WriteAllLines(Path.Combine(dir, "isin.csv"),
            ["ISIN,SYMBOL,SERIES",
             .. stemToIsin.Select(kv => $"{kv.Value},{kv.Key.ToUpperInvariant()},EQ")]);
        foreach (var (stem, list) in days)
            File.WriteAllLines(Path.Combine(dir, "daily", stem + ".csv"),
                ["Date,Open,High,Low,Close,Volume,Series",
                 .. list.Select(d => $"{d.Date:yyyy-MM-dd},{d.Open},{d.High},{d.Low},{d.Close},{d.Volume},{d.Series}")]);
        return new Eod2Reference(dir);
    }

    [Fact]
    public void Common_day_that_matches_produces_no_anomaly()
    {
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] = [new ReferenceDay(Day, 100, 101, 99, 100.5, 1_000, "EQ")] });
        var ours = new Dictionary<string, List<DailyBar>>
        {
            ["INE1"] = [new DailyBar(Day, 100, 101, 99, 100.5, 1_000, 375, "09:15", "15:29")],
        };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Empty(result.NewAnomalies);
        Assert.Equal("both", Assert.Single(result.Coverage).Bucket);
    }

    [Fact]
    public void Reference_day_with_volume_we_lack_is_a_missing_day()
    {
        var day2 = new DateOnly(2026, 9, 29);
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] =
            [
                new ReferenceDay(Day, 100, 101, 99, 100, 1_000, "EQ"),
                new ReferenceDay(day2, 100, 101, 99, 100, 5_000, "EQ"),
            ] });
        var ours = new Dictionary<string, List<DailyBar>> { ["INE1"] = [Bar(Day, 100)] };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        var missing = Assert.Single(result.NewAnomalies);
        Assert.Equal("missing_day", missing.Kind);
        Assert.Equal(day2, missing.Date);
    }

    [Fact]
    public void Reference_day_before_floor_or_untraded_is_not_missing()
    {
        var floorDay = new DateOnly(2017, 7, 1);            // before floor
        var zeroVol = new DateOnly(2026, 9, 29);            // no trade
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] =
            [
                new ReferenceDay(floorDay, 100, 101, 99, 100, 9_999, "EQ"),
                new ReferenceDay(zeroVol, 100, 101, 99, 100, 0, "EQ"),
            ] });
        var ours = new Dictionary<string, List<DailyBar>> { ["INE1"] = [] };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Empty(result.NewAnomalies);
    }

    [Fact]
    public void Isin_only_in_reference_is_a_missing_isin()
    {
        var reference = RefWith(
            new() { ["tcs"] = "INE1", ["sbin"] = "INE2" },
            new() { ["tcs"] = [new ReferenceDay(Day, 1, 2, 0.5, 1.5, 500, "EQ")],
                    ["sbin"] = [new ReferenceDay(Day, 1, 2, 0.5, 1.5, 500, "EQ")] });
        var ours = new Dictionary<string, List<DailyBar>> { ["INE2"] = [Bar(Day, 1)] };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        // Brief's literal text compared .Isin against "missing_isin" (the Kind);
        // the ISIN only in the reference here is INE1.
        Assert.Equal("INE1",
            Assert.Single(result.NewAnomalies, a => a.Kind == "missing_isin").Isin);
        Assert.Equal("eod2_only", result.Coverage.Single(c => c.Isin == "INE1").Bucket);
        Assert.Equal("both", result.Coverage.Single(c => c.Isin == "INE2").Bucket);
    }

    [Fact]
    public void Baselined_missing_isin_does_not_refire()
    {
        var reference = RefWith(
            new() { ["tcs"] = "INE1" },
            new() { ["tcs"] = [new ReferenceDay(Day, 1, 2, 0.5, 1.5, 500, "EQ")] });
        var ours = new Dictionary<string, List<DailyBar>>();

        var known = new KnownIssues();
        known.Accept([new Anomaly("missing_isin", "INE1", null, null, 1, "")]);

        var result = ValidationEngine.Validate(ours, reference, known,
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Empty(result.NewAnomalies);      // was 1 before the fix
        Assert.Equal(1, result.BaselineCount);
        Assert.Equal("eod2_only", Assert.Single(result.Coverage).Bucket);
    }

    [Fact]
    public void Baselined_anomalies_are_subtracted_and_counted()
    {
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] = [new ReferenceDay(Day, 100, 101, 99, 100, 5_000, "EQ")] });
        var ours = new Dictionary<string, List<DailyBar>>
        {
            ["INE1"] = [new DailyBar(Day, 100, 101, 99, 100, 900, 375, "09:15", "15:29")],  // −10% volume
        };
        var known = new KnownIssues();
        known.Accept([new Anomaly("volume", "INE1", Day, 900, 5_000, "")]);

        var result = ValidationEngine.Validate(ours, reference, known,
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Empty(result.NewAnomalies);
        Assert.Equal(1, result.BaselineCount);
    }

    [Fact]
    public void Constant_price_ratio_beyond_the_band_is_tagged_split_factor()
    {
        // 35 common days at exactly half price → median ratio 0.5 → split_factor,
        // and its OHLC differences must NOT appear as anomalies.
        var days = Enumerable.Range(0, 35)
            .Select(i => new DateOnly(2026, 8, 1).AddDays(i))
            .ToList();
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] = days.Select(d => new ReferenceDay(d, 200, 202, 198, 200, 1_000, "EQ")).ToList() });
        var ours = new Dictionary<string, List<DailyBar>>
        {
            ["INE1"] = days.Select(d => new DailyBar(d, 100, 101, 99, 100, 1_000, 375, "09:15", "15:29")).ToList(),
        };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Equal("split_factor", Assert.Single(result.Coverage).Tag);
        Assert.Empty(result.NewAnomalies);
    }

    [Fact]
    public void Ours_day_absent_in_reference_is_an_extra_day()
    {
        var day2 = new DateOnly(2026, 9, 29);
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] = [new ReferenceDay(Day, 100, 101, 99, 100, 1_000, "EQ")] });
        var ours = new Dictionary<string, List<DailyBar>>
        {
            ["INE1"] = [Bar(Day, 100), Bar(day2, 100)],   // day2 only on our side
        };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Empty(result.NewAnomalies);                 // informational only
        Assert.Equal(1, result.ExtraDayCount);
    }

    [Fact]
    public void Duplicate_reference_dates_keep_the_eq_row()
    {
        // A stem can carry both an EQ and a BE row for the same date; EQ wins.
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] =
            [
                new ReferenceDay(Day, 100, 101, 99, 100, 1_000, "BE"),
                new ReferenceDay(Day, 200, 201, 199, 200, 2_000, "EQ"),
            ] });
        var ours = new Dictionary<string, List<DailyBar>>
        {
            ["INE1"] = [new DailyBar(Day, 200, 201, 199, 200, 2_000, 375, "09:15", "15:29")],
        };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        Assert.Empty(result.NewAnomalies);   // matches the EQ row, not the BE row
    }

    [Fact]
    public void Missing_day_with_duplicate_series_rows_counts_once()
    {
        var day2 = new DateOnly(2026, 9, 29);
        var reference = RefWith(
            new() { ["sbin"] = "INE1" },
            new() { ["sbin"] =
            [
                new ReferenceDay(Day, 100, 101, 99, 100, 1_000, "EQ"),
                new ReferenceDay(day2, 100, 101, 99, 100, 5_000, "BE"),
                new ReferenceDay(day2, 100, 101, 99, 100, 6_000, "EQ"),
            ] });
        var ours = new Dictionary<string, List<DailyBar>> { ["INE1"] = [Bar(Day, 100)] };

        var result = ValidationEngine.Validate(ours, reference, new KnownIssues(),
            new ValidationOptions(new DateOnly(2017, 7, 3)));

        var missing = Assert.Single(result.NewAnomalies);          // ONE, not two
        Assert.Equal("missing_day", missing.Kind);
        Assert.Equal(day2, missing.Date);
        Assert.Equal(6_000, missing.Reference);                    // the EQ row's volume
        Assert.Equal(1, result.Coverage.Single().MissingDays);
    }
}

public sealed class ReportWriterTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-rw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_report_and_csvs_and_returns_zero_when_clean()
    {
        var dir = TempDir();
        try
        {
            var result = new ValidationResult(
                [new CoverageRow("INE1", "both", 10, 0, "")], [], 0, 0);

            var code = ReportWriter.Write(dir, new DateOnly(2026, 10, 2), result, null);

            Assert.Equal(0, code);
            Assert.Contains("report-2026-10-02.md", Directory.GetFiles(dir).Select(Path.GetFileName));
            Assert.Contains("anomalies-2026-10-02.csv", Directory.GetFiles(dir).Select(Path.GetFileName));
            Assert.Contains("isin_coverage-2026-10-02.csv", Directory.GetFiles(dir).Select(Path.GetFileName));
            Assert.Contains("both", File.ReadAllText(Path.Combine(dir, "isin_coverage-2026-10-02.csv")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void New_anomalies_land_in_the_csv_and_flip_the_exit_code()
    {
        var dir = TempDir();
        try
        {
            var result = new ValidationResult(
                [new CoverageRow("INE1", "both", 10, 1, "")],
                [new Anomaly("missing_day", "INE1", new DateOnly(2026, 9, 28), null, 5_000, "no bar of ours")],
                2, 3);

            var code = ReportWriter.Write(dir, new DateOnly(2026, 10, 2), result, null);

            Assert.Equal(1, code);
            var csv = File.ReadAllText(Path.Combine(dir, "anomalies-2026-10-02.csv"));
            Assert.Contains("missing_day,INE1,2026-09-28", csv);
            var md = File.ReadAllText(Path.Combine(dir, "report-2026-10-02.md"));
            Assert.Contains("missing_day", md);
            Assert.Contains("2", md);   // baseline count appears in the header
        }
        finally { Directory.Delete(dir, true); }
    }
}
