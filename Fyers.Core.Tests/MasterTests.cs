using Fyers.Core.Fyers;
using Xunit;

namespace Fyers.Core.Tests;

/// <summary>
/// Tests for the port of src/instrument_master.py: a tiny inline fixture proves the
/// structure rules (monthly FUT selection, XX-only, expiry source), and one test loads
/// the real 77 MB data/sym_master.json.
/// </summary>
public sealed class MasterTests
{
    /// <summary>
    /// Master shape (verified against the real file): top-level object keyed by the
    /// full Fyers ticker, values are entry objects whose <c>expiryDate</c> is an
    /// epoch-seconds STRING and whose <c>optType</c> is XX for futures, CE/PE for options.
    /// </summary>
    private const string Fixture = """
        {
          "NSE:RELIANCE26SEPFUT": {"symTicker":"NSE:RELIANCE26SEPFUT","optType":"XX","expiryDate":"1790676600","underSym":"RELIANCE","strikePrice":-1.0},
          "NSE:RELIANCE26OCTFUT": {"symTicker":"NSE:RELIANCE26OCTFUT","optType":"XX","expiryDate":"1793095800","underSym":"RELIANCE","strikePrice":-1.0},
          "NSE:RELIANCE26NOVFUT": {"symTicker":"NSE:RELIANCE26NOVFUT","optType":"XX","expiryDate":"1795428600","underSym":"RELIANCE","strikePrice":-1.0},
          "NSE:RELIANCE26DEC25FUT": {"symTicker":"NSE:RELIANCE26DEC25FUT","optType":"XX","expiryDate":"1797759600","underSym":"RELIANCE"},
          "NSE:RELIANCE26SEP700CE": {"symTicker":"NSE:RELIANCE26SEP700CE","optType":"CE","expiryDate":"1790676600","underSym":"RELIANCE","strikePrice":700.0},
          "NSE:RELIANCE26SEP700PE": {"symTicker":"NSE:RELIANCE26SEP700PE","optType":"PE","expiryDate":"1790676600","underSym":"RELIANCE","strikePrice":700.0},
          "NSE:RELIANCE-EQ": {"symTicker":"NSE:RELIANCE-EQ","optType":"EQ","expiryDate":null,"underSym":null},
          "NSE:BROKEN": {"symTicker":"NSE:BROKEN","optType":"XX","expiryDate":null,"underSym":"BROKEN"},
          "NSE:NIFTY26SEPFUT": {"symTicker":"NSE:NIFTY26SEPFUT","optType":"XX","expiryDate":"1790676600","underSym":"NIFTY"},
          "NSE:NIFTY26OCTFUT": {"symTicker":"NSE:NIFTY26OCTFUT","optType":"XX","expiryDate":"1793095800","underSym":"NIFTY"},
          "NSE:NIFTY26SEP24800CE": {"symTicker":"NSE:NIFTY26SEP24800CE","optType":"CE","expiryDate":"1790676600","underSym":"NIFTY","strikePrice":24800.0},
          "BSE:SENSEX26SEPFUT": {"symTicker":"BSE:SENSEX26SEPFUT","optType":"XX","expiryDate":"1790676600","underSym":"SENSEX"}
        }
        """;

    private static InstrumentMaster LoadFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sym_master_fixture_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Fixture);
        try
        {
            return InstrumentMaster.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------- cash symbol

    [Fact]
    public void CashSymbol_formats_the_fyers_eq_symbol()
    {
        Assert.Equal("NSE:RELIANCE-EQ", LoadFixture().CashSymbol("RELIANCE"));
    }

    [Theory]
    [InlineData("TCS", "NSE:TCS-EQ")]
    [InlineData("M&M", "NSE:M&M-EQ")]
    [InlineData("BAJAJ-AUTO", "NSE:BAJAJ-AUTO-EQ")]
    public void CashSymbol_is_a_pure_format_no_master_lookup_needed(string ticker, string expected)
    {
        Assert.Equal(expected, LoadFixture().CashSymbol(ticker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CashSymbol_blank_ticker_yields_null(string? ticker)
        => Assert.Null(LoadFixture().CashSymbol(ticker!));

    // ---------------------------------------------------------- futures select

    [Fact]
    public void FuturesContracts_returns_the_nearest_n_monthly_contracts_ascending()
    {
        var contracts = LoadFixture().FuturesContracts("RELIANCE", 2);

        Assert.Equal(2, contracts.Count);
        Assert.Equal("NSE:RELIANCE26SEPFUT", contracts[0].Symbol);
        Assert.Equal(1790676600L, contracts[0].ExpiryEpoch);       // expiryDate string -> epoch
        Assert.Equal("NSE:RELIANCE26OCTFUT", contracts[1].Symbol);
        Assert.Equal(1793095800L, contracts[1].ExpiryEpoch);
        Assert.True(contracts[0].ExpiryEpoch < contracts[1].ExpiryEpoch);
    }

    [Fact]
    public void FuturesContracts_skips_options_and_non_monthly_and_expiredless_entries()
    {
        // RELIANCE has 3 monthly FUTs; the CE/PE legs, the -EQ cash entry and the
        // expiry-less "BROKEN" entry must never appear.
        var contracts = LoadFixture().FuturesContracts("RELIANCE", 10);
        Assert.Equal(new[] { "NSE:RELIANCE26SEPFUT", "NSE:RELIANCE26OCTFUT", "NSE:RELIANCE26NOVFUT" },
            contracts.Select(c => c.Symbol).ToArray());
    }

    [Fact]
    public void FuturesContracts_keyed_by_bare_underlying_regardless_of_exchange_prefix()
    {
        Assert.Equal("BSE:SENSEX26SEPFUT", Assert.Single(LoadFixture().FuturesContracts("SENSEX", 3)).Symbol);
    }

    [Fact]
    public void FuturesContracts_unknown_underlying_or_non_positive_n_is_empty()
    {
        var m = LoadFixture();
        Assert.Empty(m.FuturesContracts("NOTREAL", 3));
        Assert.Empty(m.FuturesContracts("RELIANCE", 0));
        Assert.Empty(m.FuturesContracts("RELIANCE", -1));
    }

    [Fact]
    public void FuturesContracts_exactly_n_available_returns_all_of_them()
    {
        Assert.Equal(2, LoadFixture().FuturesContracts("NIFTY", 3).Count);
    }

    // --------------------------------------------------------- underlying parse

    [Theory]
    [InlineData("NSE:NIFTY26SEPFUT", "NIFTY")]
    [InlineData("NSE:NIFTY26SEP24800CE", "NIFTY")]
    [InlineData("NIFTY26SEPFUT", "NIFTY")]
    [InlineData("BSE:SENSEX26SEPFUT", "SENSEX")]
    // parity quirk: python cuts at the FIRST digit, so a ticker starting with one (or
    // holding one mid-name, e.g. NIFTYNXT50) yields "" / the wrong prefix — both ports
    // behave identically, and such names simply get no futures (callers skip).
    [InlineData("NSE:3MINDIA26SEPFUT", "")]
    [InlineData("NSE:NIFTYNXT5026SEPFUT", "NIFTYNXT")]
    public void UnderlyingOf_matches_python(string symbol, string expected)
        => Assert.Equal(expected, InstrumentMaster.UnderlyingOf(symbol));

    // ---------------------------------------------------------------- real file

    private static string? FindMaster()
    {
        // Walk up from bin/Debug/net10.0 to the repo root, then data/sym_master.json.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "data", "sym_master.json");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// The real 77 MB master. Measured ~0.6 s to load, so it is NOT gated behind a
    /// Slow category — it runs with the normal suite; drop it only if the file
    /// grows enough to slow the loop down.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Master")]
    public void Load_parses_the_real_fyers_master()
    {
        var path = FindMaster();
        // The real 77 MB master is a local fixture and is NOT committed — skip a
        // clean checkout rather than fail it (run the collector once to fetch it).
        Skip.If(path is null,
            "data/sym_master.json not found — run the collector once to fetch it");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var master = InstrumentMaster.Load(path!);
        sw.Stop();

        Assert.True(master.EntryCount > 50_000, $"unexpected entry count {master.EntryCount}");

        var contracts = master.FuturesContracts("RELIANCE", 2);
        Assert.Equal(2, contracts.Count);
        Assert.True(contracts[0].ExpiryEpoch < contracts[1].ExpiryEpoch, "expiries must ascend");
        Assert.Matches(@"^NSE:RELIANCE\d{2}[A-Z]{3}FUT$", contracts[0].Symbol);
        Assert.Matches(@"^NSE:RELIANCE\d{2}[A-Z]{3}FUT$", contracts[1].Symbol);
        Assert.True(contracts[0].ExpiryEpoch > 1_760_000_000, "expiry must be a sane future epoch");

        Assert.Equal("NSE:RELIANCE-EQ", master.CashSymbol("RELIANCE"));

        // index futures resolve too (the python run's own use case)
        var nifty = master.FuturesContracts("NIFTY", 3);
        Assert.True(nifty.Count >= 1);
        Assert.Matches(@"^NSE:NIFTY\d{2}[A-Z]{3}FUT$", nifty[0].Symbol);

        // a non-F&O ticker has no futures -> empty (callers skip)
        Assert.Empty(master.FuturesContracts("THISISNOTAFONAME", 2));

        Assert.True(sw.Elapsed.TotalMilliseconds < 15_000, $"master load took {sw.ElapsedMilliseconds}ms");
    }
}
