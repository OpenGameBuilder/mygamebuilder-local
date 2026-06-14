using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Frontend;
using MyGameBuilder.Local.Api.Pieces;
using MyGameBuilder.Local.Api.Updates;
using ZstdSharp;

namespace MyGameBuilder.Local.Api.Tests;

public sealed class UpdateTests
{
    [Fact]
    public void ReleaseSelector_OrdersPrefixedSemverTagsNewestFirst()
    {
        var ordered = UpdateReleaseSelector.OrderByLatest(
            ["s3-v0.1.0", "s3-v0.3.0", "v9.9.9", "s3-v0.2.1", "s3-v0.2.0-beta"],
            static tag => tag,
            "s3-v");

        Assert.Equal(["s3-v0.3.0", "s3-v0.2.1", "s3-v0.1.0"], ordered);
    }

    [Fact]
    public void ReleaseSelector_OrdersSuffixedSemverTagsNewestFirst()
    {
        var ordered = UpdateReleaseSelector.OrderByLatest(
            ["v0.1.0-s3", "v0.3.0-s3", "v9.9.9", "v0.2.1-s3", "v0.2.0-client"],
            static tag => tag,
            "v",
            "-s3");

        Assert.Equal(["v0.3.0-s3", "v0.2.1-s3", "v0.1.0-s3"], ordered);
    }

    [Fact]
    public async Task GitHubReleaseClient_UsesArchiveSuffixTagsAndAssetDigestFallback()
    {
        const string releaseJson =
            """
            [
              {
                "tag_name": "v1.0.0-client",
                "name": "Frontend Archive v1.0.0",
                "html_url": "https://github.com/OpenGameBuilder/mygamebuilder-archive/releases/tag/v1.0.0-client",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "frontend.sqlite",
                    "browser_download_url": "https://example.test/frontend.sqlite",
                    "size": 10,
                    "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111"
                  }
                ]
              },
              {
                "tag_name": "v1.1.0-client",
                "name": "Frontend Archive v1.1.0",
                "html_url": "https://github.com/OpenGameBuilder/mygamebuilder-archive/releases/tag/v1.1.0-client",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "frontend.sqlite",
                    "browser_download_url": "https://example.test/frontend-v1.1.0.sqlite",
                    "size": 20,
                    "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222"
                  }
                ]
              },
              {
                "tag_name": "v1.0.0-s3",
                "name": "S3 Data Archive v1.0.0",
                "html_url": "https://github.com/OpenGameBuilder/mygamebuilder-archive/releases/tag/v1.0.0-s3",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "name": "archive.sqlite.zst.part-001",
                    "browser_download_url": "https://example.test/archive.sqlite.zst.part-001",
                    "size": 12,
                    "digest": "sha256:4444444444444444444444444444444444444444444444444444444444444444"
                  },
                  {
                    "name": "archive.sqlite.zst.part-000",
                    "browser_download_url": "https://example.test/archive.sqlite.zst.part-000",
                    "size": 11,
                    "digest": "sha256:3333333333333333333333333333333333333333333333333333333333333333"
                  }
                ]
              }
            ]
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(releaseJson));
        var client = new GitHubUpdateReleaseClient(
            httpClient,
            Options.Create(new UpdateOptions()),
            NullLogger<GitHubUpdateReleaseClient>.Instance);

        var frontendRelease = await client.GetLatestReleaseAsync(UpdateTarget.FrontendArchive, CancellationToken.None);
        var s3Release = await client.GetLatestReleaseAsync(UpdateTarget.S3Archive, CancellationToken.None);

        Assert.NotNull(frontendRelease);
        Assert.Equal("v1.1.0-client", frontendRelease.Tag);
        Assert.Equal("1.1.0", frontendRelease.Version);
        var frontendManifest = Assert.IsType<ArchiveReleaseManifest>(frontendRelease.Manifest);
        Assert.Equal("frontend", frontendManifest.Kind);
        Assert.Equal("frontend.sqlite", frontendManifest.TargetFileName);
        Assert.Equal("2222222222222222222222222222222222222222222222222222222222222222", frontendManifest.SqliteSha256);
        Assert.Equal(20, frontendManifest.SqliteSizeBytes);

        Assert.NotNull(s3Release);
        Assert.Equal("v1.0.0-s3", s3Release.Tag);
        Assert.Equal("1.0.0", s3Release.Version);
        var s3Manifest = Assert.IsType<ArchiveReleaseManifest>(s3Release.Manifest);
        Assert.Equal("s3", s3Manifest.Kind);
        Assert.Equal("archive.sqlite", s3Manifest.TargetFileName);
        Assert.Equal("", s3Manifest.SqliteSha256);
        Assert.Equal(["archive.sqlite.zst.part-000", "archive.sqlite.zst.part-001"], s3Manifest.Assets.Select(static asset => asset.Name));
        Assert.Equal([0, 1], s3Manifest.Assets.Select(static asset => asset.Order));
    }

    [Fact]
    public async Task GitHubReleaseClient_DownloadAssetAsync_ReplacesDestinationAfterClosingDownloadFile()
    {
        var bytes = Encoding.UTF8.GetBytes("updated frontend archive bytes");
        using var current = new TempArchive(createArchive: false);
        var destinationPath = Path.Combine(current.Root, "frontend.sqlite");
        await File.WriteAllTextAsync(destinationPath, "old");
        var progressValues = new List<long>();

        using var httpClient = new HttpClient(new StaticBytesHandler(bytes));
        var client = new GitHubUpdateReleaseClient(
            httpClient,
            Options.Create(new UpdateOptions()),
            NullLogger<GitHubUpdateReleaseClient>.Instance);

        await client.DownloadAssetAsync(
            new GithubReleaseAsset("frontend.sqlite", new Uri("https://example.test/frontend.sqlite"), bytes.Length),
            destinationPath,
            new InlineProgress<long>(progressValues.Add),
            CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destinationPath));
        Assert.False(File.Exists(destinationPath + ".download"));
        Assert.Contains(bytes.Length, progressValues);
    }

    [Theory]
    [InlineData("overlay.sqlite")]
    [InlineData("archive.sqlite")]
    [InlineData("archive.sqlite.zst.part-000")]
    [InlineData("frontend.sqlite")]
    [InlineData("frontend.sqlite.zst.part-000")]
    [InlineData("appsettings.Local.json")]
    [InlineData(".mygamebuilder-backups/s3/archive.sqlite")]
    [InlineData(".mygamebuilder-updates/state.json")]
    public void SelfUpdatePreserveList_ProtectsUserAndArchiveData(string relativePath)
    {
        Assert.True(SelfUpdateApplier.ShouldPreserve(relativePath));
    }

    [Fact]
    public async Task UpdateStatus_WhenFrontendArchiveMissing_ReportsFirstRunSetup()
    {
        using var pieces = new TempArchive();
        var missingFrontendArchive = Path.Combine(pieces.Root, "missing-frontend.sqlite");
        using var factory = new BackendFactory(pieces, missingFrontendArchive);
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<UpdateStatusDto>("/_updates/status");

        Assert.NotNull(status);
        Assert.True(status.FirstRunSetupNeeded);
        Assert.True(status.FrontendArchive.Missing);
    }

    [Fact]
    public void UpdateStatus_ResolvesRelativeArchivePathsUnderDataRoot()
    {
        using var current = new TempArchive();
        var contentRoot = Path.Combine(current.Root, "install");
        Directory.CreateDirectory(contentRoot);
        var frontendPath = Path.Combine(current.Root, "frontend.sqlite");
        TempFrontendArchive.CreateArchive(frontendPath);

        var paths = new ApplicationPathRoots(contentRoot, current.Root);
        var updateOptions = Options.Create(new UpdateOptions());
        var pieceOptions = Options.Create(new PieceStoreOptions
        {
            ArchivePath = "archive.sqlite",
            OverlayPath = "overlay.sqlite",
        });
        var frontendOptions = Options.Create(new FrontendOptions
        {
            ArchivePath = "frontend.sqlite",
        });
        var updatePaths = new UpdatePaths(paths, updateOptions);
        var stateStore = new UpdateStateStore(updatePaths);
        var fakeClient = new FakeReleaseClient();
        var environment = new TestEnvironment(contentRoot);

        var archiveInstaller = new ArchiveUpdateInstaller(
            fakeClient,
            stateStore,
            updatePaths,
            pieceOptions,
            frontendOptions,
            new ArchivePieceStore(current.ArchivePath),
            new FrontendArchiveStore(frontendPath, FrontendOptions.DefaultCaptureDateTime),
            NullLogger<ArchiveUpdateInstaller>.Instance);
        var appInstaller = new AppUpdateInstaller(
            fakeClient,
            stateStore,
            updatePaths,
            environment,
            new TestApplicationLifetime(),
            NullLogger<AppUpdateInstaller>.Instance);
        var coordinator = new UpdateCoordinator(
            fakeClient,
            stateStore,
            archiveInstaller,
            appInstaller,
            updatePaths,
            updateOptions,
            pieceOptions,
            frontendOptions,
            environment,
            NullLogger<UpdateCoordinator>.Instance);

        var status = coordinator.GetStatus();

        Assert.False(status.FirstRunSetupNeeded);
        Assert.False(status.S3Archive.Missing);
        Assert.False(status.FrontendArchive.Missing);
    }

    [Fact]
    public void SelfUpdateWritableProbe_ReturnsTrueForWritableDirectory()
    {
        using var current = new TempArchive();

        Assert.True(SelfUpdateApplier.CanWriteInstallDirectory(current.Root));
        Assert.Empty(Directory.EnumerateFiles(current.Root, ".mgb-write-test-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SelfUpdateWritableProbe_ReturnsFalseForMissingDirectory()
    {
        using var current = new TempArchive();

        Assert.False(SelfUpdateApplier.CanWriteInstallDirectory(Path.Combine(current.Root, "missing")));
    }

    [Fact]
    public async Task UpdateStatus_WhenAppUpdateAvailableOutsidePublishedLayout_DisablesInstallWithMessage()
    {
        using var current = new TempArchive();
        var contentRoot = Path.Combine(current.Root, "install");
        Directory.CreateDirectory(contentRoot);
        var frontendPath = Path.Combine(current.Root, "frontend.sqlite");
        TempFrontendArchive.CreateArchive(frontendPath);

        var paths = new ApplicationPathRoots(contentRoot, current.Root);
        var updateOptions = Options.Create(new UpdateOptions());
        var pieceOptions = Options.Create(new PieceStoreOptions
        {
            ArchivePath = "archive.sqlite",
            OverlayPath = "overlay.sqlite",
        });
        var frontendOptions = Options.Create(new FrontendOptions
        {
            ArchivePath = "frontend.sqlite",
        });
        var updatePaths = new UpdatePaths(paths, updateOptions);
        var stateStore = new UpdateStateStore(updatePaths);
        var fakeClient = new FakeReleaseClient();
        fakeClient.Releases[UpdateTarget.App] = CreateAppRelease("99.0.0");
        var environment = new TestEnvironment(contentRoot);

        var archiveInstaller = new ArchiveUpdateInstaller(
            fakeClient,
            stateStore,
            updatePaths,
            pieceOptions,
            frontendOptions,
            new ArchivePieceStore(current.ArchivePath),
            new FrontendArchiveStore(frontendPath, FrontendOptions.DefaultCaptureDateTime),
            NullLogger<ArchiveUpdateInstaller>.Instance);
        var appInstaller = new AppUpdateInstaller(
            fakeClient,
            stateStore,
            updatePaths,
            environment,
            new TestApplicationLifetime(),
            NullLogger<AppUpdateInstaller>.Instance);
        var coordinator = new UpdateCoordinator(
            fakeClient,
            stateStore,
            archiveInstaller,
            appInstaller,
            updatePaths,
            updateOptions,
            pieceOptions,
            frontendOptions,
            environment,
            NullLogger<UpdateCoordinator>.Instance);

        await coordinator.CheckForUpdatesAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.True(status.App.UpdateAvailable);
        Assert.False(status.App.CanInstall);
        Assert.Contains("published MyGameBuilder Local release folder", status.App.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateStatus_WhenArchiveVersionMatchesLatest_DisablesInstallWithUpToDateMessage()
    {
        using var current = new TempArchive();
        SetArchiveInfo(current.ArchivePath, "release_version", "1.0.0");
        var contentRoot = Path.Combine(current.Root, "install");
        Directory.CreateDirectory(contentRoot);
        var frontendPath = Path.Combine(current.Root, "frontend.sqlite");
        TempFrontendArchive.CreateArchive(frontendPath);

        var paths = new ApplicationPathRoots(contentRoot, current.Root);
        var updateOptions = Options.Create(new UpdateOptions());
        var pieceOptions = Options.Create(new PieceStoreOptions
        {
            ArchivePath = "archive.sqlite",
            OverlayPath = "overlay.sqlite",
        });
        var frontendOptions = Options.Create(new FrontendOptions
        {
            ArchivePath = "frontend.sqlite",
        });
        var updatePaths = new UpdatePaths(paths, updateOptions);
        var stateStore = new UpdateStateStore(updatePaths);
        var fakeClient = new FakeReleaseClient();
        fakeClient.Releases[UpdateTarget.S3Archive] = CreateArchiveRelease(UpdateTarget.S3Archive, "1.0.0");
        var environment = new TestEnvironment(contentRoot);

        var archiveInstaller = new ArchiveUpdateInstaller(
            fakeClient,
            stateStore,
            updatePaths,
            pieceOptions,
            frontendOptions,
            new ArchivePieceStore(current.ArchivePath),
            new FrontendArchiveStore(frontendPath, FrontendOptions.DefaultCaptureDateTime),
            NullLogger<ArchiveUpdateInstaller>.Instance);
        var appInstaller = new AppUpdateInstaller(
            fakeClient,
            stateStore,
            updatePaths,
            environment,
            new TestApplicationLifetime(),
            NullLogger<AppUpdateInstaller>.Instance);
        var coordinator = new UpdateCoordinator(
            fakeClient,
            stateStore,
            archiveInstaller,
            appInstaller,
            updatePaths,
            updateOptions,
            pieceOptions,
            frontendOptions,
            environment,
            NullLogger<UpdateCoordinator>.Instance);

        await coordinator.CheckForUpdatesAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.False(status.S3Archive.UpdateAvailable);
        Assert.False(status.S3Archive.CanInstall);
        Assert.Equal("Up to date.", status.S3Archive.Message);
    }

    [Fact]
    public async Task UpdatesPage_ServesUpdateChecker()
    {
        using var pieces = new TempArchive();
        using var factory = new BackendFactory(pieces);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/updates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MyGameBuilder Local updates", body);
        Assert.Contains("/_updates/status", body);
    }

    [Fact]
    public async Task UpdatePost_WithoutToken_IsForbidden()
    {
        using var pieces = new TempArchive();
        using var factory = new BackendFactory(pieces);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/_updates/check", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task S3ArchiveInstall_VerifiesBacksUpReplacesAndPreservesOverlay()
    {
        using var current = new TempArchive();
        current.AddObject("old/p/tile/A", [1]);
        File.WriteAllText(current.OverlayPath, "local user data");

        using var replacement = new TempArchive();
        replacement.AddObject("new/p/tile/B", [2]);
        SetArchiveInfo(replacement.ArchivePath, "release_version", "1.0.0");

        var installer = CreateArchiveInstaller(current, out var fakeClient);
        var release = AddCompressedArchiveAsset(fakeClient, replacement.ArchivePath, "archive.sqlite.zst.part-000");

        await installer.InstallAsync(UpdateTarget.S3Archive, release, CancellationToken.None);

        Assert.Equal("local user data", File.ReadAllText(current.OverlayPath));
        Assert.Equal("1.0.0", ReadArchiveInfo(current.ArchivePath, "release_version"));
        Assert.Equal(1, CountObjects(current.ArchivePath, "new/p/tile/B"));
        Assert.Equal(0, CountObjects(current.ArchivePath, "old/p/tile/A"));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(current.Root, "backups", "s3"), "archive.sqlite", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task S3ArchiveInstall_WhenChecksumFails_LeavesLiveFilesUntouched()
    {
        using var current = new TempArchive();
        current.AddObject("old/p/tile/A", [1]);
        File.WriteAllText(current.OverlayPath, "local user data");

        using var replacement = new TempArchive();
        replacement.AddObject("new/p/tile/B", [2]);
        SetArchiveInfo(replacement.ArchivePath, "release_version", "1.0.0");

        var installer = CreateArchiveInstaller(current, out var fakeClient);
        var release = AddCompressedArchiveAsset(fakeClient, replacement.ArchivePath, "archive.sqlite.zst.part-000", overrideSha256: new string('0', 64));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(UpdateTarget.S3Archive, release, CancellationToken.None));

        Assert.Equal("local user data", File.ReadAllText(current.OverlayPath));
        Assert.Null(ReadArchiveInfo(current.ArchivePath, "release_version"));
        Assert.Equal(1, CountObjects(current.ArchivePath, "old/p/tile/A"));
        Assert.Equal(0, CountObjects(current.ArchivePath, "new/p/tile/B"));
    }

    private static ArchiveUpdateInstaller CreateArchiveInstaller(
        TempArchive current,
        out FakeReleaseClient fakeClient)
    {
        var contentRoot = Path.Combine(current.Root, "install");
        Directory.CreateDirectory(contentRoot);
        var paths = new ApplicationPathRoots(contentRoot, current.Root);
        var updateOptions = Options.Create(new UpdateOptions());
        var pieceOptions = Options.Create(new PieceStoreOptions
        {
            ArchivePath = "archive.sqlite",
            OverlayPath = "overlay.sqlite",
        });
        var frontendPath = Path.Combine(current.Root, "frontend.sqlite");
        TempFrontendArchive.CreateArchive(frontendPath);
        var frontendOptions = Options.Create(new FrontendOptions
        {
            ArchivePath = "frontend.sqlite",
        });

        fakeClient = new FakeReleaseClient();
        var updatePaths = new UpdatePaths(paths, updateOptions);
        return new ArchiveUpdateInstaller(
            fakeClient,
            new UpdateStateStore(updatePaths),
            updatePaths,
            pieceOptions,
            frontendOptions,
            new ArchivePieceStore(current.ArchivePath),
            new FrontendArchiveStore(frontendPath, FrontendOptions.DefaultCaptureDateTime),
            NullLogger<ArchiveUpdateInstaller>.Instance);
    }

    private static UpdateRelease AddCompressedArchiveAsset(
        FakeReleaseClient fakeClient,
        string sqlitePath,
        string assetName,
        string? overrideSha256 = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-update-test-assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partPath = Path.Combine(directory, assetName);
        File.WriteAllBytes(partPath, CompressZstd(ReadAllBytesShared(sqlitePath)));
        var partSha = overrideSha256 ?? Sha256File(partPath);
        fakeClient.Assets[assetName] = partPath;
        var size = new FileInfo(partPath).Length;
        var manifest = new ArchiveReleaseManifest(
            "s3",
            "1.0.0",
            "v1.0.0-s3",
            "archive.sqlite",
            Sha256File(sqlitePath),
            new FileInfo(sqlitePath).Length,
            [new ArchiveReleaseAsset(assetName, partSha, size, 0)]);
        return new UpdateRelease(
            UpdateTarget.S3Archive,
            "v1.0.0-s3",
            "1.0.0",
            "v1.0.0-s3",
            new Uri("https://github.com/OpenGameBuilder/mygamebuilder-archive/releases/tag/v1.0.0-s3"),
            manifest,
            new Dictionary<string, GithubReleaseAsset>(StringComparer.Ordinal)
            {
                [assetName] = new GithubReleaseAsset(assetName, new Uri("https://example.test/" + assetName), size),
            });
    }

    private static UpdateRelease CreateAppRelease(string version)
    {
        var asset = new AppReleaseAsset(
            RuntimeAssetSelector.CurrentRid(),
            "mygamebuilder-local-test.zip",
            new string('1', 64),
            123);
        return new UpdateRelease(
            UpdateTarget.App,
            "v" + version,
            version,
            "MyGameBuilder Local " + version,
            new Uri("https://github.com/OpenGameBuilder/mygamebuilder-local/releases/tag/v" + version),
            new AppReleaseManifest(version, "v" + version, [asset]),
            new Dictionary<string, GithubReleaseAsset>(StringComparer.Ordinal)
            {
                [asset.Name] = new GithubReleaseAsset(asset.Name, new Uri("https://example.test/" + asset.Name), asset.SizeBytes),
            });
    }

    private static UpdateRelease CreateArchiveRelease(UpdateTarget target, string version)
    {
        var (kind, tagSuffix, targetFileName) = target switch
        {
            UpdateTarget.S3Archive => ("s3", "-s3", "archive.sqlite"),
            UpdateTarget.FrontendArchive => ("frontend", "-client", "frontend.sqlite"),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };
        var tag = "v" + version + tagSuffix;
        return new UpdateRelease(
            target,
            tag,
            version,
            tag,
            new Uri("https://github.com/OpenGameBuilder/mygamebuilder-archive/releases/tag/" + tag),
            new ArchiveReleaseManifest(kind, version, tag, targetFileName, string.Empty, 0, []),
            new Dictionary<string, GithubReleaseAsset>(StringComparer.Ordinal));
    }

    private static byte[] CompressZstd(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, 1, 1024, leaveOpen: true))
        {
            zstd.Write(bytes);
        }

        return output.ToArray();
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(ReadAllBytesShared(path))).ToLowerInvariant();

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void SetArchiveInfo(string archivePath, string name, string value)
    {
        using var connection = Open(archivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO archive_info(name, value)
            VALUES ($name, $value)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string? ReadArchiveInfo(string archivePath, string name)
    {
        using var connection = Open(archivePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM archive_info WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static int CountObjects(string archivePath, string key)
    {
        using var connection = Open(archivePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM s3_object WHERE key_text = $key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class FakeReleaseClient : IUpdateReleaseClient
    {
        public Dictionary<string, string> Assets { get; } = new(StringComparer.Ordinal);

        public Dictionary<UpdateTarget, UpdateRelease> Releases { get; } = new();

        public Task<UpdateRelease?> GetLatestReleaseAsync(UpdateTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(Releases.TryGetValue(target, out var release) ? release : null);

        public Task DownloadAssetAsync(GithubReleaseAsset asset, string destinationPath, IProgress<long>? bytesProgress, CancellationToken cancellationToken)
        {
            File.Copy(Assets[asset.Name], destinationPath, overwrite: true);
            bytesProgress?.Report(new FileInfo(destinationPath).Length);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StaticBytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class TestEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }
}
