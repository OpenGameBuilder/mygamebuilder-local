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
    private const string S3ArchiveTagPrefix = "s3-v";
    private const string FrontendArchiveTagPrefix = "frontend-v";
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
        var (repo, tagPrefix, manifestName) = target switch
        {
            UpdateTarget.App => (_options.Value.AppRepository, AppTagPrefix, AppManifestName),
            UpdateTarget.S3Archive => (_options.Value.ArchiveRepository, S3ArchiveTagPrefix, ArchiveManifestName),
            UpdateTarget.FrontendArchive => (_options.Value.ArchiveRepository, FrontendArchiveTagPrefix, ArchiveManifestName),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        var releases = await ListReleasesAsync(repo, cancellationToken).ConfigureAwait(false);
        var candidates = UpdateReleaseSelector
            .OrderByLatest(
                releases.Where(release => _options.Value.IncludePrereleases || (!release.Draft && !release.Prerelease)),
                static release => release.TagName,
                tagPrefix);

        foreach (var release in candidates)
        {
            var assets = release.Assets
                .Select(static asset => new GithubReleaseAsset(asset.Name, asset.BrowserDownloadUrl, asset.Size))
                .ToDictionary(static asset => asset.Name, StringComparer.Ordinal);
            if (!assets.TryGetValue(manifestName, out var manifestAsset))
            {
                _logger.LogWarning(
                    "Ignoring update release {Tag}: missing manifest asset {ManifestName}.",
                    release.TagName,
                    manifestName);
                continue;
            }

            try
            {
                var manifestJson = await GetStringAsync(manifestAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
                var manifest = target == UpdateTarget.App
                    ? (object)ReadAppManifest(manifestJson, release.TagName)
                    : ReadArchiveManifest(manifestJson, target, release.TagName);
                var version = manifest switch
                {
                    AppReleaseManifest app => app.Version,
                    ArchiveReleaseManifest archive => archive.Version,
                    _ => release.TagName[tagPrefix.Length..],
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
        [property: JsonPropertyName("size")] long Size);
}
