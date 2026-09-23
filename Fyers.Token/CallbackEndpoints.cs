using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fyers.Token;

/// <summary>The HTTP surface: GET /healthz (k8s liveness) and GET /callback
/// (Fyers' redirect). Both are thin adapters over TokenService — the routing
/// adds nothing of its own.</summary>
public static class CallbackEndpoints
{
    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Text("ok", "text/plain", Encoding.UTF8));

        app.MapGet("/callback", async (HttpRequest request, TokenService service) =>
        {
            var path = request.Path.Value ?? "/callback";
            var query = request.QueryString.Value ?? "";
            var (status, body, contentType) =
                await service.HandleCallbackAsync(path, query);
            return Results.Content(body, contentType, Encoding.UTF8, status);
        });

        return app;
    }
}
