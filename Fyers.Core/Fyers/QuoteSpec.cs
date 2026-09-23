namespace Fyers.Core.Fyers;

/// <summary>
/// One symbol the collector wants quoted this minute — port of the Python
/// <c>_build_quote_specs</c> tuple <c>(symbol, type, underlying, expiry_epoch)</c>
/// (<c>src/collector.py</c>, decision 06).
/// </summary>
/// <param name="Symbol">Full Fyers ticker, e.g. NSE:NIFTY26SEPFUT, NSE:INDIAVIX, NSE:RELIANCE-EQ.</param>
/// <param name="Type">FUTURE / SPOT / CASH / VIX.</param>
/// <param name="Underlying">Bare ticker (null for VIX).</param>
/// <param name="ExpiryEpoch">Contract expiry epoch seconds (futures only).</param>
public readonly record struct QuoteSpec(string Symbol, string Type, string? Underlying, long? ExpiryEpoch);
