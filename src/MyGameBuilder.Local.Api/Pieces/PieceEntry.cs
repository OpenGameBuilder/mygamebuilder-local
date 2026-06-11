namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>Internal unified entry shared by the archive (base) and data (overlay) stores.</summary>
internal sealed record PieceEntry(
    string Key,
    long Size,
    DateTimeOffset LastModified,
    string? ContentType,
    IReadOnlyList<KeyValuePair<string, string>> AmzMeta,
    Func<CancellationToken, ValueTask<byte[]>> BodyLoader);
