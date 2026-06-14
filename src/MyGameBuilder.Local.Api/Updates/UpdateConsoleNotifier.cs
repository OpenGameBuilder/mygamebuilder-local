using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdateConsoleNotifier
{
    private readonly IServer _server;
    private readonly Lock _gate = new();
    private bool _hasWritten;

    public UpdateConsoleNotifier(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    public void WriteAvailableUpdatesOnce(UpdateStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var lines = BuildLines(status);
        if (lines.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_hasWritten)
            {
                return;
            }

            _hasWritten = true;
        }

        var foreground = Console.ForegroundColor;
        try
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Updates are available:");
            Console.ForegroundColor = ConsoleColor.Gray;
            foreach (var line in lines)
            {
                Console.WriteLine("  - " + line);
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Open " + ResolveBrowseUrl() + "/updates");
            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = foreground;
        }
    }

    private static IReadOnlyList<string> BuildLines(UpdateStatusDto status)
    {
        var lines = new List<string>();
        AddLine(lines, status.FrontendArchive, "Frontend files");
        AddLine(lines, status.S3Archive, "S3 data archive");
        AddLine(lines, status.App, "App");
        return lines;
    }

    private static void AddLine(List<string> lines, UpdateTargetStatusDto status, string label)
    {
        if (!status.UpdateAvailable)
        {
            return;
        }

        var version = string.IsNullOrWhiteSpace(status.AvailableVersion)
            ? "new release"
            : status.AvailableVersion;
        var suffix = status.Target == "s3" ? " (optional large download)" : string.Empty;
        lines.Add($"{label}: {version}{suffix}");
    }

    private string ResolveBrowseUrl()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault();
        if (string.IsNullOrEmpty(address))
        {
            return "http://localhost:3000";
        }

        return address
            .Replace("://127.0.0.1", "://localhost", StringComparison.Ordinal)
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://*", "://localhost", StringComparison.Ordinal)
            .TrimEnd('/');
    }
}
