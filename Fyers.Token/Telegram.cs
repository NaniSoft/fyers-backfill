using Microsoft.Extensions.Logging;

namespace Fyers.Token;

/// <summary>Notifier seam (src/token_service.py's duck-typed `notifier`).
/// Fire-and-forget: never throws into the service.</summary>
public interface INotifier
{
    bool Enabled { get; }

    /// <summary>Returns true iff Telegram accepted the message (HTTP 200).</summary>
    Task<bool> NotifyAsync(string text, string level = "info");
}

/// <summary>Telegram sendMessage — 1:1 port of src/notifier.py:Notifier:
/// same URL shape, same form fields, same [INFO]/[WARN]/[ERR] prefixes, same
/// "disabled when bot token or chat id is empty" gate, same never-raise
/// contract.</summary>
public sealed class TelegramNotifier : INotifier
{
    private const string Api = "https://api.telegram.org/bot{0}/sendMessage";

    private static readonly Dictionary<string, string> LevelPrefix = new(StringComparer.Ordinal)
    {
        ["info"] = "[INFO]",
        ["warn"] = "[WARN]",
        ["error"] = "[ERR]",
    };

    private readonly string _token;
    private readonly string _chatId;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public TelegramNotifier(TokenConfig cfg, ILogger? log = null, HttpClient? http = null)
    {
        _token = cfg.TelegramBotToken;
        _chatId = cfg.TelegramChatId;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _http = http ?? new HttpClient();
    }

    public bool Enabled => _token.Length > 0 && _chatId.Length > 0;

    public async Task<bool> NotifyAsync(string text, string level = "info")
    {
        if (!Enabled) return false;
        var prefix = LevelPrefix.GetValueOrDefault(level, "[INFO]");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, string.Format(Api, _token))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = _chatId,
                    ["text"] = $"{prefix} {text}",
                }),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, timeout.Token);
            return (int)resp.StatusCode == 200;
        }
        catch (Exception e)
        {
            _log.LogWarning("telegram notify failed: {Type}", e.GetType().Name);
            return false;
        }
    }
}

/// <summary>Kept so a test/other caller can record prompts without sockets
/// (parity with the Python tests' fake notifier).</summary>
public sealed class RecordingNotifier : INotifier
{
    public List<(string Text, string Level)> Sent { get; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Force the next send to "fail" (returns false), like a Telegram
    /// outage — the service then does not arm its re-mind timer.</summary>
    public bool FailNext { get; set; }

    public Task<bool> NotifyAsync(string text, string level = "info")
    {
        if (FailNext) return Task.FromResult(false);
        Sent.Add((text, level));
        return Task.FromResult(true);
    }
}
