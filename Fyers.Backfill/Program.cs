using Fyers.Backfill;
using Fyers.Backfill.Config;
using Fyers.Backfill.Failures;
using Fyers.Backfill.Instruments;
using Fyers.Backfill.Ledger;
using Fyers.Backfill.Parquet;
using Fyers.Core;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Fyers.Token;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// Fyers.Backfill — the local historical-data image.
//
//   fyers-backfill login                 # one click/day: serve the OAuth callback
//   fyers-backfill universe              # print the instrument universe size
//   fyers-backfill pilot [--pilot N]     # end-to-end check on N instruments
//   fyers-backfill backfill              # full sweep (resumable)
//   fyers-backfill update                # newest window per instrument (daily)
//   fyers-backfill status                # Ledger + dataset summary
//
// Dataset: <root>/<resolution>/<SYMBOL>.parquet (Zstd), where <root> is the
// mounted file share (default data/backfill, container default /data).
// ---------------------------------------------------------------------------

ConsoleEncodingSetup.TryUtf8();

var cli = Cli.Parse(args);
if (cli.Command is null or "help" or "--help" or "-h")
{
    Console.WriteLine(Cli.Usage);
    return 0;
}

var repoRoot = cli.RepoRoot ?? TokenRunOptions.DetectRepoRoot();
var configPath = cli.ConfigPath ?? Path.Combine(repoRoot, "config.yaml");
var envPath = cli.EnvPath ?? Path.Combine(repoRoot, ".env");
var dataDir = cli.DataDir ?? Path.Combine(repoRoot, "data");
Directory.CreateDirectory(dataDir);

var cfg = BackfillConfig.Load(configPath, envPath);
var rootOverride = cli.Root ?? Environment.GetEnvironmentVariable("FYERS_BACKFILL_ROOT");
if (rootOverride is not null)
    cfg = cfg with { Root = rootOverride };

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Information);
    b.AddProvider(new TokenConsoleLoggerProvider());
});
var log = loggerFactory.CreateLogger("backfill");

var tokenPath = Path.Combine(dataDir, "fyers_access_token.json");

// The token service needs a token-free command surface: `login` and `status`
// must work before (or without) a valid token.
if (cli.Command == "login")
    return await LoginAsync(cfg, repoRoot, dataDir, tokenPath, log);

if (cli.Command == "status")
    return Status(cfg, log);

// Everything below needs a live token.
var tokenFile = new TokenFile(tokenPath);
var token = tokenFile.AccessToken();
if (token is null)
{
    log.LogError("no fresh Fyers token at {Path} — run `fyers-backfill login` first", tokenPath);
    return 2;
}

using var http = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = System.Net.DecompressionMethods.All,
})
{
    Timeout = FyersClient.RequestTimeout,
};
http.DefaultRequestHeaders.UserAgent.ParseAdd(FyersClient.UserAgent);

var limiter = new RateLimiter(cfg.PerMinute, cfg.PerSecond)
{
    Warn = m => log.LogWarning("{Message}", m),
};
var client = new FyersClient(http, () => tokenFile.AccessToken(), limiter, log, appId: cfg.AppId);

using var ledger = new ResumeLedger(Path.Combine(cfg.Root, "_ledger.db"));
var store = new CandleStore(cfg.Root);
var failures = new FailuresManifest(Path.Combine(cfg.Root, "_failures.jsonl"));
var runner = new BackfillRunner(cfg, client, ledger, store, failures, log);

if (cli.Command == "universe")
{
    var instruments = await new UniverseBuilder(cfg, client, log).BuildAsync(default);
    var byKind = instruments.GroupBy(i => i.Kind).OrderBy(g => g.Key)
        .Select(g => $"{g.Key}={g.Count()}");
    Console.WriteLine($"universe: {instruments.Count} instruments ({string.Join(", ", byKind)})");
    return 0;
}

if (cfg.MarketHoursOnly && IsMarketHours(DateTime.UtcNow))
{
    log.LogWarning("backfill: inside NSE market hours and market_hours_only=true — not starting");
    return 0;
}

var mode = cli.Command switch
{
    "backfill" => RunMode.Backfill,
    "update" => RunMode.Update,
    "pilot" => RunMode.Pilot,
    _ => throw new ArgumentException($"unknown command '{cli.Command}'"),
};

var report = await runner.RunAsync(mode, cli.PilotCount, CancellationToken.None);
return report.AuthExpired ? 2 : 0;

// ---------------------------------------------------------------------------

/// <summary>Runs the OAuth callback host until a fresh token lands, then exits.
/// One interactive login per day is the design (daily 2FA); the token file is
/// shared with the live collector when both mount the same data dir.</summary>
static async Task<int> LoginAsync(BackfillConfig cfg, string repoRoot, string dataDir,
    string tokenPath, ILogger log)
{
    var redirect = cfg.Env.Get("FYERS_REDIRECT_URI") ?? TokenConfig.DefaultRedirectUri;
    var port = new Uri(redirect).Port;
    var bind = Environment.GetEnvironmentVariable("FYERS_BIND_URL")
               ?? $"http://0.0.0.0:{port}";

    var tokenCfg = new TokenConfig
    {
        AppId = cfg.Env.Get("FYERS_APP_ID") ?? "",
        SecretId = cfg.Env.Get("FYERS_SECRET_ID") ?? "",
        RedirectUri = redirect,
        CallbackBaseUrl = cfg.Env.Get("FYERS_CALLBACK_BASE_URL") ?? "",
        DataDir = dataDir,
        TelegramBotToken = cfg.Env.Get("TELEGRAM_BOT_TOKEN") ?? "",
        TelegramChatId = cfg.Env.Get("TELEGRAM_CHAT_ID") ?? "",
        BindUrl = bind,
    };
    tokenCfg.Validate();

    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddProvider(new TokenConsoleLoggerProvider());
    builder.WebHost.UseUrls(bind);
    builder.Services.AddFyersTokenService(tokenCfg);

    var app = builder.Build();
    app.MapTokenEndpoints();

    var file = new TokenFile(tokenPath);
    if (file.AccessToken() is not null)
    {
        log.LogInformation("login: token at {Path} is already fresh — nothing to do", tokenPath);
        return 0;
    }

    log.LogInformation("login: serving {Redirect} on {Bind}; a Telegram prompt is sent at the "
        + "prompt time — log in on the Fyers page and the token lands here", redirect, bind);
    await app.StartAsync();

    // Poll the token file; exit as soon as a fresh token appears (or on Ctrl-C).
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    while (!cts.IsCancellationRequested)
    {
        if (file.AccessToken() is not null)
        {
            log.LogInformation("login: fresh token written to {Path}", tokenPath);
            await app.StopAsync();
            return 0;
        }
        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); }
        catch (TaskCanceledException) { break; }
    }

    await app.StopAsync();
    return 0;
}

/// <summary>Ledger + dataset summary.</summary>
static int Status(BackfillConfig cfg, ILogger log)
{
    var ledgerPath = Path.Combine(cfg.Root, "_ledger.db");
    if (!File.Exists(ledgerPath))
    {
        Console.WriteLine($"status: no ledger at {ledgerPath} (nothing fetched yet)");
        return 0;
    }

    using var ledger = new ResumeLedger(ledgerPath);
    var counts = ledger.Counts();
    var failures = new FailuresManifest(Path.Combine(cfg.Root, "_failures.jsonl"));
    var files = Directory.Exists(cfg.Root)
        ? Directory.EnumerateFiles(cfg.Root, "*.parquet", SearchOption.AllDirectories).Count()
        : 0;

    Console.WriteLine($"dataset root : {cfg.Root}");
    Console.WriteLine($"partitions   : {files} parquet files");
    Console.WriteLine($"units done   : {counts.Done}");
    Console.WriteLine($"units failed : {counts.Failed}");
    Console.WriteLine($"rows written : {counts.Rows}");
    Console.WriteLine($"failures log : {failures.Path} ({failures.Count()} lines)");
    Console.WriteLine($"last run     : {ledger.GetMeta("last_run_at") ?? "-"} ({ledger.GetMeta("last_run_mode") ?? "-"})");
    if (ledger.GetMeta("parked_at") is { } parked)
        Console.WriteLine($"PARKED (auth): {parked} at {ledger.GetMeta("parked_symbol")}");

    var top = ledger.SymbolSummary(20);
    if (top.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("recent symbols:");
        foreach (var (symbol, done, failed, rows, err) in top)
            Console.WriteLine($"  {symbol,-34} done={done,-4} failed={failed,-3} rows={rows}{(err is null ? "" : $"  ! {err}")}");
    }
    return 0;
}

/// <summary>NSE continuous session (09:15–15:30 IST, Mon–Fri). Holidays are not
/// modelled here: a holiday simply wastes no requests because the window returns
/// no data.</summary>
static bool IsMarketHours(DateTime utcNow)
{
    var ist = TimeZoneInfo.ConvertTimeFromUtc(utcNow, IstZone());
    if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        return false;
    var t = TimeOnly.FromDateTime(ist);
    return t >= new TimeOnly(9, 15) && t <= new TimeOnly(15, 30);
}

static TimeZoneInfo IstZone()
{
    try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
}

/// <summary>Minimal positional/flag CLI parser for the backfill image.</summary>
internal sealed record Cli(
    string? Command,
    string? ConfigPath,
    string? EnvPath,
    string? DataDir,
    string? RepoRoot,
    string? Root,
    int PilotCount)
{
    public const string Usage = """
        fyers-backfill — local NSE historical 1-minute OHLCV backfill (Fyers v3)

        Commands:
          login                 Serve the OAuth callback and wait for a fresh token
          universe              Print the instrument universe size
          pilot [--pilot N]     Fetch N instruments end-to-end (default 5)
          backfill              Full resumable sweep
          update                Newest window per instrument (daily increment)
          status                Ledger + dataset summary

        Options:
          --config PATH         config.yaml (default <repo>/config.yaml)
          --env PATH            .env with Fyers credentials (default <repo>/.env)
          --data-dir PATH       where fyers_access_token.json lives (default <repo>/data)
          --repo-root PATH      repo root used for the defaults above
          --root PATH           dataset root / file share (overrides backfill.root)
        """;

    public static Cli Parse(string[] args)
    {
        string? command = null, config = null, env = null, dataDir = null, repoRoot = null, root = null;
        var pilot = 5;

        for (var i = 0; i < args.Length; i++)
        {
            string Value()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"missing value for {args[i]}");
                return args[++i];
            }

            switch (args[i])
            {
                case "--config": config = Value(); break;
                case "--env": env = Value(); break;
                case "--data-dir": dataDir = Value(); break;
                case "--repo-root": repoRoot = Value(); break;
                case "--root": root = Value(); break;
                case "--pilot":
                    pilot = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"unrecognised option '{args[i]}'");
                    command ??= args[i];
                    break;
            }
        }

        return new Cli(command, config, env, dataDir, repoRoot, root, pilot);
    }
}
