using System.Globalization;
using Fyers.Core.Config;
using YamlDotNet.RepresentationModel;

namespace Fyers.Backfill.Config;

/// <summary>
/// The <c>backfill:</c> section of config.yaml, plus the .env file (secrets).
/// Deliberately independent of <see cref="AppConfig"/>: the backfill is a batch
/// job that runs against a mounted file share and does not need the collector's
/// <c>symbols</c>/<c>options</c>/<c>session</c> sections. Absent keys fall back
/// to the defaults below, so a minimal config.yaml works.
/// </summary>
public sealed record BackfillConfig
{
    /// <summary>Documented 1-minute floor for Fyers /data/history.</summary>
    public static readonly DateOnly DefaultFrom = new(2017, 7, 3);

    /// <summary>Dataset root: <c>&lt;root&gt;/&lt;resolution&gt;/&lt;SYMBOL&gt;.parquet</c>.</summary>
    public string Root { get; init; } = "data/backfill";

    public DateOnly From { get; init; } = DefaultFrom;

    /// <summary>Inclusive end date; null means "today, IST".</summary>
    public DateOnly? To { get; init; }

    /// <summary>Max calendar days per /data/history request (Fyers caps 1-min at 100).</summary>
    public int ChunkDays { get; init; } = 100;

    /// <summary>Resolutions to pull: "1" (minute) and/or "D" (daily).</summary>
    public IReadOnlyList<string> Resolutions { get; init; } = ["1"];

    /// <summary>Request oi_flag/include_oi for derivatives.</summary>
    public bool IncludeOi { get; init; } = true;

    /// <summary>
    /// Write each window as its own part file under <c>_parts/</c> instead of
    /// read-merge-rewriting the whole symbol file. Much faster for a long
    /// backward sweep; run the <c>compact</c> pass afterwards to fold parts into
    /// the single per-symbol file.
    /// </summary>
    public bool PartFiles { get; init; }

    /// <summary>Max API requests one run may spend (the account pool is 100k/day).</summary>
    public int DailyBudget { get; init; } = 90_000;

    /// <summary>Wall-clock cap for one run, in minutes (0 = unlimited).</summary>
    public int MaxMinutes { get; init; }

    /// <summary>Only run outside NSE market hours (protects the live collector's budget).</summary>
    public bool MarketHoursOnly { get; init; }

    public int PerMinute { get; init; } = 190;
    public int PerSecond { get; init; } = 8;

    public bool UniverseEquities { get; init; } = true;
    public bool UniverseFutures { get; init; } = true;
    public bool UniverseOptions { get; init; } = true;
    public bool UniverseExpired { get; init; } = true;

    /// <summary>
    /// Include EXPIRED options. Default false and effectively unusable: Fyers
    /// returns no_data for expired option history (live-verified 2026-09-25), so
    /// enabling this only burns requests.
    /// </summary>
    public bool UniverseExpiredOptions { get; init; }

    /// <summary>Optional whitelist of underlyings (bare stems). Empty = every underlying.</summary>
    public IReadOnlyList<string> Underlyings { get; init; } = [];

    /// <summary>Where symbol masters are cached (default <c>&lt;root&gt;/_masters</c>).</summary>
    public string? MasterCacheDir { get; init; }

    /// <summary>Secrets + paths from the .env file (never the process environment).</summary>
    public EnvFile Env { get; init; } = EnvFile.Load("");

    public string MasterCache => MasterCacheDir ?? Path.Combine(Root, "_masters");

    public string AppId => Env.Get("FYERS_APP_ID") ?? "";

    /// <summary>Resolved end date: <see cref="To"/> or today in IST.</summary>
    public DateOnly EndDate =>
        To ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTime.UtcNow, "Asia/Kolkata"));

    public static BackfillConfig Load(string yamlPath, string envPath)
    {
        var text = File.Exists(yamlPath) ? File.ReadAllText(yamlPath) : "";
        return Parse(text, EnvFile.Load(envPath));
    }

    public static BackfillConfig Parse(string yamlText, EnvFile? env = null)
    {
        env ??= EnvFile.Load("");
        var root = Map.Parse(yamlText).Sub("backfill");

        var from = ParseDate(root.Str("from", "2017-07-03"), "backfill.from");
        var toRaw = root.StrOrNull("to");
        var universe = root.Sub("universe");
        var rate = root.Sub("rate_limit");

        return new BackfillConfig
        {
            Root = root.Str("root", "data/backfill"),
            From = from,
            To = toRaw is null ? null : ParseDate(toRaw, "backfill.to"),
            ChunkDays = root.Int("chunk_days", 100),
            Resolutions = ListOr(root.StrList("resolutions"), ["1"]),
            IncludeOi = root.Bool("include_oi", true),
            PartFiles = root.Bool("part_files", false),
            DailyBudget = root.Int("daily_budget", 90_000),
            MaxMinutes = root.Int("max_minutes", 0),
            MarketHoursOnly = root.Bool("market_hours_only", false),
            PerMinute = rate.Int("per_minute", 190),
            PerSecond = rate.Int("per_second", 8),
            UniverseEquities = universe.Bool("equities", true),
            UniverseFutures = universe.Bool("futures", true),
            UniverseOptions = universe.Bool("options", true),
            UniverseExpired = universe.Bool("expired", true),
            UniverseExpiredOptions = universe.Bool("expired_options", false),
            Underlyings = universe.StrList("underlyings"),
            MasterCacheDir = root.StrOrNull("master_cache_dir"),
            Env = env,
        };
    }

    private static DateOnly ParseDate(string raw, string path)
    {
        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d;
        throw new InvalidDataException($"config: '{path}' must be yyyy-MM-dd (got '{raw}')");
    }

    private static IReadOnlyList<string> ListOr(IReadOnlyList<string> got, IReadOnlyList<string> def)
        => got.Count > 0 ? got : def;
}

/// <summary>Minimal forgiving YAML mapping accessor (mirrors Core's internal YMap).</summary>
internal sealed class Map
{
    internal static readonly Map Empty = new(new YamlMappingNode());

    private readonly YamlMappingNode _node;

    private Map(YamlMappingNode node) => _node = node;

    public static Map Parse(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
            return Empty;
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root
            ? new Map(root)
            : Empty;
    }

    public Map Sub(string key) =>
        TryGet(key, out var child) && child is YamlMappingNode map ? new Map(map) : Empty;

    public string Str(string key, string def) => StrOrNull(key) ?? def;

    public string? StrOrNull(string key)
    {
        if (!TryGet(key, out var node) || node is not YamlScalarNode scalar)
            return null;
        // A YAML null (`to: null`, `to: ~`, `to:`) arrives as a scalar with the
        // null tag or a null-ish value — treat it as absent, not the string "null".
        if (IsNullScalar(scalar) || string.IsNullOrEmpty(scalar.Value))
            return null;
        return scalar.Value;
    }

    private static bool IsNullScalar(YamlScalarNode scalar)
    {
        if (scalar.Tag.ToString().Contains("null", StringComparison.OrdinalIgnoreCase))
            return true;
        var v = scalar.Value;
        return v is "" or "~"
               || string.Equals(v, "null", StringComparison.OrdinalIgnoreCase);
    }

    public int Int(string key, int def)
    {
        var text = StrOrNull(key);
        return text is not null &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : def;
    }

    public bool Bool(string key, bool def)
    {
        var text = StrOrNull(key)?.ToLowerInvariant();
        return text switch
        {
            "true" or "yes" or "on" => true,
            "false" or "no" or "off" => false,
            _ => def,
        };
    }

    public IReadOnlyList<string> StrList(string key)
    {
        if (!TryGet(key, out var node) || node is not YamlSequenceNode seq)
            return [];
        var items = new List<string>();
        foreach (var item in seq)
        {
            if (item is YamlScalarNode scalar && !string.IsNullOrEmpty(scalar.Value))
                items.Add(scalar.Value);
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
}
