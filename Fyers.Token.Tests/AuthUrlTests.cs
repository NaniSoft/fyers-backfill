using Fyers.Token;

namespace Fyers.Token.Tests;

/// <summary>build_auth_url byte-parity with src/auth.py, plus the hash,
/// staleness and describe_token semantics the service leans on.</summary>
public class AuthUrlTests
{
    [Fact]
    public void Build_auth_url_matches_the_python_param_set_and_order()
    {
        // src/auth.py: BASE + "/generate-authcode?" + urlencode({client_id,
        // redirect_uri, response_type, state}) — that exact order.
        var (url, state) = FyersAuth.BuildAuthUrl("ABC123-100", TestEnv.RedirectUri, "st4te");

        Assert.Equal("https://api-t1.fyers.in/api/v3/generate-authcode?"
                     + "client_id=ABC123-100"
                     + "&redirect_uri=http%3A%2F%2F127.0.0.1%3A8001%2Fcallback"
                     + "&response_type=code&state=st4te", url);
        Assert.Equal("st4te", state);
    }

    [Fact]
    public void Build_auth_url_generates_the_same_entropy_python_does()
    {
        var (url, state) = FyersAuth.BuildAuthUrl(TestEnv.AppId, TestEnv.RedirectUri);

        // secrets.token_urlsafe(8): 8 random bytes -> 11 unpadded base64url chars
        Assert.Matches("^[A-Za-z0-9_-]{11}$", state);
        Assert.Contains("&state=" + state, url);

        var (_, stateTwo) = FyersAuth.BuildAuthUrl(TestEnv.AppId, TestEnv.RedirectUri);
        Assert.NotEqual(state, stateTwo);   // every prompt rotates the state
    }

    [Fact]
    public void Redirect_uri_decodes_to_the_registered_callback()
    {
        var (url, _) = FyersAuth.BuildAuthUrl(TestEnv.AppId, TestEnv.RedirectUri, "s");
        var query = url[(url.IndexOf('?') + 1)..].Split('&')
            .Select(p => p.Split('='))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        Assert.Equal(new[] { "client_id", "redirect_uri", "response_type", "state" },
            url[(url.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=')[0]).ToArray());
        Assert.Equal(TestEnv.AppId, query["client_id"]);
        Assert.Equal("http://127.0.0.1:8001/callback", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
    }

    [Fact]
    public void App_id_hash_is_lowercase_hex_sha256_of_app_id_colon_secret()
    {
        // python: hashlib.sha256(b"appid:secret").hexdigest()
        Assert.Equal("e87028b12df6fcfbc7c1f74cb18cf5637ac66ebd0516ea38876db838b727a601",
            FyersAuth.AppIdHash("appid", "secret"));
        Assert.Equal(TestEnv.AppIdHash, FyersAuth.AppIdHash(TestEnv.AppId, TestEnv.SecretId));
    }

    [Fact]
    public void Token_is_stale_on_ist_date_mismatch_only()
    {
        var today = "2026-09-15";
        var sameDay = new DateTimeOffset(2026, 9, 15, 1, 0, 0, TestEnv.IstOffset)
            .ToUnixTimeSeconds();
        var previousDay = new DateTimeOffset(2026, 9, 14, 23, 30, 0, TestEnv.IstOffset)
            .ToUnixTimeSeconds();

        Assert.False(FyersAuth.TokenIsStale(sameDay, today));     // 01:00 IST today
        Assert.True(FyersAuth.TokenIsStale(previousDay, today));  // 23:30 IST yesterday
        Assert.True(FyersAuth.TokenIsStale(0, today));            // epoch -> 1970
    }

    [Fact]
    public void Next_death_ist_is_the_next_0600()
    {
        Assert.Equal(TestEnv.Ist(6, 0, day: 16),
            FyersAuth.NextDeathIst(TestEnv.Ist(10, 54, day: 15)));
        Assert.Equal(TestEnv.Ist(6, 0, day: 16),
            FyersAuth.NextDeathIst(TestEnv.Ist(6, 0, day: 15)));
        Assert.Equal(TestEnv.Ist(6, 0, day: 15),
            FyersAuth.NextDeathIst(TestEnv.Ist(3, 0, day: 15)));
    }

    [Fact]
    public async Task Describe_token_reports_the_three_states()
    {
        var token = TestEnv.Jwt(1789000000);   // 2026-09-10 05:56 IST (informational only)
        var now = TestEnv.Ist(10, 54);
        // 19h06m to the next ~06:00 reset
        Assert.Equal((true,
                "VALID — stops working at the ~06:00 IST Fyers reset (~19h06m from now). "
                + "JWT exp claim 2026-09-10 05:56 IST; Collector picks it up automatically."),
            await FyersAuth.DescribeTokenAsync(TestEnv.AppId, token, (_, _) => Task.FromResult<bool?>(true), now));

        Assert.Equal((false,
                "NOT VALID — Fyers already rejects it (click the next login link). "
                + "JWT exp claim 2026-09-10 05:56 IST; "),
            await FyersAuth.DescribeTokenAsync(TestEnv.AppId, token, (_, _) => Task.FromResult<bool?>(false), now));

        Assert.Equal((null,
                "saved; could not reach Fyers to validate. stops working at the ~06:00 IST "
                + "Fyers reset (~19h06m from now)."),
            await FyersAuth.DescribeTokenAsync(TestEnv.AppId, token, (_, _) => Task.FromResult<bool?>(null), now));
    }

    [Fact]
    public async Task Describe_token_handles_a_non_jwt_token()
    {
        var now = TestEnv.Ist(6, 0);
        var (ok, detail) = await FyersAuth.DescribeTokenAsync(
            TestEnv.AppId, "not-a-jwt", (_, _) => Task.FromResult<bool?>(true), now);
        Assert.True(ok);
        Assert.Equal("VALID — stops working at the ~06:00 IST Fyers reset (~24h00m from now). "
                     + "Collector picks it up automatically.", detail);
    }

    [Fact]
    public void State_entropy_and_query_encoding_match_python_quote_plus()
    {
        // urlencode() encodes ':' and '/' as %3A/%2F and leaves '-', '_', '.' alone.
        Assert.Equal("a%3Ab%2Fc-d_e.f", FyersAuth.UrlEncode("a:b/c-d_e.f"));
    }
}
