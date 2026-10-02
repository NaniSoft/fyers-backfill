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
            Directory.CreateDirectory(Path.Combine(eod2, "daily"));   // File.WriteAllLines does not mkdir
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
