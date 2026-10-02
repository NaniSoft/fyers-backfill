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
        foreach (var src in DiscoverCashSources(datasetDir))
        {
            List<DailyBar> bars;
            if (cache.IsCurrent(src))
            {
                bars = cache.ReadIsin(src.Isin);
            }
            else
            {
                bars = DailyAggregator.Aggregate(await CandleStore.ReadAsync(src.Path, ct));
                cache.WriteIsin(src.Isin, bars, src);
            }
            if (bars.Count > 0) oursByIsin[src.Isin] = bars;
        }

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
    /// ISIN taken from the directory name (self-healing — no stale manifest).</summary>
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
        return sources.OrderBy(s => s.Isin, StringComparer.Ordinal).ToList();
    }
}
