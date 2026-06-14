using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class AppUpdateInstaller
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IUpdateReleaseClient _releaseClient;
    private readonly UpdateStateStore _stateStore;
    private readonly UpdatePaths _paths;
    private readonly IHostEnvironment _environment;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AppUpdateInstaller> _logger;

    public AppUpdateInstaller(
        IUpdateReleaseClient releaseClient,
        UpdateStateStore stateStore,
        UpdatePaths paths,
        IHostEnvironment environment,
        IHostApplicationLifetime lifetime,
        ILogger<AppUpdateInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(releaseClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        _releaseClient = releaseClient;
        _stateStore = stateStore;
        _paths = paths;
        _environment = environment;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task InstallAsync(UpdateRelease release, CancellationToken cancellationToken)
    {
        if (release.Manifest is not AppReleaseManifest manifest)
        {
            throw new InvalidOperationException("Selected release does not contain an app manifest.");
        }

        if (!SelfUpdateApplier.IsPublishedLayout(_environment.ContentRootPath))
        {
            throw new InvalidOperationException("App self-update is only available from a published MyGameBuilder Local release folder.");
        }

        var rid = RuntimeAssetSelector.CurrentRid();
        var asset = manifest.Assets.FirstOrDefault(asset => string.Equals(asset.Rid, rid, StringComparison.Ordinal));
        if (asset is null)
        {
            throw new InvalidOperationException($"Release {release.Tag} does not include an app asset for {rid}.");
        }

        ValidatePlainFileName(asset.Name);
        if (!release.Assets.TryGetValue(asset.Name, out var githubAsset))
        {
            throw new InvalidOperationException($"Release asset '{asset.Name}' was listed in the manifest but not present on the GitHub Release.");
        }

        var stagingDirectory = _paths.CreateOperationStagingDirectory(UpdateTarget.App);
        var packagePath = Path.Combine(stagingDirectory, asset.Name);
        var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
        Directory.CreateDirectory(extractedDirectory);

        try
        {
            _stateStore.SetWorking(UpdateTarget.App, "Downloading app update.", 10);
            var progress = new Progress<long>(bytes =>
            {
                var percent = 10 + (int)Math.Min(45, (bytes * 45) / Math.Max(1, asset.SizeBytes));
                _stateStore.SetWorking(UpdateTarget.App, "Downloading app update.", percent);
            });
            await _releaseClient.DownloadAssetAsync(githubAsset, packagePath, progress, cancellationToken).ConfigureAwait(false);
            await Checksum.EnsureSha256FileAsync(packagePath, asset.Sha256, cancellationToken).ConfigureAwait(false);

            _stateStore.SetWorking(UpdateTarget.App, "Extracting app update.", 65);
            ExtractPackage(packagePath, extractedDirectory);

            var executableName = RuntimeAssetSelector.ExecutableNameForRid(rid);
            var stagedExecutable = Path.Combine(extractedDirectory, executableName);
            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidOperationException($"Extracted update did not contain {executableName}.");
            }

            _stateStore.SetWorking(UpdateTarget.App, "Preparing safe app handoff.", 80);
            var backupDirectory = _paths.CreateBackupDirectory(UpdateTarget.App);
            var planPath = Path.Combine(stagingDirectory, "self-update-plan.json");
            var plan = new SelfUpdatePlan(
                _environment.ContentRootPath,
                extractedDirectory,
                backupDirectory,
                Environment.ProcessId,
                executableName,
                Path.Combine(backupDirectory, "self-update.log"));
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, s_jsonOptions), cancellationToken).ConfigureAwait(false);

            StartUpdater(stagedExecutable, planPath, extractedDirectory);
            _stateStore.SetWorking(UpdateTarget.App, "Update staged. MyGameBuilder Local will restart.", 95);
            _logger.LogInformation("Started app updater from {StagedExecutable}.", stagedExecutable);
            _lifetime.StopApplication();
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            _stateStore.SetError(UpdateTarget.App, exc.Message);
            throw;
        }
    }

    private static void ExtractPackage(string packagePath, string destinationDirectory)
    {
        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(packagePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        if (packagePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            packagePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.OpenRead(packagePath);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
            return;
        }

        throw new InvalidOperationException($"Unsupported app update package type: {Path.GetFileName(packagePath)}.");
    }

    private static void StartUpdater(string stagedExecutable, string planPath, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add(planPath);
        Process.Start(info);
    }

    private static void ValidatePlainFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Manifest asset name '{fileName}' must be a plain file name.");
        }
    }
}
