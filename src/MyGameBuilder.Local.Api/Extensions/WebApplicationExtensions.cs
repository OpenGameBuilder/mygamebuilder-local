using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Extensions;

/// <summary>
/// Front-end hosting middleware: serves the legacy Flash client's static assets and prints a
/// startup banner with the navigable URL. Mirrors the old Python server, which served the
/// client bundle and a Ruffle launcher page from the same origin as the backend.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>Request path the front-end directory is mounted under (matches the client's <c>apphost/</c> URLs).</summary>
    public const string FrontendRequestPath = "/apphost";

    /// <summary>Well-known route of the Ruffle launcher page (the old client used <c>/play</c>).</summary>
    public const string PlayPath = FrontendRequestPath + "/MGB.html";

    /// <summary>
    /// Mounts the configured front-end directory under <see cref="FrontendRequestPath"/> so the
    /// client's rewritten <c>apphost/...</c> URLs resolve to local files. The directory is created
    /// if missing so it is a known drop location and the file provider never throws.
    /// </summary>
    public static WebApplication UseFrontend(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<FrontendOptions>>().Value;
        var root = ResolveContentPath(app.Environment.ContentRootPath, options.RootPath);

        // Create the drop directory up front: PhysicalFileProvider throws on a missing root,
        // and a known empty folder is friendlier than a hard startup failure when the (optional)
        // client bundle has not been placed yet.
        Directory.CreateDirectory(root);

        // .swf is served with the Flash MIME type; ServeUnknownFileTypes keeps arbitrary assets the
        // client may request (audio, images, data) serveable, matching the permissive Python server.
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".swf"] = "application/x-shockwave-flash";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = FrontendRequestPath,
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
        });

        return app;
    }

    /// <summary>
    /// Prints a console banner with the navigable URLs and resolved data directories once the
    /// server is listening. Written straight to the console so the URL stands out from the logs.
    /// </summary>
    public static WebApplication LogStartupBanner(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var frontend = app.Services.GetRequiredService<IOptions<FrontendOptions>>().Value;
            var pieces = app.Services.GetRequiredService<IOptions<PieceStoreOptions>>().Value;
            var contentRoot = app.Environment.ContentRootPath;

            var frontendRoot = ResolveContentPath(contentRoot, frontend.RootPath);
            var archiveRoot = ResolveContentPath(contentRoot, pieces.ArchiveRoot);
            var dataRoot = ResolveContentPath(contentRoot, pieces.DataRoot);
            var baseUrl = ResolveBrowseUrl(app);
            var archiveNote = Directory.Exists(archiveRoot) ? string.Empty : "  (optional - not present yet)";

            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine("  MyGameBuilder Local  -  backend + Flash front-end (Ruffle)");
            Console.WriteLine("------------------------------------------------------------------");
            Console.WriteLine($"  Landing page :  {baseUrl}/");
            Console.WriteLine($"  Play (Ruffle):  {baseUrl}{PlayPath}");
            Console.WriteLine($"  Health check :  {baseUrl}/healthz");
            Console.WriteLine("------------------------------------------------------------------");
            Console.WriteLine($"  Front-end dir:  {frontendRoot}");
            Console.WriteLine($"                  drop {frontend.SwfName} (and its assets) here");
            Console.WriteLine($"  Archive dir  :  {archiveRoot}{archiveNote}");
            Console.WriteLine($"  Data dir     :  {dataRoot}");
            Console.WriteLine("==================================================================");
            Console.WriteLine();
        });

        return app;
    }

    /// <summary>Resolves a configured path against the content root (absolute paths pass through).</summary>
    internal static string ResolveContentPath(string contentRoot, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return contentRoot;
        }

        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    private static string ResolveBrowseUrl(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault();
        if (string.IsNullOrEmpty(address))
        {
            return "http://localhost:3000";
        }

        // Replace bind-all placeholders with a host the browser can actually reach.
        return address
            .Replace("://127.0.0.1", "://localhost", StringComparison.Ordinal)
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://*", "://localhost", StringComparison.Ordinal)
            .TrimEnd('/');
    }
}
