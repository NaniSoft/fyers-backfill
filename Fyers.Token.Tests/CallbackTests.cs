using System.Net;
using System.Text.Json;
using Fyers.Token;

namespace Fyers.Token.Tests;

/// <summary>The HTTP surface through the real Program.cs host (in-memory test
/// server): /healthz, state rotation, the exchange, the atomic token write,
/// the reauth signal consumption, and the Telegram messages.</summary>
[Collection("token-app")]
public class CallbackTests
{
    [Fact]
    public async Task Healthz_is_200_ok()
    {
        using var app = new TokenApp();
        var resp = await app.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_path_is_404_not_found()
    {
        using var app = new TokenApp();
        var resp = await app.Client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_state_is_400_with_the_python_text()
    {
        using var app = new TokenApp();
        app.Service.NewLoginUrl();   // rotate to a fresh state (a live prompt)

        var resp = await app.Client.GetAsync(
            "/callback?s=ok&code=200&auth_code=ABC&state=someOtherFlowState");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(TokenService.StateMismatchText, body);
        Assert.Contains("Fyers login failed.", body);
    }

    [Fact]
    public async Task Missing_auth_code_is_400_with_the_python_text()
    {
        using var app = new TokenApp();
        var resp = await app.Client.GetAsync("/callback?s=ok&code=200&state=x");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(TokenService.NoAuthCodeText, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Happy_path_exchanges_writes_token_and_consumes_the_signal()
    {
        using var app = new TokenApp();
        var token = TestEnv.Jwt(exp: 1789000000);
        app.Handler.ExchangeOk(token);
        app.Handler.Profile(HttpStatusCode.OK, "{\"s\": \"ok\", \"code\": 200}");
        app.TouchReauth();   // the collector asked for a re-login
        app.Service.NewLoginUrl();   // a live prompt rotates the state
        var state = app.Service.PendingState!;

        var resp = await app.Client.GetAsync(
            "/callback?s=ok&code=200&auth_code=AUTHCODE123&state=" + Uri.EscapeDataString(state));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Token saved", await resp.Content.ReadAsStringAsync());

        // token file: same keys/order as the Python writer
        Assert.True(File.Exists(app.TokenPath), "token file written");
        var raw = File.ReadAllText(app.TokenPath);
        Assert.StartsWith("{\"access_token\": \"" + token + "\", ", raw);
        Assert.Contains($"\", \"app_id\": \"{TestEnv.AppId}\", \"saved_at\": ", raw);
        Assert.EndsWith("}", raw);
        Assert.False(raw.Contains('\n'), "single-line json.dump shape");
        using var doc = JsonDocument.Parse(raw);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "access_token", "app_id", "saved_at" }, keys);
        Assert.Equal(token, doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal(TestEnv.AppId, doc.RootElement.GetProperty("app_id").GetString());
        var savedAt = doc.RootElement.GetProperty("saved_at").GetInt64();
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - savedAt) <= 120,
            "saved_at is the current epoch second");

        // the collector's reauth signal is consumed
        Assert.False(File.Exists(app.ReauthPath), "fyers_reauth.request deleted");

        // exactly one Telegram success, built from describe_token
        var msg = Assert.Single(app.Notifier.Sent);
        Assert.Equal("info", msg.Level);
        Assert.StartsWith("✅ Fyers token saved — VALID — stops working at the ~06:00 IST "
                          + "Fyers reset (~", msg.Text);
        Assert.Contains("JWT exp claim ", msg.Text);
        Assert.EndsWith("Collector picks it up automatically.", msg.Text);

        // the state is consumed: the same link cannot be replayed
        Assert.Null(app.Service.PendingState);
        var replay = await app.Client.GetAsync(
            "/callback?s=ok&code=200&auth_code=AUTHCODE123&state="
            + Uri.EscapeDataString(state));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(TokenService.StateMismatchText, await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Exchange_uses_the_python_body_and_headers()
    {
        using var app = new TokenApp();
        app.Handler.ExchangeOk(TestEnv.Jwt(1789000000));
        app.Handler.Profile(HttpStatusCode.OK, "{\"s\": \"ok\"}");
        app.Service.NewLoginUrl();   // a live prompt rotates the state
        var state = app.Service.PendingState!;

        await app.Client.GetAsync("/callback?auth_code=AUTHCODE123&state="
                                  + Uri.EscapeDataString(state));

        var exchange = app.Handler.Calls.Single(c =>
            c.Url == "https://api-t1.fyers.in/api/v3/validate-authcode");
        Assert.Equal("POST", exchange.Method);
        Assert.Equal("{\"grant_type\": \"authorization_code\", \"appIdHash\": \""
                     + TestEnv.AppIdHash + "\", \"code\": \"AUTHCODE123\"}", exchange.Body);
        Assert.Equal("application/json", exchange.ContentType);
        Assert.Equal(FyersAuth.UserAgent, exchange.UserAgent);

        var profile = app.Handler.Calls.Single(c =>
            c.Url == FyersAuth.ProfileUrl);
        Assert.Equal("GET", profile.Method);
        Assert.Equal($"{TestEnv.AppId}:{TestEnv.Jwt(1789000000)}", profile.Authorization);
        Assert.Equal(FyersAuth.UserAgent, profile.UserAgent);
    }

    [Fact]
    public async Task Failed_exchange_is_502_and_telegrams_the_error()
    {
        using var app = new TokenApp();
        app.Handler.ExchangeFail(400, "Invalid auth code");
        app.Service.NewLoginUrl();   // a live prompt rotates the state
        var state = app.Service.PendingState!;

        var resp = await app.Client.GetAsync(
            "/callback?auth_code=DEAD&state=" + Uri.EscapeDataString(state));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Login failed: validate-authcode failed: 400 Invalid auth code (error)",
            body);
        Assert.Contains("Close this tab and click the Telegram link again.", body);
        var msg = Assert.Single(app.Notifier.Sent);
        Assert.Equal("error", msg.Level);
        Assert.Contains("Token exchange FAILED: validate-authcode failed: 400 Invalid "
                        + "auth code (error) — click the login link again.", msg.Text);
        // the exchange failed, so the previous token file is left alone
        Assert.True(File.Exists(app.TokenPath));
        Assert.Contains("seed-token", File.ReadAllText(app.TokenPath));
    }

    [Fact]
    public async Task Bot_blocked_profile_is_reported_unreachable_not_invalid()
    {
        using var app = new TokenApp();
        app.Handler.ExchangeOk(TestEnv.Jwt(1789000000));
        // Cloudflare fronts this endpoint: a 403 is not a verdict.
        app.Handler.ExchangeOk(TestEnv.Jwt(1789000000));   // the click itself succeeds
        app.Handler.Profile(HttpStatusCode.Forbidden, "error code: 1010");
        app.Service.NewLoginUrl();   // a live prompt rotates the state
        var state = app.Service.PendingState!;

        var resp = await app.Client.GetAsync(
            "/callback?auth_code=AUTHCODE123&state=" + Uri.EscapeDataString(state));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var msg = Assert.Single(app.Notifier.Sent);
        Assert.Equal("warn", msg.Level);
        Assert.StartsWith("• Fyers token saved — saved; could not reach Fyers to validate.",
            msg.Text);
    }

    [Fact]
    public async Task Rejected_by_fyers_profile_is_reported_not_valid()
    {
        using var app = new TokenApp();
        app.Handler.ExchangeOk(TestEnv.Jwt(1789000000));
        app.Handler.ExchangeOk(TestEnv.Jwt(1789000000));   // the click itself succeeds
        app.Handler.Profile(HttpStatusCode.Unauthorized, "{\"s\": \"error\", \"code\": -8}");
        app.Service.NewLoginUrl();   // a live prompt rotates the state
        var state = app.Service.PendingState!;

        await app.Client.GetAsync("/callback?auth_code=AUTHCODE123&state="
                                  + Uri.EscapeDataString(state));

        var msg = Assert.Single(app.Notifier.Sent);
        Assert.Equal("error", msg.Level);
        Assert.StartsWith("❌ Fyers token saved — NOT VALID — Fyers already rejects it "
                          + "(click the next login link). ", msg.Text);
    }
}
