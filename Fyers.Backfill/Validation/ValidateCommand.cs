using Fyers.Backfill.Parquet;

namespace Fyers.Backfill.Validation;

/// <summary>The offline `validate` command (spec §Commands): aggregate ours to
/// daily bars incrementally, compare against eod2, write the dated report.
/// Never touches Fyers — no token involved.</summary>
public sealed class ValidateCommand(
    string datasetDir, string eod2Dir, string validationDir,
    DateOnly floor, bool acceptBaseline)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(datasetDir) || !Directory.Exists(eod2Dir))
            return 2;   // config/path error (spec §Commands exit codes)
        Directory.CreateDirectory(validationDir);   // --accept-baseline writes the baseline before any report

        var reference = new Eod2Reference(eod2Dir);
        var known = KnownIssues.Load(Path.Combine(validationDir, "known_issues.csv"));
        var cache = new AggregationCache(validationDir);

        var oursByIsin = new Dictionary<string, List<DailyBar>>();
        var fileBars = new Dictionary<string, List<List<DailyBar>>>();
        foreach (var src in DiscoverCashSources(datasetDir))
        {
            List<DailyBar> bars;
            if (cache.IsCurrent(src))
            {
                bars = cache.ReadIsin(src);
            }
            else
            {
                bars = DailyAggregator.Aggregate(await CandleStore.ReadAsync(src.Path, ct));
                cache.WriteIsin(src, bars);
            }
            if (bars.Count == 0) continue;
            if (!fileBars.TryGetValue(src.Isin, out var lists))
                fileBars[src.Isin] = lists = [];
            lists.Add(bars);
        }

        // An ISIN's files are merged in memory every run (only the aggregation is
        // cached), so a series file added later joins the day it belongs to
        // instead of overwriting the ISIN's earlier bars.
        foreach (var (isin, lists) in fileBars)
            oursByIsin[isin] = lists.Count == 1 ? lists[0] : DailyAggregator.MergeFiles(lists);

        var result = ValidationEngine.Validate(oursByIsin, reference, known,
            new ValidationOptions(floor));

        if (acceptBaseline && result.NewAnomalies.Count > 0)
        {
            known.Accept(result.NewAnomalies);
            known.Save(Path.Combine(validationDir, "known_issues.csv"));
            return 0;
        }

        return ReportWriter.Write(validationDir, TodayIst(), result, reference.LastUpdateUtc);
    }

    private static DateOnly TodayIst() => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Kolkata"));

    /// <summary>Every cash parquet: <c>&lt;dataset&gt;/&lt;ISIN&gt;/cash/*.parquet</code>,
    /// ISIN taken from the directory name (self-healing — no stale manifest). Files
    /// of one ISIN are grouped together and ordered EQ first, then by name, so the
    /// per-file merge is deterministic and never depends on directory order.</summary>
    public static IReadOnlyList<IsinSource> DiscoverCashSources(string datasetDir)
    {
        if (!Directory.Exists(datasetDir)) return [];
        var sources = new List<IsinSource>(3_000);
        foreach (var isinDir in Directory.EnumerateDirectories(datasetDir))
        {
            var isin = Path.GetFileName(isinDir);
            if (isin.StartsWith('_')) continue;                       // _INDEX, _masters, …
            var cash = Path.Combine(isinDir, "cash");
            if (!Directory.Exists(cash)) continue;
            foreach (var f in Directory.EnumerateFiles(cash, "*.parquet"))
                sources.Add(IsinSource.Of(isin, f));
        }
        return sources.OrderBy(s => s.Isin, StringComparer.Ordinal)
            .ThenBy(SeriesKey, StringComparer.Ordinal)                 // EQ first: anchors a merged day
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Sort key ordering an ISIN's files EQ first (the primary session,
    /// which anchors a merged day's open), every other series after it by name:
    /// "" sorts before any series name, so EQ maps to "".</summary>
    private static string SeriesKey(IsinSource s)
    {
        var stem = Path.GetFileNameWithoutExtension(s.Path);
        var dash = stem.LastIndexOf('-');
        var series = dash >= 0 ? stem[(dash + 1)..] : "";
        return series == "EQ" ? "" : series;
    }
}
