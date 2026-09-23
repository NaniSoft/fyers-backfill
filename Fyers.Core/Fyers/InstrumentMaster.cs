using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Fyers.Core.Fyers;

/// <summary>A single (nearest-n) monthly futures contract for an underlying.</summary>
/// <param name="Symbol">Full Fyers ticker, e.g. NSE:RELIANCE26SEPFUT.</param>
/// <param name="ExpiryEpoch">Expiry as unix seconds (master's <c>expiryDate</c>).</param>
public sealed record FutContract(string Symbol, long ExpiryEpoch);

/// <summary>
/// Fyers NSE F&amp;O instrument master — .NET port of <c>src/instrument_master.py</c>
/// (decision 13). The master file is
/// <c>https://public.fyers.in/sym_details/NSE_FO_sym_master.json</c> cached at
/// <c>data/sym_master.json</c>: a top-level JSON object keyed by full Fyers ticker
/// (<c>"NSE:RELIANCE26SEPFUT"</c>) whose values are entry objects carrying
/// <c>symTicker</c>, <c>optType</c>, <c>expiryDate</c> (epoch-seconds STRING),
/// <c>underSym</c>, <c>strikePrice</c>, ... (77 MB, ~74k entries).
///
/// Expiry *selection* is not done here (the live option-chain's
/// <c>expiryData</c> drives that, as in Python). This class only resolves the
/// monthly futures tickers — they are absent from the chain response — and
/// formats cash EQ symbols.
///
/// The same three-field entry shape is shared by the other two Fyers symbol
/// masters, so one parser serves all segments (see <see cref="LoadMulti"/>):
/// NSE_FO (equity/index F&amp;O), NSE_CD (currency derivatives) and NSE_COM
/// (commodity derivatives). Only the URL and the stems differ — the CDS master
/// also carries WEEKLY currency futures (<c>USDINR26O01FUT</c>), which the
/// monthly tail rule already filters out.
/// </summary>
public sealed partial class InstrumentMaster
{
    /// <summary>Monthly FUT tail: RELIANCE26<b>SEPFUT</b> -> "26SEPFUT" (decision 13).</summary>
    [GeneratedRegex(@"^\d{2}[A-Z]{3}FUT$")]
    private static partial Regex MonthlyFutTail();

    /// <summary>Segment label of the equity/index F&amp;O master (the default one).</summary>
    public const string SegmentFo = "FO";

    /// <summary>Segment label of the currency-derivatives master (NSE_CD).</summary>
    public const string SegmentCds = "CDS";

    /// <summary>Segment label of the commodity-derivatives master (NSE_COM).</summary>
    public const string SegmentCom = "COM";

    /// <summary>public.fyers.in master URLs, per segment (CDS is literally "NSE_CD_...").</summary>
    public static readonly IReadOnlyDictionary<string, string> MasterUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SegmentFo] = "https://public.fyers.in/sym_details/NSE_FO_sym_master.json",
            [SegmentCds] = "https://public.fyers.in/sym_details/NSE_CD_sym_master.json",
            [SegmentCom] = "https://public.fyers.in/sym_details/NSE_COM_sym_master.json",
        };

    /// <summary>How long a downloaded master stays trusted (Program.EnsureMasterAsync rule).</summary>
    public static readonly TimeSpan CacheFreshness = TimeSpan.FromHours(12);

    /// <summary>Cache file name for a segment inside the data dir: sym_master_fo.json, ...</summary>
    public static string CacheFileName(string segment)
        => $"sym_master_{(segment ?? "").Trim().ToLowerInvariant()}.json";

    /// <summary>Full cache path for a segment inside <paramref name="cacheDir"/>.</summary>
    public static string CachePathFor(string segment, string cacheDir)
        => Path.Combine(cacheDir, CacheFileName(segment));

    /// <summary>True when the string is an http(s) URL rather than a local path.</summary>
    public static bool IsUrl(string? pathOrUrl)
        => !string.IsNullOrWhiteSpace(pathOrUrl)
           && (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Per-segment futures index (segment label -> underlying -> contracts).</summary>
    private readonly IReadOnlyDictionary<string, Dictionary<string, FutContract[]>> _segments;

    /// <summary>The default (F&amp;O) index — every 2-argument lookup lands here.</summary>
    private readonly Dictionary<string, FutContract[]> _futsByUnderlying;

    private InstrumentMaster(Dictionary<string, FutContract[]> futsByUnderlying, int entryCount,
        IReadOnlyDictionary<string, Dictionary<string, FutContract[]>>? segments = null)
    {
        _futsByUnderlying = futsByUnderlying;
        EntryCount = entryCount;
        var map = new Dictionary<string, Dictionary<string, FutContract[]>>(StringComparer.OrdinalIgnoreCase)
        {
            [SegmentFo] = futsByUnderlying,
        };
        if (segments is not null)
        {
            foreach (var (seg, index) in segments)
                map[seg.Trim().ToUpperInvariant()] = index;
            map[SegmentFo] = futsByUnderlying;      // the default index always wins for "FO"
        }
        _segments = map;
    }

    /// <summary>Number of entries in the master file (parity with the Python log line).
    /// Across <see cref="LoadMulti"/> segments: the sum of all loaded files.</summary>
    public int EntryCount { get; }

    /// <summary>Monthly FUT contracts indexed for one segment (0 when that segment is
    /// not loaded). Distinct from <see cref="EntryCount"/>, which counts raw master
    /// entries across every loaded file.</summary>
    public int MonthlyCountOf(string segment)
        => _segments.TryGetValue(NormalizeSegment(segment), out var s) ? s.Sum(l => l.Value.Length) : 0;

    /// <summary>True when a segment's futures index is available (per-segment lookups).</summary>
    public bool HasSegment(string segment) => _segments.ContainsKey(NormalizeSegment(segment));

    /// <summary>Segments currently loaded, e.g. ["FO"] after <see cref="Load"/> or
    /// ["FO","CDS","COM"] after <see cref="LoadMulti"/>.</summary>
    public IReadOnlyList<string> Segments => _segments.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Parse the master json. Only the four fields this class needs are read;
    /// the file is streamed (Utf8JsonReader) so the 77 MB DOM is never built.
    /// </summary>
    public static InstrumentMaster Load(string path)
    {
        var (futs, entryCount) = ParseMaster(path);
        return new InstrumentMaster(futs, entryCount);
    }

    /// <summary>
    /// Parse a master file into (underlying -> ascending monthly contracts) plus its
    /// entry count. The streaming/discard-as-read pass of the original <c>Load</c>.
    /// </summary>
    private static (Dictionary<string, FutContract[]> Futs, int EntryCount) ParseMaster(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"instrument master not found: {path}", path);

        // Top level: { "NSE:XXX": { entry }, ... } — one pass, entries discarded as read.
        var bytes = File.ReadAllBytes(path);
        var futs = new Dictionary<string, List<FutContract>>(StringComparer.Ordinal);
        var entry = new EntryFields();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int entryCount = 0;

        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, new JsonReaderState());
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException($"instrument master {path}: expected a top-level JSON object");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;                       // end of the master object
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new InvalidDataException($"instrument master {path}: malformed entry key");

            entryCount++;
            var key = reader.GetString() ?? "";
            reader.Read();                   // -> entry value
            ReadEntry(ref reader, ref entry);

            var sym = entry.SymTicker ?? key;
            if (entry.OptType != "XX" || entry.Expiry is null || !seen.Add(sym))
                continue;
            var (underlying, tail) = SplitUnderlying(sym);
            if (!MonthlyFutTail().IsMatch(tail))
                continue;                    // XX but not a monthly future
            if (!futs.TryGetValue(underlying, out var list))
                futs[underlying] = list = new List<FutContract>();
            list.Add(new FutContract(sym, entry.Expiry.Value));
        }

        var sorted = new Dictionary<string, FutContract[]>(futs.Count, StringComparer.Ordinal);
        foreach (var (underlying, list) in futs)
        {
            // Parity: python sorts (expiry, symbol) tuples ascending.
            list.Sort(static (a, b) =>
            {
                var c = a.ExpiryEpoch.CompareTo(b.ExpiryEpoch);
                return c != 0 ? c : string.CompareOrdinal(a.Symbol, b.Symbol);
            });
            sorted[underlying] = list.ToArray();
        }
        return (sorted, entryCount);
    }

    // -------------------------------------------------------------- multi segment

    /// <summary>
    /// Load several segment masters into one instance: <c>("path-or-url", "FO")</c>,
    /// <c>("path-or-url", "CDS")</c>, <c>("path-or-url", "COM")</c>. The default
    /// (<see cref="SegmentFo"/>) index — what the 2-argument
    /// <see cref="FuturesContracts(string,int)"/> and <see cref="CashSymbol"/> see — is
    /// taken from the first FO entry; later FO entries merge into it.
    ///
    /// Caching: an http(s) entry is downloaded to
    /// <c>{cacheDir}/sym_master_{segment}.json</c> and reused while that file is
    /// younger than <see cref="CacheFreshness"/> (12 h) — the same rule
    /// <c>Program.EnsureMasterAsync</c> applies to the F&amp;O master. Downloads go to
    /// a sibling <c>.tmp</c> first and are moved into place, so a killed download can
    /// never leave a fresh-looking truncated cache. A local path entry is used as-is.
    ///
    /// Failure policy: an entry that cannot be downloaded or parsed is skipped with a
    /// warning, EXCEPT <see cref="SegmentFo"/> — a broken F&amp;O master throws, because
    /// the whole collector depends on it. Missing segments simply have no futures
    /// (per-segment lookups return empty), so the macro universe degrades to
    /// index-spots only instead of taking capture down.
    /// </summary>
    public static InstrumentMaster LoadMulti(
        IReadOnlyList<(string pathOrUrl, string segment)> masters, string cacheDir, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(masters);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        Directory.CreateDirectory(cacheDir);

        var merged = new Dictionary<string, Dictionary<string, FutContract[]>>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        var defaultIndex = new Dictionary<string, FutContract[]>(StringComparer.Ordinal);
        var hasFo = false;

        foreach (var (raw, seg) in masters)
        {
            var segment = NormalizeSegment(seg);
            try
            {
                var path = EnsureCached(raw, segment, cacheDir, log);
                var (futs, entries) = ParseMaster(path);
                entryCount += entries;
                merged[segment] = MergeIndex(merged.TryGetValue(segment, out var soFar) ? soFar : null, futs);
                if (segment == SegmentFo)
                {
                    hasFo = true;
                    foreach (var (u, list) in futs)
                        defaultIndex[u] = list;
                }
            }
            catch (Exception e)
            {
                if (segment == SegmentFo)
                    throw;                       // the collector cannot run without FO
                Log(log, LogLevel.Warning,
                    "symbol master {Segment} unavailable, continuing without it: {Message}", segment, e.Message);
            }
        }

        Log(log, LogLevel.Information, "symbol masters loaded: {Segments} ({Entries} entries)",
            string.Join(",", merged.Keys.OrderBy(k => k, StringComparer.Ordinal)), entryCount);
        return new InstrumentMaster(hasFo ? defaultIndex : new Dictionary<string, FutContract[]>(StringComparer.Ordinal),
            entryCount, merged);
    }

    /// <summary>Union of two per-underlying contract indexes (later file wins on ties).</summary>
    private static Dictionary<string, FutContract[]> MergeIndex(Dictionary<string, FutContract[]>? first,
        Dictionary<string, FutContract[]> second)
    {
        if (first is null || first.Count == 0)
            return second;
        foreach (var (underlying, contracts) in second)
        {
            first[underlying] = !first.TryGetValue(underlying, out var existing) || existing.Length == 0
                ? contracts
                : existing.Concat(contracts)
                          .Distinct(ContractComparer.Instance)
                          .OrderBy(c => c.ExpiryEpoch)
                          .ThenBy(c => c.Symbol, StringComparer.Ordinal)
                          .ToArray();
        }
        return first;
    }

    private sealed class ContractComparer : IEqualityComparer<FutContract>
    {
        public static readonly ContractComparer Instance = new();
        public bool Equals(FutContract? x, FutContract? y) => x?.Symbol == y?.Symbol;
        public int GetHashCode(FutContract o) => o.Symbol?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }

    /// <summary>upper-cased, trimmed segment label ("fo" -> "FO"); null/empty -> FO.</summary>
    private static string NormalizeSegment(string? segment)
        => string.IsNullOrWhiteSpace(segment) ? SegmentFo : segment.Trim().ToUpperInvariant();

    /// <summary>
    /// Cache rule shared by <see cref="Program.EnsureMasterAsync"/> and
    /// <see cref="LoadMulti"/>: a local path comes back untouched; a URL is downloaded
    /// into <see cref="CachePathFor"/> unless that file is younger than
    /// <see cref="CacheFreshness"/>. Returns the local path to parse.
    /// </summary>
    public static string EnsureCached(string pathOrUrl, string segment, string cacheDir, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrUrl);
        if (!IsUrl(pathOrUrl))
        {
            if (!File.Exists(pathOrUrl))
                throw new FileNotFoundException($"instrument master not found: {pathOrUrl}", pathOrUrl);
            return pathOrUrl;
        }

        var cache = CachePathFor(segment, cacheDir);
        if (File.Exists(cache) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < CacheFreshness)
        {
            Log(log, LogLevel.Debug, "symbol master {Segment} cache is fresh: {Path}", segment, cache);
            return cache;
        }

        Log(log, LogLevel.Information, "downloading Fyers {Segment} symbol master -> {Path}", segment, cache);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };   // 77 MB FO / 27 MB COM
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FyersClient.UserAgent);
        using var resp = http.GetAsync(pathOrUrl, HttpCompletionOption.ResponseHeadersRead)
                             .GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();

        // .tmp + atomic move: a truncated download must never look like a fresh cache.
        var tmp = cache + ".tmp";
        using (var fs = File.Create(tmp))
            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        File.Move(tmp, cache, overwrite: true);
        return cache;
    }

    /// <summary>
    /// ILogger path keeps the structured template; the logger-free fallback (tests, a
    /// bare container run) renders the same line positionally so progress is visible
    /// on stdout, like <c>Program.EnsureMasterAsync</c>'s INFO line.
    /// </summary>
    private static void Log(ILogger? log, LogLevel level, string message, params object?[] args)
    {
        if (log is not null)
            log.Log(level, message, args);
        else if (level >= LogLevel.Information)
            Console.WriteLine($"{level.ToString().ToUpperInvariant()} [instrument-master] {Render(message, args)}");
    }

    /// <summary>Positional <c>{"Name"}</c> substitution for the logger-free path.</summary>
    private static string Render(string message, object?[] args)
    {
        if (args.Length == 0)
            return message;
        var sb = new System.Text.StringBuilder(message.Length + 32);
        var next = 0;
        var i = 0;
        while (i < message.Length)
        {
            var open = message.IndexOf('{', i);
            if (open < 0)
                break;
            var close = message.IndexOf('}', open);
            if (close < 0)
                break;
            sb.Append(message, i, open - i);
            sb.Append(next < args.Length ? args[next++]?.ToString() : "");
            i = close + 1;
        }
        sb.Append(message, i, message.Length - i);
        return sb.ToString();
    }

    /// <summary>
    /// Bare NSE ticker -> Fyers cash EQ symbol. Parity with
    /// <c>src/instrument_master.py:cash_symbol</c> (pure string format — the F&amp;O
    /// master contains no -EQ entries, so there is nothing to look up).
    /// </summary>
    public string? CashSymbol(string ticker)
        => string.IsNullOrWhiteSpace(ticker) ? null : $"NSE:{ticker}-EQ";

    /// <summary>
    /// The nearest <paramref name="nMonths"/> monthly FUT contracts for
    /// <paramref name="underlying"/>, ascending by expiry (current month first).
    /// Empty when the underlying has no futures (non-F&amp;O names — callers skip).
    /// Reads the default <see cref="SegmentFo"/> index.
    /// </summary>
    public IReadOnlyList<FutContract> FuturesContracts(string underlying, int nMonths)
        => FuturesContracts(underlying, nMonths, SegmentFo);

    /// <summary>
    /// Same selection, in an explicit segment: <see cref="SegmentFo"/> (equity/index
    /// F&amp;O), <see cref="SegmentCds"/> (currency futures — weekly contracts are
    /// already filtered out by the monthly tail rule) or <see cref="SegmentCom"/>
    /// (commodity futures). Empty when that segment was not loaded or the underlying
    /// has no monthly contracts there, so "up to n_months" is the caller's contract.
    /// </summary>
    public IReadOnlyList<FutContract> FuturesContracts(string underlying, int nMonths, string segment)
    {
        if (nMonths <= 0 || string.IsNullOrEmpty(underlying))
            return Array.Empty<FutContract>();
        return _segments.TryGetValue(NormalizeSegment(segment), out var index)
               && index.TryGetValue(underlying, out var all)
            ? (all.Length <= nMonths ? all : all.Take(nMonths).ToArray())
            : Array.Empty<FutContract>();
    }

    /// <summary>NIFTY26SEPFUT -> NIFTY ; NIFTY26SEP24800CE -> NIFTY (python _underlying_of).</summary>
    public static string UnderlyingOf(string sym) => SplitUnderlying(sym).Underlying;

    /// <summary>
    /// Splits a master ticker into the python-<c>_underlying_of</c> prefix and the
    /// remainder after it (<c>NSE:RELIANCE26SEPFUT</c> -> <c>RELIANCE</c>, <c>26SEPFUT</c>).
    /// </summary>
    private static (string Underlying, string Tail) SplitUnderlying(string sym)
    {
        var s = sym;
        var colon = s.IndexOf(':');
        if (colon >= 0)
            s = s[(colon + 1)..];
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsDigit(s[i]))
                return (s[..i], s[i..]);
        }
        return (s, string.Empty);
    }

    private static void ReadEntry(ref Utf8JsonReader reader, ref EntryFields f)
    {
        f.SymTicker = null;
        f.OptType = null;
        f.Expiry = null;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }
        var depth = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (depth == 0)
                    return;
                depth--;
                continue;
            }
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
                continue;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();                   // -> property value
            switch (name)
            {
                case "symTicker" when reader.TokenType == JsonTokenType.String:
                    f.SymTicker = reader.GetString();
                    break;
                case "optType" when reader.TokenType == JsonTokenType.String:
                    f.OptType = reader.GetString();
                    break;
                case "expiryDate":
                    f.Expiry = ReadEpoch(ref reader);
                    break;
                default:
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        depth++;
                    break;
            }
        }
    }

    /// <summary><c>expiryDate</c> is an epoch-seconds string ("1790676600"); tolerate numbers.</summary>
    private static long? ReadEpoch(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), out var v) && v > 0 ? v : null;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var n) && n > 0 ? n : null;
            default:
                reader.Skip();
                return null;
        }
    }

    private struct EntryFields
    {
        public string? SymTicker;
        public string? OptType;
        public long? Expiry;
    }
}
