using System.Text;
using System.Text.Json;

namespace Fyers.Core.Fyers;

/// <summary>
/// One <c>/data/depth</c> snapshot for a single symbol — the live-OI feed the
/// collector polls per minute for the currency + MIDCPNIFTY futures.
///
/// Verified live 2026-09-15: the endpoint answers
/// <c>{"s":"ok","d":{"&lt;symbol&gt;":{ltp, oi, pdoi, v, atp, bids[], asks[],
/// lower_ckt, upper_ckt, o, h, l, c, chp}}}</c> — <c>d</c> is an OBJECT KEYED BY
/// SYMBOL (one symbol per call), not the array <c>/data/quotes</c> returns.
/// Equity/index/currency futures carry a real <c>oi</c>; NSE COMMODITY contracts
/// always answer <c>oi=0</c> (Fyers feed gap) — they are deliberately NOT
/// depth-polled.
///
/// The first eight fields are the record the OI task pinned (order kept); the
/// trailing depth-only fields carry the rest of what the quotes row wants, so a
/// replaced quotes row does not lose top-of-book / OHLC.
/// </summary>
/// <param name="Symbol">The payload's own <c>symbol</c> field when Fyers includes
/// one, else the requested symbol. Callers keying a row should prefer the symbol
/// they ASKED for — the write path in <c>OiService</c> does.</param>
/// <param name="Ltp">d[sym].ltp.</param>
/// <param name="Oi">d[sym].oi — open interest in contracts/shares, long-or-string.</param>
/// <param name="PrevDayOi">d[sym].pdoi — previous day's OI (long-or-string).</param>
/// <param name="Volume">d[sym].v — day volume.</param>
/// <param name="Atp">d[sym].atp — average traded price.</param>
/// <param name="TsUtc">UTC instant of the snapshot minute (second/ms truncated).</param>
/// <param name="IstMinute">IST wall clock of the same minute (the quotes row key).</param>
/// <param name="Open">d[sym].o.</param>
/// <param name="High">d[sym].h.</param>
/// <param name="Low">d[sym].l.</param>
/// <param name="Close">d[sym].c (== ltp for a live snapshot).</param>
/// <param name="Bid">bids[0].price — top of book, null when the book is empty/absent.</param>
/// <param name="Ask">asks[0].price.</param>
/// <param name="BidSize">bids[0].volume.</param>
/// <param name="AskSize">asks[0].volume.</param>
/// <param name="Chp">d[sym].chp — % change over previous close.</param>
public sealed record DepthRow(
    string Symbol,
    decimal? Ltp,
    long? Oi,
    long? PrevDayOi,
    decimal? Volume,
    decimal? Atp,
    DateTime TsUtc,
    DateTime IstMinute,
    decimal? Open = null,
    decimal? High = null,
    decimal? Low = null,
    decimal? Close = null,
    decimal? Bid = null,
    decimal? Ask = null,
    decimal? BidSize = null,
    decimal? AskSize = null,
    decimal? Chp = null);

// NB: FyersClient.cs declares this type `partial`, so the depth endpoint lives in
// its own file next to History.cs (mirroring the python layout where the extra
// endpoints hang off src/fyers_client.py). No state is added here — every helper
// used below (GetJson/EnsureOk, Str, Q, LongOrNull, SnapshotMinute, EscapeSymbol)
// is the client's own.

/// <summary>Depth half of <see cref="FyersClient"/> — <c>GET /data/depth</c>.</summary>
public sealed partial class FyersClient
{
    /// <summary>Path under <see cref="BaseUrl"/> (python <c>_get("/depth", params)</c>).</summary>
    private const string DepthPath = "/depth";

    /// <summary>
    /// One symbol's market depth + OI — <c>symbol=...&amp;ohlcv_flag=1</c>.
    /// The OHLCV flag is what makes Fyers answer the o/h/l/c/ltp/v block; without
    /// it the body carries only the book.
    ///
    /// Errors: <c>s != "ok"</c> => <see cref="InvalidOperationException"/>
    /// (<see cref="EnsureOk"/>, parity with the other endpoints);
    /// <see cref="AuthExpiredException"/> and <see cref="RateLimitedException"/>
    /// flow through from the transport untouched — the caller decides skip/retry
    /// (the OI service skips the minute on auth, counts everything else).
    /// A body with an empty/absent <c>d</c>, or one whose key does not match the
    /// requested symbol, is not an error: zero rows (the caller counts the miss).
    /// </summary>
    public IReadOnlyList<DepthRow> Depth(string symbol, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var qs = new StringBuilder("symbol=").Append(EscapeSymbol(symbol))
            .Append("&ohlcv_flag=1");

        var (tsUtc, istMinute) = SnapshotMinute();
        var rows = new List<DepthRow>(1);
        // Materialize every field INSIDE the using — a JsonElement dies with its
        // JsonDocument (found live 2026-09-15 23:0x on the stock-chain path).
        using (var doc = GetJson(DepthPath, qs.ToString(), ct))
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Object)
                return rows;                       // python: body.get("d") or {}

            // Fyers keys d by the symbol it answered. Prefer the exact key; a
            // mismatched-but-single entry (e.g. a normalized ticker) is still the
            // answer to THIS request, so take it rather than dropping the minute.
            JsonElement v = default;
            var have = false;
            if (d.TryGetProperty(symbol, out var exact) && exact.ValueKind == JsonValueKind.Object)
            {
                v = exact;
                have = true;
            }
            else
            {
                foreach (var prop in d.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    v = prop.Value;
                    have = true;
                    break;
                }
            }
            if (!have)
                return rows;

            var (bid, bidSize) = TopOfBook(v, "bids");
            var (ask, askSize) = TopOfBook(v, "asks");
            rows.Add(new DepthRow(
                Symbol: Str(v, "symbol") ?? symbol,
                Ltp: Q(v, "ltp", "lp"),
                Oi: LongOrNull(v, "oi"),
                PrevDayOi: LongOrNull(v, "pdoi"),
                Volume: Q(v, "v", "volume"),
                Atp: Q(v, "atp"),
                TsUtc: tsUtc,
                IstMinute: istMinute,
                Open: Q(v, "open_price", "open", "o"),
                High: Q(v, "high_price", "high", "h"),
                Low: Q(v, "low_price", "low", "l"),
                Close: Q(v, "c", "close_price", "close"),
                Bid: bid,
                Ask: ask,
                BidSize: bidSize,
                AskSize: askSize,
                Chp: Q(v, "chp")));
        }
        return rows;
    }

    /// <summary>
    /// Top of book: <c>book[0]</c>'s price + size. Fyers depth levels are
    /// <c>{"price":..,"volume":..,"orders":..}</c>; an empty/absent/malformed
    /// book (illiquid contracts, offline feeds) yields (null, null) — never a throw.
    /// </summary>
    private static (decimal? Price, decimal? Size) TopOfBook(JsonElement v, string book)
    {
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty(book, out var levels)
            || levels.ValueKind != JsonValueKind.Array
            || levels.GetArrayLength() == 0)
            return (null, null);
        var top = levels[0];
        if (top.ValueKind != JsonValueKind.Object)
            return (null, null);
        return (Q(top, "price", "p"), Q(top, "volume", "v"));
    }
}
