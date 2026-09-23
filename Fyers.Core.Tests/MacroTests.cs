using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Fyers.Core.Storage;
using Fyers.Core.Universe;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

// NB: the class names collide with their own namespace names (namespace
// Fyers.Core.Storage + class Storage, Fyers.Core.Universe + class Universe), so in
// this nested Fyers.Core.Tests namespace a bare `Storage` binds to the NAMESPACE
// (CS0118) — same trap StorageTests hits. Alias the two types under names that
// cannot collide and use those.
using StorageWriter = global::Fyers.Core.Storage.Storage;
using UniverseSpecs = global::Fyers.Core.Universe.Universe;

// NB: the RateLimiter type is referenced unqualified — `using Fyers.Core.RateLimiter;`
// is CS0138 (the type shadows its own namespace name).

namespace Fyers.Core.Tests;

/// <summary>
/// The macro universe: the second/third symbol masters (NSE_CDS currency, NSE_COM
/// commodity) on <see cref="InstrumentMaster"/>, the <c>macro_universe</c> config
/// section, the specs <see cref="Universe.BuildQuoteSpecs"/> appends for them, and
/// the atp/bid/ask field parity on <see cref="QuoteRow"/> (the user's VWAP metric).
/// The real <c>.scratch/cds.json</c> + <c>.scratch/com.json</c> master copies are the
/// fixtures — their entry shape was verified live against the Fyers API, not invented.
/// </summary>
public sealed class MacroTests
{
    // ------------------------------------------------------------------ fixtures

    private static string? FindUp(Func<string, bool> probe)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (probe(dir.FullName))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Repo root (config.yaml sentinel), exactly as ConfigTests finds it.</summary>
    private static string RepoRoot => FindUp(d => File.Exists(Path.Combine(d, "config.yaml")))
        ?? throw new InvalidOperationException($"config.yaml not found walking up from {AppContext.BaseDirectory}");

    private static string ComPath => Path.Combine(RepoRoot, ".scratch", "com.json");
    private static string CdsPath => Path.Combine(RepoRoot, ".scratch", "cds.json");

    private static void RequireFixtures()
    {
        // The real NSE masters are large local fixtures (COM ~26 MB, CDS ~11 MB)
        // and are NOT committed. Skip the parity tests that need them rather
        // than fail a clean checkout, so CI without the files stays green.
        Skip.If(!File.Exists(ComPath) || !File.Exists(CdsPath),
            "real NSE master fixtures absent (.scratch/com.json + .scratch/cds.json)");
    }

    /// <summary>LoadMulti over the two scratch masters. Deliberately NO FO entry, so
    /// the default (FO) index stays empty and the tests prove segment isolation: a
    /// 2-argument lookup must never answer with commodity/currency data.</summary>
    private static InstrumentMaster LoadCdsCom()
    {
        RequireFixtures();
        return InstrumentMaster.LoadMulti(
            new List<(string, string)> { (ComPath, "COM"), (CdsPath, "CDS") }, NewTempDir());
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "macrouni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteMasterFile(string dir, string fileName, string json)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>SILVER-style edge: exactly one monthly contract, plus a weekly tail,
    /// a mini contract of a different stem and an option that must all be ignored.</summary>
    private const string SilverOnlyJson = """
        {
          "NSE:SILVER26DECFUT": {"symTicker":"NSE:SILVER26DECFUT","optType":"XX","expiryDate":"1796407200"},
          "NSE:SILVERM26OCTFUT": {"symTicker":"NSE:SILVERM26OCTFUT","optType":"XX","expiryDate":"1791223200"},
          "NSE:SILVER26DEC5500CE": {"symTicker":"NSE:SILVER26DEC5500CE","optType":"CE","expiryDate":"1796407200"},
          "NSE:SILVER26O01FUT": {"symTicker":"NSE:SILVER26O01FUT","optType":"XX","expiryDate":"1790838000"}
        }
        """;

    // ------------------------------------------------------- segment resolution

    [SkippableFact]
    public void LoadMulti_resolves_commodity_futures_from_the_com_segment()
    {
        var master = LoadCdsCom();

        // GOLD monthlies verified live 2026-09-16: Oct, Nov, Dec, ... nearest first.
        var gold = master.FuturesContracts("GOLD", 2, InstrumentMaster.SegmentCom);
        Assert.Equal(2, gold.Count);
        Assert.Equal("NSE:GOLD26OCTFUT", gold[0].Symbol);
        Assert.Equal(1791223200L, gold[0].ExpiryEpoch);
        Assert.Equal("NSE:GOLD26NOVFUT", gold[1].Symbol);
        Assert.Equal(1793901600L, gold[1].ExpiryEpoch);
        Assert.True(gold[0].ExpiryEpoch < gold[1].ExpiryEpoch, "expiries ascend");

        var crude = master.FuturesContracts("CRUDEOIL", 2, "COM");
        Assert.Equal(new[] { "NSE:CRUDEOIL26SEPFUT", "NSE:CRUDEOIL26OCTFUT" },
            crude.Select(c => c.Symbol).ToArray());

        var natgas = master.FuturesContracts("NATURALGAS", 1, "com");       // segment is case-insensitive
        Assert.Equal("NSE:NATURALGAS26SEPFUT", Assert.Single(natgas).Symbol);
    }

    [SkippableFact]
    public void Segment_lookups_stay_isolated_and_the_two_arg_overload_stays_fo()
    {
        var master = LoadCdsCom();

        // The commodity/currency stems live ONLY in their own segment: the default
        // (FO) index — what the 2-argument overload and every existing caller sees —
        // must keep answering "no futures" for them.
        Assert.Empty(master.FuturesContracts("GOLD", 2));
        Assert.Empty(master.FuturesContracts("USDINR", 2));
        Assert.Empty(master.FuturesContracts("GOLD", 2, InstrumentMaster.SegmentCds));
        Assert.Empty(master.FuturesContracts("USDINR", 2, InstrumentMaster.SegmentCom));
        Assert.Empty(master.FuturesContracts("GOLD", 2, "NOPE"));
        Assert.Empty(master.FuturesContracts("RELIANCE", 2, InstrumentMaster.SegmentCom));

        Assert.True(master.HasSegment("COM"));
        Assert.True(master.HasSegment("CDS"));
        Assert.False(master.HasSegment("MIDCPNIFTY"));
    }

    [SkippableFact]
    public void LoadMulti_resolves_currency_futures_from_the_cds_segment_and_skips_weeklies()
    {
        var master = LoadCdsCom();

        var usdinr = master.FuturesContracts("USDINR", 2, InstrumentMaster.SegmentCds);
        Assert.Equal(2, usdinr.Count);
        Assert.Equal("NSE:USDINR26SEPFUT", usdinr[0].Symbol);
        Assert.Equal(1790578800L, usdinr[0].ExpiryEpoch);
        Assert.Equal("NSE:USDINR26OCTFUT", usdinr[1].Symbol);
        Assert.Equal(1793170800L, usdinr[1].ExpiryEpoch);

        // The CDS master carries WEEKLY currency futures (USDINR26O01FUT,
        // USDINR26N13FUT, EURINR26918FUT, ...). None of them may ever surface.
        var monthly = new Regex(@"^NSE:[A-Z]{6}\d{2}[A-Z]{3}FUT$", RegexOptions.Compiled);
        var all = master.FuturesContracts("USDINR", 99, "CDS")
                        .Concat(master.FuturesContracts("EURINR", 99, "CDS"))
                        .ToList();
        Assert.True(all.Count >= 6, $"unexpectedly few currency monthlies: {all.Count}");
        foreach (var c in all)
            Assert.Matches(monthly, c.Symbol);

        var eur = master.FuturesContracts("EURINR", 3, "CDS");
        Assert.Equal(3, eur.Count);
        Assert.True(eur.SequenceEqual(eur.OrderBy(c => c.ExpiryEpoch)), "expiries ascend");
    }

    [SkippableFact]
    public void Silver_with_a_single_monthly_contract_yields_one_spec()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteMasterFile(dir, "com.json", SilverOnlyJson);
            var master = InstrumentMaster.LoadMulti(new List<(string, string)> { (path, "COM") }, dir);

            var contracts = master.FuturesContracts("SILVER", 2, InstrumentMaster.SegmentCom);
            var contract = Assert.Single(contracts);
            Assert.Equal("NSE:SILVER26DECFUT", contract.Symbol);
            Assert.Equal(1796407200L, contract.ExpiryEpoch);

            var specs = BuildSpecs(master, CfgEnabled());
            Assert.Equal(new[] { "NSE:SILVER26DECFUT" },
                specs.Where(s => s.Underlying == "SILVER").Select(s => s.Symbol).ToArray());
            Assert.Equal("FUTURE", Assert.Single(specs, s => s.Symbol == "NSE:SILVER26DECFUT").Type);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public void Silver_real_master_reports_its_nearest_monthly()
    {
        var master = LoadCdsCom();
        var silver = master.FuturesContracts("SILVER", 2, "COM");
        Assert.True(silver.Count >= 1, "SILVER must have at least one monthly");
        Assert.Equal("NSE:SILVER26DECFUT", silver[0].Symbol);       // December is the nearest
        Assert.True(silver.All(c => Regex.IsMatch(c.Symbol, @"^NSE:SILVER\d{2}[A-Z]{3}FUT$")),
            "no weekly/mini contract may leak into the monthly set");
    }

    // ------------------------------------------------------------ config section

    [SkippableFact]
    public void MacroUniverse_is_off_when_the_yaml_section_is_absent()
    {
        var cfg = AppConfig.Parse(MinimalYaml);
        Assert.False(cfg.MacroUniverse.Enabled);
        Assert.Equal(2, cfg.MacroUniverse.NMonths);
        // An absent section still carries the verified default lists, so enabling is
        // a one-line flip for the operator.
        Assert.Equal(AppConfig.DefaultMacroCommodities, cfg.MacroUniverse.Commodities);
        Assert.Equal(AppConfig.DefaultMacroCurrencies, cfg.MacroUniverse.Currencies);
        Assert.Equal(AppConfig.DefaultMacroIndexSpots, cfg.MacroUniverse.IndexSpots);

        // ...and the built spec list is untouched: no INDEX rows, no commodity/currency future.
        var master = LoadCdsCom();
        var specs = BuildSpecs(master, cfg);
        Assert.DoesNotContain(specs, s => s.Type == "INDEX");
        Assert.DoesNotContain(specs, s => s.Symbol.Contains("GOLD"));
        Assert.DoesNotContain(specs, s => s.Symbol.Contains("USDINR"));
        Assert.Equal(new[] { "NSE:NIFTY50-INDEX", "NSE:INDIAVIX-INDEX" }, specs.Select(s => s.Symbol).ToArray());
    }

    [SkippableFact]
    public void MacroUniverse_defaults_apply_when_only_enabled_is_set()
    {
        var cfg = AppConfig.Parse(MinimalYaml + """

            macro_universe:
              enabled: true
            """);
        Assert.True(cfg.MacroUniverse.Enabled);
        Assert.Equal(2, cfg.MacroUniverse.NMonths);
        Assert.Equal(new[] { "GOLD", "SILVER", "CRUDEOIL", "NATURALGAS" }, cfg.MacroUniverse.Commodities);
        Assert.Equal(new[] { "USDINR", "EURINR" }, cfg.MacroUniverse.Currencies);
        Assert.Equal(new[]
        {
            "NIFTYIT", "NIFTYAUTO", "NIFTYPHARMA", "NIFTYMETAL",
            "NIFTYFMCG", "NIFTYREALTY", "MIDCPNIFTY", "NIFTYSMLCAP100",
        }, cfg.MacroUniverse.IndexSpots);
    }

    [SkippableFact]
    public void MacroUniverse_reads_the_snake_case_keys()
    {
        var cfg = AppConfig.Parse(MinimalYaml + """

            macro_universe:
              enabled: true
              n_months: 3
              commodities: [GOLD, CRUDEOIL]
              currencies: [GBPINR]
              index_spots: [NIFTYIT, NIFTYMETAL]
            """);
        var m = cfg.MacroUniverse;
        Assert.True(m.Enabled);
        Assert.Equal(3, m.NMonths);
        Assert.Equal(new[] { "GOLD", "CRUDEOIL" }, m.Commodities);
        Assert.Equal(new[] { "GBPINR" }, m.Currencies);
        Assert.Equal(new[] { "NIFTYIT", "NIFTYMETAL" }, m.IndexSpots);
    }

    // -------------------------------------------------------------- quote specs

    [SkippableFact]
    public void BuildQuoteSpecs_appends_macro_futures_and_index_spots()
    {
        var master = LoadCdsCom();
        var cfg = CfgEnabled();
        var specs = BuildSpecs(master, cfg);

        // commodity futures: up to n_months monthlies from the COM segment
        Assert.Equal(new[] { "NSE:GOLD26OCTFUT", "NSE:GOLD26NOVFUT" },
            specs.Where(s => s.Underlying == "GOLD").Select(s => s.Symbol).ToArray());
        Assert.Equal(new[] { "NSE:CRUDEOIL26SEPFUT", "NSE:CRUDEOIL26OCTFUT" },
            specs.Where(s => s.Underlying == "CRUDEOIL").Select(s => s.Symbol).ToArray());
        Assert.Equal(new[] { "NSE:NATURALGAS26SEPFUT", "NSE:NATURALGAS26OCTFUT" },
            specs.Where(s => s.Underlying == "NATURALGAS").Select(s => s.Symbol).ToArray());
        // SILVER: December is the nearest monthly; only two exist within reach
        Assert.Equal(new[] { "NSE:SILVER26DECFUT", "NSE:SILVER27MARFUT" },
            specs.Where(s => s.Underlying == "SILVER").Select(s => s.Symbol).ToArray());
        Assert.All(specs.Where(s => s.Underlying is "GOLD" or "SILVER" or "CRUDEOIL" or "NATURALGAS"),
            s =>
            {
                Assert.Equal("FUTURE", s.Type);
                Assert.NotNull(s.ExpiryEpoch);
            });

        // currency futures from the CDS segment
        Assert.Equal(new[] { "NSE:USDINR26SEPFUT", "NSE:USDINR26OCTFUT" },
            specs.Where(s => s.Underlying == "USDINR").Select(s => s.Symbol).ToArray());
        Assert.Equal(new[] { "NSE:EURINR26SEPFUT", "NSE:EURINR26OCTFUT" },
            specs.Where(s => s.Underlying == "EURINR").Select(s => s.Symbol).ToArray());

        // index spots: exactly one per name, INDEX type, no underlying, no expiry
        var spots = specs.Where(s => s.Type == "INDEX").ToList();
        Assert.Equal(cfg.MacroUniverse.IndexSpots.Count, spots.Count);
        foreach (var name in cfg.MacroUniverse.IndexSpots)
        {
            var spot = Assert.Single(spots, s => s.Symbol == $"NSE:{name}-INDEX");
            Assert.Equal("INDEX", spot.Type);
            Assert.Null(spot.Underlying);
            Assert.Null(spot.ExpiryEpoch);
        }
        Assert.Equal("NSE:NIFTYIT-INDEX", spots[0].Symbol);             // exact format
        Assert.Equal("NSE:NIFTYSMLCAP100-INDEX", spots[^1].Symbol);

        // MIDCPNIFTY: an index WITH futures — they come from the FO segment, which
        // this fixture does not carry, so only the spot may appear for it.
        Assert.Equal(new[] { "NSE:MIDCPNIFTY-INDEX" },
            specs.Where(s => s.Symbol.Contains("MIDCPNIFTY")).Select(s => s.Symbol).ToArray());

        // macro specs are appended after the base universe, never replacing it
        Assert.Equal("NSE:INDIAVIX-INDEX", specs[1].Symbol);
        Assert.Equal("VIX", specs[1].Type);
    }

    [SkippableFact]
    public void BuildQuoteSpecs_dedups_macro_against_existing_specs()
    {
        var dir = NewTempDir();
        try
        {
            var comPath = WriteMasterFile(dir, "com.json", """
                {
                  "NSE:GOLD26OCTFUT": {"symTicker":"NSE:GOLD26OCTFUT","optType":"XX","expiryDate":"1791223200"},
                  "NSE:SILVER26DECFUT": {"symTicker":"NSE:SILVER26DECFUT","optType":"XX","expiryDate":"1796407200"}
                }
                """);
            var foPath = WriteMasterFile(dir, "fo.json", """
                {
                  "NSE:MIDCPNIFTY26OCTFUT": {"symTicker":"NSE:MIDCPNIFTY26OCTFUT","optType":"XX","expiryDate":"1791223200"},
                  "NSE:RELIANCE26SEPFUT": {"symTicker":"NSE:RELIANCE26SEPFUT","optType":"XX","expiryDate":"1790676600"}
                }
                """);
            var master = InstrumentMaster.LoadMulti(new List<(string, string)>
            {
                (foPath, "FO"), (comPath, "COM"),
            }, dir);

            var cfg = AppConfig.Parse(MidcpniftyYaml + """

                macro_universe:
                  enabled: true
                  commodities: [GOLD, SILVER, GOLD]
                  currencies: []
                  index_spots: [MIDCPNIFTY, NIFTYIT]
                """);

            var specs = BuildSpecs(master, cfg);

            // the spot is already claimed by cfg.Symbols -> exactly one, still a SPOT
            var midSpots = specs.Where(s => s.Symbol == "NSE:MIDCPNIFTY-INDEX").ToList();
            Assert.Single(midSpots);
            Assert.Equal("SPOT", midSpots[0].Type);

            // MIDCPNIFTY futures: once, resolved from the FO segment by the macro loop
            Assert.Equal(new[] { "NSE:MIDCPNIFTY26OCTFUT" },
                specs.Where(s => s.Underlying == "MIDCPNIFTY" && s.Type == "FUTURE")
                     .Select(s => s.Symbol).ToArray());

            // duplicates inside the macro lists collapse to one
            Assert.Single(specs, s => s.Symbol == "NSE:GOLD26OCTFUT");
            Assert.Single(specs, s => s.Symbol == "NSE:SILVER26DECFUT");
            Assert.Single(specs, s => s.Symbol == "NSE:NIFTYIT-INDEX");
            Assert.Single(specs, s => s.Symbol == "NSE:MIDCPNIFTY26OCTFUT");

            // base specs are untouched
            Assert.Single(specs, s => s.Symbol == "NSE:INDIAVIX-INDEX");
            Assert.DoesNotContain(specs, s => s.Symbol == "NSE:RELIANCE26SEPFUT");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public void BuildQuoteSpecs_emits_no_macro_specs_for_a_non_positive_n_months()
    {
        var master = LoadCdsCom();
        var cfg = AppConfig.Parse(MinimalYaml + """

            macro_universe:
              enabled: true
              n_months: 0
            """);
        var specs = BuildSpecs(master, cfg);
        Assert.DoesNotContain(specs, s => s.Type == "INDEX");
        Assert.DoesNotContain(specs, s => s.Underlying == "GOLD");
    }

    // ------------------------------------------------------- master load + cache

    [SkippableFact]
    public void LoadMulti_sums_entry_counts_and_names_segments_cache_files()
    {
        var master = LoadCdsCom();

        // entry counts are summed over the loaded masters (com 25.1k + cds 11.1k today)
        Assert.True(master.EntryCount > 30_000, $"unexpected entry count {master.EntryCount}");
        Assert.True(master.MonthlyCountOf("COM") > 100, $"com monthlies {master.MonthlyCountOf("COM")}");
        Assert.True(master.MonthlyCountOf("CDS") > 50, $"cds monthlies {master.MonthlyCountOf("CDS")}");
        Assert.Equal(0, master.MonthlyCountOf("FO"));
        Assert.Equal(new[] { "CDS", "COM" }, master.Segments.Where(s => s != "FO").OrderBy(s => s).ToArray());

        // cache file naming (sym_master_{segment}.json) — the layout a URL-driven run
        // leaves in the data dir
        Assert.Equal("sym_master_fo.json", InstrumentMaster.CacheFileName("fo"));
        Assert.Equal("sym_master_cds.json", InstrumentMaster.CacheFileName("cds"));
        Assert.Equal("sym_master_com.json", InstrumentMaster.CacheFileName("COM"));
        Assert.Equal(Path.Combine("data-dotnet", "sym_master_com.json"),
            InstrumentMaster.CachePathFor("COM", "data-dotnet"));
    }

    [SkippableFact]
    public void LoadMulti_reuses_a_fresh_cache_without_touching_the_network()
    {
        var dir = NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "sym_master_com.json");
            File.WriteAllText(cache,
                """{"NSE:SILVER26DECFUT":{"symTicker":"NSE:SILVER26DECFUT","optType":"XX","expiryDate":"1796407200"}}""");
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow);           // minutes old

            // an unresolvable host proves the 12h rule short-circuits the download
            var path = InstrumentMaster.EnsureCached(
                "https://cache-hit.invalid/NSE_COM_sym_master.json", "COM", dir);
            Assert.Equal(cache, path);

            var master = InstrumentMaster.LoadMulti(
                new List<(string, string)>
                {
                    ("https://cache-hit.invalid/NSE_COM_sym_master.json", "COM"),
                }, dir);
            Assert.Equal("NSE:SILVER26DECFUT",
                Assert.Single(master.FuturesContracts("SILVER", 2, "COM")).Symbol);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public void LoadMulti_downloads_a_stale_cache_and_never_silently_reuses_it()
    {
        var dir = NewTempDir();
        try
        {
            var cache = Path.Combine(dir, "sym_master_com.json");
            File.WriteAllText(cache, "{}");
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow - TimeSpan.FromHours(13));  // > 12h stale

            // 127.0.0.1:1 refuses immediately (no network, no hang); the point is that
            // the stale file is not reused.
            Assert.ThrowsAny<Exception>(() => InstrumentMaster.EnsureCached(
                "http://127.0.0.1:1/NSE_COM_sym_master.json", "COM", dir));

            // a local path that does not exist is a hard error
            Assert.Throws<FileNotFoundException>(() => InstrumentMaster.EnsureCached(
                Path.Combine(dir, "nope", "sym_master.json"), "COM", dir));

            // a local path is returned as-is (no download, no cache copy)
            var real = WriteMasterFile(dir, "com.json", "{}");
            Assert.Equal(real, InstrumentMaster.EnsureCached(real, "COM", dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public void LoadMulti_throws_for_a_broken_fo_master_but_skips_a_broken_side_segment()
    {
        var dir = NewTempDir();
        try
        {
            var comPath = WriteMasterFile(dir, "com.json",
                """{"NSE:GOLD26OCTFUT":{"symTicker":"NSE:GOLD26OCTFUT","optType":"XX","expiryDate":"1791223200"}}""");
            var log = new CapturingLogger();

            // CDS missing: warn + continue — capture keeps running, macro futures drop out.
            var master = InstrumentMaster.LoadMulti(new List<(string, string)>
            {
                (comPath, "COM"), (Path.Combine(dir, "missing_cds.json"), "CDS"),
            }, dir, log);
            Assert.True(master.HasSegment("COM"));
            Assert.Equal("NSE:GOLD26OCTFUT", Assert.Single(master.FuturesContracts("GOLD", 2, "COM")).Symbol);
            Assert.Empty(master.FuturesContracts("USDINR", 2, "CDS"));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("CDS"));

            // FO missing: fatal — every existing caller depends on it.
            Assert.ThrowsAny<Exception>(() => InstrumentMaster.LoadMulti(new List<(string, string)>
            {
                (comPath, "COM"), (Path.Combine(dir, "missing_fo.json"), "FO"),
            }, dir, log));

            // FO unparsable: fatal too.
            var badFo = WriteMasterFile(dir, "bad_fo.json", "not json at all");
            Assert.ThrowsAny<Exception>(() => InstrumentMaster.LoadMulti(
                new List<(string, string)> { (badFo, "FO") }, dir, log));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public void Load_parses_a_single_master_exactly_like_before()
    {
        var dir = NewTempDir();
        try
        {
            var path = WriteMasterFile(dir, "fo.json", """
                {
                  "NSE:RELIANCE26SEPFUT": {"symTicker":"NSE:RELIANCE26SEPFUT","optType":"XX","expiryDate":"1790676600"},
                  "NSE:RELIANCE26OCTFUT": {"symTicker":"NSE:RELIANCE26OCTFUT","optType":"XX","expiryDate":"1793095800"}
                }
                """);
            var master = InstrumentMaster.Load(path);
            Assert.Equal("NSE:RELIANCE26SEPFUT", master.FuturesContracts("RELIANCE", 1)[0].Symbol);
            Assert.Equal("NSE:RELIANCE26SEPFUT",
                master.FuturesContracts("RELIANCE", 1, InstrumentMaster.SegmentFo)[0].Symbol);
            Assert.Equal("NSE:RELIANCE-EQ", master.CashSymbol("RELIANCE"));
            Assert.Equal(new[] { "FO" }, master.Segments);
            Assert.Empty(master.FuturesContracts("RELIANCE", 1, "COM"));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // --------------------------------------------------- QuoteRow field parity

    private const string MacroQuotesBody = """
        {"s":"ok","code":200,"message":"done","d":[
          {"n":"NSE:GOLD26OCTFUT","s":"ok","v":{"lp":74210.0,"atp":73955.25,"bid":74190.0,"ask":74230.0,
           "open_price":73800.0,"high_price":74400.0,"low_price":73750.0,"prev_close_price":73690.0,
           "volume":1234,"spread":40.0}},
          {"n":"NSE:USDINR26SEPFUT","s":"ok","v":{"lp":88.1250,"atp":88.0412,"bid_price":88.1225,
           "ask_price":88.1275,"volume":99}},
          {"n":"NSE:NIFTYIT-INDEX","s":"ok","v":{"lp":38210.45,"ch":120.5}}
        ]}
        """;

    [SkippableFact]
    public void Quotes_map_atp_bid_and_ask_with_the_python_key_fallbacks()
    {
        var (client, handler) = Build(responder: _ => Json(MacroQuotesBody));

        var rows = client.Quotes(new List<QuoteSpec>
        {
            new("NSE:GOLD26OCTFUT", "FUTURE", "GOLD", 1791223200),
            new("NSE:USDINR26SEPFUT", "FUTURE", "USDINR", 1790578800),
            new("NSE:NIFTYIT-INDEX", "INDEX", null, null),
        });

        Assert.Equal(3, rows.Count);
        Assert.Single(handler.Urls);                        // one 3-symbol chunk

        var gold = rows[0];
        Assert.Equal(73955.25m, gold.Atp);                  // atp — the VWAP input
        Assert.Equal(74190.0m, gold.Bid);                   // bid
        Assert.Equal(74230.0m, gold.Ask);                   // ask
        Assert.Equal("FUTURE", gold.Type);
        Assert.Equal("GOLD", gold.Underlying);
        Assert.Equal(1791223200L, gold.ExpiryEpoch);

        var usdinr = rows[1];
        Assert.Equal(88.0412m, usdinr.Atp);
        Assert.Equal(88.1225m, usdinr.Bid);                 // bid -> bid_price
        Assert.Equal(88.1275m, usdinr.Ask);                 // ask -> ask_price

        // a v dict without atp/bid/ask leaves them null (python .get() miss)
        var spot = rows[2];
        Assert.Null(spot.Atp);
        Assert.Null(spot.Bid);
        Assert.Null(spot.Ask);
        Assert.Equal("INDEX", spot.Type);
        Assert.Null(spot.Underlying);
        Assert.Null(spot.ExpiryEpoch);
    }

    [SkippableFact]
    public void Storage_writes_the_quote_atp_bid_ask_columns()
    {
        var dir = NewTempDir();
        try
        {
            using (var storage = new StorageWriter(dir, Path.Combine(dir, "summary.db")))
            {
                var tsUtc = new DateTime(2026, 9, 16, 5, 30, 0, DateTimeKind.Utc);
                var ist = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Unspecified);
                var day = DateOnly.FromDateTime(tsUtc);
                var row = new QuoteRow(
                    Symbol: "NSE:GOLD26OCTFUT", Type: "FUTURE", Underlying: "GOLD",
                    ExpiryEpoch: 1791223200, Ltp: 74210.0m, Open: 73800.0m, High: 74400.0m,
                    Low: 73750.0m, PrevClose: 73690.0m, Volume: 1234m, Spread: 40.0m,
                    TsUtc: tsUtc, IstMinute: ist, InstrumentType: "FUTURE",
                    Atp: 73955.25m, Bid: 74190.0m, Ask: 74230.0m);
                storage.WriteQuotes(day, new List<QuoteRow> { row });

                // nulls still bind as NULL (python .get() miss parity)
                storage.WriteQuotes(day, new List<QuoteRow> { row with { Atp = null, Bid = null, Ask = null } });
            }

            var path = Path.Combine(dir, "snapshots-2026-09-16.db");
            Assert.True(File.Exists(path));
            using var conn = new SqliteConnection(string.Format(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString()));
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ltp, bid, ask, bid_size, ask_size, oi, spread, atp FROM quotes ORDER BY id";
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(74210.0, r.GetDouble(0));
                Assert.Equal(74190.0, r.GetDouble(1));              // bid
                Assert.Equal(74230.0, r.GetDouble(2));              // ask
                Assert.True(r.IsDBNull(3), "bid_size");             // still not collected
                Assert.True(r.IsDBNull(4), "ask_size");
                Assert.True(r.IsDBNull(5), "oi");
                Assert.Equal(40.0, r.GetDouble(6));
                Assert.Equal(73955.25, r.GetDouble(7));             // atp

                Assert.True(r.Read());
                Assert.True(r.IsDBNull(1), "null bid");
                Assert.True(r.IsDBNull(2), "null ask");
                Assert.True(r.IsDBNull(7), "null atp");
                Assert.False(r.Read());
            }

            // the QuoteRow positional shape is unchanged for existing callers:
            // the three new fields default to null
            QuoteRow bare = new("NSE:X-EQ", "CASH", null, null, 1m, null, null, null, null, null,
                null, DateTime.UtcNow, DateTime.UtcNow, "CASH");
            Assert.Null(bare.Atp);
            Assert.Null(bare.Bid);
            Assert.Null(bare.Ask);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Smallest yaml the parser needs, WITHOUT a macro_universe section.</summary>
    private const string MinimalYaml = """
        symbols:
          - underlying: NIFTY50
            chain_symbol: NSE:NIFTY50-INDEX
            spot_symbol: NSE:NIFTY50-INDEX
            weekly: false
        options:
          strikecount: 50
        futures:
          n_months: 3
        session:
          tz: Asia/Kolkata
          start: "09:00"
          end: "16:00"
          eod_time: "15:31"
        rate_limit:
          per_minute: 190
        storage:
          daily_dir: data-dotnet
        retention:
          hot_days: 1
        notify:
          missed_min_alert: 3
        """;

    /// <summary>Same yaml with a MIDCPNIFTY symbol (used by the dedup test, which
    /// then appends macro_universe without repeating the symbols key).</summary>
    private const string MidcpniftyYaml = """
        symbols:
          - underlying: MIDCPNIFTY
            chain_symbol: NSE:MIDCPNIFTY-INDEX
            spot_symbol: NSE:MIDCPNIFTY-INDEX
            weekly: false
        options:
          strikecount: 50
        futures:
          n_months: 3
        session:
          tz: Asia/Kolkata
          start: "09:00"
          end: "16:00"
          eod_time: "15:31"
        rate_limit:
          per_minute: 190
        storage:
          daily_dir: data-dotnet
        retention:
          hot_days: 1
        notify:
          missed_min_alert: 3
        """;

    private static AppConfig CfgEnabled() => AppConfig.Parse(MinimalYaml + """

        macro_universe:
          enabled: true
        """);

    private static IReadOnlyList<QuoteSpec> BuildSpecs(InstrumentMaster master, AppConfig cfg)
        => new UniverseSpecs(new HttpClient(NoopHandler.Instance), master).BuildQuoteSpecs(cfg);

    /// <summary>HttpClient handler that fails any request — BuildQuoteSpecs makes none.</summary>
    private sealed class NoopHandler : HttpMessageHandler
    {
        public static readonly NoopHandler Instance = new();
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no http expected");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new InvalidOperationException("no http expected"));
    }

    /// <summary>ILogger that keeps every formatted line (macro load-failure assertions).</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // quotes-client fakes (same shape as ClientTests, kept local on purpose)

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            return Responder?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"s":"ok","code":200,"d":[]}""",
                        Encoding.UTF8, "application/json"),
                };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (FyersClient Client, FakeHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new FakeHandler { Responder = responder };
        var now = new DateTime(2026, 9, 16, 5, 30, 0, DateTimeKind.Utc);
        var client = new FyersClient(new HttpClient(handler), () => "TOKEN",
            new RateLimiter(10_000, 10_000, () => now), log: null, appId: "APP-1", clock: () => now);
        return (client, handler);
    }
}
