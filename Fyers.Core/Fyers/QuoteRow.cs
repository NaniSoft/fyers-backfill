namespace Fyers.Core.Fyers;

/// <summary>
/// One row of the <c>quotes</c> table — .NET port of
/// <c>src/collector.py:_quote_row_from_v</c> (the subset of the Fyers
/// <c>/data/quotes</c> <c>v</c> dict this collector stores).
/// Field names follow <c>src/storage.py</c> quotes DDL via the C# properties.
/// </summary>
/// <param name="Symbol">Fyers ticker as returned by the API (falls back to the
/// request chunk's <c>n</c> key), e.g. <c>NSE:RELIANCE-EQ</c>.</param>
/// <param name="Type">Quote-spec type: SPOT / FUTURE / CASH / VIX.</param>
/// <param name="Underlying">Bare ticker the row belongs to (null for VIX).</param>
/// <param name="ExpiryEpoch">Contract expiry (futures only, else null).</param>
/// <param name="Ltp">v.lp (fallback v.ltp).</param>
/// <param name="Open">v.open_price (fallback open, o).</param>
/// <param name="High">v.high_price (fallback high, h).</param>
/// <param name="Low">v.low_price (fallback low, l).</param>
/// <param name="PrevClose">v.prev_close_price (fallback prev_close, pdc).</param>
/// <param name="Volume">v.volume (fallback v.tv).</param>
/// <param name="Spread">v.spread.</param>
/// <param name="TsUtc">UTC instant of the snapshot minute (second/ms truncated).</param>
/// <param name="IstMinute">IST wall-clock of the same minute.</param>
/// <param name="InstrumentType">Parity name for <see cref="Type"/> (Python's
/// <c>instrument_type</c> column). Always equal to <see cref="Type"/>.</param>
/// <param name="Atp">v.atp — average traded price (the VWAP the user's metric needs).</param>
/// <param name="Bid">v.bid (fallback v.bid_price).</param>
/// <param name="Ask">v.ask (fallback v.ask_price).</param>
/// <remarks>The three nullable fields are last with defaults so every existing
/// positional/named construction keeps compiling; bid_size/ask_size/oi/ch/chp stay
/// uncollected (NULL in the quotes table) exactly as before.</remarks>
public sealed record QuoteRow(
    string Symbol,
    string Type,
    string? Underlying,
    long? ExpiryEpoch,
    decimal Ltp,
    decimal? Open,
    decimal? High,
    decimal? Low,
    decimal? PrevClose,
    decimal? Volume,
    decimal? Spread,
    DateTime TsUtc,
    DateTime IstMinute,
    string InstrumentType,
    decimal? Atp = null,
    decimal? Bid = null,
    decimal? Ask = null);
