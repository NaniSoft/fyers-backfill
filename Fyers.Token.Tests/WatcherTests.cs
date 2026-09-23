using Fyers.Token;

namespace Fyers.Token.Tests;

/// <summary>The expiry watcher driven directly with an injected clock — no
/// sockets, no Fyers, no Telegram (same seams the Python tests use).</summary>
public class WatcherTests
{
    private static TokenConfig Cfg(string dataDir) => new()
    {
        AppId = TestEnv.AppId,
        SecretId = TestEnv.SecretId,
        RedirectUri = TestEnv.RedirectUri,
        DataDir = dataDir,
        PromptTime = "06:30",
        RemindIntervalMin = 60,
        WatchIntervalSec = 60,
    };

    /// <summary>Epoch seconds for 2026-09-14 20:00 IST (yesterday vs the tests' today).</summary>
    private static long YesterdayEveningEpoch() =>
        new DateTimeOffset(2026, 9, 14, 20, 0, 0, TestEnv.IstOffset).ToUnixTimeSeconds();

    /// <summary>Epoch seconds inside the tests' IST "today" (2026-09-15 01:00).</summary>
    private static long TodayEarlyEpoch() =>
        new DateTimeOffset(2026, 9, 15, 1, 0, 0, TestEnv.IstOffset).ToUnixTimeSeconds();

    [Fact]
    public async Task Quiet_window_no_prompt_before_prompt_time()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier();
            var svc = new TokenService(Cfg(root), notifier: notifier,
                now: () => TestEnv.Ist(3, 0));

            Assert.False(await svc.WatchOnceAsync());
            Assert.Empty(notifier.Sent);
            Assert.Null(svc.PendingState);   // no login URL generated either
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task At_prompt_time_stale_token_prompts_and_reprompts_after_60_min()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier();
            var now = TestEnv.Ist(6, 30);
            var svc = new TokenService(Cfg(root), notifier: notifier, now: () => now);
            File.WriteAllText(Path.Combine(root, "fyers_access_token.json"),
                "{\"access_token\": \"old\", \"app_id\": \"TESTAPPID\", \"saved_at\": "
                + YesterdayEveningEpoch() + "}");

            // 06:30 sharp: the daily routine prompt goes out
            Assert.True(await svc.WatchOnceAsync());
            var first = Assert.Single(notifier.Sent);
            Assert.Equal("warn", first.Level);
            Assert.StartsWith("Fyers token missing/expired — daily login needed "
                              + "(one click/day):", first.Text);
            Assert.Contains("\nhttps://api-t1.fyers.in/api/v3/generate-authcode?"
                            + "client_id=TESTAPPID&redirect_uri=http%3A%2F%2F127.0.0.1%3A8001"
                            + "%2Fcallback&response_type=code&state=", first.Text);
            Assert.EndsWith("\nLog in on the Fyers page; the token lands automatically and "
                            + "the collector resumes on its own.", first.Text);
            var stateOne = svc.PendingState;
            Assert.NotNull(stateOne);

            // 07:29 — still waiting on the user, no spam
            now = TestEnv.Ist(7, 29);
            Assert.False(await svc.WatchOnceAsync());
            Assert.Single(notifier.Sent);

            // 07:31 — a re-mind goes out, with a freshly rotated state
            now = TestEnv.Ist(7, 31);
            Assert.True(await svc.WatchOnceAsync());
            Assert.Equal(2, notifier.Sent.Count);
            Assert.NotEqual(stateOne, svc.PendingState);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reauth_signal_prompts_immediately_any_hour_and_is_deleted()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier();
            var svc = new TokenService(Cfg(root), notifier: notifier,
                now: () => TestEnv.Ist(3, 0));
            File.WriteAllText(Path.Combine(root, "fyers_reauth.request"), "");

            Assert.True(await svc.WatchOnceAsync());   // incident: no quiet window
            var msg = Assert.Single(notifier.Sent);
            Assert.Contains("/generate-authcode?client_id=TESTAPPID", msg.Text);
            Assert.False(File.Exists(Path.Combine(root, "fyers_reauth.request")),
                "the signal is consumed: the prompt replaces it");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Healthy_same_day_token_clears_the_signal_and_says_nothing()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier();
            var svc = new TokenService(Cfg(root), notifier: notifier,
                now: () => TestEnv.Ist(11, 0));
            File.WriteAllText(Path.Combine(root, "fyers_access_token.json"),
                "{\"access_token\": \"fresh\", \"app_id\": \"TESTAPPID\", \"saved_at\": "
                + TodayEarlyEpoch() + "}");
            File.WriteAllText(Path.Combine(root, "fyers_reauth.request"), "");

            Assert.False(await svc.WatchOnceAsync());
            Assert.Empty(notifier.Sent);
            Assert.False(File.Exists(Path.Combine(root, "fyers_reauth.request")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Unparsable_token_file_counts_as_missing()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier();
            var svc = new TokenService(Cfg(root), notifier: notifier,
                now: () => TestEnv.Ist(9, 0));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "fyers_access_token.json"), "{not json");

            Assert.True(await svc.WatchOnceAsync());
            Assert.Single(notifier.Sent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_notify_does_not_arm_the_re_mind_timer()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier { FailNext = true };
            var now = TestEnv.Ist(7, 0);
            var svc = new TokenService(Cfg(root), notifier: notifier, now: () => now);

            Assert.False(await svc.WatchOnceAsync());   // Telegram down -> not "sent"
            notifier.FailNext = false;
            now = TestEnv.Ist(7, 1);
            Assert.True(await svc.WatchOnceAsync());   // so it tries again right away
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Same_day_death_prompts_immediately_even_in_the_quiet_window()
    {
        var root = Directory.CreateTempSubdirectory("fyers-watch-").FullName;
        try
        {
            var notifier = new RecordingNotifier();
            var svc = new TokenService(Cfg(root), notifier: notifier,
                now: () => TestEnv.Ist(3, 0));
            // valid earlier today (a previous tick saw it healthy)
            File.WriteAllText(Path.Combine(root, "fyers_access_token.json"),
                "{\"access_token\": \"fresh\", \"app_id\": \"TESTAPPID\", \"saved_at\": "
                + TodayEarlyEpoch() + "}");
            Assert.False(await svc.WatchOnceAsync());

            // then it dies (Fyers reset ran late) — that is an incident, not a
            // routine prompt: it goes out at 03:00, no quiet-window wait.
            File.WriteAllText(Path.Combine(root, "fyers_access_token.json"),
                "{\"access_token\": \"old\", \"app_id\": \"TESTAPPID\", \"saved_at\": "
                + YesterdayEveningEpoch() + "}");
            Assert.True(await svc.WatchOnceAsync());
            Assert.Single(notifier.Sent);
        }
        finally { Directory.Delete(root, true); }
    }
}
