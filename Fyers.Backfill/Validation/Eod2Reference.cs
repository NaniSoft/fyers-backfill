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
    private readonly Lazy<IReadOnlyDictionary<string, string>> _stemToIsin = new(() => LoadIsinMap(eod2Dir));
    private readonly Lazy<IReadOnlyList<string>> _stems = new(() => LoadStems(eod2Dir));

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

    private static Dictionary<string, string> LoadIsinMap(string eod2Dir)
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

    private static IReadOnlyList<string> LoadStems(string eod2Dir) =>
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
