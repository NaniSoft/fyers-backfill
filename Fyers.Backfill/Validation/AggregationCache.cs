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
        // Length throws FileNotFoundException for a directory (or missing) path,
        // so fall back to 0 — the last-write time still distinguishes real files.
        return new IsinSource(isin, path, fi.Exists ? fi.Length : 0, fi.LastWriteTimeUtc);
    }
}

/// <summary>state.json on-disk shape (fingerprints only — bars live in parquet).</summary>
internal sealed record CacheStateEntry(string Path, long Length, DateTime LastWriteUtc);
internal sealed record CacheState(Dictionary<string, CacheStateEntry> Isins);

// Parquet.Net's DeserializeAsync<T> constrains T to `new()` and maps by property
// name, so this is a mutable class (like CandleRowDto) — a positional record
// fails the constraint. The parameterless ctor is Parquet's; the other one keeps
// the positional construction in ToDto.
internal sealed class DailyBarDto
{
    public DailyBarDto() { }

    public DailyBarDto(
        long DateYmd, double Open, double High, double Low, double Close,
        long Volume, int Bars, string FirstMinute, string LastMinute)
    {
        this.DateYmd = DateYmd;
        this.Open = Open;
        this.High = High;
        this.Low = Low;
        this.Close = Close;
        this.Volume = Volume;
        this.Bars = Bars;
        this.FirstMinute = FirstMinute;
        this.LastMinute = LastMinute;
    }

    public long DateYmd { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public long Volume { get; set; }
    public int Bars { get; set; }
    public string FirstMinute { get; set; } = "";
    public string LastMinute { get; set; } = "";
}

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
