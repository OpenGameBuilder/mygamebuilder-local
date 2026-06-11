namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>Forces piece-store startup work such as validating archive and overlay databases.</summary>
public sealed class PieceStoreInitializer : IHostedService
{
    private readonly ArchivePieceStore _archive;
    private readonly DataPieceStore _data;
    private readonly ILogger<PieceStoreInitializer> _logger;

    public PieceStoreInitializer(ArchivePieceStore archive, DataPieceStore data, ILogger<PieceStoreInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(logger);
        _archive = archive;
        _data = data;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _archive.Initialize();
        _data.Initialize();
        _logger.LogInformation("SQLite piece-store archive and overlay are ready.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
