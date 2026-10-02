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
            ? $"Reference last update: {u.ToString("yyyy-MM-dd HH\\:mm", CultureInfo.InvariantCulture)} UTC"
            : "Reference last update: unknown");
        md.AppendLine();
        md.AppendLine($"* **new anomalies: {result.NewAnomalies.Count}**" +
                      $" · baseline (accepted) diffs: {result.BaselineCount}" +
                      $" · extra days (ours only): {result.ExtraDayCount}");
        md.AppendLine();
        md.AppendLine("| bucket | ISINs |");
        md.AppendLine("|---|---|");
        foreach (var g in result.Coverage.GroupBy(c => c.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal))
            md.AppendLine($"| {g.Key} | {g.Count().ToString("N0", CultureInfo.InvariantCulture)} |");
        if (result.RefUnmappedStems is { Count: > 0 } unmapped)
            md.AppendLine($"| ref_unmapped (stems) | {unmapped.Count.ToString("N0", CultureInfo.InvariantCulture)} |");
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
            .Select(a => $"{a.Kind},{a.Isin},{a.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""}," +
                         $"{a.Ours?.ToString(CultureInfo.InvariantCulture) ?? ""},{a.Reference?.ToString(CultureInfo.InvariantCulture) ?? ""},{a.Note}"));
        File.WriteAllLines(Path.Combine(outDir, $"anomalies-{stamp}.csv"), anomalyLines);

        var coverageLines = new List<string> { "isin,bucket,common_days,missing_days,tag" };
        coverageLines.AddRange(result.Coverage
            .OrderBy(c => c.Isin, StringComparer.Ordinal)
            .Select(c => $"{c.Isin},{c.Bucket},{c.CommonDays.ToString(CultureInfo.InvariantCulture)},{c.MissingDays.ToString(CultureInfo.InvariantCulture)},{c.Tag}"));
        File.WriteAllLines(Path.Combine(outDir, $"isin_coverage-{stamp}.csv"), coverageLines);

        return result.NewAnomalies.Count > 0 ? 1 : 0;
    }
}
