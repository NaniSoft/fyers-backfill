using System.Text.Json;
using Fyers.Backfill.Config;
using Fyers.Core.Fyers;
using Microsoft.Extensions.Logging;

namespace Fyers.Backfill.Instruments;

/// <summary>
/// Builds the instrument universe the backfill sweeps:
/// <list type="number">
/// <item>Cash equities from the <c>NSE_CM</c> master (<c>exSeries == "EQ"</c>).</item>
/// <item>Live futures + options from the <c>NSE_FO</c> master.</item>
/// <item>Expired futures + options discovered through the
/// <c>/data/history/fno/expired/*</c> endpoints, cached to disk so the discovery
/// cost is paid weekly rather than every run.</item>
/// </list>
/// An optional <c>universe.underlyings</c> whitelist narrows (1) and (2)/(3) by
/// underlying stem.
/// </summary>
public sealed class UniverseBuilder(BackfillConfig cfg, FyersClient client, ILogger log)
{
    public const string CmMasterUrl = "https://public.fyers.in/sym_details/NSE_CM_sym_master.json";
    public const string FoMasterUrl = "https://public.fyers.in/sym_details/NSE_FO_sym_master.json";

    /// <summary>Stems whose Fyers underlying symbol is an index, not a cash equity.</summary>
    private static readonly Dictionary<string, string> IndexSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = "NSE:NIFTY50-INDEX",
        ["BANKNIFTY"] = "NSE:NIFTYBANK-INDEX",
        ["FINNIFTY"] = "NSE:FINNIFTY-INDEX",
        ["MIDCPNIFTY"] = "NSE:MIDCPNIFTY-INDEX",
        ["NIFTYNXT50"] = "NSE:NIFTYNXT50-INDEX",
        ["INDIAVIX"] = "NSE:INDIAVIX-INDEX",
    };

    public async Task<IReadOnlyList<Instrument>> BuildAsync(CancellationToken ct)
    {
        var instruments = new List<Instrument>();
        Directory.CreateDirectory(cfg.MasterCache);

        if (cfg.UniverseEquities)
        {
            var cmPath = InstrumentMaster.EnsureCached(CmMasterUrl, "CM", cfg.MasterCache, log);
            instruments.AddRange(MasterScanner.Scan(cmPath, "CM")
                .Where(i => i.Kind == Instrument.KindEquity));
            log.LogInformation("universe: {Count} cash equities from NSE_CM", instruments.Count);
        }

        if (cfg.UniverseFutures || cfg.UniverseOptions)
        {
            var foPath = InstrumentMaster.EnsureCached(FoMasterUrl, "FO", cfg.MasterCache, log);
            var before = instruments.Count;
            foreach (var inst in MasterScanner.Scan(foPath, "FO"))
            {
                if (inst.Kind == Instrument.KindFuture && cfg.UniverseFutures)
                    instruments.Add(inst);
                else if (inst.Kind == Instrument.KindOption && cfg.UniverseOptions)
                    instruments.Add(inst);
            }
            log.LogInformation("universe: {Count} live F&O contracts from NSE_FO",
                instruments.Count - before);
        }

        if (cfg.UniverseExpired)
        {
            var expired = await ExpiredUniverseAsync(ct);
            instruments.AddRange(expired);
            log.LogInformation("universe: {Count} expired F&O contracts", expired.Count);
        }

        var filtered = ApplyWhitelist(instruments);
        log.LogInformation("universe: {Total} instruments after whitelist", filtered.Count);
        return filtered;
    }

    /// <summary>
    /// Expired contracts, cached at <c>&lt;master_cache&gt;/expired_universe.json</c>.
    /// Discovery is one <c>expiry-dates</c> call per underlying plus one
    /// <c>underlying-symbols</c> call per expiry — expensive, so the result is
    /// reused for <see cref="BackfillConfig"/>'s refresh window (7 days).
    /// </summary>
    public async Task<IReadOnlyList<Instrument>> ExpiredUniverseAsync(CancellationToken ct)
    {
        var cache = Path.Combine(cfg.MasterCache, "expired_universe.json");
        if (File.Exists(cache) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(7))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<Instrument>>(File.ReadAllText(cache));
                if (cached is { Count: > 0 })
                {
                    log.LogInformation("universe: expired cache hit ({Count} contracts)", cached.Count);
                    return cached;
                }
            }
            catch (Exception e)
            {
                log.LogWarning("expired cache unreadable, re-discovering: {Message}", e.Message);
            }
        }

        var stems = await DiscoverStemsAsync(ct);
        var instruments = new List<Instrument>();
        foreach (var stem in stems)
        {
            ct.ThrowIfCancellationRequested();
            var underlyingSymbol = UnderlyingSymbol(stem);
            try
            {
                var expiries = client.ExpiredExpiryDates(underlyingSymbol, cfg.From, cfg.EndDate, ct);
                foreach (var expiry in expiries)
                {
                    ct.ThrowIfCancellationRequested();
                    var expiryDate = DateOnly.FromDateTime(
                        DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime.AddHours(5.5));
                    foreach (var symbol in client.ExpiredUnderlyingSymbols(underlyingSymbol, expiryDate, ct))
                    {
                        instruments.Add(ClassifyExpired(symbol, stem, expiry));
                    }
                }
                log.LogInformation("universe: expired {Stem}: {Count} contracts over {Expiries} expiries",
                    stem, instruments.Count, expiries.Count);
            }
            catch (AuthExpiredException)
            {
                throw;
            }
            catch (Exception e)
            {
                log.LogWarning("universe: expired discovery for {Stem} failed: {Message}", stem, e.Message);
            }
        }

        if (instruments.Count > 0)
        {
            Directory.CreateDirectory(cfg.MasterCache);
            File.WriteAllText(cache, JsonSerializer.Serialize(instruments));
            log.LogInformation("universe: expired universe cached -> {Path}", cache);
        }
        return instruments;
    }

    /// <summary>
    /// The distinct underlying stems to probe. From the live F&amp;O master's
    /// futures/options, or the explicit whitelist when one is configured (the
    /// whitelist is what bounds the enormous expired universe).
    /// </summary>
    private async Task<IReadOnlyList<string>> DiscoverStemsAsync(CancellationToken ct)
    {
        if (cfg.Underlyings.Count > 0)
            return cfg.Underlyings;

        await Task.CompletedTask;
        var foPath = InstrumentMaster.EnsureCached(FoMasterUrl, "FO", cfg.MasterCache, log);
        var stems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in MasterScanner.Scan(foPath, "FO"))
        {
            ct.ThrowIfCancellationRequested();
            if (inst.Underlying is { Length: > 0 } u)
                stems.Add(u);
        }
        return stems.ToArray();
    }

    /// <summary>Fyers underlying symbol for a stem (index map, else the cash-equity form).</summary>
    public static string UnderlyingSymbol(string stem)
        => IndexSymbols.TryGetValue(stem, out var idx) ? idx : $"NSE:{stem}-EQ";

    /// <summary>
    /// Classify one expired contract symbol. Fyers option tickers end in CE/PE
    /// (e.g. <c>NSE:NIFTY26MAR24800CE</c>); everything else is treated as a future.
    /// </summary>
    private static Instrument ClassifyExpired(string symbol, string stem, long expiryEpoch)
    {
        var upper = symbol.ToUpperInvariant();
        var optionType = upper.EndsWith("CE", StringComparison.Ordinal) ? "CE"
            : upper.EndsWith("PE", StringComparison.Ordinal) ? "PE"
            : null;
        var kind = optionType is null ? Instrument.KindFuture : Instrument.KindOption;
        return new Instrument(symbol, kind, Underlying: stem, ExpiryEpoch: expiryEpoch,
            OptionType: optionType, Segment: "FO", Expired: true);
    }

    private IReadOnlyList<Instrument> ApplyWhitelist(List<Instrument> instruments)
    {
        if (cfg.Underlyings.Count == 0)
            return instruments;

        var allow = new HashSet<string>(cfg.Underlyings, StringComparer.OrdinalIgnoreCase);
        return instruments.Where(i =>
        {
            if (i.Underlying is { Length: > 0 } u && allow.Contains(u))
                return true;
            // cash equities have no underlying stem — match the ticker prefix
            if (i.Kind == Instrument.KindEquity)
            {
                var stem = InstrumentMaster.UnderlyingOf(i.Symbol);
                return allow.Contains(stem);
            }
            return false;
        }).ToList();
    }
}
