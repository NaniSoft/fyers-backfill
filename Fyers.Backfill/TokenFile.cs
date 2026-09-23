using System.Text.Json;

namespace Fyers.Backfill;

/// <summary>
/// Reads the shared Fyers token file written by the token service
/// (<c>{dataDir}/fyers_access_token.json</c>). A token is "fresh" only when its
/// <c>saved_at</c> IST date is today — Fyers resets access tokens daily at
/// ~06:00 IST. Mirrors the collector's <c>TokenFileReader</c> so the backfill and
/// the live collector can share one login.
/// </summary>
public sealed class TokenFile(string tokenPath)
{
    private static readonly TimeZoneInfo Ist = ResolveIst();

    public string Path { get; } = tokenPath;

    /// <summary>Today's token, or null when missing/stale/unparsable.</summary>
    public string? AccessToken()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var savedAt = doc.RootElement.TryGetProperty("saved_at", out var sa) ? sa.GetInt64() : 0;
            var savedIst = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeSeconds(savedAt).UtcDateTime, Ist);
            var todayIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist).Date;
            if (savedIst.Date < todayIst)
                return null;
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private static TimeZoneInfo ResolveIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }
}
