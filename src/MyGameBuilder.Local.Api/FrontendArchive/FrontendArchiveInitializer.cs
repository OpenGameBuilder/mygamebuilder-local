using MyGameBuilder.Local.Api.Archives;

namespace MyGameBuilder.Local.Api.Frontend;

/// <summary>Forces frontend archive startup validation.</summary>
public sealed class FrontendArchiveInitializer : IHostedService
{
    private readonly FrontendArchiveStore _archive;
    private readonly ILogger<FrontendArchiveInitializer> _logger;

    public FrontendArchiveInitializer(FrontendArchiveStore archive, ILogger<FrontendArchiveInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(logger);
        _archive = archive;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            SplitArchiveAssembler.EnsureSqliteArchiveReady(_archive.ArchivePath, _logger);
            if (_archive.Initialize() == FrontendArchiveStatus.Missing)
            {
                _logger.LogWarning(
                    "Frontend archive was not found at '{ArchivePath}'. Setup instructions will be shown in the browser.",
                    _archive.ArchivePath);
            }
            else
            {
                _logger.LogInformation("SQLite frontend archive is ready.");
            }
        }
        catch (InvalidOperationException exc)
        {
            WriteFatalStartupError(exc.Message);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void WriteFatalStartupError(string message)
    {
        var foreground = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("************************************************************");
            Console.WriteLine("  MYGAMEBUILDER LOCAL COULD NOT START");
            Console.WriteLine("************************************************************");
            Console.WriteLine("  " + message);
            Console.WriteLine("************************************************************");
            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = foreground;
        }
    }
}
