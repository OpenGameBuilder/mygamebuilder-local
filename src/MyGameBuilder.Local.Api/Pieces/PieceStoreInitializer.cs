namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>Forces piece-store startup work such as creating the writable data root.</summary>
public sealed class PieceStoreInitializer : IHostedService
{
    private readonly DataPieceStore _data;
    private readonly ILogger<PieceStoreInitializer> _logger;

    public PieceStoreInitializer(DataPieceStore data, ILogger<PieceStoreInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(logger);
        _data = data;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Writable piece-store data directory is ready.");
        _ = _data;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
