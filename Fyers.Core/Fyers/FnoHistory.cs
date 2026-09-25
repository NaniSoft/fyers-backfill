using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fyers.Core.Fyers;

/// <summary>Expiries available for an underlying, split by instrument class.</summary>
public sealed record ExpiredExpiries(IReadOnlyList<DateOnly> Futures, IReadOnlyList<DateOnly> Options);

/// <summary>Contracts listed at one expiry, split by instrument class.</summary>
public sealed record ExpiredContracts(IReadOnlyList<string> Futures, IReadOnlyList<string> Options);

/// <summary>
/// Expired F&amp;O history half of <see cref="FyersClient"/> — the three
/// <c>/data/history/fno/expired/*</c> endpoints the official SDK exposes
/// (<c>expiry_dates</c>, <c>history_underlying_symbols</c>, <c>fno_historical_data</c>).
///
/// Live-verified response shapes (2026-09-25):
/// <code>
/// expiry-dates        -> data.expiry_dates.{futures[],options[]}   ("yyyy-MM-dd")
/// underlying-symbols  -> data.contracts.{futures[],options[]}      (Fyers tickers)
/// historical-data     -> candles[[epoch,o,h,l,c,v(,oi)]]
/// </code>
///
/// IMPORTANT (live-verified): <b>expired futures return candles; expired options
/// return s=no_data</b> for every strike/range/flag combination tried. Fyers does
/// not serve expired option history (it is TrueData-only), so an expired-option
/// backfill is not possible — callers should request futures only.
/// </summary>
public sealed partial class FyersClient
{
    private const string ExpiredExpiryDatesPath = "/history/fno/expired/expiry-dates";
    private const string ExpiredUnderlyingSymbolsPath = "/history/fno/expired/underlying-symbols";
    private const string ExpiredHistoricalDataPath = "/history/fno/expired/historical-data";

    /// <summary>Expiries available for <paramref name="underlying"/> in [from, to].</summary>
    public ExpiredExpiries ExpiredExpiryDates(string underlying, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(underlying))
            .Append("&date_format=1")
            .Append("&range_from=").Append(Date(from))
            .Append("&range_to=").Append(Date(to));

        using var doc = JsonDocument.Parse(GetRaw(ExpiredExpiryDatesPath, qs.ToString(), ct), JsonOpts);
        var root = doc.RootElement;
        if (!string.Equals(Str(root, "s"), "ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"expired expiry-dates {underlying} -> {Str(root, "s")}");

        var data = Child(root, "data");
        var dates = Child(data, "expiry_dates");
        return new ExpiredExpiries(
            DateList(dates, "futures"),
            DateList(dates, "options"));
    }

    /// <summary>Contracts listed for <paramref name="underlying"/> at <paramref name="expiry"/>.</summary>
    public ExpiredContracts ExpiredUnderlyingSymbols(string underlying, DateOnly expiry,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(underlying))
            .Append("&expiry_date=").Append(Date(expiry));

        using var doc = JsonDocument.Parse(GetRaw(ExpiredUnderlyingSymbolsPath, qs.ToString(), ct), JsonOpts);
        var root = doc.RootElement;
        if (!string.Equals(Str(root, "s"), "ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"expired underlying-symbols {underlying} -> {Str(root, "s")}");

        var contracts = Child(Child(root, "data"), "contracts");
        return new ExpiredContracts(
            StringList(contracts, "futures"),
            StringList(contracts, "options"));
    }

    /// <summary>
    /// Candles for one expired contract. Live-verified: futures return data,
    /// options return <c>no_data</c> (empty list, not an error).
    /// </summary>
    public IReadOnlyList<CandleRow> FnoHistoricalData(string symbol, DateOnly from, DateOnly to,
        string resolution = HistoryResolution1Min, bool includeOi = false, bool includeGreeks = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(symbol))
            .Append("&resolution=").Append(Uri.EscapeDataString(resolution))
            .Append("&date_format=1")
            .Append("&range_from=").Append(Date(from))
            .Append("&range_to=").Append(Date(to));
        if (includeOi)
            qs.Append("&include_oi=1");
        if (includeGreeks)
            qs.Append("&include_greeks=1");

        using var doc = JsonDocument.Parse(GetRaw(ExpiredHistoricalDataPath, qs.ToString(), ct), JsonOpts);
        var root = doc.RootElement;
        var s = Str(root, "s");
        if (string.Equals(s, "no_data", StringComparison.Ordinal))
            return [];
        if (!string.Equals(s, "ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"fno history {symbol} -> {s}: {Str(root, "message") ?? ""}");

        var rows = new List<CandleRow>();
        if (!root.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array)
            return rows;
        foreach (var candle in candles.EnumerateArray())
        {
            if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() < 5)
                continue;
            var epoch = NumAt(candle, 0);
            rows.Add(new CandleRow(
                Symbol: symbol,
                TsUtc: epoch is null ? 0L : (long)epoch.Value,
                Open: NumAt(candle, 1) ?? 0m,
                High: NumAt(candle, 2) ?? 0m,
                Low: NumAt(candle, 3) ?? 0m,
                Close: NumAt(candle, 4) ?? 0m,
                Volume: candle.GetArrayLength() > 5 ? NumAt(candle, 5) ?? 0m : 0m));
        }
        return rows;
    }

    // ---------------------------------------------------------------- helpers

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonElement Child(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v : default;

    /// <summary>"yyyy-MM-dd" (or epoch seconds / {date|expiry} object) list under a key.</summary>
    private static IReadOnlyList<DateOnly> DateList(JsonElement parent, string key)
    {
        var arr = Child(parent, key);
        if (arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<DateOnly>();
        foreach (var item in arr.EnumerateArray())
        {
            var d = DateOnlyOrNull(item);
            if (d is not null)
                list.Add(d.Value);
        }
        return list;
    }

    private static DateOnly? DateOnlyOrNull(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && epoch > 0)
                return EpochToDate(epoch);
            return null;
        }
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n > 0)
            return EpochToDate(n);
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "date", "expiry_date", "expiry" })
            {
                var v = Child(e, key);
                var d = DateOnlyOrNull(v);
                if (d is not null)
                    return d;
            }
        }
        return null;
    }

    private static DateOnly EpochToDate(long epoch)
        => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.AddHours(5.5));

    private static IReadOnlyList<string> StringList(JsonElement parent, string key)
    {
        var arr = Child(parent, key);
        if (arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s!);
        }
        return list;
    }
}
