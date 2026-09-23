using System.Globalization;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Microsoft.Extensions.Logging;

namespace Fyers.Core.Universe;

/// <summary>Constituent universe (NIFTY50 + BANKNIFTY) plus the NIFTY 500
/// quote-only extension — ports of <c>src/constituents.py</c> refreshed from the
/// NSE archives CSVs. For phase 1 the day's universe is held in memory; the
/// dated <c>constituents</b> summary-table persistence lands with tonight's
/// summary-db work.</summary>
public sealed class Universe
{
    public const string VixSymbol = "NSE:INDIAVIX-INDEX";
    public const string Nifty50Url = "https://archives.nseindia.com/content/indices/ind_nifty50list.csv";
    public const string NiftyBankUrl = "https://archives.nseindia.com/content/indices/ind_niftybanklist.csv";
    public const string Nifty500Url = "https://archives.nseindia.com/content/indices/ind_nifty500list.csv";

    public IReadOnlyList<string> ConstituentTickers { get; private set; } = [];
    public IReadOnlyList<string> Nifty500Tickers { get; private set; } = [];

    private readonly HttpClient _http;
    private readonly Fyers.InstrumentMaster _master;
    private readonly ILogger? _log;

    public Universe(HttpClient http, Fyers.InstrumentMaster master, ILogger? log = null)
    {
        _http = http;
        _master = master;
        _log = log;
    }

    /// <summary>Refresh constituents (+ NIFTY 500 when enabled). Never throws —
    /// on failure the universe stays empty so indices still capture.</summary>
    public async Task RefreshAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var cons = new List<string>();
        try
        {
            if (cfg.ConstituentsEnabled)
            {
                cons.AddRange(await TickersFromCsv(Nifty50Url, ct));
                cons.AddRange(await TickersFromCsv(NiftyBankUrl, ct));
                cons = cons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch (Exception e)
        {
            _log?.LogWarning("constituent universe unavailable: {Message}", e.Message);
        }
        ConstituentTickers = cons;

        if (cfg.QuoteUniverse.Enabled)
        {
            try
            {
                Nifty500Tickers = (await TickersFromCsv(Nifty500Url, ct))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e)
            {
                _log?.LogWarning("nifty500 universe unavailable: {Message}", e.Message);
                Nifty500Tickers = [];
            }
        }
        _log?.LogInformation("constituents refreshed: {Cons} names (+{N500} nifty500)",
            cons.Count, Nifty500Tickers.Count);
    }

    private async Task<List<string>> TickersFromCsv(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(Fyers.FyersClient.UserAgent);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct);
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var header = Array.FindIndex(lines, l => l.Split(',').Any(h => h.Trim().Equals("Symbol", StringComparison.OrdinalIgnoreCase)));
        if (header < 0) throw new InvalidDataException($"no Symbol header in {url}");
        // "Symbol" is NOT column 0 — column 0 is the company name (found live
        // 2026-09-15 23:03: cash symbols came out as "NSE:Tech Mahindra Ltd.-EQ")
        var symCol = Array.FindIndex(lines[header].Split(','),
            h => h.Trim().Equals("Symbol", StringComparison.OrdinalIgnoreCase));
        var outList = new List<string>();
        for (var i = header + 1; i < lines.Length; i++)
        {
            var cols = lines[i].Split(',');
            if (cols.Length > symCol && cols[symCol].Trim() is { Length: > 0 } t)
                outList.Add(t.Trim());
        }
        return outList;
    }

    /// <summary>Sectoral index spot ticker: NIFTYIT -> NSE:NIFTYIT-INDEX.</summary>
    public static string IndexSpotSymbol(string name) => $"NSE:{name}-INDEX";

    /// <summary>Port of <c>_build_quote_specs</c> extended by the NIFTY 500
    /// quote-only universe: index futures + spots + VIX, cash for every
    /// constituent, F&O-member futures at cfg.StockFuturesNMonths for the 59,
    /// then 500-cash (deduped) and 500-only F&O futures at
    /// cfg.QuoteUniverse.FuturesNMonths. No option chains for the 500.
    ///
    /// When cfg.MacroUniverse is enabled the macro universe is appended at the end:
    /// commodity futures (NSE_COM), currency futures (NSE_CDS) and, per index_spots
    /// name, the FO futures that exist for it (MIDCPNIFTY is an index WITH futures —
    /// it is absent from the constituent CSVs) plus one NSE:{name}-INDEX spot.
    /// Everything is deduped against the specs built above.</summary>
    public IReadOnlyList<Fyers.QuoteSpec> BuildQuoteSpecs(AppConfig cfg)
    {
        var specs = new List<Fyers.QuoteSpec>();
        foreach (var sym in cfg.Symbols)
        {
            foreach (var fc in _master.FuturesContracts(sym.Underlying, cfg.FuturesNMonths))
                specs.Add(new(fc.Symbol, "FUTURE", sym.Underlying, fc.ExpiryEpoch));
            specs.Add(new(sym.SpotSymbol, "SPOT", sym.Underlying, null));
        }
        specs.Add(new(VixSymbol, "VIX", null, null));

        var seenCash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddName(string ticker, int futMonths)
        {
            var cash = _master.CashSymbol(ticker);
            if (cash is not null && seenCash.Add(cash))
                specs.Add(new(cash, "CASH", ticker, null));
            if (futMonths > 0 && seenFut.Add(ticker))
                foreach (var fc in _master.FuturesContracts(ticker, futMonths))
                    specs.Add(new(fc.Symbol, "FUTURE", ticker, fc.ExpiryEpoch));
        }

        foreach (var t in ConstituentTickers) AddName(t, cfg.StockFuturesNMonths);
        if (cfg.QuoteUniverse.Enabled)
            foreach (var t in Nifty500Tickers) AddName(t, cfg.QuoteUniverse.FuturesNMonths);

        AppendMacroSpecs(cfg, specs);
        return specs;
    }

    /// <summary>
    /// The macro universe (cfg.MacroUniverse), appended when enabled and deduped
    /// against every symbol already in <paramref name="specs"/> (MIDCPNIFTY's spot or
    /// futures may already be there via cfg.Symbols). A commodity/currency with no
    /// monthly contract in its segment master contributes nothing; one with fewer
    /// monthlies than n_months contributes what exists (SILVER's nearest monthly is
    /// December) — the master lookup already returns "up to n".
    /// Never throws: a missing/unloadable segment master just yields no futures and
    /// the index spots still capture.
    /// </summary>
    private void AppendMacroSpecs(AppConfig cfg, List<Fyers.QuoteSpec> specs)
    {
        var macro = cfg.MacroUniverse;
        if (!macro.Enabled || macro.NMonths <= 0)
            return;

        // One symbol set over everything built above — the macro specs may not repeat it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
            seen.Add(s.Symbol);

        void Add(string symbol, string type, string? underlying, long? expiry)
        {
            if (seen.Add(symbol))
                specs.Add(new(symbol, type, underlying, expiry));
        }

        try
        {
            foreach (var commodity in macro.Commodities)
                foreach (var fc in _master.FuturesContracts(commodity, macro.NMonths, Fyers.InstrumentMaster.SegmentCom))
                    Add(fc.Symbol, "FUTURE", commodity, fc.ExpiryEpoch);

            foreach (var currency in macro.Currencies)
                foreach (var fc in _master.FuturesContracts(currency, macro.NMonths, Fyers.InstrumentMaster.SegmentCds))
                    Add(fc.Symbol, "FUTURE", currency, fc.ExpiryEpoch);

            foreach (var name in macro.IndexSpots)
            {
                // FO futures for the index names that have them (MIDCPNIFTY today);
                // the rest resolve to nothing and only the spot below is quoted.
                foreach (var fc in _master.FuturesContracts(name, macro.NMonths))
                    Add(fc.Symbol, "FUTURE", name, fc.ExpiryEpoch);
                Add(IndexSpotSymbol(name), "INDEX", null, null);
            }
        }
        catch (Exception e)
        {
            _log?.LogWarning("macro universe partially skipped: {Message}", e.Message);
        }
    }
}
