using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fyers.Token;

/// <summary>
/// Wires the token service into any ASP.NET Core host. The collector's
/// Program calls <see cref="AddFyersTokenService"/> so ONE process both
/// captures and owns login (merged 2026-09-23); the token tests call it
/// against an in-memory TestServer.
///
/// The HTTP surface is mapped separately by
/// <see cref="CallbackEndpoints.MapTokenEndpoints"/>.
/// </summary>
public static class TokenHosting
{
    /// <summary>Registers the callback handler, the expiry watcher and their
    /// seams. A <see cref="HttpMessageHandler"/> registered by the caller (test
    /// fake) is honoured; otherwise a pooled <see cref="SocketsHttpHandler"/>
    /// is used. An <see cref="INotifier"/> registered by the caller wins (last
    /// registration wins in MS DI) — the Telegram notifier is the default.</summary>
    public static IServiceCollection AddFyersTokenService(
        this IServiceCollection services, TokenConfig cfg)
    {
        services.AddSingleton(cfg);
        services.AddSingleton(sp =>
        {
            var handler = sp.GetService<HttpMessageHandler>() ?? new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        });
        services.AddSingleton(sp => new TokenApiClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<INotifier>(sp => new TelegramNotifier(
            sp.GetRequiredService<TokenConfig>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("token"),
            sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new TokenService(
            sp.GetRequiredService<TokenConfig>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("token"),
            sp.GetRequiredService<INotifier>(),
            (appId, secretId, code) => sp.GetRequiredService<TokenApiClient>()
                .ExchangeAsync(appId, secretId, code),
            (appId, token) => sp.GetRequiredService<TokenApiClient>().ProfileValidAsync(appId, token)));
        services.AddHostedService<PromptWatcher>();
        return services;
    }
}
