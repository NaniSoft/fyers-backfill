namespace Fyers.Backfill.Validation;

/// <summary>One row of isin_coverage CSV. Bucket: both | eod2_only | ours_only |
/// ref_unmapped(unused here — the engine sees only mapped stems). Tag: "" or
/// split_factor.</summary>
public sealed record CoverageRow(string Isin, string Bucket, int CommonDays, int MissingDays, string Tag);

public sealed record ValidationResult(
    IReadOnlyList<CoverageRow> Coverage,
    IReadOnlyList<Anomaly> NewAnomalies,
    int BaselineCount,
    int ExtraDayCount,
    IReadOnlyList<string>? RefUnmappedStems = null);

public sealed record ValidationOptions(
    DateOnly Floor, int SplitFactorMinDays = 30,
    double SplitFactorLow = 0.8, double SplitFactorHigh = 1.25);

/// <summary>The comparison pass (spec §Join, §Tolerances, §Known-issues): joins
/// our daily bars to the reference by ISIN, buckets coverage, tags split
/// factors, applies the tolerances, subtracts the baseline. Reference days are
/// retained only for ISINs we carry — the rest keep a traded-day count, so the
/// full reference set is never held in memory.</summary>
public static class ValidationEngine
{
    public static ValidationResult Validate(
        IReadOnlyDictionary<string, List<DailyBar>> oursByIsin,
        Eod2Reference reference,
        KnownIssues known,
        ValidationOptions options)
    {
        // stem → our ISIN, for the four coverage buckets.
        var refByIsin = new Dictionary<string, List<ReferenceDay>>();
        var refTraded = new Dictionary<string, int>();      // eod2_only: count, not days
        var refUnmapped = new List<string>();
        foreach (var stem in reference.Stems)
        {
            if (!reference.StemToIsin.TryGetValue(stem, out var isin))
            {
                refUnmapped.Add(stem);              // ref_unmapped — report-only
                continue;
            }
            if (!oursByIsin.ContainsKey(isin))
            {
                // eod2_only — a traded-day count is all the bucket needs.
                var traded = reference.Days(stem)
                    .Count(d => d.Date >= options.Floor && ValidationRules.ReferenceTraded(d));
                refTraded[isin] = refTraded.TryGetValue(isin, out var n) ? n + traded : traded;
                continue;
            }
            if (!refByIsin.TryGetValue(isin, out var list))
                refByIsin[isin] = list = [];
            list.AddRange(reference.Days(stem));
        }

        var coverage = new List<CoverageRow>();
        var anomalies = new List<Anomaly>();
        var baselineCount = 0;
        var extraDayCount = 0;

        foreach (var isin in oursByIsin.Keys.Concat(refByIsin.Keys).Concat(refTraded.Keys)
                     .Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            var ours = oursByIsin.TryGetValue(isin, out var o) ? o : [];
            var referenceDays = refByIsin.TryGetValue(isin, out var r) ? r : [];

            if (ours.Count == 0 && (refTraded.ContainsKey(isin) || referenceDays.Count > 0))
            {
                var traded = refTraded.TryGetValue(isin, out var t) ? t
                    : referenceDays.Count(d => d.Date >= options.Floor && ValidationRules.ReferenceTraded(d));
                coverage.Add(new CoverageRow(isin, "eod2_only", 0, traded, ""));
                if (traded > 0)
                {
                    var a = new Anomaly("missing_isin", isin, null, null, traded,
                        FormattableString.Invariant($"{traded} traded reference days since floor"));
                    if (known.Contains(a)) baselineCount++;
                    else anomalies.Add(a);
                }
                continue;
            }
            if (ours.Count > 0 && referenceDays.Count == 0)
            {
                coverage.Add(new CoverageRow(isin, "ours_only", 0, 0, ""));
                continue;
            }

            // One date can carry both an EQ and a BE row; EQ wins (the same rule
            // as the stem→ISIN map), which also keeps the join collision-free.
            var byDate = new Dictionary<DateOnly, ReferenceDay>();
            foreach (var d in referenceDays)
                if (d.Series == "EQ" || !byDate.ContainsKey(d.Date))
                    byDate[d.Date] = d;

            var ourDates = ours.Select(b => b.Date).ToHashSet();

            // split-factor: a constant ratio outside the band on enough common
            // days means a corporate action hit one side only — compare nothing.
            var common = ours.Where(b => byDate.ContainsKey(b.Date)).ToList();
            var tag = MedianRatio(common, byDate, options) is { } ratio
                      && (ratio < options.SplitFactorLow || ratio > options.SplitFactorHigh)
                ? "split_factor" : "";

            foreach (var bar in ours)
            {
                if (!byDate.TryGetValue(bar.Date, out var refDay))
                {
                    extraDayCount++;                        // informational
                    continue;
                }
                if (tag == "split_factor") continue;
                foreach (var a in ValidationRules.CompareDay(isin, bar.Date, bar, refDay))
                {
                    if (known.Contains(a)) baselineCount++;
                    else anomalies.Add(a);
                }
            }
            // Missing days come from the EQ-deduped join, never the raw rows:
            // a date can carry an EQ and a BE row and must count once, with the
            // EQ row's volume.
            var missingDays = byDate.Values
                .Where(d => d.Date >= options.Floor && ValidationRules.ReferenceTraded(d)
                            && !ourDates.Contains(d.Date))
                .ToList();

            if (missingDays.Count > 0 && tag != "split_factor")
                foreach (var d in missingDays)
                {
                    var a = new Anomaly("missing_day", isin, d.Date, null, d.Volume, "no bar of ours");
                    if (known.Contains(a)) baselineCount++; else anomalies.Add(a);
                }

            coverage.Add(new CoverageRow(isin, "both", common.Count, missingDays.Count, tag));
        }

        return new ValidationResult(coverage, anomalies, baselineCount, extraDayCount, refUnmapped);
    }

    /// <summary>Median ours/reference close ratio over common days, or null when
    /// there are fewer than SplitFactorMinDays of them.</summary>
    private static double? MedianRatio(
        List<DailyBar> common, Dictionary<DateOnly, ReferenceDay> byDate, ValidationOptions options)
    {
        if (common.Count < options.SplitFactorMinDays) return null;
        var ratios = common.Select(b => b.Close / byDate[b.Date].Close)
            .Where(r => r > 0)
            .OrderBy(r => r)
            .ToList();
        return ratios.Count == 0 ? null : ratios[ratios.Count / 2];
    }
}
