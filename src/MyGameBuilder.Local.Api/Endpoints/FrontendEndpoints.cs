using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Extensions;
using MyGameBuilder.Local.Api.Frontend;
using MyGameBuilder.Local.Api.Http;
using MyGameBuilder.Local.Api.Updates;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// Front-end browser endpoints: the archived site root at <c>/</c>, the Ruffle launcher at
/// <c>/apphost/MGB.html</c> (the old Python client used <c>/play</c>), the Flash
/// <c>crossdomain.xml</c> policy, and archived frontend assets from the frontend SQLite
/// database.
/// </summary>
public static class FrontendEndpoints
{
    public static IEndpointRouteBuilder MapFrontendEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ServiceProvider.GetRequiredService<IOptions<FrontendOptions>>().Value;
        var swfUrl = $"{WebApplicationExtensions.FrontendRequestPath}/{options.SwfName}";

        app.MapGet(
            "/",
            async (
                HttpRequest request,
                FrontendArchiveStore frontendArchive,
                UpdateSecurityToken updateToken,
                CancellationToken cancellationToken) =>
        {
            if (frontendArchive.IsMissing)
            {
                return MissingFrontendArchive(frontendArchive.ArchivePath);
            }

            var asset = await frontendArchive.GetMyGameBuilderAssetAsync(
                string.Empty,
                request.QueryString.Value ?? string.Empty,
                cancellationToken);

            return asset is null
                ? Results.Text(
                    "The recovered MyGameBuilder home page was not found in frontend.sqlite.",
                    "text/plain",
                    Encoding.UTF8,
                    StatusCodes.Status404NotFound)
                : FrontendAsset(string.Empty, request, asset);
        });

        // Ruffle launcher. Kept generated so URL rewrite rules point the recovered SWF at the
        // local backend rather than retired production endpoints.
        app.MapGet(
            WebApplicationExtensions.PlayPath,
            (FrontendArchiveStore frontendArchive) => frontendArchive.IsMissing
                ? MissingFrontendArchive(frontendArchive.ArchivePath)
                : Html(BuildRufflePage(swfUrl)));

        // Flash cross-domain policy (allow-all is fine for a local single-origin host).
        app.MapGet("/crossdomain.xml", () => XmlResults.Xml(CrossDomainPolicy));

        app.MapGet("/favicon.ico", (IHostEnvironment environment) => ServeFavicon(environment));

        app.MapGet(
            WebApplicationExtensions.RuffleRequestPath + "/{fileName}",
            (string? fileName, IHostEnvironment environment) => ServeRuffleAsset(fileName, environment));

        app.MapGet(
            WebApplicationExtensions.FrontendRequestPath + "/{**path}",
            async (
                string? path,
                HttpRequest request,
                FrontendArchiveStore frontendArchive,
                UpdateSecurityToken updateToken,
                CancellationToken cancellationToken) =>
        {
            if (frontendArchive.IsMissing)
            {
                return MissingFrontendArchive(frontendArchive.ArchivePath);
            }

            if (string.IsNullOrEmpty(path))
            {
                return Results.NotFound();
            }

            var asset = await frontendArchive.GetAppHostAssetAsync(
                path,
                request.QueryString.Value ?? string.Empty,
                cancellationToken);
            if (asset is not null)
            {
                return FrontendAsset(path, request, asset);
            }

            if (string.Equals(path.TrimStart('/'), options.SwfName, StringComparison.Ordinal))
            {
                return Results.Text(
                    $"{options.SwfName} was not found in frontend.sqlite.",
                    "text/plain",
                    Encoding.UTF8,
                    StatusCodes.Status404NotFound);
            }

            return Results.NotFound();
        });

        app.MapGet(
            "/{**path}",
            async (
                string? path,
                HttpRequest request,
                FrontendArchiveStore frontendArchive,
                UpdateSecurityToken updateToken,
                CancellationToken cancellationToken) =>
        {
            if (frontendArchive.IsMissing)
            {
                return MissingFrontendArchive(frontendArchive.ArchivePath);
            }

            var asset = await frontendArchive.GetMyGameBuilderAssetAsync(
                path ?? string.Empty,
                request.QueryString.Value ?? string.Empty,
                cancellationToken);

            return asset is null
                ? Results.NotFound()
                : FrontendAsset(path ?? string.Empty, request, asset);
        });

        return app;
    }

    private static IResult Html(string content) => Results.Text(content, "text/html", Encoding.UTF8);

    private static IResult MissingFrontendArchive(string archivePath) =>
        Results.Text(UpdatePageRenderer.BuildMissingFrontendArchivePage(archivePath), "text/html", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);

    private static IResult ServeFavicon(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var path = ResolvePackagedAssetPath(environment, Path.Combine("Assets", "favicon.ico"));
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        return Results.File(
            path,
            "image/x-icon",
            lastModified: File.GetLastWriteTimeUtc(path),
            enableRangeProcessing: false);
    }

    private static IResult ServeRuffleAsset(string? fileName, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        var path = ResolvePackagedAssetPath(environment, Path.Combine("Assets", "Ruffle", fileName));
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        return Results.File(
            path,
            GetRuffleContentType(fileName),
            lastModified: File.GetLastWriteTimeUtc(path),
            enableRangeProcessing: true);
    }

    private static string ResolvePackagedAssetPath(IHostEnvironment environment, string relativePath)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(outputPath)
            ? outputPath
            : Path.Combine(environment.ContentRootPath, relativePath);
    }

    private static string GetRuffleContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".js" => "application/javascript",
            ".wasm" => "application/wasm",
            ".map" or ".json" => "application/json",
            ".md" => "text/markdown; charset=utf-8",
            _ => "text/plain; charset=utf-8",
        };

    private static IResult FrontendAsset(string path, HttpRequest request, FrontendArchiveAsset asset)
    {
        var body = FrontendUrlRewriter.RewriteIfInspectable(
            asset.Body,
            asset.ContentType,
            path,
            BuildServerBaseUrl(request));
        return Results.Bytes(body, asset.ContentType);
    }

    private static string BuildServerBaseUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    private static string BuildRufflePage(string swfUrl) =>
        RufflePageTemplate.Replace("__MGB_SWF_URL__", swfUrl, StringComparison.Ordinal);

    private const string CrossDomainPolicy =
        """
        <?xml version="1.0"?>
        <cross-domain-policy>
          <allow-access-from domain="*" />
        </cross-domain-policy>
        """;

    private const string RufflePageTemplate =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <link rel="icon" href="/favicon.ico">
          <title>MyGameBuilder Local</title>
          <style>
            html, body { height: 100%; margin: 0; background: #1e1e1e; color: #eee; font-family: Arial, sans-serif; }
            body { display: flex; align-items: center; justify-content: center; }
            main { width: min(100vw, 1366px); height: min(100vh, 768px); display: flex; align-items: center; justify-content: center; }
            ruffle-player { display: block; width: 100%; height: 100%; max-width: 1366px; max-height: 768px; }
            a { color: #8ec5ff; }
          </style>
        </head>
        <body>
          <main id="player"></main>
          <script>
            window.RufflePlayer = window.RufflePlayer || {};
            window.RufflePlayer.config = {
              autoplay: 'on',
              unmuteOverlay: 'hidden',
              splashScreen: false,
              allowScriptAccess: true,
              socketProxy: [],
              upgradeToHttps: false,
              urlRewriteRules: [
                ['https://s3.amazonaws.com/soap', '/soap'],
                ['http://s3.amazonaws.com/soap', '/soap'],
                ['http://50.18.54.95:3000/user/flexlogin', '/user/flexlogin'],
                ['http://50.18.54.95:3000/user/flexcreateuser', '/user/flexcreateuser'],
                ['http://50.18.54.95:3000/user/flexlogout', '/user/flexlogout'],
                ['http://50.18.54.95:3000/user/flex_heartbeat', '/user/flex_heartbeat'],
                ['http://50.18.54.95:3000/user/flex_heartbeat_safe', '/user/flex_heartbeat_safe'],
                ['http://50.18.54.95:3000/user/get_user_stats', '/user/get_user_stats'],
                ['http://50.18.54.95:3000/user/flex_browse_users', '/user/flex_browse_users'],
                ['http://50.18.54.95:3000/log/logbug', '/log/logbug'],
                [/^http:\/\/50\.18\.54\.95:3000\/(.*)$/i, '/$1'],
                [/^http:\/\/ec2-75-101-194-223\.compute-1\.amazonaws\.com\/(.*)$/i, '/$1'],
                [/^https?:\/\/s3\.amazonaws\.com\/apphost\/(.*)$/i, '/apphost/$1'],
              ],
            };
          </script>
          <script src="/ruffle/ruffle.js"></script>
          <script>
            const ruffle = window.RufflePlayer.newest();
            const player = ruffle.createPlayer();
            document.getElementById('player').appendChild(player);
            player.load({ url: '__MGB_SWF_URL__' });
          </script>
        </body>
        </html>
        """;

}
