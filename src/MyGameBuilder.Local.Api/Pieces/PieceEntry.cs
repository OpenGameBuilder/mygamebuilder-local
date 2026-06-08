namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>Internal unified index entry shared by the archive (base) and data (overlay) stores.</summary>
internal sealed record PieceEntry(
    string Key,
    string BodyPath,
    long Size,
    DateTimeOffset LastModified,
    string? ContentType,
    IReadOnlyList<KeyValuePair<string, string>> AmzMeta);
