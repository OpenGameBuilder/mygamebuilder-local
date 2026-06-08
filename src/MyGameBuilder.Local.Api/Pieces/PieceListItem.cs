namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>Lightweight listing entry (no body) used by ListBucket.</summary>
public readonly record struct PieceListItem(string Key, long Size, DateTimeOffset LastModified);
