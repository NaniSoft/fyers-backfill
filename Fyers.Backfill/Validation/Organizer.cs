using System.Text.Json;
using Fyers.Backfill.Parquet;
using Parquet.Serialization;

namespace Fyers.Backfill.Validation;

public sealed record OrganizeResult(int MappedFiles, int MergedRows, IReadOnlyList<string> LeftInPlace);

/// <summary>The `organize` stage of the daily chain (spec §Commands step 3):
/// fold each fresh <c>1min/NSE_*-EQ.parquet</c> into
/// <c>&lt;dataset&gt;/&lt;ISIN&gt;/cash/</c>, merge-on-write by ts_utc. ISIN from
/// <c>isin_symbol_map.json</c> sym2isin; anything unmappable (and every F&amp;O
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
            // Last-write-wins: pull rows overwrite the target's row for the same
            // ts_utc (a re-pull is authoritative). Do NOT flip this to first-wins.
            var byTs = new Dictionary<long, CandleRowDto>();
            if (File.Exists(target))
                foreach (var r in await CandleStore.ReadAsync(target, ct))
                    byTs[r.ts_utc] = r;
            foreach (var r in await CandleStore.ReadAsync(path, ct))
                byTs[r.ts_utc] = r;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp";
            await using (var fs = File.Create(tmp))
                await ParquetSerializer.SerializeAsync(
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
            // An empty ISIN would collapse the target to <dataset>/cash/ and get
            // the source deleted, so drop the mapping instead.
            if (stem.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
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
            {
                // An empty ISIN would collapse the target to <dataset>/cash/ and
                // get the source deleted, so drop the mapping instead.
                var isin = p.Value.GetString();
                if (string.IsNullOrWhiteSpace(isin)) continue;
                map[p.Name.Trim().ToUpperInvariant()] = isin;
            }
        return map;
    }
}
