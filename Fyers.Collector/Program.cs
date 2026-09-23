using Fyers.Collector.Capture;
using Fyers.Collector.Services;
using Fyers.Core;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Fyers.Core.Storage;
using Fyers.Core.Universe;
using Fyers.Token;
using Microsoft.Extensions.Logging;

namespace Fyers.Collector;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var root = FindRepoRoot();
        var configPath = Arg(args, "--config") ?? Path.Combine(root, "config.yaml");
        var envPath = Arg(args, "--env") ?? Path.Combine(root, ".env");
        var dataDir = Arg(args, "--data-dir") ?? Path.Combine(root, "data-dotnet");
        var masterPath = Arg(args, "--master") ?? Path.Combine(root, "data", "sym_master.json");
        Directory.CreateDirectory(dataDir);

        var cfg = AppConfig.Load(configPath, envPath);
        // data goes ONLY where --data-dir points; host default is data-dotnet/.

        await EnsureMasterAsync(masterPath);

        var builder = WebApplication.CreateBuilder(args);
        // Python-style console format for BOTH capture and token logs — the
        // token provider was written as "the collector's format", and the two
        // now share one process (merged 2026-09-23).
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddProvider(new TokenConsoleLoggerProvider());

        // FO comes from the arg-resolved path (already ensured); CDS + COM are
        // downloaded/cached by the same 12h rule into the data dir. A broken
        // CDS/COM degrades to index-spots-only; FO is fatal (capture needs it).
        var master = InstrumentMaster.LoadMulti(
        [
            (masterPath, InstrumentMaster.SegmentFo),
            (InstrumentMaster.MasterUrls[InstrumentMaster.SegmentCds], InstrumentMaster.SegmentCds),
            (InstrumentMaster.MasterUrls[InstrumentMaster.SegmentCom], InstrumentMaster.SegmentCom),
        ], dataDir, Microsoft.Extensions.Logging.Abstractions.NullLogger<InstrumentMaster>.Instance);
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = FyersClient.RequestTimeout,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FyersClient.UserAgent);

        var tokens = new TokenFileReader(Path.Combine(dataDir, "fyers_access_token.json"));
        var limiter = new RateLimiter(cfg.RateLimit.PerMinute, cfg.RateLimit.PerSecond);
        var storage = new Storage(dataDir, Path.Combine(dataDir, "summary.db"));
        var cal = new MarketCalendar(cfg, DateOnly.FromDateTime(DateTime.UtcNow),
            MarketCalendar.HolidaysFromConfig(cfg));

        builder.Services.AddSingleton(cfg);
        builder.Services.AddSingleton(cal);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton<Universe>(sp => new Universe(http, master,
            sp.GetRequiredService<ILogger<Universe>>()));
        builder.Services.AddSingleton<FyersClient>(sp => new FyersClient(http, tokens.AccessToken, limiter,
            sp.GetRequiredService<ILogger<FyersClient>>(), appId: cfg.Env.Get("FYERS_APP_ID")));
        builder.Services.AddSingleton<SnapshotRunner>(sp => new SnapshotRunner(cfg,
            sp.GetRequiredService<FyersClient>(), master, sp.GetRequiredService<Universe>(),
            storage, sp.GetRequiredService<ILogger<SnapshotRunner>>()));

        // run.py once parity: one forced snapshot, no scheduler (mode via --mode).
        // No token service here — a smoke run uses whatever token is on disk.
        if (args.Contains("--once"))
        {
            await using var once = builder.Build();
            var uni = once.Services.GetRequiredService<Universe>();
            await uni.RefreshAsync(cfg);
            var mode = Arg(args, "--mode") ?? "full";
            await once.Services.GetRequiredService<SnapshotRunner>()
                .RunAsync(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SnapshotRunner.IstZone), mode);
            return;
        }

        // ---- token service: this process owns login (merged 2026-09-23) ----
        // The callback listener, the token file and the collector's reauth
        // signal now share one process. The bind URL derives from the
        // registered redirect URI unless --bind / FYERS_BIND_URL overrides it
        // (k8s binds 0.0.0.0 so the Service can reach the pod).
        var tokenCfg = BuildTokenConfig(cfg, dataDir, args);
        builder.WebHost.UseUrls(tokenCfg.BindUrl);
        builder.Services.AddFyersTokenService(tokenCfg);

        builder.Services.AddHostedService<UniverseRefreshService>();
        builder.Services.AddHostedService<CaptureLoopService>();
        builder.Services.AddHostedService<Services.EodService>();
        builder.Services.AddHostedService<Services.CandlesService>();
        builder.Services.AddHostedService<Services.BackupService>();
        // live OI: depth-poll the CDS + MIDCPNIFTY futures every full-mode minute
        builder.Services.AddHostedService(sp => new Services.OiService(
            cfg,
            sp.GetRequiredService<FyersClient>(),
            cal,
            () => sp.GetRequiredService<Universe>().BuildQuoteSpecs(cfg).ToArray(),
            sp.GetRequiredService<Storage>(),
            sp.GetRequiredService<ILogger<Services.OiService>>()));

        var app = builder.Build();
        app.MapTokenEndpoints();
        app.Logger.LogInformation(
            "token service listening on {Bind} (redirect {Redirect}, token file {Token})",
            tokenCfg.BindUrl, tokenCfg.RedirectUri, tokenCfg.TokenPath);
        await app.RunAsync();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The token-service view over config.yaml + .env: config.yaml's
    /// <c>auth.*</c> is the single source of truth, .env carries the secrets.
    /// Mirrors what the standalone token service read before the merge.</summary>
    private static TokenConfig BuildTokenConfig(AppConfig cfg, string dataDir, string[] args)
    {
        var redirect = cfg.Env.Get("FYERS_REDIRECT_URI");
        if (string.IsNullOrEmpty(redirect)) redirect = TokenConfig.DefaultRedirectUri;
        var port = new Uri(redirect).Port;
        if (port <= 0) port = 80;
        var bind = Arg(args, "--bind")
            ?? Environment.GetEnvironmentVariable("FYERS_BIND_URL")
            ?? $"http://127.0.0.1:{port}";

        var tokenCfg = new TokenConfig
        {
            AppId = cfg.Env.Get("FYERS_APP_ID") ?? "",
            SecretId = cfg.Env.Get("FYERS_SECRET_ID") ?? "",
            RedirectUri = redirect,
            CallbackBaseUrl = cfg.Auth.CallbackBaseUrl ?? "",
            DataDir = dataDir,
            PromptTime = cfg.Auth.PromptTimeRaw,
            RemindIntervalMin = cfg.Auth.RemindIntervalMin,
            WatchIntervalSec = cfg.Auth.WatchIntervalSec,
            TelegramBotToken = cfg.Env.Get("TELEGRAM_BOT_TOKEN") ?? "",
            TelegramChatId = cfg.Env.Get("TELEGRAM_CHAT_ID") ?? "",
            BindUrl = bind,
        };
        tokenCfg.Validate();
        return tokenCfg;
    }

    /// <summary>Port of <c>instrument_master.load()</c>'s cache rule: use the
    /// cached file if younger than 12h, else download fresh from Fyers. The
    /// container has no repo checkout — the master MUST be fetched at start.</summary>
    private static async Task EnsureMasterAsync(string masterPath)
    {
        const string MasterUrl = "https://public.fyers.in/sym_details/NSE_FO_sym_master.json";
        if (File.Exists(masterPath) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(masterPath) < TimeSpan.FromHours(12))
            return;
        Console.WriteLine($"INFO    [collector] downloading Fyers NSE_FO symbol master -> {masterPath}");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FyersClient.UserAgent);
        using var resp = http.GetAsync(MasterUrl).Result;
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(masterPath);
        await resp.Content.CopyToAsync(fs);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config.yaml")))
            dir = dir.Parent;
        // container: no repo checkout — explicit args own the paths
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}

/// <summary>Starts the day's universe (constituents + NIFTY 500) and refreshes
/// it every 6h — the NSE lists only change intra-day on reconstitution days.</summary>
public sealed class UniverseRefreshService(Universe universe, AppConfig cfg, ILogger<UniverseRefreshService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await universe.RefreshAsync(cfg, ct); }
            catch (Exception e) { log.LogWarning("universe refresh failed: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); }
            catch (TaskCanceledException) { break; }
        }
    }
}
