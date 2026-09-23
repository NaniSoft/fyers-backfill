using Fyers.Core.Config;

namespace Fyers.Core.Calendar;

/// <summary>Per-minute snapshot mode (port of src/market_calendar.py snapshot_mode).</summary>
public enum SnapshotMode
{
    /// <summary>Outside the quotes envelope (Python returns None here).</summary>
    Off,
    /// <summary>Envelope shoulder: quotes only, no option chains.</summary>
    QuotesOnly,
    /// <summary>Chains window: constituent/index chains + quotes.</summary>
    Full,
}

/// <summary>
/// NSE market calendar + IST window helpers — 1:1 port of src/market_calendar.py
/// (class MarketCalendar) with the mode-band comparisons the scheduler makes.
///
/// PARITY NOTES (verified against the Python on 2026-09-15):
///  - src/market_calendar.py:116  in_chains_window  => session_start &lt;= t &lt; session_end
///  - src/market_calendar.py:123  in_quotes_window  => quotes_start &lt;= t &lt; quotes_end
///  - src/scheduler.py:331        retention-sweep gate uses the same half-open range.
///  Both window ENDS are EXCLUSIVE. The "&lt;=" comparisons against quotes_end in the
///  Python (market_calendar.py:63/73) are config clamps — candles.time and
///  gdrive.time must be strictly LATER than quotes_end — not window inclusivity,
///  and expected_minutes_today() = (end-start)/60 = 420 for 09:00-16:00, i.e. the
///  last captured minute is 15:59. So 16:00 with quotes_end=16:00 is Off, and this
///  port reproduces that. The plan doc's Task 3 test list (16:00 -> Off) agrees.
///
/// The calendar is pinned to one date (the constructor's <paramref name="today"/>)
/// because ModeAt/IsQuotesWindow take a time-of-day only; the trading-day gate
/// they apply is IsTradingDay(today). The Python equivalents take the full IST
/// datetime and call is_trading_day(ist_dt).
/// </summary>
public sealed class MarketCalendar
{
    /// <summary>
    /// Built-in NSE trading holidays — verbatim port of BUILTIN_HOLIDAYS in
    /// src/market_calendar.py. Overridden/extended by config holidays.dates.
    /// </summary>
    public static readonly IReadOnlySet<DateOnly> BuiltinHolidays = new HashSet<DateOnly>
    {
        new(2026, 1, 26), new(2026, 3, 7), new(2026, 3, 17), new(2026, 3, 18),
        new(2026, 3, 25), new(2026, 4, 14), new(2026, 4, 15), new(2026, 5, 1),
        new(2026, 5, 26), new(2026, 6, 30), new(2026, 8, 15), new(2026, 8, 19),
        new(2026, 10, 2), new(2026, 10, 21), new(2026, 10, 22), new(2026, 11, 16),
        new(2026, 12, 25),
    };

    private readonly DateOnly _today;
    private readonly IReadOnlySet<DateOnly> _holidays;

    public MarketCalendar(AppConfig cfg, DateOnly today, IReadOnlySet<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(holidays);

        var s = cfg.Session;
        QuotesStart = s.QuotesStart;   // config.yaml quotes_start (falls back to start in the Python)
        Start = s.Start;
        End = s.End;
        QuotesEnd = s.QuotesEnd;
        EodTime = s.EodTime;
        CandlesTime = cfg.Candles.Time;
        GdriveTime = cfg.Gdrive.Time;

        _today = today;
        _holidays = holidays;
    }

    /// <summary>08:30 — quotes envelope opens (cash/futures/spot/VIX).</summary>
    public TimeOnly QuotesStart { get; }

    /// <summary>09:00 — chains (full mode) open.</summary>
    public TimeOnly Start { get; }

    /// <summary>16:00 — chains (full mode) end. EXCLUSIVE (see parity notes).</summary>
    public TimeOnly End { get; }

    /// <summary>16:00 — quotes envelope closes. EXCLUSIVE (see parity notes).</summary>
    public TimeOnly QuotesEnd { get; }

    /// <summary>15:31 — EOD daily-summary slot.</summary>
    public TimeOnly EodTime { get; }

    /// <summary>21:00 — post-close 1-minute-candle backfill slot.</summary>
    public TimeOnly CandlesTime { get; }

    /// <summary>23:15 — rclone -> Drive backup slot.</summary>
    public TimeOnly GdriveTime { get; }

    /// <summary>Port of is_trading_day: weekends (Sat/Sun) and configured holidays are off.</summary>
    public bool IsTradingDay(DateOnly d)
    {
        if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        return !_holidays.Contains(d);
    }

    /// <summary>
    /// Port of snapshot_mode: Off outside the quotes envelope, Full inside the
    /// chains window, QuotesOnly in the envelope shoulders. Half-open bands:
    /// [QuotesStart, Start) => QuotesOnly, [Start, End) => Full (End and
    /// QuotesEnd are both EXCLUSIVE — see the parity notes on this class).
    /// Gated on IsTradingDay(<see cref="_today"/>), like the Python, which
    /// short-circuits on is_trading_day inside in_quotes_window.
    /// </summary>
    public SnapshotMode ModeAt(TimeOnly ist)
    {
        if (!IsQuotesWindow(ist))
        {
            return SnapshotMode.Off;
        }

        return ist >= Start && ist < End ? SnapshotMode.Full : SnapshotMode.QuotesOnly;
    }

    /// <summary>
    /// Port of in_quotes_window: QuotesStart &lt;= t &lt; QuotesEnd on a trading day.
    /// The sweep/EOD gating compares against this window (scheduler.py:331).
    /// </summary>
    public bool IsQuotesWindow(TimeOnly ist)
    {
        if (!IsTradingDay(_today))
        {
            return false;
        }

        return ist >= QuotesStart && ist < QuotesEnd;
    }

    /// <summary>
    /// Port of the Python's holiday set construction:
    /// set(BUILTIN_HOLIDAYS) | set(cfg.holidays), where cfg.holidays is the
    /// config.yaml `holidays.dates` list (src/config.py:362).
    /// </summary>
    public static IReadOnlySet<DateOnly> HolidaysFromConfig(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var set = new HashSet<DateOnly>(BuiltinHolidays);
        foreach (var d in cfg.Holidays)
        {
            set.Add(d);
        }

        return set;
    }
}
