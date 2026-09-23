using Fyers.Core.Config;

namespace Fyers.Core.Tests;

/// <summary>
/// Parity tests for the .NET port of src/config.py (Config) + load_env().
/// The REAL config.yaml at the repo root must load and yield the same
/// numbers the Python collector runs with today (live market 2026-09-15).
/// </summary>
public class ConfigTests
{
    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config.yaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"config.yaml not found walking up from {AppContext.BaseDirectory}");
    }

    private static string RepoConfigPath => Path.Combine(RepoRoot, "config.yaml");

    /// <summary>Load the repo's real config.yaml, yaml only (no .env side effects).</summary>
    private static AppConfig LoadRepoYaml() =>
        AppConfig.Parse(File.ReadAllText(RepoConfigPath));

    private static string WriteTemp(string content, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "cfgtests-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Smallest yaml that satisfies every key src/config.py requires.</summary>
    private const string MinimalYaml = """
        symbols:
          - underlying: NIFTY
            chain_symbol: NSE:NIFTY50-INDEX
            spot_symbol: NSE:NIFTY50-INDEX
            weekly: true
        options:
          strikecount: 50
        futures:
          n_months: 3
        session:
          tz: Asia/Kolkata
          start: "09:00"
          end: "16:00"
          eod_time: "15:31"
        """;

    // ------------------------------------------------------------------
    // real config.yaml
    // ------------------------------------------------------------------

    [Fact]
    public void RepoRootFoundByWalkingUp()
    {
        Assert.True(File.Exists(RepoConfigPath), $"expected config.yaml at {RepoConfigPath}");
    }

    [Fact]
    public void RealConfig_SessionTimesAndBands()
    {
        var cfg = LoadRepoYaml();

        Assert.Equal("Asia/Kolkata", cfg.Session.Tz);
        Assert.Equal(new TimeOnly(9, 0), cfg.Session.Start);
        Assert.Equal("09:00", cfg.Session.StartRaw);
        Assert.Equal(new TimeOnly(16, 0), cfg.Session.End);
        Assert.Equal(new TimeOnly(15, 31), cfg.Session.EodTime);
        Assert.Equal("15:31", cfg.Session.EodTimeRaw);
        Assert.Equal(new TimeOnly(8, 30), cfg.Session.QuotesStart);
        Assert.Equal(new TimeOnly(16, 0), cfg.Session.QuotesEnd);
        Assert.Equal(new[]
        {
            new TimeOnly(8, 45),
            new TimeOnly(10, 0),
        }, cfg.Session.PremarketTimes);
    }

    [Fact]
    public void RealConfig_Numbers()
    {
        var cfg = LoadRepoYaml();

        Assert.Equal(190, cfg.RateLimit.PerMinute);
        Assert.Equal(8, cfg.RateLimit.PerSecond);
        Assert.Equal(50, cfg.StrikeCount);
        Assert.Equal(2, cfg.StockNMonthly);
        Assert.Equal(3, cfg.FuturesNMonths);
        Assert.Equal(3, cfg.MissedMinAlert);
        Assert.True(cfg.ConstituentsEnabled);
    }

    [Fact]
    public void RealConfig_GdriveCandlesAuth()
    {
        var cfg = LoadRepoYaml();

        Assert.True(cfg.Gdrive.Enabled);
        Assert.Equal(new TimeOnly(23, 15), cfg.Gdrive.Time);
        Assert.Equal("23:15", cfg.Gdrive.TimeRaw);
        Assert.Equal("gdrive", cfg.Gdrive.Remote);
        Assert.Equal("fyers-snapshots", cfg.Gdrive.Path);

        Assert.True(cfg.Candles.Enabled);
        Assert.Equal(new TimeOnly(21, 0), cfg.Candles.Time);
        Assert.Equal(6, cfg.Candles.LookbackDays);
        Assert.Equal(120, cfg.Candles.BudgetMin);
        Assert.Equal(10, cfg.Candles.ProbeRetryMin);

        Assert.Equal("callback", cfg.Auth.Mode);
        Assert.Equal(new TimeOnly(6, 30), cfg.Auth.PromptTime);
    }

    [Fact]
    public void RealConfig_Symbols()
    {
        var cfg = LoadRepoYaml();

        Assert.Equal(2, cfg.Symbols.Count);
        Assert.Equal("NIFTY", cfg.Symbols[0].Underlying);
        Assert.Equal("NSE:NIFTY50-INDEX", cfg.Symbols[0].ChainSymbol);
        Assert.Equal("NSE:NIFTY50-INDEX", cfg.Symbols[0].SpotSymbol);
        Assert.True(cfg.Symbols[0].Weekly);
        Assert.Equal("BANKNIFTY", cfg.Symbols[1].Underlying);
        Assert.Equal("NSE:NIFTYBANK-INDEX", cfg.Symbols[1].ChainSymbol);
        Assert.False(cfg.Symbols[1].Weekly);
    }

    [Fact]
    public void RealConfig_Storage()
    {
        var cfg = LoadRepoYaml();

        Assert.Equal("data", cfg.DailyDir);
        Assert.Equal("data/summary.db", cfg.SummaryDb);  // verbatim from the yaml
    }

    // ------------------------------------------------------------------
    // quote_universe
    // ------------------------------------------------------------------

    [Fact]
    public void QuoteUniverse_DefaultsWhenSectionAbsent()
    {
        var cfg = AppConfig.Parse(MinimalYaml);

        Assert.True(cfg.QuoteUniverse.Enabled);
        Assert.Equal(2, cfg.QuoteUniverse.FuturesNMonths);
        Assert.Equal("https://archives.nseindia.com/content/indices/ind_nifty500list.csv",
            cfg.QuoteUniverse.ListUrl);
    }

    [Fact]
    public void QuoteUniverse_SectionHonored()
    {
        var cfg = AppConfig.Parse(MinimalYaml + """

            quote_universe:
              enabled: false
              futures_n_months: 4
              list_url: "https://example.invalid/n500.csv"
            """);

        Assert.False(cfg.QuoteUniverse.Enabled);
        Assert.Equal(4, cfg.QuoteUniverse.FuturesNMonths);
        Assert.Equal("https://example.invalid/n500.csv", cfg.QuoteUniverse.ListUrl);
    }

    // ------------------------------------------------------------------
    // session fallbacks / optional-section defaults
    // ------------------------------------------------------------------

    [Fact]
    public void Session_QuotesWindowFallsBackToStartEnd()
    {
        var cfg = AppConfig.Parse(MinimalYaml);

        Assert.Equal(cfg.Session.Start, cfg.Session.QuotesStart);
        Assert.Equal(cfg.Session.End, cfg.Session.QuotesEnd);
        Assert.Empty(cfg.Session.PremarketTimes);
    }

    [Fact]
    public void Storage_DefaultsWhenSectionAbsent()
    {
        var cfg = AppConfig.Parse(MinimalYaml);

        Assert.Equal("data", cfg.DailyDir);
        Assert.Equal(Path.Combine("data", "summary.db"), cfg.SummaryDb);
    }

    // ------------------------------------------------------------------
    // .env file (FILE only — never process environment, like load_env())
    // ------------------------------------------------------------------

    [Fact]
    public void EnvFile_ParsesCommentsQuotesAndBlanks()
    {
        const string text = """
            # full-line comment
            FYERS_APP_ID=ABC123

            # another comment
            FYERS_PIN="123456"
            TELEGRAM_BOT_TOKEN='123456:abcdef'
              SPACED_KEY  =   spaced value
            NO_EQUALS_SIGN_HERE
            EMPTY_VALUE=
            """;

        var env = EnvFile.Load(WriteTemp(text, ".env"));

        Assert.Equal("ABC123", env.Get("FYERS_APP_ID"));
        Assert.Equal("ABC123", env["FYERS_APP_ID"]);          // indexer
        Assert.Equal("123456", env.Get("FYERS_PIN"));          // double quotes stripped
        Assert.Equal("123456:abcdef", env.Get("TELEGRAM_BOT_TOKEN"));  // single quotes stripped
        Assert.Equal("spaced value", env.Get("SPACED_KEY"));   // trimmed
        Assert.Equal(string.Empty, env.Get("EMPTY_VALUE"));
        Assert.Null(env.Get("NO_EQUALS_SIGN_HERE"));           // no '=' -> skipped
        Assert.Null(env.Get("NOT_PRESENT_ANYWHERE"));          // missing -> null
    }

    [Fact]
    public void EnvFile_MissingFileIsEmpty()
    {
        var env = EnvFile.Load(Path.Combine(Path.GetTempPath(), "no-such-env-" +
            Guid.NewGuid().ToString("N") + ".env"));

        Assert.Null(env.Get("FYERS_APP_ID"));
    }

    [Fact]
    public void Load_AppliesGdriveEnvOverridesLikeConfigPy()
    {
        const string yaml = MinimalYaml + """

            gdrive:
              enabled: true
              remote: yamlremote
              path: yamlpath
              time: "23:15"
            """;
        var envPath = WriteTemp(
            "GDRIVE_REMOTE=envremote\nGDRIVE_PATH=envpath\n", ".env");

        var cfg = AppConfig.Load(WriteTemp(yaml, ".yaml"), envPath);

        Assert.Equal("envremote", cfg.Gdrive.Remote);
        Assert.Equal("envpath", cfg.Gdrive.Path);
    }

    [Fact]
    public void Load_EmptyEnvValueFallsBackToYaml()
    {
        const string yaml = MinimalYaml + """

            gdrive:
              enabled: true
              remote: yamlremote
              path: yamlpath
            """;
        var envPath = WriteTemp("GDRIVE_REMOTE=\n", ".env");

        var cfg = AppConfig.Load(WriteTemp(yaml, ".yaml"), envPath);

        Assert.Equal("yamlremote", cfg.Gdrive.Remote);  // python: env.get(..) or cfg
        Assert.Equal("yamlpath", cfg.Gdrive.Path);
    }

    [Fact]
    public void Load_RealConfigWithTempEnv_ExposesEnv()
    {
        var envPath = WriteTemp("FYERS_APP_ID=ABC123\nFYERS_SECRET_ID=shh\n", ".env");

        var cfg = AppConfig.Load(RepoConfigPath, envPath);

        Assert.Equal(2, cfg.Symbols.Count);
        Assert.Equal("ABC123", cfg.Env.Get("FYERS_APP_ID"));
        Assert.Equal("shh", cfg.Env.Get("FYERS_SECRET_ID"));
    }

    // ------------------------------------------------------------------
    // required keys fail loudly (python raises KeyError)
    // ------------------------------------------------------------------

    [Fact]
    public void MissingRequiredKey_ThrowsWithKeyName()
    {
        const string yaml = """
            symbols:
              - underlying: NIFTY
                chain_symbol: NSE:NIFTY50-INDEX
                spot_symbol: NSE:NIFTY50-INDEX
                weekly: true
            futures:
              n_months: 3
            session:
              tz: Asia/Kolkata
              start: "09:00"
              end: "16:00"
              eod_time: "15:31"
            """;   // options.strikecount missing

        var ex = Assert.Throws<InvalidDataException>(
            () => AppConfig.Parse(yaml));

        Assert.Contains("options.strikecount", ex.Message);
    }

    [Fact]
    public void MissingSessionEodTime_ThrowsWithKeyName()
    {
        const string yaml = """
            symbols: []
            options:
              strikecount: 50
            futures:
              n_months: 3
            session:
              tz: Asia/Kolkata
              start: "09:00"
              end: "16:00"
            """;

        var ex = Assert.Throws<InvalidDataException>(() => AppConfig.Parse(yaml));

        Assert.Contains("session.eod_time", ex.Message);
    }
}
