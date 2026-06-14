using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdateBackgroundService : BackgroundService
{
    private readonly UpdateCoordinator _coordinator;
    private readonly IOptions<UpdateOptions> _options;
    private readonly ILogger<UpdateBackgroundService> _logger;

    public UpdateBackgroundService(
        UpdateCoordinator coordinator,
        IOptions<UpdateOptions> options,
        ILogger<UpdateBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        if (_options.Value.CheckOnStartup)
        {
            await DelayIgnoringCancellation(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                await CheckSafelyAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.Value.CheckIntervalHours));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await CheckSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exc)
        {
            _logger.LogWarning(exc, "Background update check failed.");
        }
    }

    private static async Task DelayIgnoringCancellation(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
