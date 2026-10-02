# Daily pull-and-validate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add offline `validate` and chained `daily` commands to Fyers.Backfill that compare the ISIN-organised 1-minute dataset (`data/`) against the eod2 bhavcopy reference (`eod2_data/`) and report only new anomalies.

**Architecture:** A new `Fyers.Backfill/Validation/` folder: pure aggregation math, an incremental per-ISIN parquet cache, eod2 CSV loaders, a validation engine (tolerances + coverage buckets + known-issues baseline), a report writer, an organizer that folds pulled `1min/` files into `data/<ISIN>/cash/`, and thin command orchestration in `Program.cs`.

**Tech Stack:** .NET 10, Parquet.Net 6.1.0 (Zstd), xunit — no new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-02-daily-validation-design.md`

## Global Constraints

- Validation window floor: **2017-07-03** (`BackfillConfig.DefaultFrom` — reuse it).
- IST day key = `(ts_utc + 19800) / 86400` days since epoch (integer division).
- Tolerances: open/high/low equal after rounding to the 0.01 tick; close within **0.5%** of reference; volume within **1%** of reference.
- Missing day = reference row with **volume > 0**, date ≥ floor, no bar of ours that day.
- Coverage buckets: `both`, `eod2_only`, `ours_only`, `ref_unmapped`; `split_factor` = median close ratio outside **0.8–1.25** over ≥ **30** common days.
- Exit codes: **0** clean/baseline-only · **1** new anomalies · **2** config/auth errors.
- Exit-code-relevant anomaly kinds: `ohlc_open`, `ohlc_high`, `ohlc_low`, `close`, `volume`, `missing_day`, `missing_isin`. Report-only (never fail): `extra_day`, plus buckets `ours_only`/`ref_unmapped`/`split_factor` tags.
- Outputs go under `data/_validation/` (already gitignored via `data/`).
- `validate` must work with no token; `daily` parks with exit 2 when the token is stale/missing.
- `CandleRowDto` (Fyers.Backfill/Parquet/CandleStore.cs) is the minute-bar shape: `ts_utc` long, `ist_minute` string, `open/high/low/close` double, `volume` long.
- Tests run with `dotnet test Fyers.slnx -c Release`; single test files match repo style.
- Everything cash-only: only `<dataset>/<ISIN>/cash/*.parquet` is validated; F&O is out of scope.

---

### Task 1: Prove Parquet.Net reads the pyarrow-written dataset files

**Files:**
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (create)

**Interfaces:**
- Consumes: `CandleStore.ReadAsync(path, ct)` (existing static).
- Produces: confidence (and a permanent local smoke test) that `CandleStore.ReadAsync` can read `data/<ISIN>/cash/*.parquet`, which every later task depends on.

- [ ] **Step 1: Write the interop test**

```csharp
using Fyers.Backfill.Parquet;
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
```

- [ ] **Step 2: Run it against the real dataset**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~CandleStore_reads_a_pyarrow"`
Expected: PASS. **If it fails, STOP — Parquet.Net cannot read these files and the plan's read path must be redesigned (e.g. via Python) before anything else.** This gate exists because the spec names it a live prerequisite.

- [ ] **Step 3: Commit**

```bash
git add Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Prove Parquet.Net reads the pyarrow-written cash files"
```

---

### Task 2: DailyAggregator — minute rows to IST daily bars

**Files:**
- Create: `Fyers.Backfill/Validation/DailyBars.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append)

**Interfaces:**
- Consumes: `CandleRowDto` (existing).
- Produces:
  - `public sealed record DailyBar(DateOnly Date, double Open, double High, double Low, double Close, long Volume, int Bars, string FirstMinute, string LastMinute)` (no Isin inside — the cache/paths key by ISIN).
  - `public static class DailyAggregator { public static List<DailyBar> Aggregate(IEnumerable<CandleRowDto> minutes); public const int IstOffsetSeconds = 19800; }`

- [ ] **Step 1: Write the failing tests** (append to `ValidationTests.cs`, inside a new `public sealed class DailyAggregatorTests`)

```csharp
using Fyers.Backfill.Parquet;
using Fyers.Backfill.Validation;

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
        Assert.Equal(101, day.High);        // the duplicate row's 112 must NOT count
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~DailyAggregatorTests"`
Expected: FAIL — `DailyAggregator`/`DailyBar` do not exist (compile error counts as the failing state).

- [ ] **Step 3: Implement `Fyers.Backfill/Validation/DailyBars.cs`**

```csharp
using Fyers.Backfill.Parquet;

namespace Fyers.Backfill.Validation;

/// <summary>One ISIN's aggregated trading day, derived from 1-minute bars.</summary>
public sealed record DailyBar(
    DateOnly Date, double Open, double High, double Low, double Close,
    long Volume, int Bars, string FirstMinute, string LastMinute);

/// <summary>Minute → daily aggregation. Day boundary is IST (+19800s), matching
/// tools/build_our_eod.py. Duplicate minutes collapse keeping the FIRST row per
/// ts_utc (a re-downloaded minute must not double-count volume or move the
/// day's high/low). CandleStore's own ts_utc merge is LAST-wins — do not rely
/// on parity with it.</summary>
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
}
```

Note: `DateFromDayNumber` converts a shifted day number back to the IST calendar date; the `.AddSeconds(IstOffsetSeconds)` cancels the subtraction so the result is the IST date the offset produced. Verify with the boundary test in Step 4.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~DailyAggregatorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/DailyBars.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Aggregate 1-minute bars to IST daily bars"
```

---

### Task 3: AggregationCache — incremental per-ISIN daily cache

**Files:**
- Create: `Fyers.Backfill/Validation/AggregationCache.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append `AggregationCacheTests`)

**Interfaces:**
- Consumes: `DailyBar`, `DailyAggregator`, `CandleStore.ReadAsync`.
- Produces:
  - `public sealed record IsinSource(string Isin, string Path, long Length, DateTime LastWriteUtc)` + `static IsinSource IsinSource.Of(string isin, string path)` (fingerprints from the file).
  - `public sealed class AggregationCache(string validationDir)`:
    - `public bool IsCurrent(IsinSource src)` — true when the stored fingerprint matches.
    - `public List<DailyBar> ReadIsin(string isin)` — cached bars, empty when none.
    - `public void WriteIsin(string isin, IReadOnlyList<DailyBar> bars, IsinSource src)` — writes cache parquet + updates state atomically.
    - `public IReadOnlyDictionary<string, List<IsinSource>> LoadState()` — for resumability checks (internal use).
- Cache layout (spec): `<validationDir>/cache/<ISIN>.parquet`, `<validationDir>/state.json`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Validation;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~AggregationCacheTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement `AggregationCache.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Fyers.Backfill.Parquet;
using Parquet;
using Parquet.Serialization;

namespace Fyers.Backfill.Validation;

/// <summary>A source parquet file's identity: path + length + last-write UTC.</summary>
public sealed record IsinSource(string Isin, string Path, long Length, DateTime LastWriteUtc)
{
    public static IsinSource Of(string isin, string path)
    {
        var fi = new FileInfo(path);
        return new IsinSource(isin, path, fi.Length, fi.LastWriteTimeUtc);
    }
}

/// <summary>state.json on-disk shape (fingerprints only — bars live in parquet).</summary>
internal sealed record CacheStateEntry(string Path, long Length, DateTime LastWriteUtc);
internal sealed record CacheState(Dictionary<string, CacheStateEntry> Isins);

internal sealed record DailyBarDto(
    long DateYmd, double Open, double High, double Low, double Close,
    long Volume, int Bars, string FirstMinute, string LastMinute);

/// <summary>The incremental daily-bar cache (spec §Incremental cache): one parquet
/// per ISIN under <c>cache/</c>, fingerprints in <c>state.json</c>. Re-aggregate
/// only what changed; a killed sweep just finishes next run.</summary>
public sealed class AggregationCache(string validationDir)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private string StatePath => Path.Combine(validationDir, "state.json");
    private string CacheDir => Path.Combine(validationDir, "cache");

    private CacheState? _state;

    public bool IsCurrent(IsinSource src)
    {
        _state ??= LoadState();
        return _state.Isins.TryGetValue(src.Isin, out var e)
               && e.Path == src.Path && e.Length == src.Length && e.LastWriteUtc == src.LastWriteUtc;
    }

    public List<DailyBar> ReadIsin(string isin)
    {
        var path = Path.Combine(CacheDir, isin + ".parquet");
        if (!File.Exists(path)) return [];
        // Read as DailyBarDto — the cache parquet has this schema, NOT CandleRowDto's.
        using var fs = File.OpenRead(path);
        var result = ParquetSerializer.DeserializeAsync<DailyBarDto>(fs, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        return result.Data.Select(ToBar).ToList();
    }

    public void WriteIsin(string isin, IReadOnlyList<DailyBar> bars, IsinSource src)
    {
        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, isin + ".parquet");
        var rows = bars.Select(ToDto).ToList();
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            ParquetSerializer.SerializeAsync(rows, fs, null!, null!, CancellationToken.None)
                .GetAwaiter().GetResult();
        File.Move(tmp, path, overwrite: true);

        _state ??= LoadState();
        _state.Isins[isin] = new CacheStateEntry(src.Path, src.Length, src.LastWriteUtc);
        var tmpState = StatePath + ".tmp";
        File.WriteAllText(tmpState, JsonSerializer.Serialize(_state, JsonOpts));
        File.Move(tmpState, StatePath, overwrite: true);
    }

    private CacheState LoadState() =>
        File.Exists(StatePath)
            ? JsonSerializer.Deserialize<CacheState>(File.ReadAllText(StatePath)) ?? new(new())
            : new(new Dictionary<string, CacheStateEntry>());

    internal static DailyBar ToBar(DailyBarDto dto) => new(
        new DateOnly((int)(dto.DateYmd / 10_000), (int)(dto.DateYmd / 100 % 100), (int)(dto.DateYmd % 100)),
        dto.Open, dto.High, dto.Low, dto.Close, dto.Volume, dto.Bars, dto.FirstMinute, dto.LastMinute);

    internal static DailyBarDto ToDto(DailyBar b) =>
        new(b.Date.Year * 10_000 + b.Date.Month * 100 + b.Date.Day,
            b.Open, b.High, b.Low, b.Close, b.Volume, b.Bars, b.FirstMinute, b.LastMinute);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~AggregationCacheTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/AggregationCache.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Add the incremental per-ISIN daily-bar cache"
```

---

### Task 4: Eod2Reference — ISIN map and daily CSVs

**Files:**
- Create: `Fyers.Backfill/Validation/Eod2Reference.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append `Eod2ReferenceTests`)

**Interfaces:**
- Consumes: nothing (file IO only).
- Produces:
  - `public sealed record ReferenceDay(DateOnly Date, double Open, double High, double Low, double Close, double Volume, string Series)`.
  - `public sealed class Eod2Reference(string eod2Dir)`:
    - `IReadOnlyDictionary<string, string> StemToIsin` — lowercase stem → ISIN; when several series rows share an ISIN, `EQ` wins.
    - `IReadOnlyList<string> Stems` — every `daily/*.csv` stem (lowercase, no extension).
    - `List<ReferenceDay> Days(string stem)` — parsed rows of `daily/<stem>.csv`, unparseable lines skipped.
    - `DateTime? LastUpdateUtc` — from `meta.json` `lastUpdate`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Validation;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~Eod2ReferenceTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement `Eod2Reference.cs`**

```csharp
using System.Globalization;
using System.Text.Json;

namespace Fyers.Backfill.Validation;

/// <summary>One row of an eod2 daily CSV (Date,Open,High,Low,Close,Volume,Series,…).</summary>
public sealed record ReferenceDay(
    DateOnly Date, double Open, double High, double Low, double Close, double Volume, string Series);

/// <summary>The eod2 reference (spec §Join): <c>daily/&lt;stem&gt;.csv</c> bhavcopy
/// history per symbol, <c>isin.csv</c> for stem→ISIN, <c>meta.json</c> for
/// freshness. Files are read lazily per stem — the full set is ~449 MB.</summary>
public sealed class Eod2Reference(string eod2Dir)
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _stemToIsin = new(LoadIsinMap);
    private readonly Lazy<IReadOnlyList<string>> _stems = new(LoadStems);

    public IReadOnlyDictionary<string, string> StemToIsin => _stemToIsin.Value;
    public IReadOnlyList<string> Stems => _stems.Value;

    public DateTime? LastUpdateUtc
    {
        get
        {
            var path = Path.Combine(eod2Dir, "meta.json");
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty("lastUpdate", out var el)
                       && el.ValueKind == JsonValueKind.String
                       && el.GetString() is { } raw
                       && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var dt)
                    ? dt.ToUniversalTime()
                    : null;
            }
            catch (JsonException) { return null; }
        }
    }

    public List<ReferenceDay> Days(string stem)
    {
        var path = Path.Combine(eod2Dir, "daily", stem + ".csv");
        if (!File.Exists(path)) return [];

        var days = new List<ReferenceDay>(4_000);
        foreach (var line in File.ReadLines(path).Skip(1))       // header
        {
            var parts = line.Split(',');
            if (parts.Length < 7) continue;
            if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) continue;
            if (!TryD(parts[1], out var o) || !TryD(parts[2], out var h)
                || !TryD(parts[3], out var l) || !TryD(parts[4], out var c)
                || !TryD(parts[5], out var v)) continue;
            days.Add(new ReferenceDay(date, o, h, l, c, v, parts[6]));
        }
        return days;
    }

    private Dictionary<string, string> LoadIsinMap()
    {
        var path = Path.Combine(eod2Dir, "isin.csv");
        var map = new Dictionary<string, string>(8_000);
        if (!File.Exists(path)) return map;

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            var isin = parts[0].Trim();
            var stem = parts[1].Trim().ToLowerInvariant();
            var series = parts[2].Trim();
            if (isin.Length == 0 || stem.Length == 0) continue;
            // EQ wins over any other series for the same stem.
            if (series == "EQ" || !map.ContainsKey(stem))
                map[stem] = isin;
        }
        return map;
    }

    private IReadOnlyList<string> LoadStems() =>
        Directory.Exists(Path.Combine(eod2Dir, "daily"))
            ? Directory.GetFiles(Path.Combine(eod2Dir, "daily"), "*.csv")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!.ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList()
            : [];

    private static bool TryD(string raw, out double value) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
```

Note: `EQ` wins because a stem's EQ row must overwrite any earlier BE row, while a non-EQ row only fills a gap.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~Eod2ReferenceTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/Eod2Reference.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Load the eod2 ISIN map and daily bhavcopy reference"
```

---

### Task 5: ValidationRules — pure per-day comparison

**Files:**
- Create: `Fyers.Backfill/Validation/ValidationRules.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append `ValidationRulesTests`)

**Interfaces:**
- Consumes: `DailyBar`, `ReferenceDay`.
- Produces:
  - `public sealed record Anomaly(string Kind, string Isin, DateOnly? Date, double? Ours, double? Reference, string Note)`.
  - `public static class ValidationRules`:
    - `public const double CloseTolerance = 0.005; public const double VolumeTolerance = 0.01;`
    - `public static bool SameTick(double a, double b)` — equal after rounding to 0.01.
    - `public static List<Anomaly> CompareDay(string isin, DateOnly date, DailyBar ours, ReferenceDay reference)` — returns ohlc_open/high/low, close, volume anomalies in that order; empty when all pass.
    - `public static bool ReferenceTraded(ReferenceDay day)` — volume > 0.

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Validation;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ValidationRulesTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement `ValidationRules.cs`**

```csharp
namespace Fyers.Backfill.Validation;

/// <summary>One reported difference. Kind is one of: ohlc_open, ohlc_high,
/// ohlc_low, close, volume, missing_day, extra_day, missing_isin.</summary>
public sealed record Anomaly(
    string Kind, string Isin, DateOnly? Date, double? Ours, double? Reference, string Note);

/// <summary>The audit-based tolerances (spec §Tolerances). O/H/L must match to
/// the tick; the close is definitional (last-minute vs official) → 0.5%; the
/// volume runs ~0.2% under → 1%.</summary>
public static class ValidationRules
{
    public const double CloseTolerance = 0.005;
    public const double VolumeTolerance = 0.01;

    /// <summary>Equal after rounding to the 0.01 tick.</summary>
    public static bool SameTick(double a, double b) =>
        Math.Round(a, 2, MidpointRounding.AwayFromZero)
            .Equals(Math.Round(b, 2, MidpointRounding.AwayFromZero));

    public static bool ReferenceTraded(ReferenceDay day) => day.Volume > 0;

    public static List<Anomaly> CompareDay(
        string isin, DateOnly date, DailyBar ours, ReferenceDay reference)
    {
        var anomalies = new List<Anomaly>(5);
        if (!SameTick(ours.Open, reference.Open))
            anomalies.Add(new Anomaly("ohlc_open", isin, date, ours.Open, reference.Open, "open differs"));
        if (!SameTick(ours.High, reference.High))
            anomalies.Add(new Anomaly("ohlc_high", isin, date, ours.High, reference.High, "high differs"));
        if (!SameTick(ours.Low, reference.Low))
            anomalies.Add(new Anomaly("ohlc_low", isin, date, ours.Low, reference.Low, "low differs"));
        if (Math.Abs(ours.Close - reference.Close) > CloseTolerance * Math.Abs(reference.Close))
            anomalies.Add(new Anomaly("close", isin, date, ours.Close, reference.Close,
                FormattableString.Invariant($"close off by {ours.Close / reference.Close - 1:P3}")));
        if (reference.Volume > 0 &&
            Math.Abs(ours.Volume - reference.Volume) > VolumeTolerance * reference.Volume)
            anomalies.Add(new Anomaly("volume", isin, date, ours.Volume, reference.Volume,
                FormattableString.Invariant($"volume off by {ours.Volume / reference.Volume - 1:P3}")));
        return anomalies;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ValidationRulesTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/ValidationRules.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Add tick/0.5%/1% comparison rules"
```

---

### Task 6: KnownIssues — the baseline

**Files:**
- Create: `Fyers.Backfill/Validation/KnownIssues.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append `KnownIssuesTests`)

**Interfaces:**
- Consumes: `Anomaly`.
- Produces: `public sealed class KnownIssues`:
  - `public static KnownIssues Load(string path)` — missing file → empty set.
  - `public bool Contains(Anomaly a)` — true when (kind, isin, date-or-null) is baselined.
  - `public void Accept(IEnumerable<Anomaly> anomalies)` — adds (dedup).
  - `public void Save(string path)` — CSV `kind,isin,date,note`; date empty when null.
  - `public int Count { get; }`

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Validation;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~KnownIssuesTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement `KnownIssues.cs`**

```csharp
using System.Globalization;

namespace Fyers.Backfill.Validation;

/// <summary>The known-issues baseline (spec §Known-issues baseline):
/// <c>kind,isin,date,note</c> CSV. Rows here are accepted defects (the
/// permanent Fyers gaps, split factors, …) and are subtracted from every
/// report. Hand-editable — delete a row to start flagging it again.</summary>
public sealed class KnownIssues
{
    private readonly HashSet<(string Kind, string Isin, DateOnly? Date)> _rows = [];

    public int Count => _rows.Count;

    public static KnownIssues Load(string path)
    {
        var issues = new KnownIssues();
        if (!File.Exists(path)) return issues;

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            DateOnly? date = DateOnly.TryParseExact(parts[2], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            issues._rows.Add((parts[0].Trim(), parts[1].Trim(), date));
        }
        return issues;
    }

    public bool Contains(Anomaly a) => _rows.Contains((a.Kind, a.Isin, a.Date));

    public void Accept(IEnumerable<Anomaly> anomalies)
    {
        foreach (var a in anomalies)
            _rows.Add((a.Kind, a.Isin, a.Date));
    }

    public void Save(string path)
    {
        var lines = new List<string> { "kind,isin,date,note" };
        lines.AddRange(_rows
            .OrderBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Isin, StringComparer.Ordinal)
            .ThenBy(r => r.Date)
            .Select(r => $"{r.Kind},{r.Isin},{r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? \"\"},"));
        File.WriteAllLines(path, lines);
    }
}
```

(The note column is written empty — notes live in the daily report, not the baseline.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~KnownIssuesTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/KnownIssues.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Add the known-issues baseline store"
```

---

### Task 7: ValidationEngine — join, buckets, split-factor, run

**Files:**
- Create: `Fyers.Backfill/Validation/ValidationEngine.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append `ValidationEngineTests`)

**Interfaces:**
- Consumes: `DailyBar`, `ReferenceDay`, `Anomaly`, `ValidationRules`, `Eod2Reference`, `KnownIssues`.
- Produces:
  - `public sealed record CoverageRow(string Isin, string Bucket, int CommonDays, int MissingDays, string Tag)` — Tag is `""` or `"split_factor"`.
  - `public sealed record ValidationResult(IReadOnlyList<CoverageRow> Coverage, IReadOnlyList<Anomaly> NewAnomalies, int BaselineCount, int ExtraDayCount)`.
  - `public sealed record ValidationOptions(DateOnly Floor, int SplitFactorMinDays = 30, double SplitFactorLow = 0.8, double SplitFactorHigh = 1.25)`.
  - `public static class ValidationEngine`:
    - `public static ValidationResult Validate(IReadOnlyDictionary<string, List<DailyBar>> oursByIsin, Eod2Reference reference, KnownIssues known, ValidationOptions options)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Validation;

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

        Assert.Equal("INE1",
            Assert.Single(result.NewAnomalies, a => a.Kind == "missing_isin").Isin);
        Assert.Equal("eod2_only", result.Coverage.Single(c => c.Isin == "INE1").Bucket);
        Assert.Equal("both", result.Coverage.Single(c => c.Isin == "INE2").Bucket);
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ValidationEngineTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement `ValidationEngine.cs`**

```csharp
namespace Fyers.Backfill.Validation;

/// <summary>One row of isin_coverage CSV. Bucket: both | eod2_only | ours_only |
/// ref_unmapped(unused here — the engine sees only mapped stems). Tag: "" or
/// split_factor.</summary>
public sealed record CoverageRow(string Isin, string Bucket, int CommonDays, int MissingDays, string Tag);

public sealed record ValidationResult(
    IReadOnlyList<CoverageRow> Coverage,
    IReadOnlyList<Anomaly> NewAnomalies,
    int BaselineCount,
    int ExtraDayCount,
    IReadOnlyList<string>? RefUnmappedStems = null);

public sealed record ValidationOptions(
    DateOnly Floor, int SplitFactorMinDays = 30,
    double SplitFactorLow = 0.8, double SplitFactorHigh = 1.25);

/// <summary>The comparison pass (spec §Join, §Tolerances, §Known-issues): joins
/// our daily bars to the reference by ISIN, buckets coverage, tags split
/// factors, applies the tolerances, subtracts the baseline.</summary>
public static class ValidationEngine
{
    public static ValidationResult Validate(
        IReadOnlyDictionary<string, List<DailyBar>> oursByIsin,
        Eod2Reference reference,
        KnownIssues known,
        ValidationOptions options)
    {
        // stem → our ISIN. Full day lists are retained only for ISINs we carry
        // (the comparable set); everyone else keeps a traded-day count so the
        // ~449 MB reference is never fully materialised.
        var refByIsin = new Dictionary<string, List<ReferenceDay>>();
        var refTraded = new Dictionary<string, int>();
        var refUnmapped = new List<string>();
        foreach (var stem in reference.Stems)
        {
            if (!reference.StemToIsin.TryGetValue(stem, out var isin))
            {
                refUnmapped.Add(stem);              // ref_unmapped — report-only
                continue;
            }
            var carried = oursByIsin.ContainsKey(isin);
            if (carried)
            {
                if (!refByIsin.TryGetValue(isin, out var list))
                    refByIsin[isin] = list = [];
                list.AddRange(reference.Days(stem));
            }
            else
            {
                var traded = reference.Days(stem).Count(d =>
                    d.Date >= options.Floor && ValidationRules.ReferenceTraded(d));
                if (traded > 0)
                    refTraded[isin] = refTraded.TryGetValue(isin, out var n) ? n + traded : traded;
            }
        }

        var coverage = new List<CoverageRow>();
        var anomalies = new List<Anomaly>();
        var baselineCount = 0;
        var extraDayCount = 0;

        foreach (var isin in oursByIsin.Keys.Concat(refByIsin.Keys).Concat(refTraded.Keys)
                     .Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            var ours = oursByIsin.TryGetValue(isin, out var o) ? o : [];
            var referenceDays = refByIsin.TryGetValue(isin, out var r) ? r : [];

            // EQ wins when a stem carries several series rows for one date
            // (EQ and BE both traded that day); a plain ToDictionary would throw.
            var byDate = new Dictionary<DateOnly, ReferenceDay>();
            foreach (var d in referenceDays)
                if (d.Series == "EQ" || !byDate.ContainsKey(d.Date))
                    byDate[d.Date] = d;

            if (ours.Count == 0 && (referenceDays.Count > 0 || refTraded.TryGetValue(isin, out var tradedOnly)))
            {
                var traded = referenceDays.Count(d => d.Date >= options.Floor && ValidationRules.ReferenceTraded(d));
                if (traded == 0) traded = tradedOnly;
                coverage.Add(new CoverageRow(isin, "eod2_only", 0, traded, ""));
                if (traded > 0)
                    anomalies.Add(new Anomaly("missing_isin", isin, null, null, traded,
                        FormattableString.Invariant($"{traded} traded reference days since floor")));
                continue;
            }
            if (ours.Count > 0 && referenceDays.Count == 0)
            {
                coverage.Add(new CoverageRow(isin, "ours_only", 0, 0, ""));
                continue;
            }

            // split-factor: a constant ratio outside the band on enough common
            // days means a corporate action hit one side only — compare nothing.
            var common = ours.Where(b => byDate.ContainsKey(b.Date)).ToList();
            var tag = MedianRatio(common, byDate, options) is { } ratio
                      && (ratio < options.SplitFactorLow || ratio > options.SplitFactorHigh)
                ? "split_factor" : "";

            var ourDates = ours.Select(b => b.Date).ToHashSet();
            foreach (var bar in ours)
            {
                if (!byDate.TryGetValue(bar.Date, out var refDay))
                {
                    extraDayCount++;                        // informational
                    continue;
                }
                if (tag == "split_factor") continue;
                foreach (var a in ValidationRules.CompareDay(isin, bar.Date, bar, refDay))
                {
                    if (known.Contains(a)) baselineCount++;
                    else anomalies.Add(a);
                }
            }

            var missingDays = referenceDays.Where(d => d.Date >= options.Floor
                                                    && ValidationRules.ReferenceTraded(d)
                                                    && !ourDates.Contains(d.Date)).ToList();
            if (missingDays.Count > 0 && tag != "split_factor")
                foreach (var d in missingDays)
                {
                    var a = new Anomaly("missing_day", isin, d.Date, null, d.Volume, "no bar of ours");
                    if (known.Contains(a)) baselineCount++; else anomalies.Add(a);
                }

            coverage.Add(new CoverageRow(isin, "both", common.Count, missingDays.Count, tag));
        }

        return new ValidationResult(coverage, anomalies, baselineCount, extraDayCount, refUnmapped);
    }

    /// <summary>Median ours/reference close ratio over common days, or null when
    /// there are fewer than SplitFactorMinDays of them.</summary>
    private static double? MedianRatio(
        List<DailyBar> common, Dictionary<DateOnly, ReferenceDay> byDate, ValidationOptions options)
    {
        if (common.Count < options.SplitFactorMinDays) return null;
        var ratios = common.Select(b => b.Close / byDate[b.Date].Close)
            .Where(r => r > 0)
            .OrderBy(r => r)
            .ToList();
        return ratios.Count == 0 ? null : ratios[ratios.Count / 2];
    }
}
```

Note: the `missing` counter and `refUnmapped` list are both carried into the result so the report can show all four coverage buckets.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ValidationEngineTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/ValidationEngine.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Join ours to the reference with buckets, split-factor, baseline"
```

---

### Task 8: ReportWriter — markdown + CSVs + exit code

**Files:**
- Create: `Fyers.Backfill/Validation/ReportWriter.cs`
- Test: `Fyers.Backfill.Tests/ValidationTests.cs` (append `ReportWriterTests`)

**Interfaces:**
- Consumes: `ValidationResult`, `CoverageRow`, `Anomaly`.
- Produces: `public static class ReportWriter`:
  - `public static int Write(string outDir, DateOnly runDate, ValidationResult result, DateTime? referenceLastUpdateUtc)` — writes `report-<date>.md`, `anomalies-<date>.csv`, `isin_coverage-<date>.csv`; returns **1** when `NewAnomalies` non-empty else **0**.

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Validation;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ReportWriterTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement `ReportWriter.cs`**

```csharp
using System.Globalization;
using System.Text;

namespace Fyers.Backfill.Validation;

/// <summary>Renders the dated artefacts under <c>data/_validation/</c>
/// (spec §Reports). Returns the process exit code: 1 when anything new fired,
/// 0 when clean or baseline-only.</summary>
public static class ReportWriter
{
    public static int Write(
        string outDir, DateOnly runDate, ValidationResult result, DateTime? referenceLastUpdateUtc)
    {
        Directory.CreateDirectory(outDir);
        var stamp = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var md = new StringBuilder();
        md.AppendLine($"# Daily validation — {stamp}");
        md.AppendLine();
        md.AppendLine(referenceLastUpdateUtc is { } u
            ? $"Reference last update: {u:yyyy-MM-dd HH:mm} UTC"
            : "Reference last update: unknown");
        md.AppendLine();
        md.AppendLine($"* **new anomalies: {result.NewAnomalies.Count}**" +
                      $" · baseline (accepted) diffs: {result.BaselineCount}" +
                      $" · extra days (ours only): {result.ExtraDayCount}");
        md.AppendLine();
        md.AppendLine("| bucket | ISINs |");
        md.AppendLine("|---|---|");
        foreach (var g in result.Coverage.GroupBy(c => c.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal))
            md.AppendLine($"| {g.Key} | {g.Count():N0} |");
        if (result.RefUnmappedStems is { Count: > 0 } unmapped)
            md.AppendLine($"| ref_unmapped (stems) | {unmapped.Count:N0} |");
        md.AppendLine();
        md.AppendLine("## New anomalies by kind");
        md.AppendLine();
        md.AppendLine("| kind | count | worst offenders |");
        md.AppendLine("|---|---|---|");
        foreach (var g in result.NewAnomalies.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var worst = string.Join("; ", g.GroupBy(a => a.Isin, StringComparer.Ordinal)
                .OrderByDescending(gr => gr.Count()).Take(3).Select(gr => $"{gr.Key} ×{gr.Count()}"));
            md.AppendLine($"| {g.Key} | {g.Count()} | {worst} |");
        }
        var mdPath = Path.Combine(outDir, $"report-{stamp}.md");
        File.WriteAllText(mdPath, md.ToString());

        var anomalyLines = new List<string> { "kind,isin,date,ours,reference,note" };
        anomalyLines.AddRange(result.NewAnomalies
            .OrderBy(a => a.Kind, StringComparer.Ordinal).ThenBy(a => a.Isin, StringComparer.Ordinal).ThenBy(a => a.Date)
            .Select(a => $"{a.Kind},{a.Isin},{a.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? \"\"}," +
                         $"{a.Ours?.ToString(CultureInfo.InvariantCulture) ?? \"\"},{a.Reference?.ToString(CultureInfo.InvariantCulture) ?? \"\"},{a.Note}"));
        File.WriteAllLines(Path.Combine(outDir, $"anomalies-{stamp}.csv"), anomalyLines);

        var coverageLines = new List<string> { "isin,bucket,common_days,missing_days,tag" };
        coverageLines.AddRange(result.Coverage
            .OrderBy(c => c.Isin, StringComparer.Ordinal)
            .Select(c => $"{c.Isin},{c.Bucket},{c.CommonDays},{c.MissingDays},{c.Tag}"));
        File.WriteAllLines(Path.Combine(outDir, $"isin_coverage-{stamp}.csv"), coverageLines);

        return result.NewAnomalies.Count > 0 ? 1 : 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ReportWriterTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/ReportWriter.cs Fyers.Backfill.Tests/ValidationTests.cs
git commit -m "Render the dated validation report and exit code"
```

---

### Task 9: ValidateCommand — scan, aggregate, validate, report

**Files:**
- Create: `Fyers.Backfill/Validation/ValidateCommand.cs`
- Test: `Fyers.Backfill.Tests/ValidateCommandTests.cs` (create)

**Interfaces:**
- Consumes: `IsinSource`, `AggregationCache`, `DailyAggregator`, `CandleStore.ReadAsync`, `Eod2Reference`, `KnownIssues`, `ValidationEngine`, `ValidationOptions`, `ReportWriter`, `BackfillConfig.DefaultFrom`.
- Produces:
  - `public sealed class ValidateCommand(string datasetDir, string eod2Dir, string validationDir, DateOnly floor, bool acceptBaseline)`:
    - `public Task<int> RunAsync(CancellationToken ct)` — exit code 0/1.
  - `public static IReadOnlyList<IsinSource> DiscoverCashSources(string datasetDir)` — every `<dataset>/<ISIN>/cash/*.parquet`, ISIN = directory name.

- [ ] **Step 1: Write the failing integration test**

The test builds a miniature dataset (`data/INE1/cash/…` written through `CandleStore`-style Parquet rows) and a miniature eod2 dir, runs the command, and asserts the report. This exercises discovery → aggregation → cache → validation → report end to end.

```csharp
using Fyers.Backfill.Parquet;
using Fyers.Backfill.Validation;
using Parquet;
using Parquet.Serialization;
using Xunit;

namespace Fyers.Backfill.Tests;

public sealed class ValidateCommandTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fb-vc-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task WriteMinuteFile(string path, (long ts, double o, double h, double l, double c, long v)[] minutes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = minutes.Select(m => new CandleRowDto
        {
            symbol = "NSE:X-EQ", isin = "INE0000000001", instrument_type = "EQ",
            ts_utc = m.ts, ist_minute = "", open = m.o, high = m.h, low = m.l,
            close = m.c, volume = m.v, resolution = "1",
        }).ToList();
        await using var fs = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, fs, null!, null!, CancellationToken.None);
    }

    [Fact]
    public async void End_to_end_on_a_miniature_dataset()
    {
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        try
        {
            // ours: INE0000000001 traded 2026-09-28 09:15 IST (ts 1790567100), close 100.5
            var isinDir = Path.Combine(dataset, "INE0000000001", "cash");
            await WriteMinuteFile(Path.Combine(isinDir, "NSE_X-EQ.parquet"),
                [(1790567100, 100, 101, 99, 100.5, 1_000)]);

            // reference: same day, matching; plus 2026-09-29 with volume we lack
            File.WriteAllLines(Path.Combine(eod2, "isin.csv"),
                ["ISIN,SYMBOL,SERIES", "INE0000000001,X,EQ"]);
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series",
                 "2026-09-28,100,101,99,100.5,1000,EQ",
                 "2026-09-29,100,101,99,100.5,2000,EQ"]);

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);
            var code = await command.RunAsync(CancellationToken.None);

            Assert.Equal(1, code);   // the missing 2026-09-29 is new
            var reportDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Kolkata"));
            var md = File.ReadAllText(Path.Combine(validation,
                $"report-{reportDate:yyyy-MM-dd}.md"));
            Assert.Contains("missing_day", md);
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }

    [Fact]
    public async void Second_run_uses_the_cache_and_stays_stable()
    {
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        try
        {
            var isinDir = Path.Combine(dataset, "INE0000000001", "cash");
            await WriteMinuteFile(Path.Combine(isinDir, "NSE_X-EQ.parquet"),
                [(1790567100, 100, 101, 99, 100.5, 1_000)]);
            File.WriteAllLines(Path.Combine(eod2, "isin.csv"),
                ["ISIN,SYMBOL,SERIES", "INE0000000001,X,EQ"]);
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series", "2026-09-28,100,101,99,100.5,1000,EQ"]);

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);
            Assert.Equal(0, await command.RunAsync(CancellationToken.None));
            Assert.Equal(0, await command.RunAsync(CancellationToken.None));   // cache path

            var cache = Path.Combine(validation, "cache", "INE0000000001.parquet");
            Assert.True(File.Exists(cache));
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ValidateCommandTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement `ValidateCommand.cs`**

```csharp
using Fyers.Backfill.Config;
using Fyers.Backfill.Parquet;

namespace Fyers.Backfill.Validation;

/// <summary>The offline `validate` command (spec §Commands): aggregate ours to
/// daily bars incrementally, compare against eod2, write the dated report.
/// Never touches Fyers — no token involved.</summary>
public sealed class ValidateCommand(
    string datasetDir, string eod2Dir, string validationDir,
    DateOnly floor, bool acceptBaseline)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(datasetDir) || !Directory.Exists(eod2Dir))
            return 2;   // config/path error (spec §Commands exit codes)
        Directory.CreateDirectory(validationDir);   // --accept-baseline writes the baseline before any report

        var reference = new Eod2Reference(eod2Dir);
        var known = KnownIssues.Load(Path.Combine(validationDir, "known_issues.csv"));
        var cache = new AggregationCache(validationDir);

        var oursByIsin = new Dictionary<string, List<DailyBar>>();
        foreach (var src in DiscoverCashSources(datasetDir))
        {
            List<DailyBar> bars;
            if (cache.IsCurrent(src))
            {
                bars = cache.ReadIsin(src.Isin);
            }
            else
            {
                bars = DailyAggregator.Aggregate(await CandleStore.ReadAsync(src.Path, ct));
                cache.WriteIsin(src.Isin, bars, src);
            }
            if (bars.Count > 0) oursByIsin[src.Isin] = bars;
        }

        var result = ValidationEngine.Validate(oursByIsin, reference, known,
            new ValidationOptions(floor));

        if (acceptBaseline && result.NewAnomalies.Count > 0)
        {
            known.Accept(result.NewAnomalies);
            known.Save(Path.Combine(validationDir, "known_issues.csv"));
            return 0;
        }

        return ReportWriter.Write(validationDir, TodayIst(), result, reference.LastUpdateUtc);
    }

    private static DateOnly TodayIst() => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Kolkata"));

    /// <summary>Every cash parquet: <c>&lt;dataset&gt;/&lt;ISIN&gt;/cash/*.parquet</code>,
    /// ISIN taken from the directory name (self-healing — no stale manifest).</summary>
    public static IReadOnlyList<IsinSource> DiscoverCashSources(string datasetDir)
    {
        if (!Directory.Exists(datasetDir)) return [];
        var sources = new List<IsinSource>(3_000);
        foreach (var isinDir in Directory.EnumerateDirectories(datasetDir))
        {
            var isin = Path.GetFileName(isinDir);
            if (isin.StartsWith('_')) continue;                       // _INDEX, _masters, …
            var cash = Path.Combine(isinDir, "cash");
            if (!Directory.Exists(cash)) continue;
            foreach (var f in Directory.EnumerateFiles(cash, "*.parquet"))
                sources.Add(IsinSource.Of(isin, f));
        }
        return sources.OrderBy(s => s.Isin, StringComparer.Ordinal).ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~ValidateCommandTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/ValidateCommand.cs Fyers.Backfill.Tests/ValidateCommandTests.cs
git commit -m "Wire the offline validate command end to end"
```

---

### Task 10: Organizer — fold pulled 1min files into the ISIN dataset

**Files:**
- Create: `Fyers.Backfill/Validation/Organizer.cs`
- Test: `Fyers.Backfill.Tests/ValidateCommandTests.cs` (append `OrganizerTests`)

**Interfaces:**
- Consumes: `CandleStore.ReadAsync`, `eod2_data/isin_symbol_map.json` (`sym2isin`: SYMBOL → ISIN).
- Produces:
  - `public sealed record OrganizeResult(int MappedFiles, int MergedRows, IReadOnlyList<string> LeftInPlace)`.
  - `public sealed class Organizer(string pullDir, string datasetDir, string isinMapPath, string? manifestPath = null)`:
    - `public Task<OrganizeResult> RunAsync(CancellationToken ct)`.
  - ISIN resolution order (spec §Commands step 3): `data/_manifest.csv` symbol→isin first, then `isin_symbol_map.json` sym2isin as fallback.
  - Scope: files named `NSE_*-EQ.parquet` / `NSE_*-BE.parquet` only (cash); F&O names are left in place.

- [ ] **Step 1: Write the failing tests**

```csharp
using Fyers.Backfill.Parquet;
using Fyers.Backfill.Validation;
using Parquet;
using Parquet.Serialization;

public sealed class OrganizerTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fb-org-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task WriteMinuteFile(string path, long ts, double close, long volume, string symbol = "NSE:X-EQ")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = new List<CandleRowDto>
        {
            new() { symbol = symbol, isin = null, instrument_type = "EQ", ts_utc = ts,
                    ist_minute = "", open = close, high = close, low = close, close = close,
                    volume = volume, resolution = "1" },
        };
        await using var fs = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, fs, null!, null!, CancellationToken.None);
    }

    [Fact]
    public async void Maps_via_symbol_map_and_merges_into_the_isin_dir()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var mapPath = Path.Combine(TempDir("map"), "map.json");
        try
        {
            await WriteMinuteFile(Path.Combine(pull, "NSE_X-EQ.parquet"), 1790653500, 100.5, 1_000);
            File.WriteAllText(mapPath, """{"sym2isin": {"X": "INE0000000001"}}""");

            var result = await new Organizer(pull, dataset, mapPath).RunAsync(CancellationToken.None);

            Assert.Equal(1, result.MappedFiles);
            Assert.Empty(result.LeftInPlace);
            var target = Path.Combine(dataset, "INE0000000001", "cash", "NSE_X-EQ.parquet");
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(Path.Combine(pull, "NSE_X-EQ.parquet")));   // consumed
        }
        finally { Directory.Delete(pull, true); Directory.Delete(dataset, true); File.Delete(mapPath); }
    }

    [Fact]
    public async void Existing_target_merges_without_duplicates()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var mapPath = Path.Combine(TempDir("map"), "map.json");
        try
        {
            await WriteMinuteFile(Path.Combine(dataset, "INE0000000001", "cash", "NSE_X-EQ.parquet"),
                1790653500, 100.5, 1_000);
            await WriteMinuteFile(Path.Combine(pull, "NSE_X-EQ.parquet"), 1790653500, 100.5, 1_000);  // same minute
            await WriteMinuteFile(Path.Combine(pull, "NSE_X-EQ.parquet"), 1790653560, 101.0, 500);    // new minute
            File.WriteAllText(mapPath, """{"sym2isin": {"X": "INE0000000001"}}""");

            var result = await new Organizer(pull, dataset, mapPath).RunAsync(CancellationToken.None);

            Assert.Equal(2, result.MergedRows);
            var rows = await CandleStore.ReadAsync(
                Path.Combine(dataset, "INE0000000001", "cash", "NSE_X-EQ.parquet"), CancellationToken.None);
            Assert.Equal(2, rows.Count);   // no duplicate ts_utc
        }
        finally { Directory.Delete(pull, true); Directory.Delete(dataset, true); File.Delete(mapPath); }
    }

    [Fact]
    public async void Unmappable_and_fno_files_stay_in_place()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var mapPath = Path.Combine(TempDir("map"), "map.json");
        try
        {
            await WriteMinuteFile(Path.Combine(pull, "NSE_UNKNOWN-EQ.parquet"), 1, 1, 1);
            await WriteMinuteFile(Path.Combine(pull, "NSE_NIFTY26AUGFUT.parquet"), 1, 1, 1, "NSE:NIFTY26AUGFUT");
            File.WriteAllText(mapPath, """{"sym2isin": {}}""");

            var result = await new Organizer(pull, dataset, mapPath).RunAsync(CancellationToken.None);

            Assert.Equal(0, result.MappedFiles);
            Assert.Equal(2, result.LeftInPlace.Count);
        }
        finally { Directory.Delete(pull, true); Directory.Delete(dataset, true); File.Delete(mapPath); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~OrganizerTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement `Organizer.cs`**

```csharp
using System.Text.Json;
using Fyers.Backfill.Parquet;

namespace Fyers.Backfill.Validation;

public sealed record OrganizeResult(int MappedFiles, int MergedRows, IReadOnlyList<string> LeftInPlace);

/// <summary>The `organize` stage of the daily chain (spec §Commands step 3):
/// fold each fresh <c>1min/NSE_*-EQ.parquet</c> into
/// <c>&lt;dataset&gt;/&lt;ISIN&gt;/cash/</c>, merge-on-write by ts_utc. ISIN from
/// <c>isin_symbol_map.json</c> sym2isin; anything unmappable (and every F&O
/// name) stays in <c>1min/</c> and is reported.</summary>
public sealed class Organizer(string pullDir, string datasetDir, string isinMapPath, string? manifestPath = null)
{
    public async Task<OrganizeResult> RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(pullDir))
            return new OrganizeResult(0, 0, []);

        var sym2isin = LoadManifest();
        foreach (var (sym, isin) in LoadSym2Isin())
            sym2isin.TryAdd(sym, isin);
        var mapped = 0;
        long mergedRows = 0;
        var left = new List<string>();

        foreach (var path in Directory.EnumerateFiles(pullDir, "*.parquet").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);      // NSE_X-EQ
            var symbol = name.StartsWith("NSE_", StringComparison.Ordinal) ? name[4..] : name;
            var isCash = symbol.EndsWith("-EQ", StringComparison.Ordinal)
                         || symbol.EndsWith("-BE", StringComparison.Ordinal);
            var stem = isCash ? symbol[..^3] : null;
            if (stem is null || !sym2isin.TryGetValue(stem.ToUpperInvariant(), out var isin))
            {
                left.Add(name);
                continue;
            }

            var target = Path.Combine(datasetDir, isin, "cash", name + ".parquet");
            var byTs = new Dictionary<long, CandleRowDto>();
            if (File.Exists(target))
                foreach (var r in await CandleStore.ReadAsync(target, ct))
                    byTs[r.ts_utc] = r;
            foreach (var r in await CandleStore.ReadAsync(path, ct))
                byTs[r.ts_utc] = r;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp";
            await using (var fs = File.Create(tmp))
                await global::Parquet.ParquetSerializer.SerializeAsync(
                    byTs.Values.OrderBy(r => r.ts_utc).ToList(), fs, null!, null!, ct);
            File.Move(tmp, target, overwrite: true);
            File.Delete(path);

            mapped++;
            mergedRows += byTs.Count;
        }

        return new OrganizeResult(mapped, (int)mergedRows, left);
    }

    /// <summary>Manifest rows: isin,kind,symbol,path — symbol (e.g. NSE_KRBL-EQ)
    /// to ISIN. The path column is stale (pre-move) and unused.</summary>
    private Dictionary<string, string> LoadManifest()
    {
        var map = new Dictionary<string, string>(4_000);
        if (manifestPath is null || !File.Exists(manifestPath)) return map;
        foreach (var line in File.ReadLines(manifestPath).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            var symbol = parts[2].Trim();                     // NSE_KRBL-EQ
            var stem = symbol.StartsWith("NSE_", StringComparison.Ordinal) ? symbol[4..] : symbol;
            if (stem.EndsWith("-EQ", StringComparison.Ordinal) || stem.EndsWith("-BE", StringComparison.Ordinal))
                stem = stem[..^3];
            if (stem.Length > 0)
                map.TryAdd(stem.ToUpperInvariant(), parts[0].Trim());
        }
        return map;
    }

    private Dictionary<string, string> LoadSym2Isin()
    {
        if (!File.Exists(isinMapPath)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(isinMapPath));
        var map = new Dictionary<string, string>(4_000);
        if (doc.RootElement.TryGetProperty("sym2isin", out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                map[p.Name.Trim().ToUpperInvariant()] = p.Value.GetString() ?? "";
        return map;
    }
}
```

Note: `stem[..^3]` strips `-EQ`/`-BE` (3 chars). Verify with the mapping test.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~OrganizerTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Fyers.Backfill/Validation/Organizer.cs Fyers.Backfill.Tests/ValidateCommandTests.cs
git commit -m "Fold pulled 1min files into the ISIN dataset"
```

---

### Task 11: Program wiring — `validate`, `daily`, new flags

**Files:**
- Modify: `Fyers.Backfill/Program.cs` (command surface + CLI)
- Modify: `Fyers.Backfill/BackfillRunner.cs` only if `RunAsync` needs exposing (it already returns `RunReport` — no change expected)
- Test: `Fyers.Backfill.Tests/ValidateCommandTests.cs` (append CLI tests)

**Interfaces:**
- Consumes: everything above; `TokenFile.AccessToken()`, `BackfillRunner.RunAsync(RunMode, int, CancellationToken)`, `BackfillConfig.Load`.
- Produces: commands `validate` (token-free) and `daily` (token → update → organize → validate); flags `--dataset PATH`, `--eod2 PATH`, `--accept-baseline`; `--from` already exists and sets the validation floor for `validate`.

- [ ] **Step 1: Write the failing CLI tests**

```csharp
using Fyers.Backfill;   // Cli is `internal` — add InternalsVisibleTo if the tests project lacks it

public sealed class CliTests
{
    [Fact]
    public void Parses_validate_flags()
    {
        var cli = Cli.Parse(["validate", "--dataset", "data", "--eod2", "eod2_data", "--accept-baseline"]);
        Assert.Equal("validate", cli.Command);
        Assert.Equal("data", cli.Dataset);
        Assert.Equal("eod2_data", cli.Eod2);
        Assert.True(cli.AcceptBaseline);
    }

    [Fact]
    public void Daily_is_recognised()
    {
        Assert.Equal("daily", Cli.Parse(["daily"]).Command);
    }
}
```

`InternalsVisibleTo Include="Fyers.Backfill.Tests"` already exists in `Fyers.Backfill/Fyers.Backfill.csproj`, so the tests can see the internal `Cli` record directly. Note: `Cli` is declared after the top-level statements in `Program.cs`, i.e. in the **global** namespace — reference it as `Cli.Parse(...)` without a using.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Fyers.Backfill.Tests -c Release --filter "FullyQualifiedName~CliTests"`
Expected: FAIL — `Dataset`/`Eod2`/`AcceptBaseline` missing on `Cli`.

- [ ] **Step 3: Extend `Cli` and `Program.cs`**

In `Program.cs`, add `using Fyers.Backfill.Validation;` to the using block at the top, then extend the `Cli` record:

```csharp
internal sealed record Cli(
    string? Command,
    string? ConfigPath,
    string? EnvPath,
    string? DataDir,
    string? RepoRoot,
    string? Root,
    DateOnly? From,
    DateOnly? To,
    int PilotCount,
    string? Dataset = null,
    string? Eod2 = null,
    bool AcceptBaseline = false)
```

Add parse cases (in `Cli.Parse`, following the existing `case "--config":` pattern):

```csharp
case "--dataset": dataset = Value(); break;
case "--eod2": eod2 = Value(); break;
case "--accept-baseline": acceptBaseline = true; break;
```

Extend `Usage` with:

```
  validate [--accept-baseline]   Compare data/ against eod2_data/ (offline)
  daily                          login-check → update → organize → validate

  --dataset PATH        ISIN dataset (default <repo>/data)
  --eod2 PATH           eod2 reference dir (default <repo>/eod2_data)
  --accept-baseline     write current anomalies to known_issues.csv (first run)
```

Wire the commands in `Program.cs` top-level (before the token gate, next to `status`):

```csharp
if (cli.Command == "validate")
{
    var dataset = Path.Combine(repoRoot, cli.Dataset ?? "data");
    var eod2 = Path.Combine(repoRoot, cli.Eod2 ?? "eod2_data");
    var validationDir = Path.Combine(dataset, "_validation");
    var floor = cli.From ?? cfg.From;
    return await new ValidateCommand(dataset, eod2, validationDir, floor,
        cli.AcceptBaseline).RunAsync(CancellationToken.None);
}
```

and after the token gate (where `runner` exists), add `daily`:

```csharp
if (cli.Command == "daily")
{
    // 1. token freshness is already proven above (tokenFile.AccessToken() != null)
    // 2. pull the newest window
    var report = await runner.RunAsync(RunMode.Update, 0, CancellationToken.None);
    if (report.AuthExpired) return 2;

    // 3. fold fresh 1min files into the ISIN dataset
    var dataset = Path.Combine(repoRoot, cli.Dataset ?? "data");
    var isinMap = Path.Combine(Path.Combine(repoRoot, cli.Eod2 ?? "eod2_data"), "isin_symbol_map.json");
    var organized = await new Organizer(
        Path.Combine(cfg.Root, "1min"), dataset, isinMap,
        Path.Combine(dataset, "_manifest.csv")).RunAsync(CancellationToken.None);
    log.LogInformation("daily: organized {Mapped} files, {Left} left unmapped",
        organized.MappedFiles, organized.LeftInPlace.Count);

    // 4. validate
    var validationDir = Path.Combine(dataset, "_validation");
    return await new ValidateCommand(dataset, Path.Combine(repoRoot, cli.Eod2 ?? "eod2_data"),
        validationDir, cli.From ?? cfg.From, cli.AcceptBaseline).RunAsync(CancellationToken.None);
}
```

Also update the `Commands:` help table comment at the top of `Program.cs` (lines ~19-30) with `validate` and `daily` one-liners.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test Fyers.slnx -c Release`
Expected: PASS — all new suites plus the existing ones.

- [ ] **Step 5: Smoke the real command offline**

Run: `dotnet run --project Fyers.Backfill -c Release -- validate --dataset data --eod2 eod2_data`
Expected: completes (possibly over several minutes on first run — the full 13 GB sweep); exit code 0 with `--accept-baseline` (first baseline), 1 without. Inspect `data/_validation/report-*.md`.

- [ ] **Step 6: Commit**

```bash
git add Fyers.Backfill/Program.cs Fyers.Backfill.Tests/ValidateCommandTests.cs
git commit -m "Add validate and daily commands with --dataset/--eod2/--accept-baseline"
```

---

### Task 12: First real run + README

**Files:**
- Modify: `README.md` (Commands table + a short "Daily validation" paragraph)
- Create: `data/_validation/known_issues.csv` (generated, gitignored — never committed)

**Interfaces:**
- Consumes: everything.

- [ ] **Step 1: Bootstrap the baseline on the real dataset**

Run: `dotnet run --project Fyers.Backfill -c Release -- validate --accept-baseline`
Expected: exit 0; `data/_validation/known_issues.csv` now holds the September audit's known gaps (≈27,713 missing days, split factors, Muhurat extras) — spot-check the count is in the tens of thousands, and `report-*.md` is readable.

- [ ] **Step 2: Re-run without the flag — must be clean**

Run: `dotnet run --project Fyers.Backfill -c Release -- validate`
Expected: exit **0** (baseline-only) and `New anomalies: 0` in the fresh report. If anything new fires, investigate before proceeding — a green baseline is the daily contract.

- [ ] **Step 3: Update README.md**

In the Commands table add:

```
| `validate` | compare data/ against eod2_data/ and report new anomalies (offline) |
| `daily` | token check → update → organize → validate (the daily increment) |
```

In the Options table add `--dataset`, `--eod2`, `--accept-baseline` one-liners. Add a short section after "Data quality":

```markdown
## Daily validation

`validate` aggregates `data/` to daily bars (incrementally cached under
`data/_validation/cache/`) and compares every ISIN against `eod2_data/daily/`:
open/high/low to the tick, close within 0.5% (last-minute close vs official),
volume within 1%. Missing days count only when the reference shows volume.
The first run needs `--accept-baseline` to absorb the known permanent gaps;
after that, `validate` reports only what is new (exit 1 = new anomalies,
0 = clean, 2 = config/auth). `daily` chains the token check, the `update`
pull, the organize-into-`data/` pass, and `validate` in one resumable run.
```

- [ ] **Step 4: Full suite once more**

Run: `dotnet test Fyers.slnx -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "Document the daily validation commands"
```
