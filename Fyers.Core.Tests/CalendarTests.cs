using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Xunit;

namespace Fyers.Core.Tests;

/// <summary>
/// Parity tests for MarketCalendar against src/market_calendar.py +
/// the mode-band comparisons in src/scheduler.py.
///
/// Session used here = the live config.yaml:
///   quotes_start 08:30 | start 09:00 | end 16:00 | quotes_end 16:00 | eod 15:31
/// </summary>
public class CalendarTests
{
    private static readonly DateOnly TradingDay = new(2026, 9, 15);   // Tuesday
    private static readonly DateOnly Saturday = new(2026, 9, 12);
    private static readonly DateOnly Sunday = new(2026, 9, 13);

    /// <summary>Minimal yaml carrying the real config.yaml session/slot values.</summary>
    private const string RealSessionYaml = """
        symbols:
          - underlying: NIFTY
            chain_symbol: NSE:NIFTY50-INDEX
            spot_symbol: NSE:NIFTY50-INDEX
            weekly: true
        options:
          strikecount: 50
        futures:
          n_months: 3
        session:
          tz: Asia/Kolkata
          quotes_start: "08:30"
          start: "09:00"
          end: "16:00"
          quotes_end: "16:00"
          premarket_times: ["08:45", "10:00"]
          eod_time: "15:31"
        gdrive:
          enabled: true
          time: "23:15"
        candles:
          enabled: true
          time: "21:00"
        holidays:
          dates: []
        """;

    private static MarketCalendar Cal(
        DateOnly today,
        IReadOnlySet<DateOnly>? holidays = null,
        string? yaml = null) =>
        new(AppConfig.Parse(yaml ?? RealSessionYaml), today,
            holidays ?? new HashSet<DateOnly>());

    // ------------------------------------------------------------------ bands

    [Theory]
    [InlineData(0, 0, SnapshotMode.Off)]
    [InlineData(8, 28, SnapshotMode.Off)]
    [InlineData(8, 29, SnapshotMode.Off)]
    [InlineData(8, 30, SnapshotMode.QuotesOnly)]   // quotes_start, inclusive
    [InlineData(8, 59, SnapshotMode.QuotesOnly)]
    [InlineData(9, 0, SnapshotMode.Full)]          // chains open
    [InlineData(10, 0, SnapshotMode.Full)]
    [InlineData(15, 29, SnapshotMode.Full)]
    [InlineData(15, 59, SnapshotMode.Full)]
    [InlineData(16, 0, SnapshotMode.Off)]          // quotes_end — EXCLUSIVE (see parity note)
    [InlineData(16, 1, SnapshotMode.Off)]
    [InlineData(23, 59, SnapshotMode.Off)]
    public void ModeAt_Bands_FollowThePythonHalfOpenWindows(int h, int m, SnapshotMode expected)
    {
        var cal = Cal(TradingDay);
        Assert.Equal(expected, cal.ModeAt(new TimeOnly(h, m)));
    }

    [Fact]
    public void ModeAt_AtQuotesEnd_IsOff_PythonWindowEndIsExclusive()
    {
        // src/market_calendar.py:123  quotes_start <= t < quotes_end  (strictly less)
        // src/market_calendar.py:116  session_start <= t < session_end (strictly less)
        // The "<=" comparisons against quotes_end in that file (lines 63/73) are the
        // candles.time / gdrive.time config clamps, not window inclusivity, and
        // expected_minutes_today() = (end-start)/60 = 420 for 09:00-16:00, i.e. the
        // last captured minute is 15:59. So 16:00 with quotes_end=16:00 is Off here,
        // exactly as in Python (and as the plan doc's Task 3 test list states).
        var cal = Cal(TradingDay);
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(16, 0)));
        Assert.False(cal.IsQuotesWindow(new TimeOnly(16, 0)));
    }

    [Fact]
    public void ModeAt_ResumesQuotesOnlyAfterSessionEnd_WithTheClassicBand()
    {
        // The 09:15-15:30 shape (scheduler docstring): shoulders on both sides.
        const string yaml = """
            symbols:
              - underlying: NIFTY
                chain_symbol: NSE:NIFTY50-INDEX
            options:
              strikecount: 50
            futures:
              n_months: 3
            session:
              tz: Asia/Kolkata
              quotes_start: "08:30"
              start: "09:15"
              end: "15:30"
              quotes_end: "16:00"
              eod_time: "15:31"
            """;
        var cal = Cal(TradingDay, yaml: yaml);

        Assert.Equal(SnapshotMode.QuotesOnly, cal.ModeAt(new TimeOnly(8, 45)));
        Assert.Equal(SnapshotMode.Full, cal.ModeAt(new TimeOnly(9, 15)));
        Assert.Equal(SnapshotMode.Full, cal.ModeAt(new TimeOnly(15, 29)));
        Assert.Equal(SnapshotMode.QuotesOnly, cal.ModeAt(new TimeOnly(15, 30)));  // end exclusive
        Assert.Equal(SnapshotMode.QuotesOnly, cal.ModeAt(new TimeOnly(15, 59)));
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(16, 0)));          // quotes_end exclusive
    }

    // ------------------------------------------------------- non-trading days

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 30)]
    [InlineData(9, 0)]
    [InlineData(12, 0)]
    [InlineData(15, 31)]
    [InlineData(16, 0)]
    [InlineData(23, 59)]
    public void ModeAt_OnAHoliday_IsOffAllDay(int h, int m)
    {
        var cal = Cal(TradingDay, new HashSet<DateOnly> { TradingDay });
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(h, m)));
        Assert.False(cal.IsQuotesWindow(new TimeOnly(h, m)));
    }

    [Fact]
    public void IsTradingDay_WeekendsAndHolidays()
    {
        // Python builds its set as set(BUILTIN_HOLIDAYS) | set(cfg.holidays)
        // (market_calendar.py:80); HolidaysFromConfig produces that union and the
        // constructor takes it injected.
        var holidays = new HashSet<DateOnly>(
            MarketCalendar.HolidaysFromConfig(AppConfig.Parse(RealSessionYaml)));
        holidays.Add(TradingDay);
        var cal = Cal(TradingDay, holidays);

        Assert.False(cal.IsTradingDay(Saturday));
        Assert.False(cal.IsTradingDay(Sunday));
        Assert.False(cal.IsTradingDay(TradingDay));        // configured holiday
        Assert.True(cal.IsTradingDay(new DateOnly(2026, 9, 16)));   // Wednesday, no holiday
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 10, 2)));  // built-in NSE holiday (Fri)
    }

    [Fact]
    public void ModeAt_GatesOnTheConstructorToday_NotOtherDates()
    {
        // 2026-09-16 is a trading day; the holiday set only names the 15th.
        var cal = Cal(new DateOnly(2026, 9, 16), new HashSet<DateOnly> { TradingDay });

        Assert.Equal(SnapshotMode.Full, cal.ModeAt(new TimeOnly(9, 0)));
        Assert.True(cal.IsTradingDay(new DateOnly(2026, 9, 16)));
        Assert.False(cal.IsTradingDay(TradingDay));
    }

    // --------------------------------------------------------- quotes window

    [Theory]
    [InlineData(8, 29, false)]
    [InlineData(8, 30, true)]
    [InlineData(12, 0, true)]
    [InlineData(15, 59, true)]
    [InlineData(16, 0, false)]
    public void IsQuotesWindow_MatchesThePythonHalfOpenRange(int h, int m, bool expected)
    {
        var cal = Cal(TradingDay);
        Assert.Equal(expected, cal.IsQuotesWindow(new TimeOnly(h, m)));
    }

    [Fact]
    public void IsQuotesWindow_IsFalseOnWeekends()
    {
        var cal = Cal(Saturday);
        Assert.False(cal.IsQuotesWindow(new TimeOnly(12, 0)));
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(12, 0)));
    }

    // ------------------------------------------------------------- slot times

    [Fact]
    public void EodTime_ComesFromConfig()
    {
        Assert.Equal(new TimeOnly(15, 31), Cal(TradingDay).EodTime);
    }

    [Fact]
    public void SessionAndSlotTimes_ComeFromConfig()
    {
        var cal = Cal(TradingDay);

        Assert.Equal(new TimeOnly(8, 30), cal.QuotesStart);
        Assert.Equal(new TimeOnly(9, 0), cal.Start);
        Assert.Equal(new TimeOnly(16, 0), cal.End);
        Assert.Equal(new TimeOnly(16, 0), cal.QuotesEnd);
        Assert.Equal(new TimeOnly(21, 0), cal.CandlesTime);
        Assert.Equal(new TimeOnly(23, 15), cal.GdriveTime);
    }

    [Fact]
    public void QuotesBounds_FallBackToSessionBounds_LikeThePython()
    {
        // src/config.py:243/247 — quotes_start/quotes_end default to start/end.
        const string yaml = """
            symbols:
              - underlying: NIFTY
                chain_symbol: NSE:NIFTY50-INDEX
            options:
              strikecount: 50
            futures:
              n_months: 3
            session:
              tz: Asia/Kolkata
              start: "09:15"
              end: "15:30"
              eod_time: "15:31"
            """;
        var cal = Cal(TradingDay, yaml: yaml);

        Assert.Equal(cal.Start, cal.QuotesStart);
        Assert.Equal(cal.End, cal.QuotesEnd);
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(9, 0)));      // before the envelope
        Assert.Equal(SnapshotMode.Full, cal.ModeAt(new TimeOnly(9, 15)));
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(15, 30)));
    }

    // --------------------------------------------------------------- holidays

    [Fact]
    public void HolidaysFromConfig_UnionsBuiltinsWithConfigDates()
    {
        // src/market_calendar.py:80 — set(BUILTIN_HOLIDAYS) | set(cfg.holidays)
        const string yaml = """
            symbols:
              - underlying: NIFTY
                chain_symbol: NSE:NIFTY50-INDEX
            options:
              strikecount: 50
            futures:
              n_months: 3
            session:
              tz: Asia/Kolkata
              start: "09:00"
              end: "16:00"
              eod_time: "15:31"
            holidays:
              dates: ["2026-09-15", "2026-10-02"]
            """;
        var holidays = MarketCalendar.HolidaysFromConfig(AppConfig.Parse(yaml));

        Assert.Equal(MarketCalendar.BuiltinHolidays.Count + 1, holidays.Count); // 10-02 already built-in
        Assert.Contains(new DateOnly(2026, 9, 15), holidays);                   // from config
        Assert.Contains(new DateOnly(2026, 1, 26), holidays);                   // built-in kept

        var cal = Cal(TradingDay, holidays);
        Assert.False(cal.IsTradingDay(TradingDay));
        Assert.Equal(SnapshotMode.Off, cal.ModeAt(new TimeOnly(10, 0)));
    }

    [Fact]
    public void HolidaysFromConfig_WithNoConfigDates_IsJustTheBuiltinList()
    {
        var holidays = MarketCalendar.HolidaysFromConfig(AppConfig.Parse(RealSessionYaml));

        // a copy, not the same instance — callers may mutate their own set
        Assert.NotSame(MarketCalendar.BuiltinHolidays, holidays);
        Assert.Equal(MarketCalendar.BuiltinHolidays.Count, holidays.Count);
        Assert.Contains(new DateOnly(2026, 8, 19), holidays);
    }
}
