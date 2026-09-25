using Fyers.Backfill.Config;
using Fyers.Backfill.Failures;
using Fyers.Backfill.Instruments;
using Fyers.Backfill.Ledger;
using Fyers.Backfill.Parquet;
using Fyers.Backfill.Work;
using Fyers.Core.Config;
using Fyers.Core.Fyers;
using Xunit;

namespace Fyers.Backfill.Tests;

public sealed class BackfillTests
{
    // ---------------------------------------------------------------- master

    [Fact]
    public void MasterScanner_classifies_equity_future_and_option()
    {
        var path = TempFile("""
            {
              "NSE:SBIN-EQ": { "symTicker": "NSE:SBIN-EQ", "exSeries": "EQ", "isin": "INE062A01020", "optType": "XX" },
              "NSE:NIFTY50-INDEX": { "symTicker": "NSE:NIFTY50-INDEX", "exSeries": "XX", "optType": "XX" },
              "NSE:NIFTY26AUGFUT": { "symTicker": "NSE:NIFTY26AUGFUT", "optType": "XX", "expiryDate": "1790676600", "underSym": "NIFTY" },
              "NSE:NIFTY26AUG24800CE": { "symTicker": "NSE:NIFTY26AUG24800CE", "optType": "CE", "expiryDate": "1790676600", "underSym": "NIFTY", "strikePrice": 24800 }
            }
            """);

        var instruments = MasterScanner.Scan(path, "FO");

        Assert.Equal(3, instruments.Count);
        var eq = Assert.Single(instruments, i => i.Kind == Instrument.KindEquity);
        Assert.Equal("INE062A01020", eq.Isin);
        var fut = Assert.Single(instruments, i => i.Kind == Instrument.KindFuture);
        Assert.Equal("NIFTY", fut.Underlying);
        Assert.Equal(1790676600, fut.ExpiryEpoch);
        var opt = Assert.Single(instruments, i => i.Kind == Instrument.KindOption);
        Assert.Equal("CE", opt.OptionType);
        Assert.Equal(24800m, opt.Strike);
    }

    // --------------------------------------------------------------- planner

    [Fact]
    public void WorkPlanner_chunks_equities_and_keeps_derivatives_in_one_window()
    {
        var cfg = BackfillConfig.Parse("""
            backfill:
              from: "2026-01-01"
              to: "2026-04-30"
              chunk_days: 100
            """) with { Root = "." };

        var eq = new Instrument("NSE:SBIN-EQ", Instrument.KindEquity);
        var fut = new Instrument("NSE:NIFTY26APRFUT", Instrument.KindFuture,
            Underlying: "NIFTY", ExpiryEpoch: Epoch(2026, 4, 30));

        var items = WorkPlanner.Plan([eq, fut], cfg);

        var eqItems = items.Where(i => i.Instrument == eq).ToList();
        Assert.Equal(2, eqItems.Count);                       // 2026-01-01..04-10, 04-11..04-30
        Assert.Equal(new DateOnly(2026, 4, 10), eqItems.Min(i => i.To));
        Assert.Equal(new DateOnly(2026, 4, 30), eqItems.Max(i => i.To));

        var futItem = Assert.Single(items, i => i.Instrument == fut);
        Assert.Equal(new DateOnly(2026, 4, 30), futItem.To);
        Assert.Equal(new DateOnly(2026, 1, 20), futItem.From); // expiry - 100d
    }

    [Fact]
    public void WorkPlanner_is_breadth_first_by_recency()
    {
        var cfg = BackfillConfig.Parse("""
            backfill: { from: "2026-01-01", to: "2026-04-30", chunk_days: 50 }
            """) with { Root = "." };
        var a = new Instrument("NSE:AAA-EQ", Instrument.KindEquity);
        var b = new Instrument("NSE:BBB-EQ", Instrument.KindEquity);

        var items = WorkPlanner.Plan([a, b], cfg);

        // Newest window first, and the newest window covers every symbol before
        // the next window back is touched.
        Assert.Equal(new DateOnly(2026, 4, 30), items[0].To);
        Assert.Equal(new DateOnly(2026, 4, 30), items[1].To);
        Assert.Equal(new DateOnly(2026, 4, 10), items[2].To);
    }

    [Fact]
    public void NewestPerInstrument_keeps_only_the_latest_window()
    {
        var cfg = BackfillConfig.Parse("""
            backfill: { from: "2026-01-01", to: "2026-04-30", chunk_days: 50 }
            """) with { Root = "." };
        var a = new Instrument("NSE:AAA-EQ", Instrument.KindEquity);

        var planned = WorkPlanner.Plan([a], cfg);
        var newest = BackfillRunner.NewestPerInstrument(planned);

        Assert.Single(newest);
        Assert.Equal(new DateOnly(2026, 4, 30), newest[0].To);
    }

    // ---------------------------------------------------------------- config

    [Fact]
    public void BackfillConfig_defaults_and_overrides()
    {
        var d = BackfillConfig.Parse("");
        Assert.Equal("data/backfill", d.Root);
        Assert.Equal(new DateOnly(2017, 7, 3), d.From);
        Assert.Equal(100, d.ChunkDays);
        Assert.True(d.UniverseEquities);
        Assert.True(d.UniverseExpired);
        Assert.Equal(["1"], d.Resolutions);

        var o = BackfillConfig.Parse("""
            backfill:
              root: /mnt/share/fyers
              from: "2020-01-01"
              chunk_days: 90
              resolutions: ["1", "D"]
              universe:
                equities: false
                underlyings: [NIFTY, RELIANCE]
            """);
        Assert.Equal("/mnt/share/fyers", o.Root);
        Assert.Equal(new DateOnly(2020, 1, 1), o.From);
        Assert.Equal(90, o.ChunkDays);
        Assert.False(o.UniverseEquities);
        Assert.Equal(["1", "D"], o.Resolutions);
        Assert.Equal(["NIFTY", "RELIANCE"], o.Underlyings);
    }

    [Fact]
    public void BackfillConfig_treats_yaml_null_to_as_absent()
    {
        Assert.Null(BackfillConfig.Parse("backfill:\n  to: null\n").To);
        Assert.Null(BackfillConfig.Parse("backfill:\n  to: ~\n").To);
        Assert.Null(BackfillConfig.Parse("backfill:\n  to:\n").To);
        Assert.Equal(new DateOnly(2026, 9, 24), BackfillConfig.Parse("backfill:\n  to: \"2026-09-24\"\n").To);
    }

    // ---------------------------------------------------------------- parquet

    [Fact]
    public async Task CandleStore_merge_write_is_idempotent_and_roundtrips()
    {
        var root = TempDir();
        var store = new CandleStore(root);
        var inst = new Instrument("NSE:SBIN-EQ", Instrument.KindEquity, Isin: "INE062A01020");

        var rows = new List<CandleRow>
        {
            new("NSE:SBIN-EQ", 1790676600, 100m, 101m, 99m, 100.5m, 1234m),
            new("NSE:SBIN-EQ", 1790676660, 100.5m, 102m, 100m, 101.5m, 4321m),
        };
        await store.MergeWriteAsync(inst, "1", rows, default);
        // Re-write the same window plus one new bar: idempotent by ts_utc.
        await store.MergeWriteAsync(inst, "1", [rows[1], new CandleRow("NSE:SBIN-EQ", 1790676720, 1m, 1m, 1m, 1m, 1m)], default);

        var path = store.PartitionPath("1", "NSE:SBIN-EQ");
        Assert.True(File.Exists(path));
        Assert.EndsWith(Path.Combine("1min", "NSE_SBIN-EQ.parquet"), path);

        var got = await CandleStore.ReadAsync(path, default);
        Assert.Equal(3, got.Count);
        Assert.Equal(new[] { 1790676600L, 1790676660L, 1790676720L }, got.Select(r => r.ts_utc));
        Assert.Equal("INE062A01020", got[0].isin);
        Assert.Equal("EQ", got[0].instrument_type);
        Assert.Equal("1", got[0].resolution);
        Assert.Equal("2026-09-29 15:40", got[0].ist_minute);   // 1790676600Z -> IST
    }

    [Fact]
    public void CandleStore_sanitizes_windows_hostile_symbols()
    {
        Assert.Equal("NSE_SBIN-EQ", CandleStore.Sanitize("NSE:SBIN-EQ"));
        Assert.Equal("NSE_NIFTY26AUG24800CE", CandleStore.Sanitize("NSE:NIFTY26AUG24800CE"));
        Assert.Equal("1min", CandleStore.ResolutionDir("1"));
        Assert.Equal("1day", CandleStore.ResolutionDir("D"));
    }

    // ----------------------------------------------------------------- ledger

    [Fact]
    public void Ledger_records_done_and_failed_and_counts()
    {
        var path = Path.Combine(TempDir(), "_ledger.db");
        using var ledger = new ResumeLedger(path);
        var item = new WorkItem(new Instrument("NSE:SBIN-EQ", Instrument.KindEquity), "1",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 10));
        var bad = new WorkItem(new Instrument("NSE:BAD-EQ", Instrument.KindEquity), "1",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 10));

        Assert.False(ledger.IsDone(item.Key));
        ledger.MarkDone(item, rows: 375, requests: 1);
        ledger.MarkFailed(bad, requests: 1, error: "no_data boom");

        Assert.True(ledger.IsDone(item.Key));
        Assert.False(ledger.IsDone(bad.Key));
        var counts = ledger.Counts();
        Assert.Equal(1, counts.Done);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(375, counts.Rows);
    }

    // -------------------------------------------------------------- failures

    [Fact]
    public void FailuresManifest_appends_jsonl()
    {
        var path = Path.Combine(TempDir(), "_failures.jsonl");
        var manifest = new FailuresManifest(path);
        manifest.Record("NSE:X-EQ", "1", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "EQ", "boom");
        manifest.Record("NSE:Y-EQ", "1", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "EQ", "boom2");
        Assert.Equal(2, manifest.Count());
    }

    // ----------------------------------------------------- expired F&O parser

    [Fact]
    public void FnoHistory_parses_expired_endpoints_tolerantly()
    {
        var responder = (HttpRequestMessage req) =>
        {
            var url = req.RequestUri!.ToString();
            var body = url.Contains("expiry-dates")
                ? """{"s":"ok","data":{"expiry_dates":{"futures":["2026-08-25"],"options":["2026-08-04","2026-08-11"]}}}"""
                : url.Contains("underlying-symbols")
                    ? """{"s":"ok","data":{"contracts":{"futures":["NSE:NIFTY26AUGFUT"],"options":["NSE:NIFTY26AUG18000CE"]}}}"""
                    : """{"s":"ok","candles":[[1790676600,100,101,99,100.5,1234]]}""";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        };

        var client = BuildClient(responder);

        var expiries = client.ExpiredExpiryDates("NSE:NIFTY50-INDEX",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        Assert.Equal([new DateOnly(2026, 8, 25)], expiries.Futures);
        Assert.Equal([new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 11)], expiries.Options);

        var symbols = client.ExpiredUnderlyingSymbols("NSE:NIFTY50-INDEX", new DateOnly(2026, 8, 25));
        Assert.Equal(["NSE:NIFTY26AUGFUT"], symbols.Futures);
        Assert.Equal(["NSE:NIFTY26AUG18000CE"], symbols.Options);

        var candles = client.FnoHistoricalData("NSE:NIFTY26AUGFUT",
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 25));
        Assert.Single(candles);
        Assert.Equal(100.5m, candles[0].Close);
    }

    // ----------------------------------------------------- token freshness

    [Fact]
    public void TokenFile_stays_fresh_across_midnight_until_06_IST()
    {
        var saved = IstEpoch(2026, 9, 24, 23, 40);        // minted just before midnight

        Assert.True(TokenFile.IsFresh(saved, IstUtc(2026, 9, 25, 0, 0)));    // past midnight, still valid
        Assert.True(TokenFile.IsFresh(saved, IstUtc(2026, 9, 25, 5, 59)));   // just before the 06:00 reset
        Assert.False(TokenFile.IsFresh(saved, IstUtc(2026, 9, 25, 6, 1)));   // reset passed

        // A token minted before 06:00 is only good until that same day's 06:00.
        var earlyMorning = IstEpoch(2026, 9, 24, 5, 0);
        Assert.False(TokenFile.IsFresh(earlyMorning, IstUtc(2026, 9, 24, 6, 1)));
        Assert.True(TokenFile.IsFresh(earlyMorning, IstUtc(2026, 9, 24, 5, 30)));
    }

    // -------------------------------------------------------------- helpers

    private static TimeZoneInfo IstTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    private static long IstEpoch(int y, int m, int d, int h, int min)
    {
        var local = new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, IstTz())).ToUnixTimeSeconds();
    }

    private static DateTime IstUtc(int y, int m, int d, int h, int min)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), IstTz());

    private static FyersClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new HttpClient(new StubHandler(responder));
        var limiter = new Fyers.Core.RateLimiter(100_000, 100_000);
        return new FyersClient(http, () => "TOKEN", limiter, null, appId: "APPID");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
            => responder(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private static long Epoch(int y, int m, int d)
        => new DateTimeOffset(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fyers-backfill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempFile(string content)
    {
        var path = Path.Combine(TempDir(), "master.json");
        File.WriteAllText(path, content);
        return path;
    }
}
