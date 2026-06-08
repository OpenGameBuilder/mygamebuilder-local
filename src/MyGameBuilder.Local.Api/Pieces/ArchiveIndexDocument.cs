using System.Text.Json.Serialization;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Parsed form of a per-directory <c>_index.json</c> (README 3.3). Maps original S3
/// keys/prefixes to the opaque on-disk file or subdirectory name. Subdirectory
/// entries have keys that end with <c>/</c>; body-file entries do not.
/// </summary>
public sealed class ArchiveIndexDocument
{
    /// <summary>The S3 prefix this directory represents (no trailing slash; empty at the archive root).</summary>
    [JsonPropertyName("prefix")]
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Maps S3 key (body file) or S3 prefix + trailing slash (subdirectory) to its on-disk name.</summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);
}
