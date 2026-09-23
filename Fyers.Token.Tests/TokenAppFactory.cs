using System.Buffers.Text;
using System.Net;
using System.Text;
using Fyers.Token;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fyers.Token.Tests;

/// <summary>Shared fixtures: a fake Fyers HTTP layer, a temp .env/data-dir app
/// host, and the IST clock helpers the watcher tests inject.</summary>
public static class TestEnv
{
    public const string AppId = "TESTAPPID";
    public const string SecretId = "testsecret";

    /// <summary>sha256("TESTAPPID:testsecret") — the appIdHash Python's
    /// _app_id_hash produces for these credentials.</summary>
    public const string AppIdHash =
        "756a1192677db652f2bef04b5578948f064e08d70995442d9e9677294d93cb05";

    public const string RedirectUri = "http://127.0.0.1:8001/callback";

    public static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static DateTimeOffset Ist(int hour, int minute, int day = 15) =>
        new(2026, 9, day, hour, minute, 0, IstOffset);

    /// <summary>A real-shaped JWT with an exp claim (describe_token reads it).</summary>
    public static string Jwt(long exp) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))
        + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"exp\":{exp}}}"))
        + ".sig";

    public static string WriteDotEnv(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".env");
        File.WriteAllLines(path, new[]
        {
            "# test env",
            "FYERS_APP_ID=" + AppId,
            "FYERS_SECRET_ID=" + SecretId,
            "FYERS_REDIRECT_URI=" + RedirectUri,
            "TELEGRAM_BOT_TOKEN=123456:abcdef",
            "TELEGRAM_CHAT_ID=987654321",
        });
        return path;
    }
}

/// <summary>Records every request and answers from a scripted map, so the
/// callback tests never touch the network.</summary>
public sealed class FakeFyersHandler : HttpMessageHandler
{
    public sealed record Call(string Method, string Url, string? Body,
        string? Authorization, string? UserAgent, string? ContentType);

    public List<Call> Calls { get; } = new();

    private readonly Dictionary<string, Func<Call, HttpResponseMessage>> _scripted = new();

    public void On(string methodAndUrl, Func<Call, HttpResponseMessage> respond) =>
        _scripted[methodAndUrl] = respond;

    /// <summary>POST validate-authcode -> {"s":"ok","code":200,"access_token":...}</summary>
    public void ExchangeOk(string accessToken) =>
        On("POST https://api-t1.fyers.in/api/v3/validate-authcode", _ => Json(HttpStatusCode.OK,
            "{\"s\": \"ok\", \"code\": 200, \"message\": \"[auth] Auth code exchange succesful\", "
            + $"\"access_token\": \"{accessToken}\"}}"));

    public void ExchangeFail(int code, string message) =>
        On("POST https://api-t1.fyers.in/api/v3/validate-authcode", _ => Json(HttpStatusCode.OK,
            $"{{\"s\": \"error\", \"code\": {code}, \"message\": \"{message}\", \"access_token\": null}}"));

    public void Profile(HttpStatusCode status, string body) =>
        On("GET https://api-t1.fyers.in/api/v3/profile", _ => Json(status, body));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new Call(
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            body,
            request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : null,
            // User-Agent is parsed into ProductInfo tokens ("Mozilla/5.0",
            // "(Windows NT 10.0; Win64; x64)") — join them back with a space.
            request.Headers.TryGetValues("User-Agent", out var u) ? string.Join(" ", u) : null,
            request.Content?.Headers.ContentType?.ToString());
        Calls.Add(call);
        var key = $"{call.Method} {call.Url}";
        if (_scripted.TryGetValue(key, out var respond)) return respond(call);
        return Json(HttpStatusCode.NotFound, "{\"s\": \"error\", \"message\": \"no script\"}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new()
    {
        StatusCode = status,
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

/// <summary>Boots the token service exactly the way the collector host does —
/// <see cref="TokenHosting.AddFyersTokenService"/> + MapTokenEndpoints — against
/// an in-memory TestServer, a temp .env file and a temp data dir, with every
/// Fyers call scripted. Nothing binds a port.</summary>
public sealed class TokenApp : IDisposable
{
    private WebApplication _app = null!;

    public string Root { get; }
    public string DataDir { get; }
    public FakeFyersHandler Handler { get; } = new();
    public RecordingNotifier Notifier { get; } = new();

    public TokenApp()
    {
        Root = Directory.CreateTempSubdirectory("fyers-token-").FullName;
        DataDir = Path.Combine(Root, "data-dotnet");
        Start();
    }

    public void Start()
    {
        var envPath = TestEnv.WriteDotEnv(Root);
        var cfg = TokenConfig.Build(EnvFile.Load(envPath), new TokenRunOptions
        {
            EnvPath = envPath,
            DataDir = DataDir,
            BindUrl = "http://127.0.0.1:8001",   // TestServer ignores this — nothing binds
        });

        // A same-day healthy token, so the hosted PromptWatcher stays quiet
        // for the whole test (otherwise its first tick would Telegram a login
        // prompt into the recorder and race the callback tests).
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(TokenPath,
            "{\"access_token\": \"seed-token\", \"app_id\": \"" + TestEnv.AppId
            + "\", \"saved_at\": " + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<HttpMessageHandler>(Handler);
        builder.Services.AddFyersTokenService(cfg);
        // Registered after the service's default: last wins in MS DI.
        builder.Services.AddSingleton<INotifier>(Notifier);

        _app = builder.Build();
        _app.MapTokenEndpoints();
        // Starts the in-memory server AND the hosted PromptWatcher.
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public HttpClient Client => ((TestServer)_app.Services
        .GetRequiredService<IServer>()).CreateClient();

    public TokenService Service => _app.Services.GetRequiredService<TokenService>();

    public string TokenPath => Path.Combine(DataDir, "fyers_access_token.json");

    public string ReauthPath => Path.Combine(DataDir, "fyers_reauth.request");

    public void TouchReauth()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ReauthPath, "");
    }

    /// <summary>A token file saved `savedAtEpoch` epoch seconds before the IST
    /// "today" of the tests (2026-09-15).</summary>
    public void WriteToken(long savedAtEpoch, string accessToken = "stale-token")
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(TokenPath,
            "{\"access_token\": \"" + accessToken + "\", \"app_id\": \""
            + TestEnv.AppId + "\", \"saved_at\": " + savedAtEpoch + "}");
    }

    public void Dispose()
    {
        try { _app.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception) { /* already stopped */ }
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}

[CollectionDefinition("token-app")]
public sealed class TokenAppCollection
{
}
