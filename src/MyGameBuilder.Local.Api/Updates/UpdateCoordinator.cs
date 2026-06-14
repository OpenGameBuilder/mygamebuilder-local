using System.Reflection;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdateCoordinator
{
    private readonly IUpdateReleaseClient _releaseClient;
    private readonly UpdateStateStore _stateStore;
    private readonly ArchiveUpdateInstaller _archiveInstaller;
    private readonly AppUpdateInstaller _appInstaller;
    private readonly UpdatePaths _paths;
    private readonly IOptions<UpdateOptions> _updateOptions;
    private readonly IOptions<PieceStoreOptions> _pieceOptions;
    private readonly IOptions<FrontendOptions> _frontendOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<UpdateCoordinator> _logger;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public UpdateCoordinator(
        IUpdateReleaseClient releaseClient,
        UpdateStateStore stateStore,
        ArchiveUpdateInstaller archiveInstaller,
        AppUpdateInstaller appInstaller,
        UpdatePaths paths,
        IOptions<UpdateOptions> updateOptions,
        IOptions<PieceStoreOptions> pieceOptions,
        IOptions<FrontendOptions> frontendOptions,
        IHostEnvironment environment,
        ILogger<UpdateCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(releaseClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(archiveInstaller);
        ArgumentNullException.ThrowIfNull(appInstaller);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(updateOptions);
        ArgumentNullException.ThrowIfNull(pieceOptions);
        ArgumentNullException.ThrowIfNull(frontendOptions);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _releaseClient = releaseClient;
        _stateStore = stateStore;
        _archiveInstaller = archiveInstaller;
        _appInstaller = appInstaller;
        _paths = paths;
        _updateOptions = updateOptions;
        _pieceOptions = pieceOptions;
        _frontendOptions = frontendOptions;
        _environment = environment;
        _logger = logger;
    }

    public UpdateStatusDto GetStatus()
    {
        var state = _stateStore.Snapshot();
        var frontendPath = _paths.ResolveDataPath(_frontendOptions.Value.ArchivePath);
        var s3Path = _paths.ResolveDataPath(_pieceOptions.Value.ArchivePath);
        var appInstalled = CurrentAppVersion();
        var s3Installed = ArchiveVersionReader.ReadReleaseVersion(s3Path);
        var frontendInstalled = ArchiveVersionReader.ReadReleaseVersion(frontendPath);
        var appPublishedLayout = SelfUpdateApplier.IsPublishedLayout(_environment.ContentRootPath);
        var appUpdateAvailable = IsUpdateAvailable(state.App.AvailableVersion, appInstalled, missing: false);
        var appCanInstall = appPublishedLayout;
        string? appMessageOverride = null;
        if (appUpdateAvailable && !appPublishedLayout)
        {
            appCanInstall = false;
            appMessageOverride = "App self-update is only available from a published MyGameBuilder Local release folder.";
        }
        else if (appUpdateAvailable && !SelfUpdateApplier.CanWriteInstallDirectory(_environment.ContentRootPath))
        {
            appCanInstall = false;
            appMessageOverride = SelfUpdateApplier.BuildManualUpdateInstructions(ParseReleaseUrl(state.App.ReleaseUrl));
        }

        return new UpdateStatusDto(
            _updateOptions.Value.Enabled,
            DateTimeOffset.UtcNow,
            !File.Exists(frontendPath) || !File.Exists(s3Path),
            BuildTargetStatus(
                UpdateTarget.App,
                appInstalled,
                missing: false,
                canInstall: appCanInstall,
                state.App,
                messageOverride: appMessageOverride),
            BuildTargetStatus(
                UpdateTarget.S3Archive,
                s3Installed,
                missing: !File.Exists(s3Path),
                canInstall: true,
                state.S3Archive),
            BuildTargetStatus(
                UpdateTarget.FrontendArchive,
                frontendInstalled,
                missing: !File.Exists(frontendPath),
                canInstall: true,
                state.FrontendArchive));
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_updateOptions.Value.Enabled)
        {
            return;
        }

        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var target in new[] { UpdateTarget.App, UpdateTarget.S3Archive, UpdateTarget.FrontendArchive })
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CheckTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public async Task InstallAppUpdateAsync(CancellationToken cancellationToken)
    {
        if (!_updateOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Updates are disabled.");
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var release = await _releaseClient.GetLatestReleaseAsync(UpdateTarget.App, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No app update release is available.");
            await _appInstaller.InstallAsync(release, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async Task InstallArchiveUpdateAsync(UpdateTarget target, CancellationToken cancellationToken)
    {
        if (target is not (UpdateTarget.S3Archive or UpdateTarget.FrontendArchive))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }

        if (!_updateOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Updates are disabled.");
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var release = await _releaseClient.GetLatestReleaseAsync(target, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No {UpdateTargets.ToId(target)} archive update release is available.");
            await _archiveInstaller.InstallAsync(target, release, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task CheckTargetAsync(UpdateTarget target, CancellationToken cancellationToken)
    {
        try
        {
            _stateStore.SetWorking(target, "Checking GitHub Releases.");
            var release = await _releaseClient.GetLatestReleaseAsync(target, cancellationToken).ConfigureAwait(false);
            _stateStore.Mutate(state =>
            {
                var item = state.For(target);
                item.LastCheckedUtc = DateTimeOffset.UtcNow;
                item.State = "idle";
                item.ProgressPercent = 0;
                if (release is null)
                {
                    item.AvailableVersion = null;
                    item.ReleaseName = null;
                    item.ReleaseUrl = null;
                    item.DownloadSizeBytes = null;
                    item.Message = "No release is available yet.";
                    return;
                }

                item.AvailableVersion = release.Version;
                item.ReleaseName = release.Name;
                item.ReleaseUrl = release.HtmlUrl.ToString();
                item.DownloadSizeBytes = release.DownloadSizeBytes;
                item.Message = $"Latest release is {release.Tag}.";
            });
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            _logger.LogWarning(exc, "Update check failed for {Target}.", target);
            _stateStore.SetError(target, exc.Message);
        }
    }

    private static UpdateTargetStatusDto BuildTargetStatus(
        UpdateTarget target,
        string? installedVersion,
        bool missing,
        bool canInstall,
        UpdateTargetState state,
        string? messageOverride = null)
    {
        var updateAvailable = IsUpdateAvailable(state.AvailableVersion, installedVersion, missing);
        var effectiveCanInstall = canInstall && updateAvailable && state.State != "working";
        var message = messageOverride ?? state.Message;
        if (messageOverride is null &&
            state.State is not ("working" or "error") &&
            !missing &&
            !updateAvailable &&
            !string.IsNullOrWhiteSpace(state.AvailableVersion))
        {
            message = "Up to date.";
        }

        return new UpdateTargetStatusDto(
            UpdateTargets.ToId(target),
            installedVersion,
            state.AvailableVersion,
            missing,
            updateAvailable,
            effectiveCanInstall,
            state.ReleaseName,
            state.ReleaseUrl,
            state.DownloadSizeBytes,
            state.State,
            state.ProgressPercent,
            message,
            state.LastCheckedUtc);
    }

    private static bool IsUpdateAvailable(string? availableVersion, string? installedVersion, bool missing) =>
        !string.IsNullOrWhiteSpace(availableVersion) &&
        (missing || IsNewer(availableVersion, installedVersion));

    private static bool IsNewer(string? availableVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(availableVersion))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return true;
        }

        return UpdateReleaseSelector.TryParsePrefixedVersion("v" + availableVersion, "v", out var available, out _) &&
            UpdateReleaseSelector.TryParsePrefixedVersion("v" + installedVersion, "v", out var installed, out _) &&
            (available.Major, available.Minor, available.Patch).CompareTo((installed.Major, installed.Minor, installed.Patch)) > 0;
    }

    private static Uri? ParseReleaseUrl(string? releaseUrl) =>
        Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri) ? uri : null;

    private static string? CurrentAppVersion()
    {
        var version = typeof(ArchivePieceStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
