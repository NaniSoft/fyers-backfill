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
    public async Task Empty_eod2_reference_refuses_to_report_green()
    {
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");      // exists, but carries no daily/ and no isin.csv
        var validation = TempDir("val");
        try
        {
            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);

            Assert.Equal(2, await command.RunAsync(CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(validation, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }

    [Fact]
    public async Task Missing_input_dir_names_what_is_absent_and_exits_2()
    {
        var dataset = Path.Combine(TempDir("ds"), "absent");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        try
        {
            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);

            Assert.Equal(2, await command.RunAsync(CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(validation, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(eod2, true); Directory.Delete(Path.GetDirectoryName(dataset)!, true); Directory.Delete(validation, true); }
    }

    [Fact]
    public async Task Stale_reference_warns_but_still_runs()
    {
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        try
        {
            File.WriteAllLines(Path.Combine(eod2, "isin.csv"),
                ["ISIN,SYMBOL,SERIES", "INE0000000001,X,EQ"]);
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));   // File.WriteAllLines does not mkdir
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series", "2026-09-28,100,101,99,100.5,1000,EQ"]);
            File.WriteAllText(Path.Combine(eod2, "meta.json"),
                $$"""{"lastUpdate": "{{DateTime.UtcNow.AddDays(-30):o}}"}""");

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);

            // Staleness is a warning only — 2 is reserved for a broken reference.
            Assert.NotEqual(2, await command.RunAsync(CancellationToken.None));
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }

    [Fact]
    public async Task Organized_pull_file_is_folded_into_the_validated_day()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        var mapDir = TempDir("map");
        var mapPath = Path.Combine(mapDir, "map.json");
        try
        {
            // 1790653500 = 2026-09-29 09:15 IST — the session the pull carries.
            await WriteMinuteFile(Path.Combine(pull, "NSE_X-EQ.parquet"),
                [(1790653500, 100.6, 102, 100, 101.0, 500)]);
            File.WriteAllText(mapPath, """{"sym2isin": {"X": "INE0000000001"}}""");

            Assert.Equal(1, (await new Organizer(pull, dataset, mapPath)
                .RunAsync(CancellationToken.None)).MappedFiles);
            Assert.True(File.Exists(Path.Combine(dataset, "INE0000000001", "cash", "NSE_X-EQ.parquet")));
            Assert.False(File.Exists(Path.Combine(pull, "NSE_X-EQ.parquet")));    // consumed

            File.WriteAllLines(Path.Combine(eod2, "isin.csv"),
                ["ISIN,SYMBOL,SERIES", "INE0000000001,X,EQ"]);
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));   // File.WriteAllLines does not mkdir
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series", "2026-09-29,100.6,102,100,101,500,EQ"]);

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);

            Assert.Equal(0, await command.RunAsync(CancellationToken.None));  // the folded day matches
        }
        finally
        {
            Directory.Delete(pull, true); Directory.Delete(dataset, true);
            Directory.Delete(eod2, true); Directory.Delete(validation, true); Directory.Delete(mapDir, true);
        }
    }

    [Fact]
    public async Task End_to_end_on_a_miniature_dataset()
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
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));   // File.WriteAllLines does not mkdir
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
    public async Task Second_run_uses_the_cache_and_stays_stable()
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
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));   // File.WriteAllLines does not mkdir
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series", "2026-09-28,100,101,99,100.5,1000,EQ"]);

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);
            Assert.Equal(0, await command.RunAsync(CancellationToken.None));
            Assert.Equal(0, await command.RunAsync(CancellationToken.None));   // cache path

            var cache = Path.Combine(validation, "cache", "INE0000000001~NSE_X-EQ.parquet");
            Assert.True(File.Exists(cache));
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }

    [Fact]
    public async Task Two_ticker_files_merge_into_one_isin_day()
    {
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        try
        {
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));
            var isinDir = Path.Combine(dataset, "INE0000000001", "cash");
            await WriteMinuteFile(Path.Combine(isinDir, "NSE_X-EQ.parquet"),
                [(1790567100, 100, 101, 99, 100.5, 1_000)]);      // 09:15 IST
            await WriteMinuteFile(Path.Combine(isinDir, "NSE_X-BE.parquet"),
                [(1790589540, 100.6, 102, 100, 101.0, 500)]);     // 15:29 IST
            File.WriteAllLines(Path.Combine(eod2, "isin.csv"),
                ["ISIN,SYMBOL,SERIES", "INE0000000001,X,EQ"]);
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series",
                 "2026-09-28,100,101,99,100.5,1000,EQ"]);

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);

            Assert.Equal(0, await command.RunAsync(CancellationToken.None));  // the EQ bar wins the co-traded day

            var state = File.ReadAllText(Path.Combine(validation, "state.json"));
            Assert.Contains("INE0000000001::NSE_X-EQ.parquet", state);
            Assert.Contains("INE0000000001::NSE_X-BE.parquet", state);
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }

    [Fact]
    public async Task Second_file_added_later_is_picked_up_without_losing_the_first()
    {
        var dataset = TempDir("ds");
        var eod2 = TempDir("eod");
        var validation = TempDir("val");
        try
        {
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));
            var isinDir = Path.Combine(dataset, "INE0000000001", "cash");
            await WriteMinuteFile(Path.Combine(isinDir, "NSE_X-EQ.parquet"),
                [(1790567100, 100, 101, 99, 100.5, 1_000)]);
            File.WriteAllLines(Path.Combine(eod2, "isin.csv"),
                ["ISIN,SYMBOL,SERIES", "INE0000000001,X,EQ"]);
            File.WriteAllLines(Path.Combine(eod2, "daily", "x.csv"),
                ["Date,Open,High,Low,Close,Volume,Series",
                 "2026-09-28,100,101,99,100.5,1000,EQ",
                 "2026-09-29,100.6,102,100,101,500,BE"]);

            var command = new ValidateCommand(dataset, eod2, validation,
                new DateOnly(2017, 7, 3), acceptBaseline: false);
            Assert.Equal(1, await command.RunAsync(CancellationToken.None));  // BE volume missing → missing_day

            await WriteMinuteFile(Path.Combine(isinDir, "NSE_X-BE.parquet"),
                [(1790653500, 100.6, 102, 100, 101.0, 500)]);     // 2026-09-29, arrives later
            Assert.Equal(0, await command.RunAsync(CancellationToken.None));  // now complete
        }
        finally { Directory.Delete(dataset, true); Directory.Delete(eod2, true); Directory.Delete(validation, true); }
    }
}

/// <summary>Program-wiring checks for the `validate` / `daily` command surface.
/// <see cref="Cli"/> lives in the global namespace (declared after Program.cs's
/// top-level statements) and is visible here via InternalsVisibleTo.</summary>
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
    public void Flags_default_when_omitted()
    {
        var cli = Cli.Parse(["validate"]);
        Assert.Equal("validate", cli.Command);
        Assert.Null(cli.Dataset);
        Assert.Null(cli.Eod2);
        Assert.False(cli.AcceptBaseline);
    }

    [Fact]
    public void Daily_is_recognised()
    {
        Assert.Equal("daily", Cli.Parse(["daily"]).Command);
    }
}

public sealed class OrganizerTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fb-org-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task WriteMinuteFile(string path, (long ts, double close, long volume)[] minutes,
        string symbol = "NSE:X-EQ")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = minutes.Select(m => new CandleRowDto
        {
            symbol = symbol, isin = null, instrument_type = "EQ", ts_utc = m.ts,
            ist_minute = "", open = m.close, high = m.close, low = m.close,
            close = m.close, volume = m.volume, resolution = "1",
        }).ToList();
        await using var fs = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, fs, null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task Maps_via_symbol_map_and_merges_into_the_isin_dir()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var mapPath = Path.Combine(TempDir("map"), "map.json");
        try
        {
            await WriteMinuteFile(Path.Combine(pull, "NSE_X-EQ.parquet"), [(1790653500, 100.5, 1_000)]);
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
    public async Task Existing_target_merges_without_duplicates()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var mapPath = Path.Combine(TempDir("map"), "map.json");
        try
        {
            // Seed the target with BOTH the colliding minute (at a distinct close,
            // so last-write-wins is visible) and a minute the pull does NOT carry
            // (so replacing instead of merging is visible). One call — a second
            // WriteMinuteFile on the same path would truncate the first.
            await WriteMinuteFile(Path.Combine(dataset, "INE0000000001", "cash", "NSE_X-EQ.parquet"),
                [(1790653500, 42.0, 1_000), (1790653620, 55.0, 2_000)]);
            // The pull file: the colliding minute plus a brand-new one.
            await WriteMinuteFile(Path.Combine(pull, "NSE_X-EQ.parquet"),
                [(1790653500, 100.5, 1_000), (1790653560, 101.0, 500)]);
            File.WriteAllText(mapPath, """{"sym2isin": {"X": "INE0000000001"}}""");

            var result = await new Organizer(pull, dataset, mapPath).RunAsync(CancellationToken.None);

            Assert.Equal(3, result.MergedRows);
            var rows = await CandleStore.ReadAsync(
                Path.Combine(dataset, "INE0000000001", "cash", "NSE_X-EQ.parquet"), CancellationToken.None);
            Assert.Equal(3, rows.Count);                       // no duplicate ts_utc
            Assert.Equal(100.5, rows.Single(r => r.ts_utc == 1790653500).close);  // pull wins
            Assert.Equal(101.0, rows.Single(r => r.ts_utc == 1790653560).close);  // pull-only minute
            Assert.Equal(55.0, rows.Single(r => r.ts_utc == 1790653620).close);   // target-only survives
        }
        finally { Directory.Delete(pull, true); Directory.Delete(dataset, true); File.Delete(mapPath); }
    }

    [Fact]
    public async Task Unmappable_and_fno_files_stay_in_place()
    {
        var pull = TempDir("pull");
        var dataset = TempDir("ds");
        var mapPath = Path.Combine(TempDir("map"), "map.json");
        try
        {
            await WriteMinuteFile(Path.Combine(pull, "NSE_UNKNOWN-EQ.parquet"), [(1, 1, 1)]);
            await WriteMinuteFile(Path.Combine(pull, "NSE_NIFTY26AUGFUT.parquet"), [(1, 1, 1)],
                "NSE:NIFTY26AUGFUT");
            File.WriteAllText(mapPath, """{"sym2isin": {}}""");

            var result = await new Organizer(pull, dataset, mapPath).RunAsync(CancellationToken.None);

            Assert.Equal(0, result.MappedFiles);
            Assert.Equal(2, result.LeftInPlace.Count);
        }
        finally { Directory.Delete(pull, true); Directory.Delete(dataset, true); File.Delete(mapPath); }
    }
}
