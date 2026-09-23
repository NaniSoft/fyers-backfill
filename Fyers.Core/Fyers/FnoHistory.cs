using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fyers.Core.Fyers;

/// <summary>
/// Expired F&amp;O history half of <see cref="FyersClient"/> — the three
/// <c>/data/history/fno/expired/*</c> endpoints the SDK exposes
/// (<c>expiry_dates</c>, <c>history_underlying_symbols</c>,
/// <c>fno_historical_data</c>). <c>/data/history</c> itself does not serve
/// expired contracts (Fyers staff: expired F&amp;O data is a separate
/// endpoint), so a full historical F&amp;O backfill has to discover the expired
/// contracts first and then ask this endpoint for their candles.
///
/// The response shapes of these endpoints are not in the public v3 docs; the
/// parsers here are deliberately tolerant — they accept the value either as a
/// bare array under <c>data</c> (or <c>candles</c>) or as an array of objects,
/// and pull the first recognisable field from each element. A live pilot run is
/// the only way to pin the exact shape, so nothing here assumes one.
/// </summary>
public sealed partial class FyersClient
{
    private const string ExpiredExpiryDatesPath = "/history/fno/expired/expiry-dates";
    private const string ExpiredUnderlyingSymbolsPath = "/history/fno/expired/underlying-symbols";
    private const string ExpiredHistoricalDataPath = "/history/fno/expired/historical-data";

    /// <summary>
    /// Expiry epochs available for <paramref name="underlying"/> between
    /// <paramref name="from"/> and <paramref name="to"/> (inclusive), ascending.
    /// Each element is an expiry (unix seconds). Port of the SDK's
    /// <c>expiry_dates</c> (<c>/history/fno/expired/expiry-dates</c>).
    /// </summary>
    public IReadOnlyList<long> ExpiredExpiryDates(string underlying, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(underlying))
            .Append("&date_format=1")
            .Append("&range_from=").Append(Date(from))
            .Append("&range_to=").Append(Date(to));

        using var doc = JsonDocument.Parse(GetRaw(ExpiredExpiryDatesPath, qs.ToString(), ct), JsonOpts);
        var payload = Payload(doc.RootElement, "data", "expiryDates", "expiry_dates");

        var epochs = new SortedSet<long>();
        foreach (var item in Elements(payload))
        {
            var v = item.ValueKind == JsonValueKind.Object
                ? FirstNumber(item, "expiry", "expiry_epoch", "expiryDate", "date")
                : Number(item);
            if (v is not null and > 0)
                epochs.Add((long)v.Value);
        }
        return epochs.ToArray();
    }

    /// <summary>
    /// Contract symbols listed for <paramref name="underlying"/> at
    /// <paramref name="expiry"/> (futures + every option strike/type). Port of
    /// the SDK's <c>history_underlying_symbols</c>
    /// (<c>/history/fno/expired/underlying-symbols</c>).
    /// </summary>
    public IReadOnlyList<string> ExpiredUnderlyingSymbols(string underlying, DateOnly expiry,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(underlying))
            .Append("&expiry_date=").Append(Date(expiry));

        using var doc = JsonDocument.Parse(GetRaw(ExpiredUnderlyingSymbolsPath, qs.ToString(), ct), JsonOpts);
        var payload = Payload(doc.RootElement, "data", "symbols", "underlyingSymbols");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new List<string>();
        foreach (var item in Elements(payload))
        {
            var sym = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : FirstString(item, "symbol", "symTicker", "fyToken");
            if (!string.IsNullOrWhiteSpace(sym) && seen.Add(sym!))
                symbols.Add(sym!);
        }
        return symbols;
    }

    /// <summary>
    /// 1-minute (or chosen resolution) candles for one expired F&amp;O contract —
    /// port of the SDK's <c>fno_historical_data</c>
    /// (<c>/history/fno/expired/historical-data</c>). The response carries the
    /// same <c>candles</c> array as <c>/data/history</c> (with optional oi/greeks
    /// columns appended); this parser keeps the first six OHLCV fields exactly as
    /// <see cref="History(string, DateOnly, DateOnly, bool, bool, string, CancellationToken)"/> does.
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

        var payload = Payload(root, "candles", "data");
        var rows = new List<CandleRow>();
        foreach (var candle in Elements(payload))
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

    /// <summary>The first of the named properties that holds an array, else the root.</summary>
    private static JsonElement Payload(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Array)
                return v;
        }
        return root;
    }

    private static IEnumerable<JsonElement> Elements(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Array
            ? payload.EnumerateArray()
            : Array.Empty<JsonElement>();

    private static decimal? Number(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
            return e.GetDecimal();
        if (e.ValueKind == JsonValueKind.String
            && decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private static decimal? FirstNumber(JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (obj.TryGetProperty(k, out var v))
            {
                var n = Number(v);
                if (n is not null)
                    return n;
            }
        }
        return null;
    }

    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        return null;
    }
}
