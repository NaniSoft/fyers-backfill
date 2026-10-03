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
            var kind = parts[0].Trim();
            var isin = parts[1].Trim();
            issues._rows.Add((kind, isin, date));
            if (date is not null && IsToleranceClass(kind))
                issues._rows.Add((kind, isin, null));   // lift a legacy per-date row to ISIN level
        }
        return issues;
    }

    /// <summary>close/volume differences are definitional (last-minute vs
    /// official close; ~0.2% feed shortfall) and re-fire on every new trading
    /// day — accept them at ISIN level so one decision covers all dates.
    /// A NEW ISIN firing still alarms; per-date keys stay for every other kind.</summary>
    internal static bool IsToleranceClass(string kind) => kind is "close" or "volume";

    public bool Contains(Anomaly a) =>
        !IsToleranceClass(a.Kind) || a.Date is null
            ? _rows.Contains((a.Kind, a.Isin, a.Date))
            : _rows.Contains((a.Kind, a.Isin, a.Date)) || _rows.Contains((a.Kind, a.Isin, null));

    public void Accept(IEnumerable<Anomaly> anomalies)
    {
        foreach (var a in anomalies)
            _rows.Add(IsToleranceClass(a.Kind) ? (a.Kind, a.Isin, null) : (a.Kind, a.Isin, a.Date));
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
