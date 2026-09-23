using System.Text.Json.Nodes;

namespace Fyers.Token;

/// <summary>DI-facing wrapper so the service's exchange/profile delegates share
/// the one HttpClient (which the tests replace with a fake handler —
/// src/token_service.py passes the seams the same way). Named TokenApiClient
/// because the collector's own FyersClient (Fyers.Core.Fyers) now shares this
/// process after the merge.</summary>
public sealed class TokenApiClient
{
    private readonly HttpClient _http;

    public TokenApiClient(HttpClient http) => _http = http;

    public Task<JsonObject?> ExchangeAsync(string appId, string secretId, string authCode) =>
        FyersAuth.ExchangeAuthCodeAsync(_http, appId, secretId, authCode);

    public Task<bool?> ProfileValidAsync(string appId, string accessToken) =>
        FyersAuth.ProfileValidAsync(_http, appId, accessToken);
}
