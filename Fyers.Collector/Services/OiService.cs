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

/// <summary>What one depth minute did — returned by
/// <see cref="OiService.RunMinute"/> for tests/callers. Null means the minute
/// was skipped outright (overrun, quotes_only/off mode, or an empty depth set).</summary>
/// <param name="IstMinute">The IST minute the rows are keyed to.</param>
/// <param name="Day">IST trading day the rows landed in.</param>
/// <param name="Total">Depth specs polled (after the FUTURE/underlying filter).</param>
/// <param name="Written">Quote rows enriched into the day db.</param>
/// <param name="Errors">Symbols that failed (per-symbol error, limiter skip, or an
/// empty depth answer).</param>
/// <param name="AuthExpired">True when a 401/token failure ended the minute early —
/// the capture loop owns reauth, so this service only stops polling.</param>
/// <param name="Ms">Wall time of the whole minute's polling + write.</param>
public sealed record OiTickSummary(
    DateTime IstMinute,
    DateOnly Day,
    int Total,
    int Written,
    int Errors,
    bool AuthExpired,
    int Ms);

/// <summary>
/// Live open-interest capture — polls <c>/data/depth</c> per minute for the
/// currency futures (USDINR/EURINR current + 2 monthlies) and the MIDCPNIFTY
/// futures (3 contracts), 9 calls/minute, and writes one <b>enriched</b> row per
/// symbol into that day's <c>quotes</c> table (instrument_type FUTURE) filling the
/// columns the batched <c>/data/quotes</c> path leaves NULL: <c>oi</c>, <c>atp</c>,
/// <c>bid</c>/<c>ask</c> (+ sizes), <c>chp</c> and o/h/l.
///
/// WHY / WHY NOT (user decision, 2026-09-16):
///  * COMMODITY contracts are excluded — Fyers' depth feed answers oi=0 for every
///    NSE COMMODITY contract (verified live), so polling them buys nothing.
///  * The depth set is selected HERE, from the same universe the orchestrator
///    hands the macro spec-builder, so this service does not depend on that agent
///    having landed: every spec with Type==FUTURE whose Underlying is
///    USDINR / EURINR / MIDCPNIFTY. Cash / SPOT / VIX / commodity specs in the
///    same array are ignored.
///
/// Scheduling mirrors <see cref="CaptureLoopService"/>: align to the IST minute
/// boundary, skip the minute when the wake-up is more than
/// <see cref="OverrunSeconds"/> late, and poll ONLY in Full mode
/// (<see cref="MarketCalendar.ModeAt"/>) — the quotes_only shoulders and the off
/// session get no depth traffic. This loop is NOT coordinated with the capture
/// runner; the global <c>RateLimiter</c> inside <see cref="FyersClient"/> paces
/// everything, so the 9 extra calls simply take their share of the budget.
/// </summary>
public sealed class OiService(
    AppConfig cfg,
    Fyers.Core.Fyers.FyersClient client,
    MarketCalendar cal,
    Func<QuoteSpec[]>? depthSpecs,
    Storage storage,
    ILogger<OiService> log,
    Func<DateTime>? clock = null) : BackgroundService
{
    /// <summary>IST zone (the collector assembly's own resolved zone).</summary>
    internal static readonly TimeZoneInfo IstZone = SnapshotRunner.IstZone;

    /// <summary>Capture-loop parity: a wake-up landing &gt;5s past the boundary means
    /// the previous minute's work bled over — skip the minute.</summary>
    internal const double OverrunSeconds = 5;

    /// <summary>
    /// The depth set's underlyings — currency futures (spot + 2 monthlies each)
    /// and MIDCPNIFTY. Case-insensitive on purpose: a lower-cased underlying in a
    /// spec must still get its OI rather than silently drop out of the set.
    /// </summary>
    internal static readonly IReadOnlySet<string> DepthUnderlyings = new HashSet<string>(
        ["USDINR", "EURINR", "MIDCPNIFTY"], StringComparer.OrdinalIgnoreCase);

    /// <summary>How often the loop re-reads its clock is irrelevant — it sleeps
    /// exactly to the next boundary; the slice only bounds a pathological wake-up.</summary>
    internal static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(65);

    private readonly Func<DateTime> _clock = clock ?? DefaultIstClock;

    /// <summary>IST wall clock (python <c>now_ist()</c>; Kind=Unspecified).</summary>
    private static DateTime DefaultIstClock() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstZone);

    // ------------------------------------------------------------------ filter

    /// <summary>
    /// THE DEPTH SET: every spec with <c>Type == "FUTURE"</c> whose
    /// <c>Underlying</c> is USDINR, EURINR or MIDCPNIFTY. Both comparisons ignore
    /// case (Fyers tickers are upper-case; a variant spelling still gets its OI).
    /// CASH / SPOT / VIX specs never match the type test, and a FUTURE typed spec
    /// for GOLD/SILVER/CRUDEOIL (or any other underlying) never matches the set —
    /// so commodity and cash/index specs are dropped even when they appear in the
    /// array. Symbols are deduped, first spec wins (parity with
    /// <c>FyersClient.Quotes(specs)</c>).
    /// </summary>
    internal static IReadOnlyList<QuoteSpec> DepthSet(IEnumerable<QuoteSpec>? specs)
    {
        if (specs is null)
            return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var set = new List<QuoteSpec>();
        foreach (var spec in specs)
        {
            if (string.IsNullOrEmpty(spec.Symbol)) continue;
            if (!string.Equals(spec.Type, "FUTURE", StringComparison.OrdinalIgnoreCase)) continue;
            if (spec.Underlying is null || !DepthUnderlyings.Contains(spec.Underlying)) continue;
            if (!seen.Add(spec.Symbol)) continue;
            set.Add(spec);
        }
        return set;
    }

    // ------------------------------------------------------------------- loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation(
            "oi service started: depth set = FUTURE with underlying USDINR/EURINR/MIDCPNIFTY, chains {Start}-{End} IST only (overrun {S:0}s)",
            cfg.Session.Start, cfg.Session.End, OverrunSeconds);
        while (!ct.IsCancellationRequested)
        {
            var now = _clock();
            var boundary = now.AddMinutes(1).AddSeconds(-now.Second)
                .AddMilliseconds(-now.Millisecond);
            var delay = boundary - now;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (TaskCanceledException) { break; }
            }

            var istNow = _clock();
            try
            {
                await Task.Run(() => RunMinute(istNow, boundary, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // supervised loop, like the capture loop — never take the host down
                log.LogError("oi tick failed (supervised, continuing): {Message}", e.Message);
            }
        }
        log.LogInformation("oi service stopped");
    }

    /// <summary>
    /// One minute's depth pass — the unit tests drive this directly (the loop above
    /// only aligns the clock). Skips (returns null) when the wake-up is more than
    /// <see cref="OverrunSeconds"/> past <paramref name="boundary"/>, when the
    /// calendar says the minute is not a Full minute, or when the depth set is
    /// empty. Otherwise polls every symbol and writes the enriched rows.
    /// </summary>
    /// <remarks>Synchronous on purpose: <see cref="FyersClient.Depth"/> is the
    /// sync wire call every other collector path uses (the pacing is inside the
    /// limiter). The loop above runs it on the pool so the host stays responsive.</remarks>
    internal OiTickSummary? RunMinute(DateTime istNow, DateTime boundary, CancellationToken ct = default)
    {
        var late = (istNow - boundary).TotalSeconds;
        if (late > OverrunSeconds)
        {
            log.LogWarning("oi: overran the minute boundary by {Seconds:F0}s — skipping {Minute}",
                late, boundary);
            return null;
        }

        // Full minutes only: the quotes_only shoulders (08:30-09:00, 16:00) and the
        // off session are capture-runner territory and get no depth traffic.
        if (cal.ModeAt(TimeOnly.FromDateTime(istNow)) != SnapshotMode.Full)
            return null;

        var set = DepthSet(depthSpecs?.Invoke());
        if (set.Count == 0)
            return null;

        var istMinute = istNow.AddSeconds(-istNow.Second).AddMilliseconds(-istNow.Millisecond);
        var tsUtc = TimeZoneInfo.ConvertTimeToUtc(istMinute, IstZone);
        var day = DateOnly.FromDateTime(istNow);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<(QuoteSpec Spec, DepthRow Row)>(set.Count);
        var errors = 0;
        var failed = new List<string>();
        var authExpired = false;

        foreach (var spec in set)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<DepthRow> answer;
            try
            {
                answer = client.Depth(spec.Symbol, ct);
            }
            catch (AuthExpiredException e)
            {
                // capture loop owns reauth — stop polling, keep what we already have
                authExpired = true;
                log.LogError("oi {Minute} IST: AUTH EXPIRED — skipping the rest of the minute ({Message})",
                    MinuteStr(istMinute), e.Message);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)                         // per-symbol: count + continue
            {
                errors++;
                failed.Add(spec.Symbol);
                if (failed.Count <= 5)
                    log.LogWarning("oi depth {Symbol} failed ({I}/{N}): {Message}",
                        spec.Symbol, errors, set.Count, e.Message);
                continue;
            }

            if (answer.Count == 0)
            {
                errors++;
                failed.Add(spec.Symbol);
                continue;
            }
            // The row is keyed to THIS minute (the service's clock), not to the
            // client's snapshot minute, so the replace key can never straddle a
            // boundary when a poll lands a whisker past the minute edge.
            rows.Add((spec, answer[0]));
        }

        var ms = (int)sw.ElapsedMilliseconds;
        var written = 0;
        if (rows.Count > 0)
        {
            try
            {
                written = WriteDepthQuotes(day, istMinute, tsUtc, rows);
            }
            catch (Exception e)
            {
                log.LogError("oi depth write rolled back for {Date} ({Count} rows): {Message}",
                    day, rows.Count, e.Message);
                throw;
            }
        }

        log.LogInformation("oi {Minute} IST: {N}/{Total} depth rows, {Ms}ms",
            MinuteStr(istMinute), written, set.Count, ms);
        if (failed.Count > 0)
            log.LogWarning("oi {Minute} IST: {Errors}/{Total} depth poll(s) failed: {Symbols}",
                MinuteStr(istMinute), errors, set.Count, string.Join(", ", failed));

        return new OiTickSummary(istMinute, day, set.Count, written, errors, authExpired, ms);
    }

    // ------------------------------------------------------------------ write

    // Column list + order verbatim from src/storage.py _quote_row / Ddl.DailySchema
    // (the same statement Storage.WriteQuotes uses, plus the named-parameter form).
    private const string InsertQuoteSql = """
        INSERT OR REPLACE INTO quotes
        (ts_utc, ist_minute, symbol, instrument_type, underlying, expiry_epoch,
         ltp, bid, ask, bid_size, ask_size, oi, volume,
         ch, chp, prev_close, open, high, low, spread, atp)
        VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19,@20)
        """;

    // The quotes table's only key is the AUTOINCREMENT id (Ddl.DailySchema), so
    // `INSERT OR REPLACE` alone cannot replace anything — the (ts_utc, symbol)
    // key the quotes-batch writer implies is enforced here: the earlier row is
    // removed before the enriched one goes in, keeping the python-era one
    // (symbol, minute) row shape.
    private const string SameMinuteSql = "SELECT prev_close, spread FROM quotes WHERE ts_utc=@ts AND symbol=@sym";
    private const string DeleteSameSql = "DELETE FROM quotes WHERE ts_utc=@ts AND symbol=@sym";

    /// <summary>
    /// Writes one enriched <c>quotes</c> row per (symbol, minute) in a single
    /// transaction — a partial minute rolls back, never half-written (parity with
    /// <see cref="Storage.WriteQuotes"/>). Columns filled from depth: oi, atp,
    /// bid/ask (+ bid_size/ask_size from the top of book), chp, o/h/l, ltp and v;
    /// prev_close/spread are absent from the depth payload, so they are carried
    /// over from the quotes-batch row being replaced when it already exists.
    /// </summary>
    internal int WriteDepthQuotes(DateOnly day, DateTime istMinute, DateTime tsUtc,
        IReadOnlyList<(QuoteSpec Spec, DepthRow Row)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var conn = OpenDay(day);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var (spec, r) in rows)
            {
                // The spec's symbol is the row identity (the same string the quotes
                // batch and the universe use), even if Fyers echoed a different key.
                var (priorPrevClose, priorSpread) = ReadPrior(conn, tx, tsUtc, spec.Symbol);
                DeleteSame(conn, tx, tsUtc, spec.Symbol);

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = InsertQuoteSql;
                for (var i = 0; i < 21; i++)
                    cmd.Parameters.Add(new SqliteParameter($"@{i}", DBNull.Value));

                var p = cmd.Parameters;
                p[0].Value = ToUnixMinute(tsUtc);
                p[1].Value = MinuteStr(istMinute);
                p[2].Value = Val(spec.Symbol);
                p[3].Value = Val("FUTURE");
                p[4].Value = Val(spec.Underlying);
                p[5].Value = Val(spec.ExpiryEpoch);
                p[6].Value = Val(ToDouble(r.Ltp));
                p[7].Value = Val(ToDouble(r.Bid));
                p[8].Value = Val(ToDouble(r.Ask));
                p[9].Value = Val(ToLong(r.BidSize));
                p[10].Value = Val(ToLong(r.AskSize));
                p[11].Value = Val(r.Oi);                       // the whole point
                p[12].Value = Val(ToLong(r.Volume));
                p[13].Value = DBNull.Value;                    // ch (depth carries chp only)
                p[14].Value = Val(ToDouble(r.Chp));
                p[15].Value = Val(priorPrevClose);             // depth has no prev_close
                p[16].Value = Val(ToDouble(r.Open));
                p[17].Value = Val(ToDouble(r.High));
                p[18].Value = Val(ToDouble(r.Low));
                p[19].Value = Val(priorSpread);                // depth has no spread
                p[20].Value = Val(ToDouble(r.Atp));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "oi depth quotes write rolled back for {Date} ({Count} rows)", day, rows.Count);
            throw;
        }
        return rows.Count;
    }

    /// <summary>prev_close/spread of the row being replaced, when there is one —
    /// the depth payload does not carry them, so the enriched row keeps what the
    /// quotes batch already stored this minute (python's <c>.get()</c> misses
    /// would have blanked them).</summary>
    private static (double? PrevClose, double? Spread) ReadPrior(
        SqliteConnection conn, SqliteTransaction tx, DateTime tsUtc, string symbol)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SameMinuteSql;
        cmd.Parameters.AddWithValue("@ts", ToUnixMinute(tsUtc));
        cmd.Parameters.AddWithValue("@sym", symbol);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.FieldCount < 2)
            return (null, null);
        return (
            r.IsDBNull(0) ? null : r.GetDouble(0),
            r.IsDBNull(1) ? null : r.GetDouble(1));
    }

    private static void DeleteSame(SqliteConnection conn, SqliteTransaction tx, DateTime tsUtc, string symbol)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = DeleteSameSql;
        cmd.Parameters.AddWithValue("@ts", ToUnixMinute(tsUtc));
        cmd.Parameters.AddWithValue("@sym", symbol);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writable connection to the day's raw db, creating it (and applying
    /// <see cref="Ddl.DailySchema"/>) on first use — the same shape Storage uses,
    /// so a day file this service creates reads identically.</summary>
    private SqliteConnection OpenDay(DateOnly d)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storage.DayPath(d),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,             // python closes each connection for real
            DefaultTimeout = 30,         // sqlite3.connect(..., timeout=30)
        }.ToString());
        conn.Open();
        try
        {
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "PRAGMA synchronous=NORMAL");
            foreach (var statement in Ddl.SplitStatements(Ddl.DailySchema))
                Exec(conn, statement);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- helpers

    internal static string MinuteStr(DateTime istMinute)
        => istMinute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Epoch seconds truncated to the minute (Storage.ToUnixMinute parity).</summary>
    private static long ToUnixMinute(DateTime utc)
    {
        var dto = utc.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(utc, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(utc.ToUniversalTime()),
            // an unspecified-kind value is taken as UTC (Storage parity)
            _ => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero),
        };
        var epoch = dto.ToUnixTimeSeconds();
        return epoch - (epoch % 60);
    }

    /// <summary>Box for binding: null becomes DBNull (python's `.get()` miss) — a
    /// boxed nullable with no value is a null reference, so one overload covers all.</summary>
    private static object Val(object? v) => v ?? DBNull.Value;
    private static long? ToLong(decimal? v) => v.HasValue ? (long)v.Value : null;
    private static double? ToDouble(decimal? v) => v.HasValue ? (double)v.Value : null;
}
