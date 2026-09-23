using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fyers.Token;

/// <summary>The watcher loop (src/token_service.py's serve_forever loop): one
/// token-health check every watch_interval_sec. A failure inside the check is
/// logged and never kills the loop.</summary>
public sealed class PromptWatcher : BackgroundService
{
    private readonly TokenService _service;
    private readonly ILogger _log;

    public PromptWatcher(TokenService service, ILogger? log = null)
    {
        _service = service;
        _log = log ?? NullLogger.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _service.WatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // the watcher must never kill the loop
                _log.LogError("watch_once failed: {Type}", e.GetType().Name);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_service.Config.WatchIntervalSec),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
