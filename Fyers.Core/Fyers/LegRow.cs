namespace Fyers.Core.Fyers;

/// <summary>
/// One row of the <c>option_chain</c> table — .NET port of
/// <c>src/collector.py:_parse_option_legs</c>. Populated from an
/// <c>options-chain-v3</c> leg: <c>symbol</c> (fallback <c>fyToken</c>),
/// <c>expiry</c> (fallback the chain-level <c>data.expiry</c>),
/// <c>strike_price</c>, <c>option_type</c> (CE/PE only), <c>oi</c>,
/// <c>oich</c>, <c>prev_oi</c>, <c>volume</c>, <c>ltp</c>, <c>ltpch</c>,
/// <c>ltpchp</c>, <c>bid</c>, <c>ask</c> and
/// <c>greeks.{iv,delta,gamma,theta,vega}</c>.
/// </summary>
public sealed record LegRow(
    string Symbol,
    string Underlying,
    decimal Strike,
    string OptionType,
    long ExpiryEpoch,
    decimal Oi,
    decimal OiChg,
    decimal? PrevOi,
    decimal? Volume,
    decimal? Iv,
    decimal? Ltp,
    decimal? LtpCh,
    decimal? LtpChp,
    decimal? Bid,
    decimal? Ask,
    decimal? Delta,
    decimal? Gamma,
    decimal? Theta,
    decimal? Vega,
    DateTime TsUtc,
    DateTime IstMinute);
