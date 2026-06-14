using System.Text;
using MyGameBuilder.Local.Api.Updates;

namespace MyGameBuilder.Local.Api.Endpoints;

public static class UpdateEndpoints
{
    public const string CorsPolicyName = "UpdatesSameOriginOnly";

    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/_updates", () => Results.Redirect("/updates", permanent: false));

        app.MapGet(
            "/updates",
            (UpdateSecurityToken token) => Results.Text(UpdatePageRenderer.BuildUpdatePage(token.Value), "text/html", Encoding.UTF8))
            .RequireCors(CorsPolicyName);

        var group = app.MapGroup("/_updates").RequireCors(CorsPolicyName);

        group.MapGet("/flash-assets/{fileName}", UpdatePageAssets.Serve);

        group.MapGet("/status", (UpdateCoordinator coordinator) => Results.Json(coordinator.GetStatus()));

        group.MapPost(
            "/check",
            async (HttpRequest request, UpdateSecurityToken token, UpdateCoordinator coordinator, CancellationToken cancellationToken) =>
            {
                if (!IsAuthorizedPost(request, token))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                await coordinator.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(coordinator.GetStatus());
            });

        group.MapPost(
            "/app/install",
            async (HttpRequest request, UpdateSecurityToken token, UpdateCoordinator coordinator, CancellationToken cancellationToken) =>
            {
                if (!IsAuthorizedPost(request, token))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                await coordinator.InstallAppUpdateAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(coordinator.GetStatus());
            });

        group.MapPost(
            "/archives/{archive}/install",
            async (string archive, HttpRequest request, UpdateSecurityToken token, UpdateCoordinator coordinator, CancellationToken cancellationToken) =>
            {
                if (!IsAuthorizedPost(request, token))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                if (!UpdateTargets.TryParseArchiveId(archive, out var target))
                {
                    return Results.NotFound();
                }

                await coordinator.InstallArchiveUpdateAsync(target, cancellationToken).ConfigureAwait(false);
                return Results.Json(coordinator.GetStatus());
            });

        return app;
    }

    private static bool IsAuthorizedPost(HttpRequest request, UpdateSecurityToken token)
    {
        if (!token.IsValid(request.Headers["X-MGB-Update-Token"].FirstOrDefault()))
        {
            return false;
        }

        return IsSameOriginHeader(request, "Origin") && IsSameOriginHeader(request, "Referer");
    }

    private static bool IsSameOriginHeader(HttpRequest request, string headerName)
    {
        var value = request.Headers[headerName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
