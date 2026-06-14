using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class GitHubUpdateReleaseClient : IUpdateReleaseClient
{
    private const string AppTagPrefix = "v";
    private const string ArchiveTagPrefix = "v";
    private const string S3ArchiveTagSuffix = "-s3";
    private const string FrontendArchiveTagSuffix = "-client";
    private const string AppManifestName = "mygamebuilder-local-release.json";
    private const string ArchiveManifestName = "mgb-archive-manifest.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<UpdateOptions> _options;
    private readonly ILogger<GitHubUpdateReleaseClient> _logger;
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<Uri, HttpCacheEntry> _cache = [];

    public GitHubUpdateReleaseClient(
        HttpClient httpClient,
        IOptions<UpdateOptions> options,
        ILogger<GitHubUpdateReleaseClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<UpdateRelease?> GetLatestReleaseAsync(UpdateTarget target, CancellationToken cancellationToken)
    {
        var (repo, tagPrefix, tagSuffix, manifestName) = target switch
        {
            UpdateTarget.App => (_options.Value.AppRepository, AppTagPrefix, string.Empty, AppManifestName),
            UpdateTarget.S3Archive => (_options.Value.ArchiveRepository, ArchiveTagPrefix, S3ArchiveTagSuffix, ArchiveManifestName),
            UpdateTarget.FrontendArchive => (_options.Value.ArchiveRepository, ArchiveTagPrefix, FrontendArchiveTagSuffix, ArchiveManifestName),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        var releases = await ListReleasesAsync(repo, cancellationToken).ConfigureAwait(false);
        var candidates = UpdateReleaseSelector
            .OrderByLatest(
                releases.Where(release => _options.Value.IncludePrereleases || (!release.Draft && !release.Prerelease)),
                static release => release.TagName,
                tagPrefix,
                tagSuffix);

        foreach (var release in candidates)
        {
            var assets = release.Assets
                .Select(static asset => new GithubReleaseAsset(
                    asset.Name,
                    asset.BrowserDownloadUrl,
                    asset.Size,
                    TryGetSha256Digest(asset.Digest)))
                .ToDictionary(static asset => asset.Name, StringComparer.Ordinal);
            if (!UpdateReleaseSelector.TryParseTaggedVersion(release.TagName, tagPrefix, tagSuffix, out _, out var tagVersion))
            {
                continue;
            }

            try
            {
                object manifest;
                if (assets.TryGetValue(manifestName, out var manifestAsset))
                {
                    var manifestJson = await GetStringAsync(manifestAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
                    manifest = target == UpdateTarget.App
                        ? ReadAppManifest(manifestJson, release.TagName)
                        : ReadArchiveManifest(manifestJson, target, release.TagName);
                }
                else if (target is UpdateTarget.S3Archive or UpdateTarget.FrontendArchive)
                {
                    manifest = BuildArchiveManifestFromReleaseAssets(target, release.TagName, tagVersion, assets);
                }
                else
                {
                    _logger.LogWarning(
                        "Ignoring update release {Tag}: missing manifest asset {ManifestName}.",
                        release.TagName,
                        manifestName);
                    continue;
                }

                var version = manifest switch
                {
                    AppReleaseManifest app => app.Version,
                    ArchiveReleaseManifest archive => archive.Version,
                    _ => tagVersion,
                };
                return new UpdateRelease(
                    target,
                    release.TagName,
                    version,
                    string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                    release.HtmlUrl,
                    manifest,
                    assets);
            }
            catch (JsonException exc)
            {
                _logger.LogWarning(exc, "Ignoring update release {Tag}: manifest is not valid JSON.", release.TagName);
            }
            catch (InvalidOperationException exc)
            {
                _logger.LogWarning(exc, "Ignoring update release {Tag}: manifest did not pass validation.", release.TagName);
            }
        }

        return null;
    }

    private static ArchiveReleaseManifest BuildArchiveManifestFromReleaseAssets(
        UpdateTarget target,
        string tag,
        string version,
        IReadOnlyDictionary<string, GithubReleaseAsset> assets)
    {
        var (kind, targetFileName) = target switch
        {
            UpdateTarget.S3Archive => ("s3", "archive.sqlite"),
            UpdateTarget.FrontendArchive => ("frontend", "frontend.sqlite"),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        var selectedAssets = SelectArchiveAssets(targetFileName, assets);
        if (selectedAssets.Count == 0)
        {
            throw new InvalidOperationException($"Archive release {tag} did not include assets for {targetFileName}.");
        }

        if (selectedAssets.Any(static asset => string.IsNullOrWhiteSpace(asset.Asset.Sha256)))
        {
            throw new InvalidOperationException($"Archive release {tag} included asset(s) without GitHub SHA-256 digests.");
        }

        var sqliteSha256 = selectedAssets.Count == 1 &&
            string.Equals(selectedAssets[0].Asset.Name, targetFileName, StringComparison.Ordinal)
                ? selectedAssets[0].Asset.Sha256!
                : string.Empty;

        return new ArchiveReleaseManifest(
            kind,
            version,
            tag,
            targetFileName,
            sqliteSha256,
            selectedAssets.Count == 1 && !string.IsNullOrEmpty(sqliteSha256) ? selectedAssets[0].Asset.SizeBytes : 0,
            selectedAssets
                .Select(static item => new ArchiveReleaseAsset(
                    item.Asset.Name,
                    item.Asset.Sha256!,
                    item.Asset.SizeBytes,
                    item.Order))
                .ToArray());
    }

    private static IReadOnlyList<(GithubReleaseAsset Asset, int Order)> SelectArchiveAssets(
        string targetFileName,
        IReadOnlyDictionary<string, GithubReleaseAsset> assets)
    {
        if (assets.TryGetValue(targetFileName, out var directSqlite))
        {
            return [(directSqlite, 0)];
        }

        var zstdName = targetFileName + ".zst";
        if (assets.TryGetValue(zstdName, out var directZstd))
        {
            return [(directZstd, 0)];
        }

        var zstdParts = SelectArchivePartAssets(targetFileName + ".zst.part-", assets);
        if (zstdParts.Count > 0)
        {
            return zstdParts;
        }

        return SelectArchivePartAssets(targetFileName + ".part-", assets);
    }

    private static IReadOnlyList<(GithubReleaseAsset Asset, int Order)> SelectArchivePartAssets(
        string partPrefix,
        IReadOnlyDictionary<string, GithubReleaseAsset> assets)
    {
        var parts = assets.Values
            .Select(asset => (Asset: asset, Order: TryParsePartOrder(asset.Name, partPrefix)))
            .Where(static item => item.Order is not null)
            .Select(static item => (item.Asset, Order: item.Order!.Value))
            .OrderBy(static item => item.Order)
            .ToArray();
        if (parts.Length == 0)
        {
            return [];
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Order != index)
            {
                throw new InvalidOperationException($"Archive release parts with prefix {partPrefix} are not contiguous. Missing part index {index:000}.");
            }
        }

        return parts;
    }

    private static int? TryParsePartOrder(string assetName, string partPrefix)
    {
        if (!assetName.StartsWith(partPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var suffix = assetName[partPrefix.Length..];
        return int.TryParse(
            suffix,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var order) && order >= 0
                ? order
                : null;
    }

    private static string? TryGetSha256Digest(string? digest)
    {
        const string Prefix = "sha256:";
        if (digest is null || !digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sha256 = digest[Prefix.Length..];
        return sha256.Length == 64 && sha256.All(static c => Uri.IsHexDigit(c))
            ? sha256.ToLowerInvariant()
            : null;
    }

    public async Task DownloadAssetAsync(
        GithubReleaseAsset asset,
        string destinationPath,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? Environment.CurrentDirectory);
        var tempPath = destinationPath + ".download";
        File.Delete(tempPath);
        try
        {
            using var request = CreateRequest(asset.DownloadUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            {
                await using var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);
                var buffer = new byte[1024 * 1024];
                var total = 0L;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    bytesProgress?.Report(total);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task<IReadOnlyList<GithubReleaseDto>> ListReleasesAsync(string repository, CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://api.github.com/repos/{ValidateRepository(repository)}/releases?per_page=100");
        var json = await GetStringAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IReadOnlyList<GithubReleaseDto>>(json, s_jsonOptions) ?? [];
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        HttpCacheEntry? cached;
        lock (_cacheGate)
        {
            _cache.TryGetValue(uri, out cached);
        }

        using var request = CreateRequest(uri);
        if (!string.IsNullOrEmpty(cached?.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Body;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            _cache[uri] = new HttpCacheEntry(response.Headers.ETag?.Tag, body);
        }

        return body;
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mygamebuilder-local", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    private static AppReleaseManifest ReadAppManifest(string manifestJson, string expectedTag)
    {
        var manifest = JsonSerializer.Deserialize<AppReleaseManifest>(manifestJson, s_jsonOptions)
            ?? throw new InvalidOperationException("App release manifest was empty.");
        if (!string.Equals(manifest.Tag, expectedTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"App manifest tag '{manifest.Tag}' did not match release tag '{expectedTag}'.");
        }

        if (manifest.Assets.Count == 0)
        {
            throw new InvalidOperationException("App manifest does not list any runtime assets.");
        }

        return manifest;
    }

    private static ArchiveReleaseManifest ReadArchiveManifest(string manifestJson, UpdateTarget target, string expectedTag)
    {
        var manifest = JsonSerializer.Deserialize<ArchiveReleaseManifest>(manifestJson, s_jsonOptions)
            ?? throw new InvalidOperationException("Archive release manifest was empty.");
        var expectedKind = target == UpdateTarget.S3Archive ? "s3" : "frontend";
        if (!string.Equals(manifest.Kind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Archive manifest kind '{manifest.Kind}' did not match expected kind '{expectedKind}'.");
        }

        if (!string.Equals(manifest.Tag, expectedTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Archive manifest tag '{manifest.Tag}' did not match release tag '{expectedTag}'.");
        }

        if (manifest.Assets.Count == 0)
        {
            throw new InvalidOperationException("Archive manifest does not list any archive assets.");
        }

        return manifest;
    }

    private static string ValidateRepository(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Updates repository must use owner/name format. Received '{repository}'.");
        }

        return string.Join('/', parts);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup after an interrupted download.
        }
    }

    private sealed record HttpCacheEntry(string? ETag, string Body);

    private sealed record GithubReleaseDto(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("html_url")] Uri HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<GithubReleaseAssetDto> Assets);

    private sealed record GithubReleaseAssetDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}
