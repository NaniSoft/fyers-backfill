using System.Diagnostics;

namespace Fyers.Core.Tests;

// RateLimiter is declared in the enclosing namespace Fyers.Core (see the note in
// RateLimiter/RateLimiter.cs), so it resolves here without a using directive.

/// <summary>
/// Parity tests for the port of src/rate_limiter.py. Every test drives the limiter through
/// an injected fake clock starting at 2026-09-15T05:00:00Z; nothing here sleeps on the wall
/// clock except the limiter's own bounded re-poll, so a blocked paced call is observed as
/// "not completed within 300ms of real time" while the fake second is frozen.
/// </summary>
public sealed class RateLimiterTests
{
    private static readonly DateTime Base = new(2026, 9, 15, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>How long a test waits before deciding a paced call really is blocked.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(300);

    /// <summary>Fake monotonic clock: advanced on demand from the test thread.</summary>
    private sealed class FakeClock
    {
        private readonly object _gate = new();
        private DateTime _utc;

        public FakeClock(DateTime utcStart) => _utc = utcStart;

        public DateTime UtcNow
        {
            get { lock (_gate) { return _utc; } }
        }

        public Func<DateTime> Clock => () => UtcNow;

        public void Advance(TimeSpan delta)
        {
            lock (_gate) { _utc += delta; }
        }
    }

    private static FakeClock NewClock() => new(Base);

    /// <summary>Waits at most <paramref name="howLong"/> for <paramref name="task"/> to finish.</summary>
    private static async Task<bool> CompletesWithin(Task task, TimeSpan howLong)
    {
        var done = await Task.WhenAny(task, Task.Delay(howLong)).ConfigureAwait(false);
        return done == task;
    }

    // ---------------------------------------------------------------- per-second pacing

    [Fact]
    public async Task AcquirePaced_BurstUpToPerSecond_IsImmediate()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 190, perSecond: 8, clock: clk.Clock);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 8; i++)
        {
            lim.AcquirePaced();
        }
        sw.Stop();

        // All 8 slots of the current second are free, so none of them may pace.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"first 8 paced calls took {sw.Elapsed.TotalMilliseconds:F0}ms — they paced");
        Assert.Equal(TimeSpan.Zero, clk.UtcNow - Base);   // no time had to pass
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AcquirePaced_PerSecondPlusOne_BlocksUntilSecondWindowSlides()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 190, perSecond: 8, clock: clk.Clock);
        for (var i = 0; i < 8; i++)
        {
            lim.AcquirePaced();
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ninth = Task.Run(() =>
        {
            started.SetResult();
            lim.AcquirePaced();
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));   // throws if it never started
        // Same fake second: the per-second window is full (8/8), so the 9th must still be
        // blocked. AcquirePaced never returns false — it waits for a slot to age out.
        Assert.False(await CompletesWithin(ninth, Patience),
            "9th call in the same second must pace, not pass");

        clk.Advance(TimeSpan.FromSeconds(1));

        Assert.True(ninth.IsCompletedSuccessfully || await CompletesWithin(ninth, TimeSpan.FromSeconds(10)),
            "9th call did not resume after the window slid");
        Assert.Equal(TimeSpan.FromSeconds(1), clk.UtcNow - Base);

        // The 9th was recorded; the windows still have room for the next call.
        Assert.True(lim.TryAcquire());
    }

    [Fact]
    public async Task AcquirePaced_WaitsAtMostOneSecondPerSlot_AtEightPerSecond()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 190, perSecond: 8, clock: clk.Clock);

        // Pacing is a 1s grid, not a fixed 1s delay: eight calls fit in a second and the
        // ninth waits for the next boundary — three boundaries in a row here. Each round
        // opens on a fresh second, so the previous round's records purge away.
        for (var slot = 1; slot <= 3; slot++)
        {
            clk.Advance(TimeSpan.FromSeconds(1));         // fresh second, empty window

            for (var i = 0; i < 8; i++)
            {
                lim.AcquirePaced();                       // all 8 slots of this second: immediate
            }

            var paced = Task.Run(lim.AcquirePaced);       // the 9th call in this second
            Assert.False(await CompletesWithin(paced, Patience), $"slot {slot} passed without waiting");
            clk.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await CompletesWithin(paced, TimeSpan.FromSeconds(10)));
            Assert.Equal(Base + TimeSpan.FromSeconds(2 * slot), clk.UtcNow);
        }
    }

    // ---------------------------------------------------------------- per-minute budget

    [Fact]
    public void TryAcquire_PerMinuteExhausted_190TrueThen191False()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 190, perSecond: 8, clock: clk.Clock);

        for (var i = 0; i < 190; i++)
        {
            Assert.True(lim.TryAcquire(), $"call {i + 1} of 190 was refused");
        }

        // Strict sliding window: the 191st call inside the same minute skips. The caller
        // drops that API call; nothing is queued and nothing is recorded.
        Assert.False(lim.TryAcquire());

        // No carry into the next window: once 60s have passed the whole minute window
        // purges (entries are at or before now-60s) and the budget is whole again.
        clk.Advance(TimeSpan.FromSeconds(60));
        Assert.True(lim.TryAcquire());
    }

    [Fact]
    public void TryAcquire_MinuteExhausted_StaysFalseUntilTheWindowActuallySlides()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 2, perSecond: 8, clock: clk.Clock);

        Assert.True(lim.TryAcquire());
        Assert.True(lim.TryAcquire());
        Assert.False(lim.TryAcquire());

        // 59s: the two entries are still inside the 60s window (purge drops only entries
        // at or before now-60s), so the budget is still gone.
        clk.Advance(TimeSpan.FromSeconds(59));
        Assert.False(lim.TryAcquire());

        clk.Advance(TimeSpan.FromSeconds(1));
        Assert.True(lim.TryAcquire());
    }

    // ------------------------------------------------- check order / slot accounting

    /// <summary>
    /// PARITY — python's acquire() loop body runs, in order:
    ///   1. drain check  -> return False
    ///   2. _min.has_room -> return False   (purges ONLY the minute window)
    ///   3. _sec.has_room -> record sec + min, return True
    /// So a minute-exhausted call must return false even when the per-second window is
    /// completely empty, and the per-second window must never gate TryAcquire. Both are
    /// observable here: at T0+2 the minute window is full (3/3) while the second window
    /// purges to empty.
    /// </summary>
    [Fact]
    public void TryAcquire_MinuteCheckedBeforeSecond_SecondWindowNeverGates()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 3, perSecond: 2, clock: clk.Clock);

        // T0: two admissions (second window at its cap of 2, minute 2/3).
        Assert.True(lim.TryAcquire());
        Assert.True(lim.TryAcquire());

        // T0+1: minute 2/3 -> room; second window purges both T0 entries (T0 <= T0+1-1s).
        clk.Advance(TimeSpan.FromSeconds(1));
        Assert.True(lim.TryAcquire());            // minute 3/3, second window 1/2

        // T0+2: minute window is full and nothing purges yet -> skip, even though the
        // second window has room. Order matters: python returns at step 2 before ever
        // looking at the second window.
        clk.Advance(TimeSpan.FromSeconds(1));
        Assert.False(lim.TryAcquire());
        Assert.False(lim.TryAcquire());

        // The rejections consumed nothing: only the three real admissions occupy the minute
        // window, so it frees up exactly one window later and not before.
        clk.Advance(TimeSpan.FromSeconds(57));    // T0+59: the T0 entries are 59s old -> kept
        Assert.False(lim.TryAcquire());

        clk.Advance(TimeSpan.FromSeconds(1));    // T0+60: all three entries purge at once
        Assert.True(lim.TryAcquire());
    }

    /// <summary>
    /// PARITY — every ADMISSION lands in both windows (python records _sec then _min), so
    /// TryAcquire'd traffic occupies per-second slots and the pacer sees the true wire rate.
    /// A REJECTED call lands nowhere: python only ever appends after has_room admits.
    /// </summary>
    [Fact]
    public async Task TryAcquire_AdmissionsOccupySecondWindow_SoPacingCoversThem()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 190, perSecond: 8, clock: clk.Clock);

        for (var i = 0; i < 8; i++)
        {
            Assert.True(lim.TryAcquire());
        }

        // The per-second window is now full from TryAcquire traffic alone; a paced call
        // must wait for it to age out rather than treat the wire as idle.
        var paced = Task.Run(lim.AcquirePaced);
        Assert.False(await CompletesWithin(paced, Patience), "pacer ignored TryAcquire'd traffic");
        clk.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await CompletesWithin(paced, TimeSpan.FromSeconds(10)));
    }

    // ---------------------------------------------------------------- 429 drain

    [Fact]
    public async Task DrainMinute_BlocksBothEntryPoints_ForTheRestOfTheMinute()
    {
        var clk = NewClock();
        var breaches = 0;
        var lim = new RateLimiter(perMinute: 190, perSecond: 8, clock: clk.Clock)
        {
            Warn = _ => breaches++,
        };

        Assert.True(lim.TryAcquire());
        lim.DrainMinute("test:429");
        Assert.Equal(1, breaches);

        // Even with room in both windows, nothing proceeds during the drain.
        Assert.False(lim.TryAcquire());

        var paced = Task.Run(lim.AcquirePaced);
        Assert.False(await CompletesWithin(paced, Patience), "paced call ignored the 429 drain");

        // Same wall-clock minute: the second 429 must not log a second breach. It does
        // re-arm the drain (python: _drained_until = now + 60), so the block now runs to
        // T0+90 rather than T0+60.
        clk.Advance(TimeSpan.FromSeconds(30));
        lim.DrainMinute("test:429-again");
        Assert.Equal(1, breaches);

        clk.Advance(TimeSpan.FromSeconds(30));           // T0+60: first drain would be over
        Assert.False(await CompletesWithin(paced, Patience), "drain did not re-arm");

        clk.Advance(TimeSpan.FromSeconds(30));           // T0+90: re-armed drain is over
        Assert.True(await CompletesWithin(paced, TimeSpan.FromSeconds(10)));
        Assert.True(lim.TryAcquire());
    }

    [Fact]
    public async Task AcquirePaced_MinuteExhausted_NeverFailsAndWaitForTheSlide()
    {
        var clk = NewClock();
        var lim = new RateLimiter(perMinute: 2, perSecond: 8, clock: clk.Clock);

        Assert.True(lim.TryAcquire());
        Assert.True(lim.TryAcquire());

        // AcquirePaced has no skip path (void, never false): with the minute budget gone it
        // holds until the window slides instead of putting a wire call out over budget.
        var paced = Task.Run(lim.AcquirePaced);
        Assert.False(await CompletesWithin(paced, Patience));
        clk.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await CompletesWithin(paced, TimeSpan.FromSeconds(10)));
    }

    // ---------------------------------------------------------------- config defaults

    [Fact]
    public void ConstructorArguments_MapAsPerMinuteThenPerSecond()
    {
        var lim = new RateLimiter(perMinute: 190, perSecond: 8);
        Assert.Equal(190, lim.PerMinute);
        Assert.Equal(8, lim.PerSecond);
    }

    /// <summary>
    /// The production wiring must be RateLimiter(190, 8): config.yaml rate_limit.per_minute /
    /// per_second, and src/rate_limiter.py _DEFAULT_PER_MIN=190 / _DEFAULT_PER_SEC=8.
    /// </summary>
    [Fact]
    public void ProductionLimits_MatchConfigYaml()
    {
        var (perMinute, perSecond) = ReadRateLimitFromConfigYaml();
        Assert.Equal(190, perMinute);
        Assert.Equal(8, perSecond);
    }

    /// <summary>Reads rate_limit.per_minute / per_second straight out of the repo's config.yaml.</summary>
    private static (int perMinute, int perSecond) ReadRateLimitFromConfigYaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var path = Path.Combine(dir.FullName, "config.yaml");
            if (File.Exists(path))
            {
                int? minute = null, second = null;
                var inBlock = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.TrimEnd();
                    if (line.StartsWith("rate_limit:", StringComparison.Ordinal))
                    {
                        inBlock = true;
                        continue;
                    }
                    if (inBlock && line.Length > 0 && !char.IsWhiteSpace(raw[0]))
                    {
                        break;                       // next top-level key: block ended
                    }
                    if (!inBlock)
                    {
                        continue;
                    }
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("per_minute:", StringComparison.Ordinal))
                    {
                        minute = int.Parse(trimmed["per_minute:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (trimmed.StartsWith("per_second:", StringComparison.Ordinal))
                    {
                        second = int.Parse(trimmed["per_second:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                Assert.True(minute.HasValue, "config.yaml rate_limit.per_minute missing");
                Assert.True(second.HasValue, "config.yaml rate_limit.per_second missing");
                return (minute.Value, second.Value);
            }

            dir = dir.Parent;
        }

        Assert.Fail("config.yaml not found above the test output directory");
        return (0, 0);
    }
}
