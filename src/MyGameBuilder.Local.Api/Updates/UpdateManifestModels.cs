using System.Text.Json.Serialization;

namespace MyGameBuilder.Local.Api.Updates;

public sealed record AppReleaseManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("assets")] IReadOnlyList<AppReleaseAsset> Assets);

public sealed record AppReleaseAsset(
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

public sealed record ArchiveReleaseManifest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("targetFileName")] string TargetFileName,
    [property: JsonPropertyName("sqliteSha256")] string SqliteSha256,
    [property: JsonPropertyName("sqliteSizeBytes")] long SqliteSizeBytes,
    [property: JsonPropertyName("assets")] IReadOnlyList<ArchiveReleaseAsset> Assets);

public sealed record ArchiveReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("order")] int Order);

public sealed record GithubReleaseAsset(
    string Name,
    Uri DownloadUrl,
    long SizeBytes,
    string? Sha256 = null);

public sealed record UpdateRelease(
    UpdateTarget Target,
    string Tag,
    string Version,
    string Name,
    Uri HtmlUrl,
    object Manifest,
    IReadOnlyDictionary<string, GithubReleaseAsset> Assets)
{
    public long DownloadSizeBytes => Target switch
    {
        UpdateTarget.App when Manifest is AppReleaseManifest app =>
            app.Assets.Sum(static asset => Math.Max(0, asset.SizeBytes)),
        UpdateTarget.S3Archive or UpdateTarget.FrontendArchive when Manifest is ArchiveReleaseManifest archive =>
            archive.Assets.Sum(static asset => Math.Max(0, asset.SizeBytes)),
        _ => 0,
    };
}
