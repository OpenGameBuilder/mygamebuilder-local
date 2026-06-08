using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Http;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// Liveness endpoint. The landing page, <c>/play</c>, Ruffle, and all static asset
/// serving are intentionally out of scope and owned by a separate front-end host.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // text/plain launch token (env MGB_LAUNCH_TOKEN) or "ok" so a launcher can
        // confirm it reached this instance (README 3).
        app.MapGet("/healthz", (IOptions<ServerOptions> options) =>
        {
            var token = options.Value.LaunchToken;
            return XmlResults.Plain(string.IsNullOrEmpty(token) ? "ok" : token);
        });

        return app;
    }
}
