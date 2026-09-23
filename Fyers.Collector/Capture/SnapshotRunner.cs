using System.Globalization;
using System.Text.Json;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Fyers.Core.Storage;
using Fyers.Core.Universe;

namespace Fyers.Collector.Capture;

/// <summary>One minute's capture — port of <c>src/collector.py:snapshot_once</c>.
/// Full mode: index chains (nearest + selected expiries), 59-stock monthly
/// chains, then the batched quotes (extended by the NIFTY 500 quote-only
/// universe). QuotesOnly: quotes only. Single transaction per minute via
/// <see cref="Storage"/>; over-budget skips logged, minute committed partial.</summary>
public sealed class SnapshotRunner(
    AppConfig cfg,
    FyersClient client,
    InstrumentMaster master,
    Universe universe,
    Storage storage,
    ILogger? log = null)
{
    /// <summary>Long from a JSON number OR numeric string — Fyers sends
    /// expiryData.expiry as a string (found live 2026-09-15 22:59, first
    /// --once run). Mirrors FyersClient.LongOrNull.</summary>
    internal static long Elong(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt64(),
        JsonValueKind.String => long.TryParse(e.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var i) ? i : 0,
        _ => 0,
    };

    internal static readonly TimeZoneInfo IstZone =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    public async Task<bool> RunAsync(DateTime istNow, string mode, CancellationToken ct = default)
    {
        var istMinute = istNow.AddSeconds(-istNow.Second)
            .AddMilliseconds(-istNow.Millisecond);
        var tsUtc = TimeZoneInfo.ConvertTimeToUtc(istMinute, IstZone);
        var day = DateOnly.FromDateTime(istNow);

        List<LegRow> optRows = [];
        List<QuoteRow> quoteRows = [];
        QuoteRow? vixRow = null;
        var errors = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (mode == "full")
            {
                var (idxRows, vix) = await FetchIndexChainsAsync(istMinute, tsUtc, ct);
                optRows.AddRange(idxRows);
                vixRow = vix;
                if (cfg.ConstituentsEnabled && universe.ConstituentTickers.Count > 0)
                {
                    log?.LogInformation("constituent F&O option chains: {Count} tickers x {Exp} expiries",
                        universe.ConstituentTickers.Count, cfg.StockNMonthly);
                    var (stockRows, stockErr) = await FetchStockChainsAsync(istMinute, tsUtc, ct);
                    optRows.AddRange(stockRows);
                    errors += stockErr;
                }
            }

            // ---- batched quotes (both modes), extended universe ----
            var quoteSpecs = universe.BuildQuoteSpecs(cfg);
            var rows = await Task.Run(() => client.Quotes(quoteSpecs, cfg.QuotesChunk, ct), ct);
            log?.LogInformation("quotes batch: {Specs} specs, {Returned} returned (mode={Mode})",
                quoteSpecs.Count, rows.Count, mode);
            var bySym = rows.GroupBy(r => r.Symbol).ToDictionary(g => g.Key, g => g.First());
            foreach (var spec in quoteSpecs)
            {
                if (bySym.TryGetValue(spec.Symbol, out var row)) { quoteRows.Add(row); continue; }
                if (spec.Type == "VIX" && vixRow is not null) { quoteRows.Add(vixRow); continue; }
                errors++;
                if (errors <= 5) log?.LogWarning("quote not returned: {Symbol} ({Type})", spec.Symbol, spec.Type);
            }
            var missing = quoteSpecs.Count - quoteRows.Count;
            if (missing > 0)
            {
                var returnedSyms = quoteRows.Select(r => r.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byType = quoteSpecs.Where(sp => !returnedSyms.Contains(sp.Symbol))
                    .GroupBy(sp => sp.Type).Select(g => $"{g.Key}={g.Count()}");
                log?.LogInformation("quotes missing breakdown: {Breakdown}", string.Join(", ", byType));
            }
        }
        catch (AuthExpiredException) { throw; }
        catch (RateLimitedException e)
        {
            log?.LogWarning("snapshot hit limiter at top level: {Message}", e.Message);
            errors++;
        }

        var fetchMs = (int)sw.ElapsedMilliseconds;
        try
        {
            storage.WriteLegs(day, optRows);
            storage.WriteQuotes(day, quoteRows);
            storage.MarkSnapshot(day, istMinute, mode, optRows.Count, quoteRows.Count, fetchMs, errors);
        }
        catch (Exception e)
        {
            log?.LogError("DB insert failed (rolled back): {Message}", e.Message);
            return false;
        }

        log?.LogInformation("SNAPSHOT {Minute} IST [{Mode}]: {Legs} option legs, {Quotes} quote rows, {Ms}ms, errors={Errors}",
            istMinute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            mode, optRows.Count, quoteRows.Count, fetchMs, errors);
        return errors == 0;
    }

    /// <summary>Port of <c>select_expiries</c>: n weekly (W flag) + n monthly (M)
    /// + n December monthlies, deduped in that order.</summary>
    internal static List<JsonElement> SelectExpiries(JsonElement expiryData, bool includeWeekly,
        int nWeekly, int nMonthly, int nYearly)
    {
        var weeks = new List<JsonElement>();
        var months = new List<JsonElement>();
        foreach (var e in expiryData.EnumerateArray())
        {
            var flag = e.TryGetProperty("expiry_flag", out var f) ? f.GetString() : null;
            if (flag == "W") weeks.Add(e);
            else if (flag == "M") months.Add(e);
        }
        var selected = new List<JsonElement>();
        if (includeWeekly) selected.AddRange(weeks.Take(nWeekly));
        selected.AddRange(months.Take(nMonthly));
        if (nYearly > 0)
        {
            var decs = new List<(int Year, JsonElement E)>();
            foreach (var e in months)
            {
                if (!e.TryGetProperty("expiry", out var exp)) continue;
                var d = DateTimeOffset.FromUnixTimeSeconds(Elong(exp)).UtcDateTime;
                if (d.Month == 12) decs.Add((d.Year, e));
            }
            foreach (var de in decs.OrderBy(x => x.Year).Take(nYearly))
                if (!selected.Any(s => s.ToString() == de.E.ToString()))
                    selected.Add(de.E);
        }
        return selected;
    }

    private async Task<(List<LegRow>, QuoteRow?)> FetchIndexChainsAsync(
        DateTime istMinute, DateTime tsUtc, CancellationToken ct)
    {
        var optRows = new List<LegRow>();
        QuoteRow? vixRow = null;
        foreach (var sym in cfg.Symbols)
        {
            string nearBody;
            try { nearBody = client.OptionsChain(sym.ChainSymbol, cfg.StrikeCount, true, null, ct); }
            catch (RateLimitedException)
            {
                log?.LogWarning("index {Underlying} nearest chain skipped by limiter", sym.Underlying);
                continue;
            }
            using var nearDoc = JsonDocument.Parse(nearBody);
            var near = nearDoc.RootElement.GetProperty("data");
            var legs = near.TryGetProperty("optionsChain", out var oc) ? oc.GetArrayLength() : 0;
            log?.LogInformation("{Underlying} nearest chain: {Legs} legs", sym.Underlying, legs);
            if (vixRow is null && near.TryGetProperty("indiavixData", out var vixData) &&
                vixData.ValueKind == JsonValueKind.Object)
                vixRow = VixFromInline(vixData, tsUtc, istMinute);

            if (!near.TryGetProperty("expiryData", out var expiryData)) continue;
            var selected = SelectExpiries(expiryData, sym.Weekly,
                cfg.NWeekly, cfg.NMonthly, cfg.NYearly);
            log?.LogInformation("{Underlying} selected {Count} expiries", sym.Underlying, selected.Count);
            if (selected.Count == 0) continue;
            var nearExp = selected[0].TryGetProperty("expiry", out var ne) ? Elong(ne) : 0;
            foreach (var e in selected)
            {
                var expEpoch = e.TryGetProperty("expiry", out var ex) ? Elong(ex) : 0;
                var body = nearBody;
                if (expEpoch != 0 && expEpoch != nearExp)
                {
                    try { body = client.OptionsChain(sym.ChainSymbol, cfg.StrikeCount, true, expEpoch, ct); }
                    catch (RateLimitedException)
                    {
                        var date = e.TryGetProperty("date", out var dd) ? dd.GetString() : "?";
                        log?.LogWarning("{Underlying} expiry {Date} skipped by limiter", sym.Underlying, date);
                        continue;
                    }
                }
                var rows = client.ParseLegs(body, sym.Underlying, tsUtc, istMinute).ToList();
                // the parser takes each leg's own/chain expiry; python pins the selected expiry — mirror that
                for (var i = 0; i < rows.Count; i++)
                    rows[i] = rows[i] with { ExpiryEpoch = expEpoch != 0 ? expEpoch : rows[i].ExpiryEpoch };
                optRows.AddRange(rows);
            }
        }
        return (optRows, vixRow);
    }

    private async Task<(List<LegRow>, int)> FetchStockChainsAsync(DateTime istMinute, DateTime tsUtc, CancellationToken ct)
    {
        var optRows = new List<LegRow>();
        var tickers = universe.ConstituentTickers;
        var n = tickers.Count;
        var errs = 0;
        for (var i = 0; i < n; i++)
        {
            var ticker = tickers[i];
            var chainSym = master.CashSymbol(ticker);
            if (chainSym is null) continue;
            string nearBody;
            try { nearBody = client.OptionsChain(chainSym, cfg.StockStrikecount, cfg.StockGreeks, null, ct); }
            catch (RateLimitedException)
            {
                log?.LogWarning("stock {Ticker} nearest chain skipped by limiter ({I}/{N})", ticker, i + 1, n);
                continue;
            }
            catch (Exception e)
            {
                // one bad symbol must not kill the minute (python: supervised
                // restart; here: partial minute with errors>0)
                log?.LogWarning("stock {Ticker} chain {Chain} failed ({I}/{N}): {Message}",
                    ticker, chainSym, i + 1, n, e.Message);
                errs++;
                continue;
            }
            List<long> expiries;
            // materialize plain values INSIDE the using — JsonElement dies with
            // its JsonDocument (found live 2026-09-15 23:0x, ObjectDisposedException)
            using (var doc = JsonDocument.Parse(nearBody))
            {
                var data = doc.RootElement.GetProperty("data");
                var selected = data.TryGetProperty("expiryData", out var ed)
                    ? SelectExpiries(ed, includeWeekly: false, 0, cfg.StockNMonthly, 0)
                    : [];
                expiries = selected
                    .Select(s => s.TryGetProperty("expiry", out var ex) ? Elong(ex) : 0)
                    .Where(x => x != 0)
                    .ToList();
            }
            if (expiries.Count == 0)
            {
                log?.LogInformation("stock {Ticker}: no expiries ({I}/{N})", ticker, i + 1, n);
                continue;
            }
            var nearExp = expiries[0];
            foreach (var expEpoch in expiries)
            {
                var body = nearBody;
                if (expEpoch != 0 && expEpoch != nearExp)
                {
                    try { body = client.OptionsChain(chainSym, cfg.StockStrikecount, cfg.StockGreeks, expEpoch, ct); }
                    catch (RateLimitedException)
                    {
                        log?.LogWarning("stock {Ticker} expiry skipped by limiter", ticker);
                        continue;
                    }
                }
                try
                {
                    var rows = client.ParseLegs(body, ticker, tsUtc, istMinute).ToList();
                    for (var j = 0; j < rows.Count; j++)
                        rows[j] = rows[j] with { ExpiryEpoch = expEpoch != 0 ? expEpoch : rows[j].ExpiryEpoch };
                    optRows.AddRange(rows);
                }
                catch (Exception pex)
                {
                    log?.LogWarning("stock {Ticker} expiry {Epoch} parse failed: {Message}",
                        ticker, expEpoch, pex.Message);
                    errs++;
                }
            }
            if ((i + 1) % 10 == 0)
                log?.LogInformation("stock chains: {Done}/{N} done, {Legs} legs so far", i + 1, n, optRows.Count);
        }
        return (optRows, errs);
    }

    /// <summary>Inline <c>indiavixData</c> fallback (full mode only) — port of
    /// <c>_parse_vix</c>, first-present-key semantics on lp/ltp.</summary>
    private QuoteRow VixFromInline(JsonElement v, DateTime tsUtc, DateTime istMinute)
    {
        static decimal? Q(JsonElement e, params string[] keys)
        {
            foreach (var k in keys)
                if (e.TryGetProperty(k, out var val) && val.ValueKind is JsonValueKind.Number)
                    return val.GetDecimal();
            return null;
        }
        return new QuoteRow(
            Symbol: v.TryGetProperty("symbol", out var s) && s.GetString() is { Length: > 0 } ss ? ss : Universe.VixSymbol,
            Type: "VIX", Underlying: null, ExpiryEpoch: null,
            Ltp: Q(v, "lp", "ltp") ?? 0m, Open: Q(v, "open", "o"), High: Q(v, "high", "h"),
            Low: Q(v, "low", "l"), PrevClose: Q(v, "prev_close", "pdc"), Volume: Q(v, "volume"),
            Spread: Q(v, "spread"), TsUtc: tsUtc, IstMinute: istMinute, InstrumentType: "VIX");
    }
}
