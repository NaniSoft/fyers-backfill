using System.Globalization;
using Fyers.Collector.Capture;
using Fyers.Core.Calendar;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Fyers.Core.Storage;
using Microsoft.Data.Sqlite;

// NB: Microsoft.Extensions.Hosting/Logging come from this project's implicit global
// usings (CaptureLoopService.cs relies on the same).
// NB: no `using Fyers.Core.RateLimiter;` — the RateLimiter TYPE shadows its own
// namespace name (CS0138); the type is visible unqualified anyway.

namespace Fyers.Collector.Services;

/// <summary>What one candles pass did — written to <c>candles_runs</c> and returned
/// by <see cref="CandlesService.RunOnceAsync"/> for tests/callers.</summary>
/// <param name="Failed">Symbols left with NO bars: per-symbol errors + limiter skips
/// + budget leftovers. Python's <c>n_failed</c> contract (<c>0 == the day is done</c>).</param>
/// <param name="Errored">Of those, the ones that failed with a hard per-symbol error.</param>
/// <param name="BudgetExpired">True when the time budget ran out with symbols unfetched.</param>
public sealed record CandlesRunSummary(
    DateOnly Date,
    int Symbols,
    int Candles,
    int Failed,
    int Errored,
    bool BudgetExpired);

/// <summary>
/// Post-close 1-minute candle backfill — a strictly TODAY-ONLY port of
/// <c>src/candles.py</c>. One pass per IST trading day at <c>candles.time</c>
/// (21:00), one <c>/data/history</c> call per symbol captured that day, bars
/// INSERT OR REPLACEd into that day's raw db (<c>candles_1m</c>,
/// UNIQUE(symbol, ts_utc)), then the completion marker in <c>summary.db</c>.
///
/// SCOPE REDUCTION (user-approved) vs <c>src/candles.py</c>: no derivative probe /
/// gate, no lookback sweep over past days, no pending-list re-sweeping, no healing,
/// no archive resurrection. The symbol list is the day's own db, and a symbol is
/// fetched exactly once per pass — the budget is the only thing that can stop the
/// list early. Failures are recorded honestly in the marker; nothing is retried.
///
/// Cost (python parity note): ~18k symbols/day at the 190/min limiter cap is ~96 min,
/// inside the default 120 min budget, so a single ordered pass converges. The
/// limiter inside <see cref="FyersClient"/> does the pacing — this loop never sleeps
/// between symbols.
/// </summary>
public sealed class CandlesService(
    AppConfig cfg,
    Fyers.Core.Fyers.FyersClient client,
    Fyers.Core.Storage.Storage storage,
    MarketCalendar cal,
    ILogger<CandlesService> log,
    Func<DateTime>? clock = null) : BackgroundService
{
    /// <summary>IST zone (the collector assembly's own resolved zone).</summary>
    internal static readonly TimeZoneInfo IstZone = SnapshotRunner.IstZone;

    /// <summary>
    /// How often the loop re-reads its clock. Short on purpose: <see cref="_clock"/>
    /// is injectable, so a fake clock can advance between slices and the pass starts
    /// without real wall-clock waiting. Cost when idle is one clock read per slice.
    /// </summary>
    internal static readonly TimeSpan PollSlice = TimeSpan.FromMilliseconds(250);

    /// <summary>Progress log cadence, in symbols (spec: every 500).</summary>
    internal const int ProgressEvery = 500;

    // candles_1m insert — column list + order verbatim from src/storage.py
    // add_candle_rows (and Ddl.DailySchema). INSERT OR REPLACE makes the day-idempotent.
    private const string InsertCandleSql = """
        INSERT OR REPLACE INTO candles_1m
        (ts_utc, ist_minute, symbol, instrument_type, underlying,
         expiry_epoch, strike_price, option_type,
         open, high, low, close, volume, oi)
        VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13)
        """;

    // The day's own captured symbols: the option legs (chain) plus the quote universe.
    private const string SymbolSql = "SELECT symbol FROM option_chain UNION SELECT symbol FROM quotes ORDER BY symbol";

    // Per-symbol metadata for the candles_1m descriptor columns. Python carries it in
    // day_symbol_meta; this port derives it from the day's own rows. A symbol's
    // descriptor is constant across the day, so MIN() over the group returns it (and
    // ignores NULLs), deterministically.
    private const string QuoteMetaSql = """
        SELECT symbol, MIN(instrument_type) AS instrument_type, MIN(underlying) AS underlying,
               MIN(expiry_epoch) AS expiry_epoch
        FROM quotes GROUP BY symbol
        """;

    private const string LegMetaSql = """
        SELECT symbol, MIN(underlying) AS underlying, MIN(expiry_epoch) AS expiry_epoch,
               MIN(strike_price) AS strike_price, MIN(option_type) AS option_type
        FROM option_chain GROUP BY symbol
        """;

    private readonly Func<DateTime> _clock = clock ?? DefaultIstClock;

    /// <summary>
    /// The clock returns the IST wall clock (python <c>now_ist()</c>; Kind=Unspecified).
    /// Default: UtcNow converted with the collector's IST zone.
    /// </summary>
    private static DateTime DefaultIstClock() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SnapshotRunner.IstZone);

    // ------------------------------------------------------------------ loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!cfg.Candles.Enabled)
        {
            log.LogInformation("candles: disabled (candles.enabled=false) — service idle");
            return;
        }

        var slot = cfg.Candles.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        log.LogInformation(
            "candles: today-only backfill armed for {Slot} IST (budget {Budget} min; no lookback, no probe gate)",
            slot, cfg.Candles.BudgetMin);

        // One pass per IST date. Set BEFORE the pass runs: a failed/expired pass is
        // not retried (user dropped healing) — the marker records what happened.
        var ran = (DateOnly?)null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = _clock();
                var today = DateOnly.FromDateTime(now);
                if (ran != today && TimeOnly.FromDateTime(now) >= cfg.Candles.Time && cal.IsTradingDay(today))
                {
                    ran = today;
                    await RunOnceAsync(today, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AuthExpiredException e)
            {
                // The capture loop owns re-auth; this pass is over for today.
                log.LogError("candles: AUTH EXPIRED — pass abandoned, token service owns re-login ({Message})", e.Message);
            }
            catch (Exception e)
            {
                log.LogError("candles pass failed (supervised, continuing): {Message}", e.Message);
            }

            try { await Task.Delay(PollSlice, ct); }
            catch (TaskCanceledException) { break; }
        }

        log.LogInformation("candles service stopped");
    }

    // ------------------------------------------------------------------ pass

    /// <summary>One pass over <paramref name="day"/>'s captured symbols. Runs late is
    /// fine (a container restarted at 22:00 still owes the day its candles). Public
    /// because it IS the unit of work — tests and a manual trigger call it directly.</summary>
    public async Task<CandlesRunSummary> RunOnceAsync(DateOnly day, CancellationToken ct)
    {
        var t0 = Environment.TickCount64;                 // python: clock() (monotonic)
        var symbols = LoadSymbols(day);
        if (symbols.Count == 0)
        {
            log.LogWarning("candles {Date}: no captured symbols — skipping", D(day));
            var nothing = new CandlesRunSummary(day, 0, 0, 0, 0, BudgetExpired: false);
            WriteMarker(nothing);
            return nothing;
        }

        var budgetMs = Math.Max(0, cfg.Candles.BudgetMin) * 60_000L;
        var errored = 0;
        var skipped = 0;                                  // limiter skips (python: ratelimited)
        var done = 0;
        var candles = 0L;
        var budgetExpired = false;

        // One writable connection for the whole run (python parity note: ~18k symbols
        // must not cost ~18k connect + schema cycles). The day is post-close, so
        // nothing else writes to it.
        using (var conn = OpenDay(day))
        {
            for (var i = 0; i < symbols.Count; i++)
            {
                if (Environment.TickCount64 - t0 >= budgetMs)
                {
                    budgetExpired = true;
                    break;
                }

                var meta = symbols[i];
                ct.ThrowIfCancellationRequested();

                IReadOnlyList<CandleRow> bars;
                try
                {
                    bars = client.History(meta.Symbol, day,
                        oiFlag: OiFor(meta), contFlag: ContFor(meta), ct);
                }
                catch (RateLimitedException e)
                {
                    // Python re-sweeps a skipped symbol until the budget dies; this
                    // port makes ONE ordered pass, so a skip is a symbol left unfetched.
                    skipped++;
                    log.LogWarning("candles {Date}: {Symbol} skipped by the limiter ({Message})", D(day), meta.Symbol, e.Message);
                    continue;
                }
                catch (AuthExpiredException)
                {
                    throw;                                // capture loop owns re-auth
                }
                catch (OperationCanceledException)
                {
                    throw;                                // host is shutting down
                }
                catch (Exception e)
                {
                    errored++;
                    log.LogError("candles {Date}: {Symbol} failed: {Message}", D(day), meta.Symbol, e.Message);
                    continue;
                }

                WriteBars(conn, meta, bars);
                done++;
                candles += bars.Count;
                if (done % ProgressEvery == 0)
                    log.LogInformation("candles {Date}: {Done}/{Total} symbols, {Candles} bars",
                        D(day), done, symbols.Count, candles);
            }
        }

        // no_data / ok-but-empty count as fetched with zero bars (python: terminal
        // no_data, never a failure); everything with no rows at all is "failed".
        var failed = symbols.Count - done;
        var summary = new CandlesRunSummary(day, symbols.Count, (int)candles, failed, errored, budgetExpired);
        WriteMarker(summary);                             // written even when incomplete

        if (budgetExpired)
            log.LogWarning("candles {Date}: budget expired with {Pending} symbols unfetched",
                D(day), symbols.Count - done - errored);
        if (skipped > 0)
            log.LogWarning("candles {Date}: {Skipped} symbols skipped by the limiter (one ordered pass — not re-swept)",
                D(day), skipped);
        log.LogInformation("candles {Date}: done {Candles} bars, {Failed} failed symbols ({Seconds:F0}s)",
            D(day), candles, failed, (Environment.TickCount64 - t0) / 1000.0);
        return summary;
    }

    // ------------------------------------------------------------- symbols

    /// <summary>The day's distinct captured symbols with their descriptor columns.
    /// An absent (or capture-less) day file means nothing to do.</summary>
    internal IReadOnlyList<SymMeta> LoadSymbols(DateOnly day)
    {
        var path = storage.DayPath(day);
        if (!File.Exists(path))
            return [];

        var bySymbol = new Dictionary<string, SymMeta>(512, StringComparer.Ordinal);
        using (var conn = OpenDayReadOnly(path))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SymbolSql;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var sym = r.GetString(0);
                    if (!string.IsNullOrEmpty(sym))
                        bySymbol[sym] = new SymMeta(sym, null, null, null, null, null);
                }
            }

            ApplyQuoteMeta(bySymbol, conn);
            ApplyLegMeta(bySymbol, conn);
        }

        // SymbolSql's ORDER BY makes the fetch order deterministic (and the log
        // sequence reproducible); keep the in-memory sort as the single authority.
        return [.. bySymbol.Values.OrderBy(s => s.Symbol, StringComparer.Ordinal)];
    }

    private static void ApplyQuoteMeta(Dictionary<string, SymMeta> bySymbol, SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = QuoteMetaSql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sym = r.GetString(0);
            if (!bySymbol.TryGetValue(sym, out var m))
                continue;
            bySymbol[sym] = m with
            {
                InstrumentType = NString(r, 1) ?? m.InstrumentType,
                Underlying = NString(r, 2) ?? m.Underlying,
                ExpiryEpoch = NLong(r, 3) ?? m.ExpiryEpoch,
            };
        }
    }

    private static void ApplyLegMeta(Dictionary<string, SymMeta> bySymbol, SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = LegMetaSql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sym = r.GetString(0);
            if (!bySymbol.TryGetValue(sym, out var m))
                continue;
            bySymbol[sym] = m with
            {
                // Option legs are OPT; the quote rows never carry the strike/type.
                InstrumentType = m.InstrumentType ?? "OPT",
                Underlying = NString(r, 1) ?? m.Underlying,
                ExpiryEpoch = NLong(r, 2) ?? m.ExpiryEpoch,
                StrikePrice = NDecimal(r, 3) ?? m.StrikePrice,
                OptionType = NString(r, 4) ?? m.OptionType,
            };
        }
    }

    /// <summary>python <c>oi_for</c>: oi_flag=1 only for derivatives (OPT/FUT).</summary>
    private static bool OiFor(SymMeta m)
        => m.InstrumentType is "OPT" or "FUTURE";

    /// <summary>python <c>cont_for</c>: cont_flag=1 only for futures.</summary>
    private static bool ContFor(SymMeta m)
        => m.InstrumentType == "FUTURE";

    // ------------------------------------------------------------- sqlite

    /// <summary>Writable day connection (creates the file + applies the daily schema),
    /// mirroring <c>Storage.open_day_for_write</c>. One per pass.</summary>
    private SqliteConnection OpenDay(DateOnly day)
    {
        var conn = Open(new SqliteConnectionStringBuilder
        {
            DataSource = storage.DayPath(day),
            Mode = SqliteOpenMode.ReadWriteCreate,
        });
        foreach (var statement in Ddl.SplitStatements(Ddl.DailySchema))
            Exec(conn, statement);
        return conn;
    }

    /// <summary>Read-only symbol sweep: never creates a day file, never writes.
    /// (No WAL pragma here — a reader neither needs it nor may set it.)</summary>
    private static SqliteConnection OpenDayReadOnly(string path) => Open(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
    }, writable: false);

    private static SqliteConnection Open(SqliteConnectionStringBuilder b, bool writable = true)
    {
        b.Pooling = false;               // python closes each connection for real
        b.DefaultTimeout = 30;           // sqlite3.connect(..., timeout=30)
        var conn = new SqliteConnection(b.ToString());
        conn.Open();
        try
        {
            if (writable)
            {
                Exec(conn, "PRAGMA journal_mode=WAL");
                Exec(conn, "PRAGMA synchronous=NORMAL");
            }
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>One transaction per symbol: a partial symbol never half-lands.</summary>
    private void WriteBars(SqliteConnection conn, SymMeta meta, IReadOnlyList<CandleRow> bars)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = InsertCandleSql;
                for (var i = 0; i < 14; i++)
                    cmd.Parameters.Add(new SqliteParameter($"@{i}", DBNull.Value));

                foreach (var b in bars)
                {
                    var p = cmd.Parameters;
                    p[0].Value = b.TsUtc;
                    p[1].Value = IstMinuteStr(b.TsUtc);
                    p[2].Value = b.Symbol;
                    p[3].Value = Val(meta.InstrumentType);
                    p[4].Value = Val(meta.Underlying);
                    p[5].Value = Val(meta.ExpiryEpoch);
                    p[6].Value = Val(ToDouble(meta.StrikePrice));
                    p[7].Value = Val(meta.OptionType);
                    p[8].Value = (double)b.Open;
                    p[9].Value = (double)b.High;
                    p[10].Value = (double)b.Low;
                    p[11].Value = (double)b.Close;
                    p[12].Value = (long)b.Volume;
                    // oi: the wire candle's 7th element (oi_flag=1) is not carried on
                    // CandleRow, so it binds NULL here (python stores it).
                    p[13].Value = DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "candles write rolled back for {Symbol} ({Count} bars)", meta.Symbol, bars.Count);
            throw;
        }
    }

    /// <summary>
    /// The <c>candles_runs</c> completion marker in the live summary db (python
    /// <c>mark_candles_run</c>). A STATUS RECORD, not a resume pointer: written even
    /// when the budget expired or symbols failed — <paramref name="summary"/>'s
    /// <c>Failed</c> is the contract (<c>0 == nothing left unfetched</c>). Written on
    /// auth-expiry too, because the caller catches it and this pass will not be retried.
    /// </summary>
    internal void WriteMarker(CandlesRunSummary summary)
    {
        using var conn = OpenSummary();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO candles_runs
            (date, n_symbols, n_candles, n_failed, legs_ok, completed_at)
            VALUES (@0,@1,@2,@3,@4,@5)
            """;
        cmd.Parameters.Add(new SqliteParameter("@0", summary.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        cmd.Parameters.Add(new SqliteParameter("@1", (long)summary.Symbols));
        cmd.Parameters.Add(new SqliteParameter("@2", (long)summary.Candles));
        cmd.Parameters.Add(new SqliteParameter("@3", (long)summary.Failed));
        // legs_ok: this port never runs the derivative probe, so the verdict is NULL
        // (python writes 1/0/NULL the same way).
        cmd.Parameters.Add(new SqliteParameter("@4", DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@5", CompletedAt()));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Live summary db connection, applying only the <c>candles_runs</c>
    /// table (the other summary tables belong to the EOD task). Picked out of
    /// <see cref="Ddl.SummarySchema"/> so the two cannot drift.</summary>
    private SqliteConnection OpenSummary()
    {
        var conn = Open(new SqliteConnectionStringBuilder
        {
            DataSource = storage.SummaryDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        });
        var ddl = Ddl.SplitStatements(Ddl.SummarySchema)
            .FirstOrDefault(s => s.StartsWith("CREATE TABLE IF NOT EXISTS candles_runs", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Ddl.SummarySchema has no candles_runs table");
        Exec(conn, ddl);
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------- helpers

    /// <summary>python <c>ist_minute_str</c>: <c>fromtimestamp(ts, IST)</c> formatted
    /// "%Y-%m-%d %H:%M".</summary>
    internal static string IstMinuteStr(long tsUtc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(tsUtc).UtcDateTime, IstZone)
            .ToString(Storage.IstMinuteFormat, CultureInfo.InvariantCulture);

    /// <summary>python: <c>now_ist().isoformat(timespec="seconds")</c> —
    /// e.g. <c>2026-09-15T21:36:04+05:30</c>.</summary>
    internal static string CompletedAt()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, IstZone)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>python <c>date_s</c>: the IST date as "yyyy-MM-dd" (invariant — log
    /// templates render with the CURRENT culture, which would reformat a DateOnly).</summary>
    internal static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object Val(object? v) => v ?? DBNull.Value;

    private static string? NString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static long? NLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));

    private static decimal? NDecimal(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));

    /// <summary>REAL column binding (Storage binds doubles the same way).</summary>
    private static double? ToDouble(decimal? v) => v.HasValue ? (double)v.Value : null;

    /// <summary>One captured symbol with the descriptor columns <c>candles_1m</c>
    /// carries alongside the bars (python: <c>day_symbol_meta</c>).</summary>
    internal sealed record SymMeta(
        string Symbol,
        string? InstrumentType,
        string? Underlying,
        long? ExpiryEpoch,
        decimal? StrikePrice,
        string? OptionType);
}
