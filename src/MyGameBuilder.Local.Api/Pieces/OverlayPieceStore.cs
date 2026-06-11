namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Composes the read-only <see cref="ArchivePieceStore"/> base with the writable
/// <see cref="DataPieceStore"/> overlay. Reads consult the overlay first (tombstones
/// hide base objects); writes and deletes affect only the overlay.
/// </summary>
public sealed class OverlayPieceStore : IPieceStore
{
    private readonly ArchivePieceStore _archive;
    private readonly DataPieceStore _data;

    public OverlayPieceStore(ArchivePieceStore archive, DataPieceStore data)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(data);
        _archive = archive;
        _data = data;
    }

    public ValueTask<PieceObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key) || _data.IsTombstoned(key))
        {
            return ValueTask.FromResult<PieceObject?>(null);
        }

        if (_data.TryGet(key, out var overlay))
        {
            return ValueTask.FromResult<PieceObject?>(ToObject(overlay));
        }

        if (_archive.TryGet(key, out var baseEntry))
        {
            return ValueTask.FromResult<PieceObject?>(ToObject(baseEntry));
        }

        return DefaultProfilePieces.TryGet(key, out var fallback)
            ? ValueTask.FromResult(fallback)
            : ValueTask.FromResult<PieceObject?>(null);
    }

    public async ValueTask PutAsync(string key, byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> amzMeta, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _data.PutAsync(key, body, contentType, amzMeta, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return ValueTask.FromResult(false);
        }

        var existsInBase = _archive.TryGet(key, out _);
        return ValueTask.FromResult(_data.Delete(key, existsInBase));
    }

    public IReadOnlyList<PieceListItem> List(string prefix)
    {
        prefix ??= string.Empty;
        var effective = new Dictionary<string, PieceListItem>(StringComparer.Ordinal);
        var tombstonedKeys = _data.TombstonedKeys(prefix);

        // The archive is queried only under the requested prefix, so a single-user
        // or project listing does not fetch object bodies.
        foreach (var entry in _archive.ListEntries(prefix))
        {
            if (!tombstonedKeys.Contains(entry.Key))
            {
                effective[entry.Key] = new PieceListItem(entry.Key, entry.Size, entry.LastModified);
            }
        }

        foreach (var entry in _data.ListEntries(prefix))
        {
            effective[entry.Key] = new PieceListItem(entry.Key, entry.Size, entry.LastModified);
        }

        foreach (var item in DefaultProfilePieces.List(prefix))
        {
            if (!tombstonedKeys.Contains(item.Key))
            {
                effective.TryAdd(item.Key, item);
            }
        }

        return effective.Values.ToList();
    }

    public bool UserExists(string user)
    {
        if (string.IsNullOrEmpty(user))
        {
            return false;
        }

        if (_data.UserExists(user))
        {
            return true;
        }

        if (string.Equals(user, "!system", StringComparison.Ordinal) ||
            string.Equals(user, "guest", StringComparison.Ordinal))
        {
            return true;
        }

        // Index-only check: no archive content is read for the existence test.
        return _archive.UserExists(user);
    }

    public IReadOnlyCollection<string> ListUsers()
    {
        var users = new HashSet<string>(StringComparer.Ordinal);

        // Archive users come from metadata-only SQLite rows; object bodies are not read.
        foreach (var user in _archive.ListUsers())
        {
            users.Add(user);
        }

        // Include users that exist only in the writable overlay.
        foreach (var user in _data.ListUsers())
        {
            users.Add(user);
        }

        users.Add("!system");
        users.Add("guest");

        return users;
    }

    public long UserSizeBytes(string user)
    {
        if (string.IsNullOrEmpty(user))
        {
            return 0;
        }

        long total = 0;
        foreach (var item in List(user + "/"))
        {
            total += item.Size;
        }

        return total;
    }

    private static PieceObject ToObject(PieceEntry entry) =>
        new(
            entry.Key,
            entry.Size,
            entry.LastModified,
            entry.ContentType,
            entry.AmzMeta,
            entry.BodyLoader);
}
