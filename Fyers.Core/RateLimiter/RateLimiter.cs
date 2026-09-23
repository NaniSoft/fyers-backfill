using System.Collections.Generic;

namespace Fyers.Core;

// NOTE: this file lives in RateLimiter/ per the plan, but the type is declared in
// namespace Fyers.Core. A namespace named Fyers.Core.RateLimiter wrapping a type named
// RateLimiter collides with itself: any consumer inside Fyers.Core.* (client, storage,
// universe) resolves the bare name "RateLimiter" to the child NAMESPACE, not the class
// (CS0118), and needs a using-alias. Keeping the type in Fyers.Core lets every consumer
// write plain "RateLimiter".

/// <summary>
/// PARITY PORT of <c>src/rate_limiter.py</c> (decision 04). Two <b>sliding windows</b>:
/// a 1-second window (cap <see cref="RateLimiter.PerSecond"/>) and a 60-second window (cap
/// <see cref="RateLimiter.PerMinute"/>), both strict — a sliding window strictly bounds the
/// count in any window, unlike a token bucket whose continuous refill lets short bursts
/// overshoot a hard cap (a live 429 on 2026-08-27 showed the token-bucket form overshooting
/// Fyers' strict 10/s sliding window). Fyers caps 10/s, 200/min, 100k/day; we run under at
/// 8/s + 190/min.
/// </summary>
/// <remarks>
/// Python's limiter has a single entry point, <c>acquire()</c>, which (a) skips when the
/// minute window is exhausted or a 429 drain is in effect and (b) paces, by blocking, on
/// the per-second window. The .NET port splits that one loop body across two methods:
/// <see cref="TryAcquire"/> is the strict minute-gate decision (caller SKIPS the API call),
/// and <see cref="AcquirePaced"/> is the pacing decision (blocks until a second-window slot
/// frees; never fails). Both record an admitted call into BOTH windows, exactly as the
/// Python <c>_sec.record(now); _min.record(now)</c> tail does, so the pacer always sees the
/// true wire rate regardless of which entry point a caller used.
/// Python is single-threaded; the port keeps one coarse lock around the same critical
/// sections so the capture loop and the universe refresh may interleave safely without
/// changing the decision order.
/// </remarks>
public sealed class RateLimiter
{
    /// <summary>Python production defaults: sec_window=1.0, min_window=60.0.</summary>
    private static readonly TimeSpan SecondWindow = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan MinuteWindow = TimeSpan.FromSeconds(60.0);

    /// <summary>Python paces with time.sleep(min(wait, 0.2)) — same 200ms cap.</summary>
    private static readonly TimeSpan SleepCap = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Floor under the computed wait. With the real monotonic clock this only rounds a
    /// sub-5ms wait up; it exists so an <c>inject</c>ed clock that does not advance while
    /// we sleep (a test's fake clock, advanced from another thread) cannot busy-spin.
    /// </summary>
    private static readonly TimeSpan SleepFloor = TimeSpan.FromMilliseconds(5);

    private readonly SlidingWindow _sec;
    private readonly SlidingWindow _min;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();      // python: self._lock (threading.Lock)

    // python: self._drained_until — 429 drain: nothing proceeds until this instant.
    private DateTime _drainedUntil;

    // python: self._breach_logged_minute — log at most one breach per wall-clock minute.
    private long _breachLoggedMinute = -1;

    /// <summary>Creates a limiter. Production wiring is RateLimiter(perMinute: 190, perSecond: 8).</summary>
    public RateLimiter(int perMinute, int perSecond, Func<DateTime>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1, nameof(perMinute));
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1, nameof(perSecond));

        PerMinute = perMinute;
        PerSecond = perSecond;
        _clock = clock ?? (() => DateTime.UtcNow);        // UTC now; monotonic enough for windows
        _sec = new SlidingWindow(perSecond, SecondWindow);
        _min = new SlidingWindow(perMinute, MinuteWindow);
    }

    /// <summary>Minute-window cap (config rate_limit.per_minute, default 190).</summary>
    public int PerMinute { get; }

    /// <summary>Second-window cap (config rate_limit.per_second, default 8).</summary>
    public int PerSecond { get; }

    /// <summary>Receives the one-breach-per-wall-clock-minute 429 warning (python: log.warning).</summary>
    public Action<string>? Warn { get; set; }

    /// <summary>
    /// STRICT, non-blocking. Returns false when the minute budget is exhausted (or a 429
    /// drain is in effect) and the caller must SKIP the API call. Never blocks.
    /// </summary>
    /// <remarks>
    /// PARITY — this is one pass of the python <c>RateLimiter.acquire()</c> loop body, in
    /// the same order and with the same side effects:
    ///
    ///     now = time.monotonic()                      # clock read BEFORE taking the lock
    ///     with self._lock:
    ///         if now &lt; self._drained_until: return False     # 1. 429 drain in effect -> skip
    ///         if not self._min.has_room(now): return False   # 2. minute budget gone -> skip (no carry)
    ///         if self._sec.has_room(now):                    # 3. per-second window
    ///             self._sec.record(now)
    ///             self._min.record(now)
    ///             return True
    ///
    /// Order is load-bearing. Step 2 runs before the second window is touched, so a
    /// minute-exhausted call prunes ONLY the minute window and never consults the second
    /// window. has_room() prunes as a side effect, but a rejected call appends nothing to
    /// either window — a false here consumes no slot, it can only age old ones out. The
    /// difference from python's loop is only that step 3's "full" branch returns false
    /// here instead of sleeping and retrying: python's non-blocking equivalent is
    /// acquire(max_wait=0). See <see cref="AcquirePaced"/> for that branch.
    /// </remarks>
    public bool TryAcquire()
    {
        var now = _clock();                       // python reads the clock outside the lock
        lock (_gate)
        {
            if (now < _drainedUntil)
            {
                return false;                     // 1. 429 drain in effect -> skip
            }

            if (!_min.HasRoom(now))
            {
                return false;                     // 2. minute budget exhausted -> skip (no carry)
            }

            // 3. The second window paces (AcquirePaced) rather than gates TryAcquire, but
            //    an admitted call still occupies a second-window slot so the pacer counts
            //    every wire call. Same record set as python.
            _sec.Record(now);
            _min.Record(now);
            return true;
        }
    }

    /// <summary>
    /// Paces against the per-second window by BLOCKING until a slot frees. Never returns
    /// failure and never skips: at 8/s a caller waits at most ~1s per slot.
    /// </summary>
    /// <remarks>
    /// PARITY — the python loop's remaining branches, same order:
    ///
    ///     if not self._sec.has_room(now):
    ///         wait = (self._sec.times[0] + 1.0) - now   # oldest slot, read AFTER the purge
    ///     if wait > 0: time.sleep(min(wait, 0.2))
    ///     ... and loop, re-reading the clock each pass.
    ///
    /// The wait is computed from the oldest surviving timestamp post-purge, so it is always
    /// positive and strictly less than one window. Where python would <c>return False</c>
    /// (minute budget exhausted, 429 drain) this void API cannot hand the decision back, so
    /// it waits for the window to slide instead of issuing a wire call — the cap is never
    /// breached either way. Callers that need python's skip semantics use <see cref="TryAcquire"/>.
    /// </remarks>
    public void AcquirePaced()
    {
        while (true)
        {
            var now = _clock();
            var wait = SleepFloor;

            lock (_gate)
            {
                if (now < _drainedUntil)
                {
                    // 429 drain in effect: hold until it lifts (poll, the clock may be fake).
                }
                else if (!_min.HasRoom(now))
                {
                    // minute budget exhausted: wait for the window to slide (no carry).
                }
                else if (_sec.HasRoom(now))
                {
                    _sec.Record(now);
                    _min.Record(now);
                    return;
                }
                else
                {
                    // per-second window full -> wait for the oldest slot to age out.
                    wait = Clamp((_sec.Oldest + SecondWindow) - now, SleepFloor, SleepCap);
                }

                _ = Monitor.Wait(_gate, wait);   // python: time.sleep(min(wait, 0.2))
            }
        }
    }

    /// <summary>
    /// Python's <c>acquire()</c> as the wire path wants it: skip (false) on 429 drain
    /// or minute exhaustion, but BLOCK on a full second window — the paced wire call.
    /// <c>GetRaw</c> uses this, matching python <c>_get</c> (found live 2026-09-15:
    /// unpaced chunk bursts tripped Fyers' server-side limiter into 429 drains).
    /// </summary>
    public bool TryAcquirePaced()
    {
        while (true)
        {
            var now = _clock();
            var wait = SleepFloor;
            lock (_gate)
            {
                if (now < _drainedUntil) return false;      // drain -> skip
                if (!_min.HasRoom(now)) return false;       // minute exhausted -> skip (no carry)
                if (_sec.HasRoom(now))
                {
                    _sec.Record(now);
                    _min.Record(now);
                    return true;
                }
                wait = Clamp((_sec.Oldest + SecondWindow) - now, SleepFloor, SleepCap);
                _ = Monitor.Wait(_gate, wait);          // inside the lock (releases it), as AcquirePaced
            }
        }
    }

    /// <summary>
    /// On a 429 from the server: block the minute window for the rest of the current
    /// minute. Remaining calls skip (<see cref="TryAcquire"/>) or hold
    /// (<see cref="AcquirePaced"/>); the window clears (purges) next minute. Logs one
    /// breach per wall-clock minute, outside the lock, exactly as python does.
    /// Port of python <c>RateLimiter.drain_minute()</c>; <see cref="Fyers.Core"/> clients
    /// call this instead of retrying inside the minute (decision 04: no in-minute retry).
    /// </summary>
    public void DrainMinute(string? reason = null)
    {
        long wallMinute;
        bool first;
        lock (_gate)
        {
            var now = _clock();
            _drainedUntil = now + MinuteWindow;
            wallMinute = (now - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond / 60;
            // python: int(time.time() // 60)
            first = wallMinute != _breachLoggedMinute;
            _breachLoggedMinute = wallMinute;
            Monitor.PulseAll(_gate);                        // wake paced waiters to re-evaluate
        }

        if (first && Warn is not null)
        {
            Warn("rate-limit BREACH (429): " + (reason ?? string.Empty) +
                 " — draining for the rest of the minute (no in-minute retry)");
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>Port of python <c>_SlidingWindow</c>: a deque of timestamps (FIFO).</summary>
    private sealed class SlidingWindow
    {
        private readonly Queue<DateTime> _times = new();
        private readonly TimeSpan _window;

        public SlidingWindow(int limit, TimeSpan window)
        {
            Limit = limit;
            _window = window;
        }

        public int Limit { get; }

        /// <summary>python _purge: drop entries at or before now - window (note the &lt;=).</summary>
        public void Purge(DateTime now)
        {
            var cutoff = now - _window;
            while (_times.Count > 0 && _times.Peek() <= cutoff)
            {
                _times.Dequeue();
            }
        }

        /// <summary>python has_room: purge, then compare depth to the cap.</summary>
        public bool HasRoom(DateTime now)
        {
            Purge(now);
            return _times.Count < Limit;
        }

        /// <summary>python record: append (only ever called after HasRoom admitted the call).</summary>
        public void Record(DateTime now) => _times.Enqueue(now);

        /// <summary>python self.times[0]; valid only right after a failed HasRoom (non-empty).</summary>
        public DateTime Oldest => _times.Peek();
    }
}
