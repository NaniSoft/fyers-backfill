using System.Text.Json;

namespace Fyers.Backfill;

/// <summary>
/// Reads the shared Fyers token file written by the token service
/// (<c>{dataDir}/fyers_access_token.json</c>). A token is "fresh" while the wall
/// clock is before the NEXT <b>06:00 IST</b> after it was saved — Fyers resets
/// access tokens daily at ~06:00 IST, so a token minted at 23:40 the previous
/// evening is still valid through the midnight boundary. (Treating "saved on an
/// earlier calendar day" as stale would needlessly park a backfill that is
/// running across midnight — observed live 2026-09-25.)
/// </summary>
public sealed class TokenFile(string tokenPath)
{
    private static readonly TimeZoneInfo Ist = ResolveIst();

    /// <summary>Fyers' daily access-token reset (~06:00 IST).</summary>
    private static readonly TimeSpan DailyReset = TimeSpan.FromHours(6);

    public string Path { get; } = tokenPath;

    /// <summary>A usable token, or null when missing/stale/unparsable.</summary>
    public string? AccessToken()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var savedAt = doc.RootElement.TryGetProperty("saved_at", out var sa) ? sa.GetInt64() : 0;

            if (!IsFresh(savedAt, DateTime.UtcNow))
                return null;

            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True while <paramref name="nowUtc"/> is before the next 06:00 IST after
    /// <paramref name="savedAtEpoch"/>. Pure and injectable so the midnight
    /// boundary is unit-tested.
    /// </summary>
    internal static bool IsFresh(long savedAtEpoch, DateTime nowUtc)
    {
        if (savedAtEpoch <= 0)
            return false;

        var savedIst = TimeZoneInfo.ConvertTimeFromUtc(
            DateTimeOffset.FromUnixTimeSeconds(savedAtEpoch).UtcDateTime, Ist);
        var reset = savedIst.Date + DailyReset;
        if (savedIst.TimeOfDay >= DailyReset)
            reset = reset.AddDays(1);                 // saved after 06:00 -> valid until tomorrow 06:00
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(
            nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime(), Ist);
        return nowIst < reset;
    }

    private static TimeZoneInfo ResolveIst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }
}
