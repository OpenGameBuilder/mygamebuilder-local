using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Archives;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Frontend;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class ArchiveUpdateInstaller
{
    private readonly IUpdateReleaseClient _releaseClient;
    private readonly UpdateStateStore _stateStore;
    private readonly UpdatePaths _paths;
    private readonly IOptions<PieceStoreOptions> _pieceOptions;
    private readonly IOptions<FrontendOptions> _frontendOptions;
    private readonly ArchivePieceStore _pieceArchive;
    private readonly FrontendArchiveStore _frontendArchive;
    private readonly ILogger<ArchiveUpdateInstaller> _logger;

    public ArchiveUpdateInstaller(
        IUpdateReleaseClient releaseClient,
        UpdateStateStore stateStore,
        UpdatePaths paths,
        IOptions<PieceStoreOptions> pieceOptions,
        IOptions<FrontendOptions> frontendOptions,
        ArchivePieceStore pieceArchive,
        FrontendArchiveStore frontendArchive,
        ILogger<ArchiveUpdateInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(releaseClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pieceOptions);
        ArgumentNullException.ThrowIfNull(frontendOptions);
        ArgumentNullException.ThrowIfNull(pieceArchive);
        ArgumentNullException.ThrowIfNull(frontendArchive);
        ArgumentNullException.ThrowIfNull(logger);
        _releaseClient = releaseClient;
        _stateStore = stateStore;
        _paths = paths;
        _pieceOptions = pieceOptions;
        _frontendOptions = frontendOptions;
        _pieceArchive = pieceArchive;
        _frontendArchive = frontendArchive;
        _logger = logger;
    }

    public async Task InstallAsync(UpdateTarget target, UpdateRelease release, CancellationToken cancellationToken)
    {
        if (target is not (UpdateTarget.S3Archive or UpdateTarget.FrontendArchive))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Only archive targets can be installed by this service.");
        }

        if (release.Manifest is not ArchiveReleaseManifest manifest)
        {
            throw new InvalidOperationException("Selected release does not contain an archive manifest.");
        }

        var stagingDirectory = _paths.CreateOperationStagingDirectory(target);
        var prepareDirectory = Path.Combine(stagingDirectory, "prepare");
        Directory.CreateDirectory(prepareDirectory);
        var totalBytes = Math.Max(1, manifest.Assets.Sum(static asset => Math.Max(0, asset.SizeBytes)));
        var completedBytes = 0L;

        try
        {
            _stateStore.SetWorking(target, "Downloading archive assets.", 5);
            foreach (var asset in manifest.Assets.OrderBy(static asset => asset.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePlainFileName(asset.Name);
                if (!release.Assets.TryGetValue(asset.Name, out var githubAsset))
                {
                    throw new InvalidOperationException($"Release asset '{asset.Name}' was listed in the manifest but not present on the GitHub Release.");
                }

                var destinationPath = Path.Combine(prepareDirectory, asset.Name);
                var assetStart = completedBytes;
                var progress = new InlineProgress<long>(bytes =>
                {
                    var percent = 5 + (int)Math.Min(45, ((assetStart + bytes) * 45) / totalBytes);
                    _stateStore.SetWorking(target, $"Downloading {asset.Name}.", percent);
                });
                await _releaseClient.DownloadAssetAsync(githubAsset, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                var verifyProgress = new InlineProgress<long>(bytes =>
                {
                    var percent = 50 + (int)Math.Min(5, (bytes * 5) / Math.Max(1, asset.SizeBytes));
                    _stateStore.SetWorking(target, $"Verifying {asset.Name}.", percent);
                });
                await Checksum.EnsureSha256FileAsync(destinationPath, asset.Sha256, verifyProgress, cancellationToken).ConfigureAwait(false);
                completedBytes += Math.Max(0, asset.SizeBytes);
            }

            _stateStore.SetWorking(target, "Assembling archive database.", 55);
            var stagedSqlitePath = Path.Combine(prepareDirectory, manifest.TargetFileName);
            SplitArchiveAssembler.EnsureSqliteArchiveReady(stagedSqlitePath, _logger);
            if (!string.IsNullOrWhiteSpace(manifest.SqliteSha256))
            {
                _stateStore.SetWorking(target, "Verifying archive database.", 65);
                var sqliteSize = new FileInfo(stagedSqlitePath).Length;
                var sqliteVerifyProgress = new InlineProgress<long>(bytes =>
                {
                    var percent = 65 + (int)Math.Min(5, (bytes * 5) / Math.Max(1, sqliteSize));
                    _stateStore.SetWorking(target, "Verifying archive database.", percent);
                });
                await Checksum.EnsureSha256FileAsync(stagedSqlitePath, manifest.SqliteSha256, sqliteVerifyProgress, cancellationToken).ConfigureAwait(false);
            }

            _stateStore.SetWorking(target, "Validating archive database.", 70);
            ValidateArchive(target, stagedSqlitePath);

            var targetPath = ResolveTargetPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? _paths.DataRoot);

            _stateStore.SetWorking(target, "Backing up existing archive files.", 80);
            var backupDirectory = _paths.CreateBackupDirectory(target);
            BackupExistingArchiveFiles(targetPath, backupDirectory);

            _stateStore.SetWorking(target, "Replacing archive database.", 90);
            await ReplaceLiveArchiveAsync(stagedSqlitePath, targetPath, cancellationToken).ConfigureAwait(false);

            ResetAndValidateLiveArchive(target);
            _stateStore.SetInstalled(target, release.Version, $"Installed {release.Tag}.");
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            _stateStore.SetError(target, exc.Message);
            throw;
        }
    }

    private string ResolveTargetPath(UpdateTarget target) => target switch
    {
        UpdateTarget.S3Archive => _paths.ResolveDataPath(_pieceOptions.Value.ArchivePath),
        UpdateTarget.FrontendArchive => _paths.ResolveDataPath(_frontendOptions.Value.ArchivePath),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    private static void ValidatePlainFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Manifest asset name '{fileName}' must be a plain file name.");
        }
    }

    private static void ValidateArchive(UpdateTarget target, string sqlitePath)
    {
        if (target == UpdateTarget.S3Archive)
        {
            new ArchivePieceStore(sqlitePath).Initialize();
            return;
        }

        var archive = new FrontendArchiveStore(sqlitePath, FrontendOptions.DefaultCaptureDateTime);
        if (archive.Initialize() != FrontendArchiveStatus.Ready)
        {
            throw new InvalidOperationException("Downloaded frontend archive did not validate as ready.");
        }
    }

    private void ResetAndValidateLiveArchive(UpdateTarget target)
    {
        if (target == UpdateTarget.S3Archive)
        {
            _pieceArchive.ResetSchemaCache();
            _pieceArchive.Initialize();
            return;
        }

        _frontendArchive.ResetSchemaCache();
        if (_frontendArchive.Initialize() != FrontendArchiveStatus.Ready)
        {
            throw new InvalidOperationException("Installed frontend archive did not validate as ready.");
        }
    }

    private static void BackupExistingArchiveFiles(string targetPath, string backupDirectory)
    {
        foreach (var path in EnumerateArchiveFileSet(targetPath))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var backupPath = Path.Combine(backupDirectory, Path.GetFileName(path));
            File.Copy(path, backupPath, overwrite: false);
        }
    }

    private static IEnumerable<string> EnumerateArchiveFileSet(string targetPath)
    {
        yield return targetPath;
        yield return targetPath + ".zst";

        var directory = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileName(targetPath);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(directory, fileName + ".part-*", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in Directory.EnumerateFiles(directory, fileName + ".zst.part-*", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }
    }

    private static async Task ReplaceLiveArchiveAsync(string stagedSqlitePath, string targetPath, CancellationToken cancellationToken)
    {
        var replacementPath = targetPath + ".updating";
        File.Delete(replacementPath);
        File.Copy(stagedSqlitePath, replacementPath);

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    SqliteConnection.ClearAllPools();
                    File.Move(replacementPath, targetPath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(replacementPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the live archive has not been replaced if this remains.
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
