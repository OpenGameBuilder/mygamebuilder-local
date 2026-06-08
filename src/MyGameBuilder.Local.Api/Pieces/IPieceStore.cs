namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// S3 piece-store emulation with overlay semantics: a read-only archive base under
/// a writable overlay, with per-key tombstones for deletes of base-only keys. All
/// keys are original, bucket-relative S3 keys (e.g. <c>alice/project1/tile/Brick</c>).
/// </summary>
public interface IPieceStore
{
    /// <summary>Resolves an object by key (overlay wins; tombstones hide base objects). Null when absent.</summary>
    ValueTask<PieceObject?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes an object to the overlay, clearing any tombstone for the key.</summary>
    ValueTask PutAsync(string key, byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> amzMeta, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object (overlay removal and/or base tombstone). True if something was removed or hidden.</summary>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Effective (non-tombstoned, overlay-wins) entries whose key starts with <paramref name="prefix"/>.</summary>
    IReadOnlyList<PieceListItem> List(string prefix);

    /// <summary>True when any effective key belongs to <paramref name="user"/> (its first path segment).</summary>
    bool UserExists(string user);

    /// <summary>Distinct owning-user names across all effective keys.</summary>
    IReadOnlyCollection<string> ListUsers();

    /// <summary>Total body bytes owned by <paramref name="user"/> across effective keys.</summary>
    long UserSizeBytes(string user);
}
