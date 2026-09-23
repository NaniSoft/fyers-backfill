using System.Globalization;
using System.Text.Json;

namespace Fyers.Backfill.Failures;

/// <summary>
/// Append-only JSONL manifest of units that could not be fetched. Failures are
/// never dropped silently — they land here (and in the ledger as <c>failed</c>)
/// so an operator can see exactly what is missing and why. One JSON object per
/// line keeps the file greppable and crash-safe.
/// </summary>
public sealed class FailuresManifest(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Record(string symbol, string resolution, DateOnly from, DateOnly to,
        string kind, string error)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            symbol,
            resolution,
            from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            kind,
            error,
        });

        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            File.AppendAllText(Path, line + Environment.NewLine);
        }
    }

    /// <summary>Line count of an existing manifest (status command).</summary>
    public int Count() => File.Exists(Path) ? File.ReadLines(Path).Count() : 0;
}
