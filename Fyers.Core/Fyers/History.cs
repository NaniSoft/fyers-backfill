using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fyers.Core.Fyers;

/// <summary>
/// One 1-minute OHLCV bar fetched from <c>/data/history</c>. The wire candle is
/// <c>[epoch, open, high, low, close, volume(, oi)]</c>; this record keeps the
/// six fields the collector stores (the symbol is attached by the client) and
/// drops the optional 7th (oi) — python's <c>candle_rows</c> keeps it, this port
/// does not (see the notes on <see cref="FyersClient.History(string, DateOnly, bool, bool, CancellationToken)"/>).
/// </summary>
public sealed record CandleRow(
    string Symbol,
    long TsUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

// NB: FyersClient.cs declares this type `partial` so the history endpoint can live
// in its own file (this one), mirroring the python layout where src/candles.py is
// a separate module from src/fyers_client.py:history(). No state is added here.

/// <summary>History half of <see cref="FyersClient"/> — <c>GET /data/history</c>.</summary>
public sealed partial class FyersClient
{
    /// <summary>Path under <see cref="BaseUrl"/> (python <c>_get("/history", params)</c>).</summary>
    private const string HistoryPath = "/history";

    /// <summary>1-minute resolution (docs' resolution list: "1 minute : \"1\"").</summary>
    public const string HistoryResolution1Min = "1";

    /// <summary>
    /// One day's 1-minute candles — the collector call: <c>range_from == range_to</c>
    /// set to the IST day, no oi/continuation flags. Port of python
    /// <c>client.history(symbol, date_s, date_s)</c> as <c>src/candles.py:_fetch_one</c>
    /// issues it for spot/VIX/cash instruments.
    /// </summary>
    public IReadOnlyList<CandleRow> History(string symbol, DateOnly day, CancellationToken ct = default)
        => History(symbol, day, oiFlag: false, contFlag: false, ct);

    /// <summary>
    /// OHLCV candles from <c>/data/history</c> for <paramref name="day"/> (IST) —
    /// port of <c>src/fyers_client.py:FyersClient.history</c>.
    ///
    /// Query (python parity, field for field): <c>symbol</c>, <c>resolution=1</c>,
    /// <c>date_format=1</c> (so <c>range_from</c>/<c>range_to</c> are the day's
    /// <c>yyyy-MM-dd</c> bounds — python passes the date string twice, which a live
    /// run verified returns the full 09:15–15:59 minute grid), optional
    /// <c>oi_flag=1</c> for derivatives and <c>cont_flag=1</c> for futures
    /// (python: <c>oi_for</c>/<c>cont_for</c>).
    ///
    /// Response states — Fyers has THREE, only two of them documented:
    ///   * <c>s=ok</c>      -> the <c>candles</c> array (possibly empty),
    ///   * <c>s=no_data</c> -> zero rows, NOT an error (undocumented; seen live for
    ///     expired/never-traded instruments — research fyers-history-api.md §7.5).
    ///     An <c>ok</c> body with an empty/missing <c>candles</c> array is the same
    ///     terminal state (python: <c>status == "no_data" or not raw</c>),
    ///   * <c>s=error</c>   -> <see cref="InvalidOperationException"/> (parity with
    ///     python's <c>RuntimeError</c> and the client's <c>EnsureOk</c> message form).
    /// Token problems surface as <see cref="AuthExpiredException"/> from the
    /// transport before this parsing runs, exactly as python's AuthExpired
    /// propagates out of <c>history()</c>.
    /// </summary>
    public IReadOnlyList<CandleRow> History(string symbol, DateOnly day, bool oiFlag, bool contFlag,
        CancellationToken ct = default)
        => History(symbol, day, day, oiFlag, contFlag, HistoryResolution1Min, ct);

    /// <summary>
    /// OHLCV candles for an arbitrary inclusive <c>[from, to]</c> window — the
    /// ranged form of <see cref="History(string, DateOnly, bool, bool, CancellationToken)"/>
    /// added for the historical backfill (the collector only ever needs one day,
    /// the backfill needs 100-day windows). Same query contract; the caller is
    /// responsible for keeping the window within Fyers' 100-day-per-request cap.
    /// </summary>
    public IReadOnlyList<CandleRow> History(string symbol, DateOnly from, DateOnly to, bool oiFlag,
        bool contFlag, string resolution = HistoryResolution1Min, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);

        var fromS = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toS = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(symbol))
            .Append("&resolution=").Append(Uri.EscapeDataString(resolution))
            .Append("&date_format=1")
            .Append("&range_from=").Append(fromS)
            .Append("&range_to=").Append(toS);
        if (oiFlag)
            qs.Append("&oi_flag=1");
        if (contFlag)
            qs.Append("&cont_flag=1");

        var body = GetRaw(HistoryPath, qs.ToString(), ct);
        using var doc = JsonDocument.Parse(body, JsonOpts);
        var root = doc.RootElement;
        var s = Str(root, "s");

        // Real third state: request valid, zero candles for that symbol/window.
        if (string.Equals(s, "no_data", StringComparison.Ordinal))
            return [];

        if (!string.Equals(s, "ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"history {symbol} -> {s}: {Str(root, "message") ?? ""}");

        var rows = new List<CandleRow>();
        if (!root.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array)
            return rows;                       // python: body.get("candles") or []

        foreach (var candle in candles.EnumerateArray())
        {
            // python indexes c[0]..c[5] (c[6] = oi when oi_flag=1); a short/odd row
            // would IndexError there — skipped here instead of aborting the day.
            if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() < 5)
                continue;

            var epoch = NumAt(candle, 0);
            rows.Add(new CandleRow(
                Symbol: symbol,
                TsUtc: epoch is null ? 0L : (long)epoch.Value,      // python int(c[0])
                Open: NumAt(candle, 1) ?? 0m,
                High: NumAt(candle, 2) ?? 0m,
                Low: NumAt(candle, 3) ?? 0m,
                Close: NumAt(candle, 4) ?? 0m,
                Volume: candle.GetArrayLength() > 5 ? NumAt(candle, 5) ?? 0m : 0m));
        }

        return rows;
    }

    /// <summary>Nth element of a candle array as a decimal (JSON number or numeric string).</summary>
    private static decimal? NumAt(JsonElement candle, int index)
    {
        if (index >= candle.GetArrayLength())
            return null;
        var v = candle[index];
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDecimal();
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
