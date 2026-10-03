namespace Fyers.Backfill.Validation;

/// <summary>One reported difference. Kind is one of: ohlc_open, ohlc_high,
/// ohlc_low, close, volume, missing_day, extra_day, missing_isin.</summary>
public sealed record Anomaly(
    string Kind, string Isin, DateOnly? Date, double? Ours, double? Reference, string Note);

/// <summary>The audit-based tolerances (spec §Tolerances). O/H/L must match to
/// the tick; the close is definitional (last-minute vs official) → 0.5%; the
/// volume runs ~0.2% under → 1%.</summary>
public static class ValidationRules
{
    public const double CloseTolerance = 0.005;
    public const double VolumeTolerance = 0.01;

    /// <summary>Equal after rounding to the 0.01 tick.</summary>
    public static bool SameTick(double a, double b) =>
        Math.Round(a, 2, MidpointRounding.AwayFromZero)
            .Equals(Math.Round(b, 2, MidpointRounding.AwayFromZero));

    public static bool ReferenceTraded(ReferenceDay day) => day.Volume > 0;

    public static List<Anomaly> CompareDay(
        string isin, DateOnly date, DailyBar ours, ReferenceDay reference)
    {
        var anomalies = new List<Anomaly>(5);
        if (!SameTick(ours.Open, reference.Open))
            anomalies.Add(new Anomaly("ohlc_open", isin, date, ours.Open, reference.Open, "open differs"));
        if (!SameTick(ours.High, reference.High))
            anomalies.Add(new Anomaly("ohlc_high", isin, date, ours.High, reference.High, "high differs"));
        if (!SameTick(ours.Low, reference.Low))
            anomalies.Add(new Anomaly("ohlc_low", isin, date, ours.Low, reference.Low, "low differs"));
        if (Math.Abs(ours.Close - reference.Close) > CloseTolerance * Math.Abs(reference.Close))
            anomalies.Add(new Anomaly("close", isin, date, ours.Close, reference.Close,
                FormattableString.Invariant($"close off by {ours.Close / reference.Close - 1:P3}")));
        if (reference.Volume > 0 &&
            Math.Abs(ours.Volume - reference.Volume) > VolumeTolerance * reference.Volume)
            anomalies.Add(new Anomaly("volume", isin, date, ours.Volume, reference.Volume,
                FormattableString.Invariant($"volume off by {ours.Volume / reference.Volume - 1:P3}")));
        return anomalies;
    }
}
