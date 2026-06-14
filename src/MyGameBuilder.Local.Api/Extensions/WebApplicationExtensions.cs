using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Extensions;

/// <summary>
/// Front-end hosting helpers and a startup banner with the navigable URL. Mirrors the old
/// Python server by exposing the client and backend from one origin.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>Request path the archived front-end is mounted under (matches the client's <c>apphost/</c> URLs).</summary>
    public const string FrontendRequestPath = "/apphost";

    /// <summary>Well-known route of the Ruffle launcher page (the old client used <c>/play</c>).</summary>
    public const string PlayPath = FrontendRequestPath + "/MGB.html";

    /// <summary>
    /// Prints a friendly console banner with the main URL once the server is listening.
    /// </summary>
    public static WebApplication LogStartupBanner(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var pieces = app.Services.GetRequiredService<IOptions<PieceStoreOptions>>().Value;
            var frontend = app.Services.GetRequiredService<IOptions<FrontendOptions>>().Value;
            var contentRoot = app.Environment.ContentRootPath;

            var archivePath = ResolveContentPath(contentRoot, pieces.ArchivePath);
            var overlayPath = ResolveContentPath(contentRoot, pieces.OverlayPath);
            var frontendArchivePath = ResolveContentPath(contentRoot, frontend.ArchivePath);
            var baseUrl = ResolveBrowseUrl(app);
            var frontendCutoffTimestamp = FrontendOptions.ToWaybackTimestamp(frontend.CaptureDateTime);
            WriteStartupBanner(
                baseUrl,
                missingPieceArchive: !File.Exists(archivePath),
                missingFrontendArchive: !File.Exists(frontendArchivePath),
                configuredFrontendDateTime: frontend.CaptureDateTime,
                frontendDateIsBeforeDefault: string.CompareOrdinal(
                    frontendCutoffTimestamp,
                    FrontendOptions.ToWaybackTimestamp(FrontendOptions.DefaultCaptureDateTime)) < 0,
                overlayPath: overlayPath,
                overlayExists: File.Exists(overlayPath));
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

    private static void WriteStartupBanner(
        string baseUrl,
        bool missingPieceArchive,
        bool missingFrontendArchive,
        string configuredFrontendDateTime,
        bool frontendDateIsBeforeDefault,
        string overlayPath,
        bool overlayExists)
    {
        var foreground = Console.ForegroundColor;
        try
        {
            if (!Console.IsOutputRedirected)
            {
                WriteStartupAnimation();
            }

            Console.WriteLine();
            WriteCentered("============================================================", ConsoleColor.DarkCyan);
            WriteCentered("MYGAMEBUILDER LOCAL IS READY", ConsoleColor.Cyan);
            WriteCentered("Open this link in your browser:", ConsoleColor.Gray);
            WriteCentered(baseUrl + "/", ConsoleColor.Yellow);
            WriteCentered($"Frontend archive date: {configuredFrontendDateTime}", ConsoleColor.Gray);
            WriteCentered(
                missingPieceArchive
                    ? "Log in as guest with any password."
                    : "Log in as any user with any password.",
                ConsoleColor.Gray);
            WriteCentered("Press Ctrl+C when you are done.", ConsoleColor.DarkGray);
            WriteCentered("============================================================", ConsoleColor.DarkCyan);

            if (missingPieceArchive)
            {
                Console.WriteLine();
                WriteCentered("No saved user archive was found yet; guest mode is available.", ConsoleColor.DarkGray);
            }

            if (missingFrontendArchive)
            {
                Console.WriteLine();
                WriteCentered("frontend.sqlite was not found; setup instructions will appear in the browser.", ConsoleColor.Yellow);
            }

            if (frontendDateIsBeforeDefault)
            {
                Console.WriteLine();
                WriteCentered("WARNING: This frontend archive date is older than May 3, 2017.", ConsoleColor.Yellow);
                WriteCentered("Older client versions may not be compatible with the data.", ConsoleColor.Yellow);

                if (overlayExists)
                {
                    WriteCentered("Data loss could occur. Back up overlay.sqlite before continuing.", ConsoleColor.Yellow);
                    WriteCentered($"Overlay database: {overlayPath}", ConsoleColor.DarkYellow);
                }
            }

            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = foreground;
        }
    }

    private static void WriteStartupAnimation()
    {
        var frames = new[] { "Starting MyGameBuilder Local", "Starting MyGameBuilder Local.", "Starting MyGameBuilder Local..", "Starting MyGameBuilder Local..." };
        foreach (var frame in frames)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write('\r');
            Console.Write(Center(frame));
            Thread.Sleep(90);
        }

        Console.Write('\r');
        Console.Write(new string(' ', Math.Max(0, ConsoleWidth() - 1)));
        Console.Write('\r');
    }

    private static void WriteCentered(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(Center(text));
    }

    private static string Center(string text)
    {
        var width = ConsoleWidth();
        if (text.Length >= width)
        {
            return text;
        }

        return new string(' ', (width - text.Length) / 2) + text;
    }

    private static int ConsoleWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth, 40, 120);
        }
        catch (IOException)
        {
            return 80;
        }
    }
}
