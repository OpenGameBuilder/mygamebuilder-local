using System.Text.Json.Serialization;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Deserialized form of a base-archive <c>&lt;name&gt;.meta.json</c> sidecar (README §3.2).
/// The <see cref="Key"/> is the authoritative original S3 key for the object.
/// </summary>
public sealed class PieceMetadata
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("last_modified")]
    public string? LastModified { get; init; }

    [JsonPropertyName("amz_meta")]
    public Dictionary<string, string> AmzMeta { get; init; } = [];
}
