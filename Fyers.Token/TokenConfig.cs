using System.Globalization;

namespace Fyers.Token;

/// <summary>.env FILE reader — parity port of src/config.py:load_env. Secrets
/// come from the file only; process environment variables are never consulted
/// for credentials (FYERS_ENV_PATH, which names the file, is the one env var
/// this app reads, and it carries no secret).</summary>
public sealed class EnvFile
{
    private readonly Dictionary<string, string> _values;

    public EnvFile(IEnumerable<KeyValuePair<string, string>> values) =>
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);

    public static EnvFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $".env file not found at '{path}' — copy .env.example to .env and fill in " +
                "the Fyers v3 app credentials (or pass --env / set FYERS_ENV_PATH)", path);
        }
        return Parse(File.ReadAllText(path));
    }

    public static EnvFile Parse(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
            var eq = line.IndexOf('=');
            var k = line[..eq].Trim();
            var v = line[(eq + 1)..].Trim();
            values[k] = v;
        }
        return new EnvFile(values);
    }

    /// <summary>Value or "" when absent (Python: env.get(key, "")).</summary>
    public string Get(string key) => _values.TryGetValue(key, out var v) ? v : "";

    public bool Has(string key) => _values.ContainsKey(key);
}

/// <summary>CLI args + path overrides. Config is .env + args only.</summary>
public sealed class TokenRunOptions
{
    public string? EnvPath { get; init; }
    public string? DataDir { get; init; }
    public string? RepoRoot { get; init; }
    public string BindUrl { get; init; } = "http://127.0.0.1:8001";

    /// <summary>--env PATH | FYERS_ENV_PATH, --data-dir PATH | FYERS_DATA_DIR,
    /// --repo-root PATH, --bind URL. Unrecognised args fail loud.</summary>
    public static TokenRunOptions Parse(IReadOnlyList<string> args)
    {
        string? envPath = null, dataDir = null, repoRoot = null, bind = null;
        for (var i = 0; i < args.Count; i++)
        {
            string? Value()
            {
                if (i + 1 >= args.Count)
                    throw new ArgumentException($"missing value for {args[i]}");
                return args[++i];
            }
            switch (args[i])
            {
                case "--env": envPath = Value(); break;
                case "--data-dir": dataDir = Value(); break;
                case "--repo-root": repoRoot = Value(); break;
                case "--bind": bind = Value(); break;
                default:
                    throw new ArgumentException(
                        $"unrecognised argument '{args[i]}' (expected --env, --data-dir, --repo-root, --bind)");
            }
        }
        return new TokenRunOptions
        {
            EnvPath = envPath ?? Environment.GetEnvironmentVariable("FYERS_ENV_PATH"),
            DataDir = dataDir ?? Environment.GetEnvironmentVariable("FYERS_DATA_DIR"),
            RepoRoot = repoRoot,
            BindUrl = bind ?? Environment.GetEnvironmentVariable("FYERS_BIND_URL") ?? "http://127.0.0.1:8001",
        };
    }

    public string ResolveRepoRoot() => RepoRoot ?? DetectRepoRoot();

    public string ResolveEnvPath() => EnvPath ?? Path.Combine(ResolveRepoRoot(), ".env");

    public string ResolveDataDir() => DataDir ?? Path.Combine(ResolveRepoRoot(), "data-dotnet");

    /// <summary>Walk up from the binary (then the cwd) to the repo root, marked
    /// by config.yaml/.env, so a bare `dotnet run` still finds .env.</summary>
    public static string DetectRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "config.yaml"))
                    || File.Exists(Path.Combine(dir.FullName, ".env")))
                {
                    return dir.FullName;
                }
            }
        }
        return Directory.GetCurrentDirectory();
    }
}

/// <summary>The frozen token-service view over .env + args. Mirrors the
/// Config properties src/token_service.py reads (config.py defaults, since
/// this app deliberately does not read config.yaml).</summary>
public sealed class TokenConfig
{
    public const string DefaultRedirectUri = "http://127.0.0.1:8001/callback";
    public const string DefaultPromptTime = "06:30";
    public const int DefaultRemindIntervalMin = 60;
    public const int DefaultWatchIntervalSec = 60;

    public required string AppId { get; init; }
    public required string SecretId { get; init; }
    public required string RedirectUri { get; init; }
    public string CallbackBaseUrl { get; init; } = "";
    public required string DataDir { get; init; }
    public string PromptTime { get; init; } = DefaultPromptTime;
    public int RemindIntervalMin { get; init; } = DefaultRemindIntervalMin;
    public int WatchIntervalSec { get; init; } = DefaultWatchIntervalSec;
    public string TelegramBotToken { get; init; } = "";
    public string TelegramChatId { get; init; } = "";
    public string BindUrl { get; init; } = "http://127.0.0.1:8001";

    /// <summary>Where the access token lives (collector + token service share
    /// this one path). data-dotnet/ so the .NET run never touches the Python
    /// data/ dir or the k8s PVC.</summary>
    public string TokenPath => Path.Combine(DataDir, "fyers_access_token.json");

    /// <summary>Collector -> token-service signal file; sits beside the token
    /// file so they share a volume (src/token_service.py contract).</summary>
    public string ReauthRequestPath => Path.Combine(DataDir, "fyers_reauth.request");

    /// <summary>Fails loud when the Fyers app credentials are absent. The merged
    /// collector calls this at startup, so a missing .env stops the process
    /// instead of surfacing later as a 401 mid-session.</summary>
    public void Validate()
    {
        if (AppId.Length == 0 || SecretId.Length == 0)
        {
            throw new InvalidOperationException(
                ".env is missing FYERS_APP_ID / FYERS_SECRET_ID — copy .env.example to .env " +
                "and fill in the Fyers v3 app credentials");
        }
    }

    public static TokenConfig Build(EnvFile env, TokenRunOptions options)
    {
        var callbackBase = env.Get("FYERS_CALLBACK_BASE_URL");
        var redirect = env.Get("FYERS_REDIRECT_URI");
        if (redirect.Length == 0)
        {
            // The registered URI is byte-exact and the port is pinned by the
            // Fyers app registration; default to it rather than guess.
            redirect = DefaultRedirectUri;
        }
        var promptTime = env.Get("AUTH_PROMPT_TIME");
        var cfg = new TokenConfig
        {
            AppId = env.Get("FYERS_APP_ID"),
            SecretId = env.Get("FYERS_SECRET_ID"),
            RedirectUri = redirect,
            CallbackBaseUrl = callbackBase,
            DataDir = options.ResolveDataDir(),
            PromptTime = promptTime.Length > 0 ? promptTime : DefaultPromptTime,
            RemindIntervalMin = ParseInt(env, "AUTH_REMIND_INTERVAL_MIN", DefaultRemindIntervalMin),
            WatchIntervalSec = ParseInt(env, "AUTH_WATCH_INTERVAL_SEC", DefaultWatchIntervalSec),
            TelegramBotToken = env.Get("TELEGRAM_BOT_TOKEN"),
            TelegramChatId = env.Get("TELEGRAM_CHAT_ID"),
            BindUrl = options.BindUrl,
        };
        cfg.Validate();
        return cfg;
    }

    private static int ParseInt(EnvFile env, string key, int fallback) =>
        int.TryParse(env.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
}
