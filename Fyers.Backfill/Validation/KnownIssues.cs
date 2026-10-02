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
            .Select(r => $"{r.Kind},{r.Isin},{r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""},"));
        File.WriteAllLines(path, lines);
    }
}
