using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fyers.Backfill.FnoPull;

/// <summary>
/// One expired contract to pull, as persisted by the python queue build
/// (<c>data/backfill/expired_pulls.json</c>): the Fyers ticker, its underlying stem
/// and its expiry date.
/// </summary>
public sealed record FnoQueueItem
{
    [JsonPropertyName("sym")] public string Sym { get; init; } = "";
    [JsonPropertyName("u")] public string Underlying { get; init; } = "";
    [JsonPropertyName("e")] public string Expiry { get; init; } = "";

    public DateOnly ExpiryDate
        => DateOnly.TryParse(Expiry, out var d) ? d : DateOnly.MinValue;
}

/// <summary>
/// The persisted work plan plus the per-contract status ledger, both inherited from
/// the python puller so an interrupted campaign resumes exactly where it stopped
/// instead of re-fetching the ~64.5k contracts already on disk.
///
/// The ledger maps a ticker to one of:
/// <list type="bullet">
/// <item><c>"ok"</c> — candles fetched and written;</item>
/// <item><c>"no_data"</c> — Fyers answered <c>no_data</c>; terminal, never retried
/// (this is the documented "valid empty result", and a strike that never listed will
/// never list);</item>
/// <item><c>"error:&lt;code&gt;"</c> — a hard failure, retried on a later run.</item>
/// </list>
/// </summary>
public sealed class FnoQueue
{
    public required string QueuePath { get; init; }
    public required string LedgerPath { get; init; }

    public IReadOnlyList<FnoQueueItem> Items { get; private set; } = [];
    public Dictionary<string, string> Ledger { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Read the persisted queue and ledger, tolerating an absent ledger.</summary>
    public static FnoQueue Load(string stateDir)
    {
        var q = new FnoQueue
        {
            QueuePath = Path.Combine(stateDir, "expired_pulls.json"),
            LedgerPath = Path.Combine(stateDir, "expired_ledger.json"),
        };

        if (!File.Exists(q.QueuePath))
            throw new FileNotFoundException(
                $"no expired queue at {q.QueuePath} — the python queue build must run first", q.QueuePath);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        q.Items = JsonSerializer.Deserialize<List<FnoQueueItem>>(File.ReadAllText(q.QueuePath), opts) ?? [];

        if (File.Exists(q.LedgerPath))
        {
            q.Ledger = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(q.LedgerPath), opts)
                        ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return q;
    }

    /// <summary>
    /// Work still outstanding, <b>oldest expiry first</b>.
    ///
    /// Chronological order is the whole point: the campaign then advances through
    /// calendar time, so "where are we" has one unambiguous answer — the expiry
    /// currently in flight — and completion is a known quantity (every item in the
    /// queue) rather than an open-ended tail. (The python puller ordered newest
    /// first, which front-loads the recent, most valuable expiries but gives no
    /// finish line.)
    ///
    /// <c>no_data</c> is terminal and excluded; <c>error:*</c> IS retried.
    /// </summary>
    public IReadOnlyList<FnoQueueItem> Remaining()
        => Items
            .Where(i => !Ledger.TryGetValue(i.Sym, out var s)
                        || (s.StartsWith("error", StringComparison.Ordinal)))
            .OrderBy(i => i.ExpiryDate)
            .ThenBy(i => i.Underlying, StringComparer.Ordinal)
            .ThenBy(i => i.Sym, StringComparer.Ordinal)
            .ToList();

    /// <summary>Counts by ledger status, for the run banner.</summary>
    public (int Ok, int NoData, int Error) StatusCounts()
    {
        int ok = 0, nodata = 0, err = 0;
        foreach (var v in Ledger.Values)
        {
            if (v == "ok") ok++;
            else if (v == "no_data") nodata++;
            else if (v.StartsWith("error", StringComparison.Ordinal)) err++;
        }
        return (ok, nodata, err);
    }

    /// <summary>
    /// Persist the ledger atomically (temp file + replace).
    ///
    /// This is a deliberate fix over the python puller, which called
    /// <c>json.dump</c> straight onto the live path every 25 contracts: a kill
    /// mid-write truncated the file, losing the entire campaign's progress record.
    /// </summary>
    public void SaveLedger()
    {
        var tmp = LedgerPath + ".tmp";
        var json = JsonSerializer.Serialize(Ledger);
        File.WriteAllText(tmp, json);
        File.Move(tmp, LedgerPath, overwrite: true);
    }
}
