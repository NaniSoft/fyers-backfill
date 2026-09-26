using System.Globalization;
using Fyers.Backfill.Instruments;
using Fyers.Core.Fyers;
using Parquet;
using Parquet.Serialization;

namespace Fyers.Backfill.Parquet;

/// <summary>
/// One row of the on-disk dataset. Property names ARE the Parquet column names
/// (Parquet.Net maps by property name and has no rename attribute), so they are
/// deliberately snake_case to give consumers the tidy contract
/// <c>symbol, isin, instrument_type, underlying, expiry_epoch, strike, option_type,
/// ts_utc, ist_minute, open, high, low, close, volume, oi, resolution</c>.
/// </summary>
public sealed class CandleRowDto
{
    public string symbol { get; set; } = "";
    public string? isin { get; set; }
    public string instrument_type { get; set; } = "";
    public string? underlying { get; set; }
    public long? expiry_epoch { get; set; }
    public double? strike { get; set; }
    public string? option_type { get; set; }
    public long ts_utc { get; set; }
    public string ist_minute { get; set; } = "";
    public double open { get; set; }
    public double high { get; set; }
    public double low { get; set; }
    public double close { get; set; }
    public long volume { get; set; }
    public long? oi { get; set; }
    public string resolution { get; set; } = "1";
}

/// <summary>
/// The Parquet dataset. Two write modes:
/// <list type="bullet">
/// <item><b>merge</b> (<see cref="MergeWriteAsync"/>) — one file per
/// <c>(resolution, instrument)</c>, read-merge-write keyed by <c>ts_utc</c>.
/// Idempotent, but the whole file is rewritten on every window, which becomes
/// I/O-bound as a 9-year file grows to ~800k rows.</item>
/// <item><b>parts</b> (<see cref="WritePartAsync"/>) — each window lands as its
/// own file under <c>&lt;root&gt;/_parts/&lt;resolution&gt;/&lt;SYMBOL&gt;/&lt;from&gt;_&lt;to&gt;.parquet</c>.
/// Cheap (no read), so a full-history sweep stays API-bound; a later
/// <c>compact</c> pass folds the parts into the single merged file.</item>
/// </list>
/// </summary>
public sealed class CandleStore(string root)
{
    private static readonly TimeZoneInfo Ist = ResolveIst();

    private static readonly ParquetOptions WriteOptions = new()
    {
        CompressionMethod = CompressionMethod.Zstd,
    };

    public string Root { get; } = root;

    /// <summary><c>&lt;root&gt;/&lt;resolution&gt;/&lt;sanitized-symbol&gt;.parquet</c>.</summary>
    public string PartitionPath(string resolution, string symbol)
        => Path.Combine(Root, ResolutionDir(resolution), Sanitize(symbol) + ".parquet");

    /// <summary><c>&lt;root&gt;/_parts/&lt;resolution&gt;/&lt;sanitized-symbol&gt;/</c>.</summary>
    public string PartsDir(string resolution, string symbol)
        => Path.Combine(Root, "_parts", ResolutionDir(resolution), Sanitize(symbol));

    /// <summary>Filesystem-safe resolution folder: "1" -> "1min", "D" -> "1day".</summary>
    public static string ResolutionDir(string resolution) => resolution switch
    {
        "1" => "1min",
        "D" or "1D" => "1day",
        "1W" => "1week",
        "1M" => "1month",
        _ => Sanitize(resolution),
    };

    /// <summary>
    /// Fyers tickers contain <c>:</c> (and options carry letters/digits); Windows
    /// forbids <c>:</c> in filenames, so map it and the other reserved characters
    /// to <c>_</c>. <c>NSE:SBIN-EQ</c> -> <c>NSE_SBIN-EQ</c>.
    /// </summary>
    public static string Sanitize(string symbol)
    {
        var chars = symbol.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is ':' or '/' or '\\' or '*' or '?' or '"' or '<' or '>' or '|')
                chars[i] = '_';
        }
        return new string(chars);
    }

    // ------------------------------------------------------------------ parts

    /// <summary>
    /// Write one window's candles as a standalone part file (overwriting any
    /// previous attempt for the same window). No read, no merge — this is the
    /// fast path for a long backward sweep. Rows are de-duplicated by
    /// <c>ts_utc</c> (Fyers repeats minutes) and sorted.
    /// </summary>
    public async Task<int> WritePartAsync(Instrument instrument, string resolution,
        DateOnly from, DateOnly to, IReadOnlyList<CandleRow> candles, CancellationToken ct)
    {
        var dir = PartsDir(resolution, instrument.Symbol);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.parquet");

        var byTs = new Dictionary<long, CandleRowDto>();
        foreach (var c in candles)
            byTs[c.TsUtc] = ToDto(instrument, resolution, c);

        await WriteRowsAsync(path, byTs.Values.OrderBy(r => r.ts_utc).ToList(), ct);
        return byTs.Count;
    }

    /// <summary>All part files for a symbol, ordered by filename (window order).</summary>
    public IReadOnlyList<string> PartFiles(string resolution, string symbol)
    {
        var dir = PartsDir(resolution, symbol);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.parquet").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : [];
    }

    /// <summary>
    /// Fold one symbol's part files (plus any existing merged base) into the
    /// single <c>&lt;resolution&gt;/&lt;SYMBOL&gt;.parquet</c>, de-duplicated by
    /// <c>ts_utc</c> and sorted, then delete the parts. The one-time cost that
    /// replaces N per-window rewrites.
    /// </summary>
    public async Task<int> CompactSymbolDirAsync(string symbolDir, CancellationToken ct)
    {
        var resDir = Path.GetFileName(Path.GetDirectoryName(symbolDir)!) ?? "";
        var symbol = Path.GetFileName(symbolDir);
        var basePath = Path.Combine(Root, resDir, symbol + ".parquet");

        var byTs = new Dictionary<long, CandleRowDto>();
        if (File.Exists(basePath))
            foreach (var r in await ReadAsync(basePath, ct))
                byTs[r.ts_utc] = r;

        var parts = Directory.GetFiles(symbolDir, "*.parquet");
        foreach (var p in parts)
            foreach (var r in await ReadAsync(p, ct))
                byTs[r.ts_utc] = r;

        if (byTs.Count == 0)
            return 0;

        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        await WriteRowsAsync(basePath, byTs.Values.OrderBy(r => r.ts_utc).ToList(), ct);

        foreach (var p in parts)
            File.Delete(p);
        try
        {
            if (Directory.GetFiles(symbolDir).Length == 0)
                Directory.Delete(symbolDir);
        }
        catch (IOException) { /* leave an empty dir; harmless */ }
        return byTs.Count;
    }

    // ------------------------------------------------------------------ merge

    /// <summary>Merge <paramref name="candles"/> into the instrument's partition.</summary>
    public async Task<int> MergeWriteAsync(
        Instrument instrument, string resolution, IReadOnlyList<CandleRow> candles, CancellationToken ct)
    {
        var path = PartitionPath(resolution, instrument.Symbol);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Read-merge-write keyed by ts_utc: idempotent, order-independent.
        var byTs = new Dictionary<long, CandleRowDto>();
        if (File.Exists(path))
        {
            foreach (var row in await ReadAsync(path, ct))
                byTs[row.ts_utc] = row;
        }

        foreach (var c in candles)
            byTs[c.TsUtc] = ToDto(instrument, resolution, c);

        await WriteRowsAsync(path, byTs.Values.OrderBy(r => r.ts_utc).ToList(), ct);
        return byTs.Count;
    }

    public static async Task<IReadOnlyList<CandleRowDto>> ReadAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var result = await ParquetSerializer.DeserializeAsync<CandleRowDto>(fs, null, null, ct);
        return result.Data.ToList();
    }

    /// <summary>Row count of a partition without materialising it (0 when absent).</summary>
    public async Task<int> CountAsync(string resolution, string symbol, CancellationToken ct)
    {
        var path = PartitionPath(resolution, symbol);
        if (!File.Exists(path))
            return 0;
        var rows = await ReadAsync(path, ct);
        return rows.Count;
    }

    // ---------------------------------------------------------------- helpers

    private static CandleRowDto ToDto(Instrument instrument, string resolution, CandleRow c) => new()
    {
        symbol = instrument.Symbol,
        isin = instrument.Isin,
        instrument_type = instrument.Kind,
        underlying = instrument.Underlying,
        expiry_epoch = instrument.ExpiryEpoch,
        strike = instrument.Strike is { } s ? (double)s : null,
        option_type = instrument.OptionType,
        ts_utc = c.TsUtc,
        ist_minute = IstMinute(c.TsUtc),
        open = (double)c.Open,
        high = (double)c.High,
        low = (double)c.Low,
        close = (double)c.Close,
        volume = (long)c.Volume,
        oi = null,                       // the wire's 7th field is not carried on CandleRow
        resolution = resolution,
    };

    private static async Task WriteRowsAsync(string path, List<CandleRowDto> rows, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await ParquetSerializer.SerializeAsync(rows, fs, WriteOptions, null!, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static string IstMinute(long tsUtc)
        => TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeSeconds(tsUtc).UtcDateTime, Ist)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }
}
