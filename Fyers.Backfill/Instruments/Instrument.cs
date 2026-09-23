namespace Fyers.Backfill.Instruments;

/// <summary>
/// One instrument the backfill can fetch history for. Cash equities come from the
/// <c>NSE_CM</c> master; live futures/options from the <c>NSE_FO</c> master; expired
/// futures/options are discovered at run time through the
/// <c>/data/history/fno/expired/*</c> endpoints.
/// </summary>
/// <param name="Symbol">Full Fyers ticker, e.g. <c>NSE:SBIN-EQ</c>, <c>NSE:NIFTY26AUGFUT</c>.</param>
/// <param name="Kind">EQ | FUTURE | OPTION.</param>
/// <param name="Isin">ISIN for cash equities (null for derivatives).</param>
/// <param name="Underlying">Bare underlying stem (NIFTY, RELIANCE) for derivatives.</param>
/// <param name="ExpiryEpoch">Contract expiry (unix seconds) for derivatives.</param>
/// <param name="Strike">Strike price for options.</param>
/// <param name="OptionType">CE / PE for options.</param>
/// <param name="Segment">CM | FO (the master the instrument came from).</param>
/// <param name="Expired">True when discovered via the expired-contract endpoints.</param>
public sealed record Instrument(
    string Symbol,
    string Kind,
    string? Isin = null,
    string? Underlying = null,
    long? ExpiryEpoch = null,
    decimal? Strike = null,
    string? OptionType = null,
    string Segment = "CM",
    bool Expired = false)
{
    public const string KindEquity = "EQ";
    public const string KindFuture = "FUTURE";
    public const string KindOption = "OPTION";

    /// <summary>oi_flag=1 only for derivatives (options and futures carry OI).</summary>
    public bool WantsOi => Kind is KindFuture or KindOption;

    /// <summary>cont_flag=1 only for futures (continuous-series stitching).</summary>
    public bool WantsCont => Kind is KindFuture;

    /// <summary>
    /// Expiry as an IST date when known. Used to bound an expired contract's work
    /// window (a contract only trades in the run-up to its own expiry).
    /// </summary>
    public DateOnly? ExpiryDate => ExpiryEpoch is { } e and > 0
        ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime
            .AddHours(5.5))
        : null;
}
