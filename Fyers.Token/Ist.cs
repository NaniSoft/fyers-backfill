using System.Globalization;

namespace Fyers.Token;

/// <summary>IST clock/date helpers — the token-service slice of
/// src/market_calendar.py (IST, now_ist, date_str, parse_hhmm).</summary>
public static class Ist
{
    private static readonly object Lock = new();
    private static TimeZoneInfo? _zone;

    /// <summary>Asia/Kolkata, resolved once. IANA id first, Windows fallback
    /// ("India Standard Time"), and finally a hard +05:30 custom zone so a
    /// bare container with no tzdata still logs/prompt at IST times.</summary>
    public static TimeZoneInfo Zone
    {
        get
        {
            if (_zone is not null) return _zone;
            lock (Lock)
            {
                if (_zone is not null) return _zone;
                TimeZoneInfo? z = TryIana() ?? TryWindows();
                _zone = z ?? TimeZoneInfo.CreateCustomTimeZone(
                    "IST", new TimeSpan(5, 30, 0), "India Standard Time", "IST");
                return _zone;
            }
        }
    }

    private static TimeZoneInfo? TryIana()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (Exception) { return null; }
    }

    private static TimeZoneInfo? TryWindows()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (Exception) { return null; }
    }

    public static TimeSpan Offset => Zone.GetUtcOffset(DateTimeOffset.UtcNow);

    /// <summary>now_ist(): the wall clock as an IST-offset DateTimeOffset.</summary>
    public static DateTimeOffset Now() => DateTimeOffset.UtcNow.ToOffset(Offset);

    public static DateTimeOffset ToIst(DateTimeOffset t) => t.ToOffset(Offset);

    /// <summary>date_str(): "yyyy-MM-dd" in IST.</summary>
    public static string DateStr(DateTimeOffset ist) =>
        ist.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static long NowEpoch() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static DateTimeOffset FromEpoch(long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds(epoch).ToOffset(Offset);

    /// <summary>parse_hhmm("06:30") -> (6, 30).</summary>
    public static (int Hour, int Minute) ParseHhmm(string s)
    {
        var parts = s.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || h is < 0 or > 23 || m is < 0 or > 59)
        {
            throw new FormatException($"invalid HH:MM time: '{s}'");
        }
        return (h, m);
    }
}
