using Fyers.Token;
using Microsoft.Extensions.Logging;

namespace Fyers.Token.Tests;

/// <summary>The .env-file reader, the CLI/env path overrides, and the
/// byte-exact redirect-URI agreement check.</summary>
public class ConfigTests
{
    [Fact]
    public void Env_file_parse_matches_python_load_env()
    {
        var env = EnvFile.Parse(
            "# comment line\n"
            + "\n"
            + "FYERS_APP_ID= ABC123-100 \n"
            + "FYERS_SECRET_ID=s3cr3t   # not a comment: python keeps it\n"
            + "THIS LINE HAS NO EQUALS\n"
            + "EMPTY=\n"
            + "TELEGRAM_CHAT_ID=\"987654321\"\n");

        Assert.Equal("ABC123-100", env.Get("FYERS_APP_ID"));
        Assert.Equal("s3cr3t   # not a comment: python keeps it", env.Get("FYERS_SECRET_ID"));
        Assert.Equal("", env.Get("EMPTY"));
        Assert.Equal("\"987654321\"", env.Get("TELEGRAM_CHAT_ID"));   // quotes kept, as in python
        Assert.Equal("", env.Get("ABSENT"));
        Assert.True(env.Has("EMPTY"));
        Assert.False(env.Has("ABSENT"));
    }

    [Fact]
    public void Env_file_missing_is_a_loud_failure()
    {
        Assert.Throws<FileNotFoundException>(() => EnvFile.Load("Z:/definitely/absent/.env"));
    }

    [Fact]
    public void Args_parse_and_unknown_args_fail_loud()
    {
        var opts = TokenRunOptions.Parse(new[] {
            "--env", "E:/x/.env", "--data-dir", "D:/data", "--bind", "http://127.0.0.1:9999",
        });
        Assert.Equal("E:/x/.env", opts.EnvPath);
        Assert.Equal("D:/data", opts.DataDir);
        Assert.Equal("http://127.0.0.1:9999", opts.BindUrl);

        Assert.Throws<ArgumentException>(() =>
            TokenRunOptions.Parse(new[] { "--nope", "x" }));
        Assert.Throws<ArgumentException>(() => TokenRunOptions.Parse(new[] { "--env" }));
    }

    [Fact]
    public void Fyers_env_path_variable_names_the_file_when_no_arg_is_given()
    {
        var previous = Environment.GetEnvironmentVariable("FYERS_ENV_PATH");
        try
        {
            Environment.SetEnvironmentVariable("FYERS_ENV_PATH", "E:/alt/.env");
            Assert.Equal("E:/alt/.env", TokenRunOptions.Parse(Array.Empty<string>()).EnvPath);
            Assert.Equal("E:/real/.env",
                TokenRunOptions.Parse(new[] { "--env", "E:/real/.env" }).EnvPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FYERS_ENV_PATH", previous);
        }
    }

    [Fact]
    public void Data_dir_defaults_to_repo_root_slash_data_dotnet()
    {
        var opts = TokenRunOptions.Parse(Array.Empty<string>());
        opts = new TokenRunOptions { RepoRoot = "R:/repo" };
        Assert.Equal(Path.Combine("R:/repo", "data-dotnet"), opts.ResolveDataDir());
        Assert.Equal(Path.Combine("R:/repo", ".env"), opts.ResolveEnvPath());
        Assert.Equal("http://127.0.0.1:8001/callback",
            TokenConfig.Build(EnvFile.Parse("FYERS_APP_ID=a\nFYERS_SECRET_ID=b\n"), opts)
                .RedirectUri);
    }

    [Fact]
    public void Missing_app_credentials_fail_loud_at_startup()
    {
        var opts = new TokenRunOptions { RepoRoot = "R:/repo", DataDir = "R:/repo/data-dotnet" };
        Assert.Throws<InvalidOperationException>(() =>
            TokenConfig.Build(EnvFile.Parse("FYERS_APP_ID=a\n"), opts));
    }

    [Fact]
    public void Callback_port_is_derived_from_the_registered_uri()
    {
        var cfg = new TokenConfig
        {
            AppId = "a",
            SecretId = "b",
            RedirectUri = TestEnv.RedirectUri,
            DataDir = "R:/repo/data-dotnet",
        };
        var svc = new TokenService(cfg);
        Assert.Equal(TestEnv.RedirectUri, svc.RedirectUri());
        Assert.Equal(8001, svc.CallbackPort);
    }

    [Fact]
    public void Callback_base_url_must_agree_with_the_registered_uri()
    {
        TokenConfig Cfg(string baseUrl) => new()
        {
            AppId = "a",
            SecretId = "b",
            RedirectUri = TestEnv.RedirectUri,
            CallbackBaseUrl = baseUrl,
            DataDir = "R:/repo/data-dotnet",
        };

        // a trailing slash is fine: the derived URI still matches byte-exactly
        Assert.Equal(TestEnv.RedirectUri,
            new TokenService(Cfg("http://127.0.0.1:8001/")).RedirectUri());

        var mismatch = Assert.Throws<InvalidOperationException>(
            () => new TokenService(Cfg("http://127.0.0.1:9002")));
        Assert.Contains("auth.callback_base_url (http://127.0.0.1:9002) + /callback != "
                        + $"FYERS_REDIRECT_URI ({TestEnv.RedirectUri}) — the registered "
                        + "URI is byte-exact; fix one of them", mismatch.Message);
    }

    [Fact]
    public void Token_paths_live_beside_each_other_in_the_data_dir()
    {
        var cfg = new TokenConfig
        {
            AppId = "a",
            SecretId = "b",
            RedirectUri = TestEnv.RedirectUri,
            DataDir = Path.Combine("R:", "repo", "data-dotnet"),
        };
        Assert.Equal(Path.Combine(cfg.DataDir, "fyers_access_token.json"), cfg.TokenPath);
        Assert.Equal(Path.Combine(cfg.DataDir, "fyers_reauth.request"), cfg.ReauthRequestPath);
        Assert.Equal("fyers_access_token.json", Path.GetFileName(cfg.TokenPath));
    }

    [Fact]
    public void Watch_and_prompt_defaults_match_the_python_config()
    {
        var cfg = TokenConfig.Build(
            EnvFile.Parse("FYERS_APP_ID=a\nFYERS_SECRET_ID=b\n"),
            new TokenRunOptions { RepoRoot = "R:/repo" });
        Assert.Equal("06:30", cfg.PromptTime);
        Assert.Equal(60, cfg.RemindIntervalMin);
        Assert.Equal(60, cfg.WatchIntervalSec);
        Assert.Equal("http://127.0.0.1:8001", cfg.BindUrl);
    }

    [Fact]
    public void Prompt_text_is_byte_identical_to_the_python_message()
    {
        Assert.Equal(
            "Fyers token missing/expired — daily login needed (one click/day):\n"
            + "{url}\n"
            + "Log in on the Fyers page; the token lands automatically and the "
            + "collector resumes on its own.",
            TokenService.PromptTemplate);
    }

    [Fact]
    public void Console_log_format_is_the_collector_format()
    {
        var sink = new StringWriter();
        var logger = new TokenConsoleLoggerProvider(sink).CreateLogger("token");
        logger.LogInformation("hello {What}", "world");

        var line = sink.ToString().TrimEnd();
        // 2026-09-15 10:54 INFO    [token] hello world
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} INFO    \[token\] hello world$", line);
        Assert.Equal("token", TokenConsoleLogger.ShortName("Fyers.Token.PromptWatcher"));
        Assert.Equal("token", TokenConsoleLogger.ShortName("token"));
        Assert.Equal("collector", TokenConsoleLogger.ShortName("Fyers.Collector.Services.Eod"));
    }

    [Fact]
    public void Callback_query_values_are_read_first_value_wins()
    {
        Assert.Equal("AUTH", TokenService.QueryValue("?s=ok&code=200&auth_code=AUTH&state=x", "auth_code"));
        Assert.Equal("x", TokenService.QueryValue("state=x&state=y", "state"));
        Assert.Null(TokenService.QueryValue("s=ok", "auth_code"));
        Assert.Equal("", TokenService.QueryValue("auth_code=", "auth_code"));
    }
}
