using System.Globalization;
using System.Text.Json;
using Fyers.Backfill.FnoPull;
using Parquet;
using Parquet.Serialization;

namespace Fyers.Backfill.FnoPull;

/// <summary>
/// Writes expired F&amp;O candles into the <b>existing</b> dataset layout, in place.
///
/// The python puller (and this campaign's 489M already-written rows) use:
/// <list type="bullet">
/// <item>index underlyings -> <c>data/_INDEX/&lt;STEM&gt;/&lt;SYMBOL&gt;.parquet</c>;</item>
/// <item>everything else -> <c>data/&lt;ISIN&gt;/fno/&lt;SYMBOL&gt;.parquet</c>.</item>
/// </list>
/// with <c>:</c> mapped to <c>_</c> in the filename. That is deliberately NOT
/// <see cref="Parquet.CandleStore"/>'s <c>&lt;root&gt;/&lt;resolution&gt;/&lt;SYMBOL&gt;</c>
/// layout: writing there instead would orphan the 130k files already on disk and
/// force a full re-fetch to rebuild the dataset under one root.
///
/// Columns match the existing files exactly (same names, order and types) so the
/// whole dataset stays readable as one table — verified against
/// <c>pq.read_schema</c> on the python-written output.
/// </summary>
public sealed class FnoStore(string repoRoot, IReadOnlyDictionary<string, string> sym2isin)
{
    private static readonly ParquetOptions Opts = new() { CompressionMethod = CompressionMethod.Zstd };

    private static readonly string[] IndexStems =
        ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "NIFTYFPI"];

    private static readonly TimeZoneInfo Ist = ResolveIst();

    private readonly HashSet<string> _indexStems = new(IndexStems, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Directory a contract's parquet lives in, or null when the underlying has no
    /// ISIN and no index mapping (the python puller skipped those too — there is
    /// nowhere to put them).
    /// </summary>
    public string? DirFor(string symbol, string underlying)
    {
        if (_indexStems.Contains(underlying))
            return Path.Combine(repoRoot, "data", "_INDEX", underlying);

        if (!sym2isin.TryGetValue(underlying, out var isin) || string.IsNullOrWhiteSpace(isin))
            return null;

        return Path.Combine(repoRoot, "data", isin, "fno");
    }

    public string? PathFor(string symbol, string underlying)
    {
        var dir = DirFor(symbol, underlying);
        return dir is null ? null : Path.Combine(dir, symbol.Replace(':', '_') + ".parquet");
    }

    /// <summary>
    /// Merge <paramref name="rows"/> into the contract's partition, keyed by
    /// <c>ts_utc</c> — idempotent and order-independent, so a re-run after a crash
    /// cannot duplicate minutes. Returns the file's total row count.
    ///
    /// The metadata columns (<c>isin</c>, <c>underlying</c>, <c>expiry_epoch</c>,
    /// <c>strike</c>, <c>option_type</c>) are written as null, matching the existing
    /// python-written rows exactly. They are derivable from the queue file, so they
    /// can be backfilled for the whole dataset in one offline pass later without
    /// re-fetching a single candle.
    /// </summary>
    public int MergeWrite(string symbol, string underlying, IReadOnlyList<FnoCandle> rows)
    {
        var path = PathFor(symbol, underlying)
                   ?? throw new InvalidOperationException($"no dataset location for {symbol} ({underlying})");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var byTs = new Dictionary<long, FnoRow>();
        if (File.Exists(path))
        {
            foreach (var r in FnoRow.Read(path))
                byTs[r.ts_utc] = r;
        }
        foreach (var c in rows)
            byTs[c.TsUtc] = ToRow(symbol, c);

        var ordered = byTs.Values.OrderBy(r => r.ts_utc).ToList();
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            ParquetSerializer.SerializeAsync(ordered, fs, Opts, null!).GetAwaiter().GetResult();
        File.Move(tmp, path, overwrite: true);
        return ordered.Count;
    }

    private static FnoRow ToRow(string symbol, FnoCandle c) => new()
    {
        symbol = symbol,
        isin = null,
        instrument_type = Kind(symbol),
        underlying = null,
        expiry_epoch = null,
        strike = null,
        option_type = null,
        ts_utc = c.TsUtc,
        ist_minute = IstMinute(c.TsUtc),
        open = (double)c.Open,
        high = (double)c.High,
        low = (double)c.Low,
        close = (double)c.Close,
        volume = c.Volume,
        oi = c.Oi,
        resolution = "1",
    };

    /// <summary>
    /// <c>FUT</c>/<c>CE</c>/<c>PE</c> from the ticker suffix — the convention the
    /// existing rows use (see <c>instrument_type</c> on the python-written files).
    /// </summary>
    private static string Kind(string symbol)
    {
        if (symbol.EndsWith("FUT", StringComparison.Ordinal)) return "FUT";
        if (symbol.EndsWith("CE", StringComparison.Ordinal)) return "CE";
        if (symbol.EndsWith("PE", StringComparison.Ordinal)) return "PE";
        return "FUT";
    }

    private static string IstMinute(long tsUtc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(tsUtc).UtcDateTime, Ist)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    /// <summary>Load the sym-&gt;ISIN map used to place non-index contracts.</summary>
    public static Dictionary<string, string> LoadSym2Isin(string isinMapPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(isinMapPath))
            return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(isinMapPath));
        if (doc.RootElement.TryGetProperty("sym2isin", out var obj)
            && obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                    map[p.Name] = v;
        return map;
    }
}

/// <summary>One bar as carried between the client and the store.</summary>
public readonly record struct FnoCandle(long TsUtc, double Open, double High, double Low,
    double Close, long Volume, long? Oi);

/// <summary>
/// One dataset row. Property names ARE the parquet column names (Parquet.Net maps
/// by name), and the declaration order is the on-disk column order, so this must
/// stay in lockstep with the existing files.
/// </summary>
public sealed class FnoRow
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

    public static List<FnoRow> Read(string path)
    {
        using var fs = File.OpenRead(path);
        return ParquetSerializer.DeserializeAsync<FnoRow>(fs, null, null, default)
            .GetAwaiter().GetResult().Data.ToList();
    }
}
