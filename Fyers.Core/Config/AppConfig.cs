using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Fyers.Core.Config;

/// <summary>One entry of config.yaml <c>symbols:</c> (an index we snapshot).</summary>
public sealed record IndexCfg(
    string Underlying,
    string ChainSymbol,
    string SpotSymbol,
    bool Weekly);

/// <summary>
/// The NIFTY 500 quote universe (new in the .NET port — no yaml section exists
/// in the Python config yet). Snake_case keys when present:
/// <c>enabled</c>, <c>futures_n_months</c>, <c>list_url</c>.
/// </summary>
public sealed record QuoteUniverseCfg(
    bool Enabled,
    int FuturesNMonths,
    string ListUrl);

/// <summary>
/// The optional macro universe (new in the port): commodity + currency futures and
/// sectoral index spots quoted alongside the equity universe. Snake_case keys when
/// present: <c>enabled</c>, <c>n_months</c>, <c>commodities</c>, <c>currencies</c>,
/// <c>index_spots</c>. Absent section => <see cref="AppConfig.MacroUniverse"/> keeps
/// <c>Enabled == false</c> (feature off) while still carrying the verified default
/// lists, so <c>enabled: true</c> alone turns on exactly the universe that was
/// checked live against the Fyers masters.
/// </summary>
public sealed record MacroUniverseCfg(
    bool Enabled,
    int NMonths,
    IReadOnlyList<string> Commodities,
    IReadOnlyList<string> Currencies,
    IReadOnlyList<string> IndexSpots);

public sealed record SessionCfg(
    string Tz,
    TimeOnly QuotesStart, string QuotesStartRaw,
    TimeOnly Start, string StartRaw,
    TimeOnly End, string EndRaw,
    TimeOnly QuotesEnd, string QuotesEndRaw,
    TimeOnly EodTime, string EodTimeRaw,
    IReadOnlyList<TimeOnly> PremarketTimes);

public sealed record RateLimitCfg(
    int PerMinute,
    int PerSecond);

public sealed record GdriveCfg(
    bool Enabled,
    TimeOnly Time, string TimeRaw,
    string Remote,
    string Path);

public sealed record AuthCfg(
    string Mode,
    TimeOnly PromptTime, string PromptTimeRaw,
    TimeOnly AuthTime, string AuthTimeRaw,
    string? TokenFile,
    string? CallbackBaseUrl,
    int RemindIntervalMin,
    int WatchIntervalSec);

public sealed record CandlesCfg(
    bool Enabled,
    TimeOnly Time, string TimeRaw,
    int LookbackDays,
    int BudgetMin,
    int ProbeRetryMin);

/// <summary>
/// Frozen view over config.yaml (+ the .env FILE) — port of
/// <c>src/config.py:class Config</c>. Yaml keys are snake_case; the C#
/// properties are their PascalCase equivalents (n_monthly -> NMonthly).
/// Defaults below are the Python property defaults, key for key.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Default NIFTY 500 constituent list (NSE archive csv).</summary>
    public const string DefaultQuoteUniverseListUrl =
        "https://archives.nseindia.com/content/indices/ind_nifty500list.csv";

    /// <summary>NSE commodity-segment futures stems quoted by default (macro_universe).</summary>
    public static readonly IReadOnlyList<string> DefaultMacroCommodities =
        ["GOLD", "SILVER", "CRUDEOIL", "NATURALGAS"];

    /// <summary>NSE currency-derivatives (CDS) futures stems quoted by default.</summary>
    public static readonly IReadOnlyList<string> DefaultMacroCurrencies = ["USDINR", "EURINR"];

    /// <summary>Sectoral index spots quoted by default (symbol form NSE:{name}-INDEX).</summary>
    public static readonly IReadOnlyList<string> DefaultMacroIndexSpots =
    [
        "NIFTYIT", "NIFTYAUTO", "NIFTYPHARMA", "NIFTYMETAL",
        "NIFTYFMCG", "NIFTYREALTY", "MIDCPNIFTY", "NIFTYSMLCAP100",
    ];

    /// <summary>Indices: chain + spot + futures per minute.</summary>
    public List<IndexCfg> Symbols { get; init; } = [];

    /// <summary>constituents.enabled (python default False).</summary>
    public bool ConstituentsEnabled { get; init; }

    /// <summary>options.strikecount (required in the Python: cfg["options"]["strikecount"]).</summary>
    public int StrikeCount { get; init; }

    /// <summary>options.greeks (python default True).</summary>
    public bool Greeks { get; init; }

    /// <summary>options.n_weekly (skipped for symbols with weekly=false).</summary>
    public int NWeekly { get; init; }

    /// <summary>options.n_monthly.</summary>
    public int NMonthly { get; init; }

    /// <summary>options.n_yearly.</summary>
    public int NYearly { get; init; }

    /// <summary>constituents.stock_options.n_monthly (python default 3).</summary>
    public int StockNMonthly { get; init; }

    /// <summary>constituents.stock_options.strikecount (python default 50).</summary>
    public int StockStrikecount { get; init; }

    /// <summary>constituents.stock_options.greeks (python default True).</summary>
    public bool StockGreeks { get; init; }

    /// <summary>constituents.stock_futures.n_months (python default 3).</summary>
    public int StockFuturesNMonths { get; init; }

    /// <summary>constituents.quotes_chunk (python default 50) — max symbols per /data/quotes call.</summary>
    public int QuotesChunk { get; init; }

    /// <summary>futures.n_months (required in the Python).</summary>
    public int FuturesNMonths { get; init; }

    public SessionCfg Session { get; init; } = new(
        "Asia/Kolkata",
        new TimeOnly(9, 0), "09:00",
        new TimeOnly(9, 0), "09:00",
        new TimeOnly(16, 0), "16:00",
        new TimeOnly(16, 0), "16:00",
        new TimeOnly(15, 31), "15:31",
        []);

    /// <summary>rate_limit.per_minute (default 190) / per_second (default 8) — hard Fyers caps.</summary>
    public RateLimitCfg RateLimit { get; init; } = new(190, 8);

    /// <summary>storage.daily_dir, AS WRITTEN in the yaml (the Python absolutizes it against
    /// the project root; here the caller resolves it — the .NET port runs on
    /// data-dotnet/ and must never write the Python's data/).</summary>
    public string DailyDir { get; init; } = "data";

    /// <summary>storage.summary_db; defaults to <c>daily_dir/summary.db</c>.</summary>
    public string SummaryDb { get; init; } = "data/summary.db";

    /// <summary>retention.hot_days (python default 1).</summary>
    public int HotDays { get; init; }

    /// <summary>notify.missed_min_alert (python default 3).</summary>
    public int MissedMinAlert { get; init; }

    public GdriveCfg Gdrive { get; init; } = new(
        false, new TimeOnly(23, 15), "23:15", "gdrive", "fyers-snapshots");

    public AuthCfg Auth { get; init; } = new(
        "legacy", new TimeOnly(6, 30), "06:30", new TimeOnly(8, 0), "08:00",
        null, null, 60, 60);

    public CandlesCfg Candles { get; init; } = new(
        true, new TimeOnly(17, 0), "17:00", 6, 120, 10);

    /// <summary>NIFTY 500 universe; hard defaults when the yaml section is absent.</summary>
    public QuoteUniverseCfg QuoteUniverse { get; init; } =
        new(true, 2, DefaultQuoteUniverseListUrl);

    /// <summary>
    /// macro_universe: commodity/currency futures + sectoral index spots. Section
    /// absent => <c>Enabled == false</c> — zero effect on the existing quote specs;
    /// the lists below are the defaults used the moment it is enabled.
    /// </summary>
    public MacroUniverseCfg MacroUniverse { get; init; } =
        new(false, 2, DefaultMacroCommodities, DefaultMacroCurrencies, DefaultMacroIndexSpots);

    /// <summary>holidays.dates — extra NSE trading holidays beyond the built-in list.</summary>
    public IReadOnlyList<DateOnly> Holidays { get; init; } = [];

    /// <summary>
    /// The .env FILE this config was loaded with (secrets live here: FYERS_APP_ID,
    /// FYERS_SECRET_ID, FYERS_REDIRECT_URI, TELEGRAM_*). Never the process
    /// environment. Never log it.
    /// </summary>
    public EnvFile Env { get; init; } = EnvFile.Empty;

    /// <summary>Loads the yaml + the .env FILE from disk (parity: src/config.py).</summary>
    public static AppConfig Load(string yamlPath, string envPath) =>
        Parse(File.ReadAllText(yamlPath), EnvFile.Load(envPath));

    /// <summary>Yaml-only parse (no .env side effects). Useful in tests.</summary>
    public static AppConfig Parse(string yamlText, EnvFile? env = null)
    {
        env ??= EnvFile.Empty;
        var root = YMap.Parse(yamlText);

        // --- symbols (indices) -------------------------------------------------
        var symbols = new List<IndexCfg>();
        foreach (var entry in root.SeqRequired("symbols", "symbols"))
        {
            var chain = entry.StrRequired("chain_symbol", "symbols[].chain_symbol");
            symbols.Add(new IndexCfg(
                entry.StrRequired("underlying", "symbols[].underlying"),
                chain,
                entry.Str("spot_symbol", chain),
                entry.Bool("weekly", false)));
        }

        // --- options (indices) -------------------------------------------------
        var options = root.Map("options");
        var strikeCount = options.IntRequired("strikecount", "options.strikecount");

        // --- futures ----------------------------------------------------------
        var futures = root.MapRequired("futures", "futures");
        var futuresNMonths = futures.IntRequired("n_months", "futures.n_months");

        // --- constituents -----------------------------------------------------
        var constituents = root.Map("constituents");
        var stockOptions = constituents.Map("stock_options");
        var stockFutures = constituents.Map("stock_futures");

        // --- session ----------------------------------------------------------
        var session = root.MapRequired("session", "session");
        var tz = session.StrRequired("tz", "session.tz");
        var startRaw = session.StrRequired("start", "session.start");
        var endRaw = session.StrRequired("end", "session.end");
        var eodRaw = session.StrRequired("eod_time", "session.eod_time");
        var quotesStartRaw = session.Str("quotes_start", startRaw);
        var quotesEndRaw = session.Str("quotes_end", endRaw);
        var premarket = new List<TimeOnly>();
        foreach (var raw in session.StrList("premarket_times"))
        {
            premarket.Add(ParseTime(raw, "session.premarket_times"));
        }

        // --- storage ----------------------------------------------------------
        var storage = root.Map("storage");
        var dailyDir = storage.Str("daily_dir", "data");
        var summaryDb = storage.Str(
            "summary_db", Path.Combine(dailyDir, "summary.db"));

        // --- rate limit / notify / retention ----------------------------------
        var rateLimit = root.Map("rate_limit");
        var hotDays = root.Map("retention").Int("hot_days", 1);
        var missedMinAlert = root.Map("notify").Int("missed_min_alert", 3);

        // --- gdrive (env overrides, exactly like config.py's gdrive_remote/path) ---
        var gdrive = root.Map("gdrive");
        var gdriveRemoteRaw = env.Get("GDRIVE_REMOTE");
        var gdriveRemote = string.IsNullOrEmpty(gdriveRemoteRaw)
            ? gdrive.Str("remote", "gdrive")
            : gdriveRemoteRaw;
        var gdrivePathRaw = env.Get("GDRIVE_PATH");
        var gdrivePath = string.IsNullOrEmpty(gdrivePathRaw)
            ? gdrive.Str("path", "fyers-snapshots")
            : gdrivePathRaw;
        var gdriveTimeRaw = gdrive.Str("time", "23:15");

        // --- auth -------------------------------------------------------------
        var auth = root.Map("auth");
        var promptTimeRaw = auth.Str("prompt_time", "06:30");
        var authTimeRaw = auth.Str("auth_time", "08:00");

        // --- candles ----------------------------------------------------------
        var candles = root.Map("candles");
        var candlesTimeRaw = candles.Str("time", "17:00");

        // --- quote_universe (new in the port) ---------------------------------
        var qu = root.Map("quote_universe");

        // --- macro_universe (optional; absent = off) --------------------------
        var macro = root.Map("macro_universe");

        // --- holidays ---------------------------------------------------------
        var holidays = new List<DateOnly>();
        foreach (var raw in root.Map("holidays").StrList("dates"))
        {
            holidays.Add(DateOnly.ParseExact(raw.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture));
        }

        return new AppConfig
        {
            Symbols = symbols,
            ConstituentsEnabled = constituents.Bool("enabled", false),
            StrikeCount = strikeCount,
            Greeks = options.Bool("greeks", true),
            NWeekly = options.Int("n_weekly", 2),
            NMonthly = options.Int("n_monthly", 3),
            NYearly = options.Int("n_yearly", 2),
            StockNMonthly = stockOptions.Int("n_monthly", 3),
            StockStrikecount = stockOptions.Int("strikecount", 50),
            StockGreeks = stockOptions.Bool("greeks", true),
            StockFuturesNMonths = stockFutures.Int("n_months", 3),
            QuotesChunk = constituents.Int("quotes_chunk", 50),
            FuturesNMonths = futuresNMonths,
            Session = new SessionCfg(
                tz,
                ParseTime(quotesStartRaw, "session.quotes_start"), quotesStartRaw,
                ParseTime(startRaw, "session.start"), startRaw,
                ParseTime(endRaw, "session.end"), endRaw,
                ParseTime(quotesEndRaw, "session.quotes_end"), quotesEndRaw,
                ParseTime(eodRaw, "session.eod_time"), eodRaw,
                premarket),
            RateLimit = new RateLimitCfg(
                rateLimit.Int("per_minute", 190),
                rateLimit.Int("per_second", 8)),
            DailyDir = dailyDir,
            SummaryDb = summaryDb,
            HotDays = hotDays,
            MissedMinAlert = missedMinAlert,
            Gdrive = new GdriveCfg(
                gdrive.Bool("enabled", false),
                ParseTime(gdriveTimeRaw, "gdrive.time"), gdriveTimeRaw,
                gdriveRemote, gdrivePath),
            Auth = new AuthCfg(
                auth.Str("mode", "legacy"),
                ParseTime(promptTimeRaw, "auth.prompt_time"), promptTimeRaw,
                ParseTime(authTimeRaw, "auth.auth_time"), authTimeRaw,
                NullIfEmpty(auth.Str("token_file", "")),
                NullIfEmpty(auth.Str("callback_base_url", "")),
                auth.Int("remind_interval_min", 60),
                auth.Int("watch_interval_sec", 60)),
            Candles = new CandlesCfg(
                candles.Bool("enabled", true),
                ParseTime(candlesTimeRaw, "candles.time"), candlesTimeRaw,
                candles.Int("lookback_days", 6),
                candles.Int("budget_min", 120),
                candles.Int("probe_retry_min", 10)),
            QuoteUniverse = new QuoteUniverseCfg(
                qu.Bool("enabled", true),
                qu.Int("futures_n_months", 2),
                qu.Str("list_url", DefaultQuoteUniverseListUrl)),
            MacroUniverse = new MacroUniverseCfg(
                macro.Bool("enabled", false),
                macro.Int("n_months", 2),
                ListOr(macro.StrList("commodities"), DefaultMacroCommodities),
                ListOr(macro.StrList("currencies"), DefaultMacroCurrencies),
                ListOr(macro.StrList("index_spots"), DefaultMacroIndexSpots)),
            Holidays = holidays,
            Env = env,
        };
    }

    private static TimeOnly ParseTime(string raw, string path)
    {
        try
        {
            return TimeOnly.Parse(raw, CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                $"config: '{path}' is not an HH:mm time (got '{raw}')", ex);
        }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>An absent (or empty) yaml list keeps the code default.</summary>
    private static IReadOnlyList<string> ListOr(IReadOnlyList<string> got, IReadOnlyList<string> def)
        => got.Count > 0 ? got : def;
}

/// <summary>
/// Thin, forgiving accessor over a YamlDotNet mapping — the dynamic-navigation
/// equivalent of Python's <c>dict.get(key, default)</c> chains in config.py.
/// Missing keys, null values (<c>key:</c> with nothing after it) and non-scalars
/// all behave as "absent", like the Python's <c>cfg.get(x, {}) or {}</c> idiom.
/// </summary>
internal sealed class YMap
{
    internal static readonly YMap Empty = new(new YamlMappingNode());

    private readonly YamlMappingNode _node;

    private YMap(YamlMappingNode node) => _node = node;

    public static YMap Parse(string yamlText)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));

        return stream.Documents.Count > 0 &&
               stream.Documents[0].RootNode is YamlMappingNode root
            ? new YMap(root)
            : Empty;
    }

    /// <summary>Child mapping, or an empty one when absent/not a mapping.</summary>
    public YMap Map(string key) =>
        TryGet(key, out var child) && child is YamlMappingNode map
            ? new YMap(map)
            : Empty;

    public YMap MapRequired(string key, string path)
    {
        var child = Map(key);
        return child == Empty && !Has(key)
            ? throw Missing(path)
            : child;
    }

    public bool Has(string key) => TryGet(key, out _);

    public string Str(string key, string def) => StrOrNull(key) ?? def;

    public string StrRequired(string key, string path) =>
        StrOrNull(key) ?? throw Missing(path);

    public int Int(string key, int def)
    {
        var text = StrOrNull(key);
        return text is not null &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : def;
    }

    public int IntRequired(string key, string path)
    {
        var text = StrOrNull(key) ?? throw Missing(path);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new InvalidDataException(
                $"config: '{path}' must be an integer (got '{text}')");
    }

    /// <summary>
    /// Boolean with YAML 1.1 spellings (true/false, yes/no, on/off) — closer to
    /// PyYAML's safe_load than plain bool.Parse.
    /// </summary>
    public bool Bool(string key, bool def)
    {
        var text = StrOrNull(key);
        if (text is null)
        {
            return def;
        }

        return text.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" => true,
            "false" or "no" or "off" => false,
            _ => def,
        };
    }

    public IReadOnlyList<string> StrList(string key)
    {
        if (!TryGet(key, out var node) || node is not YamlSequenceNode seq)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var item in seq)
        {
            if (item is YamlScalarNode scalar && !string.IsNullOrEmpty(scalar.Value))
            {
                items.Add(scalar.Value);
            }
        }

        return items;
    }

    public IReadOnlyList<YMap> SeqRequired(string key, string path)
    {
        if (!TryGet(key, out var node))
        {
            throw Missing(path);
        }

        if (node is not YamlSequenceNode seq)
        {
            throw new InvalidDataException(
                $"config: '{path}' must be a list");
        }

        var items = new List<YMap>();
        foreach (var item in seq)
        {
            if (item is YamlMappingNode map)
            {
                items.Add(new YMap(map));
            }
        }

        return items;
    }

    private bool TryGet(string key, out YamlNode value)
    {
        foreach (var (k, v) in _node)
        {
            if (k is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = v;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private string? StrOrNull(string key)
    {
        if (!TryGet(key, out var node) || node is not YamlScalarNode scalar)
        {
            return null;
        }

        return string.IsNullOrEmpty(scalar.Value) ? null : scalar.Value;
    }

    private static InvalidDataException Missing(string path) => new(
        $"config: '{path}' is required (src/config.py reads it unconditionally)");

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (k, _) in _node)
        {
            sb.Append(k).Append(' ');
        }

        return sb.ToString();
    }
}
