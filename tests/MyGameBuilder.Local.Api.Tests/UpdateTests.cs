using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(current.Root, ".mygamebuilder-backups", "s3"), "archive.sqlite", SearchOption.AllDirectories));
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
        var environment = new TestEnvironment(current.Root);
        var updateOptions = Options.Create(new UpdateOptions
        {
            StagingPath = Path.Combine(current.Root, ".mygamebuilder-updates"),
            BackupPath = Path.Combine(current.Root, ".mygamebuilder-backups"),
        });
        var pieceOptions = Options.Create(new PieceStoreOptions
        {
            ArchivePath = current.ArchivePath,
            OverlayPath = current.OverlayPath,
        });
        var frontendPath = Path.Combine(current.Root, "frontend.sqlite");
        TempFrontendArchive.CreateArchive(frontendPath);
        var frontendOptions = Options.Create(new FrontendOptions
        {
            ArchivePath = frontendPath,
        });

        fakeClient = new FakeReleaseClient();
        return new ArchiveUpdateInstaller(
            fakeClient,
            new UpdateStateStore(new UpdatePaths(environment, updateOptions)),
            new UpdatePaths(environment, updateOptions),
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
            "s3-v1.0.0",
            "archive.sqlite",
            Sha256File(sqlitePath),
            new FileInfo(sqlitePath).Length,
            [new ArchiveReleaseAsset(assetName, partSha, size, 0)]);
        return new UpdateRelease(
            UpdateTarget.S3Archive,
            "s3-v1.0.0",
            "1.0.0",
            "s3-v1.0.0",
            new Uri("https://github.com/OpenGameBuilder/mygamebuilder-archive/releases/tag/s3-v1.0.0"),
            manifest,
            new Dictionary<string, GithubReleaseAsset>(StringComparer.Ordinal)
            {
                [assetName] = new GithubReleaseAsset(assetName, new Uri("https://example.test/" + assetName), size),
            });
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

        public Task<UpdateRelease?> GetLatestReleaseAsync(UpdateTarget target, CancellationToken cancellationToken) =>
            Task.FromResult<UpdateRelease?>(null);

        public Task DownloadAssetAsync(GithubReleaseAsset asset, string destinationPath, IProgress<long>? bytesProgress, CancellationToken cancellationToken)
        {
            File.Copy(Assets[asset.Name], destinationPath, overwrite: true);
            bytesProgress?.Report(new FileInfo(destinationPath).Length);
            return Task.CompletedTask;
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public TestEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
