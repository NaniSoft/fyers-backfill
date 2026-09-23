using System.Text;
using Microsoft.Extensions.Logging;

namespace Fyers.Token;

/// <summary>Console logging with the collector's format, byte-for-byte:
/// `2026-09-15 10:54 INFO    [token] message` — IST timestamp (minute
/// resolution), level left-padded to 7, short category in brackets.</summary>
public sealed class TokenConsoleLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;

    public TokenConsoleLoggerProvider() : this(Console.Out) { }

    public TokenConsoleLoggerProvider(TextWriter writer) => _writer = writer;

    public ILogger CreateLogger(string categoryName) => new TokenConsoleLogger(categoryName, _writer);

    public void Dispose() { }
}

/// <summary>Python logging.Logger parity: WARNING is spelled out in full (the
/// level column is a 7-wide left-aligned field), IST wall clock, and a short
/// category name — `Fyers.Token.*` collapses to `token`, bare categories
/// ("token", "token-service") pass through.</summary>
public sealed class TokenConsoleLogger : ILogger
{
    private readonly string _category;
    private readonly TextWriter _writer;

    public TokenConsoleLogger(string category, TextWriter writer)
    {
        _category = category;
        _writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel is not (LogLevel.None or LogLevel.Trace or LogLevel.Debug);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var line = $"{Ist.Now():yyyy-MM-dd HH:mm} {LevelText(logLevel),-7} "
                   + $"[{ShortName(_category)}] {formatter(state, exception)}";
        lock (_writer)
        {
            _writer.WriteLine(line);
            if (exception is not null) _writer.WriteLine(exception.ToString());
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "NONE",
    };

    public static string ShortName(string category)
    {
        var parts = category.Split('.');
        if (parts.Length == 1) return category;
        if (parts[0] == "Fyers" && parts.Length >= 2) return parts[1].ToLowerInvariant();
        return parts[^1].ToLowerInvariant();
    }
}

/// <summary>Startup console encoding so the ✅/❌/• marks in the Telegram-text
/// logs survive a Windows code page (best effort; never fatal).</summary>
public static class ConsoleEncodingSetup
{
    public static void TryUtf8()
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (Exception) { /* redirected/locked console — ignore */ }
    }
}
