using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Writable overlay half of the piece store. Newly written objects and per-key
/// tombstones are persisted under a data directory using an internal, reversible
/// Base64Url-of-key naming scheme, so original S3 keys (including ones with spaces
/// or characters that are invalid in file names) round-trip verbatim. The in-memory
/// index is loaded once at construction and kept in sync on every write.
/// </summary>
public sealed class DataPieceStore
{
    private const string BodyExtension = ".bin";
    private const string MetaExtension = ".meta.json";
    private const string TombstoneExtension = ".tombstone";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    private readonly string _objectsDir;
    private readonly string _tombstonesDir;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PieceEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tombstones = new(StringComparer.Ordinal);

    public DataPieceStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        _objectsDir = Path.Combine(dataRoot, "objects");
        _tombstonesDir = Path.Combine(dataRoot, "tombstones");
        Load();
    }

    /// <summary>Writes (or replaces) an overlay object and clears any tombstone for its key.</summary>
    public async Task PutAsync(string key, byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> amzMeta, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(amzMeta);

        Directory.CreateDirectory(_objectsDir);
        var stem = EncodeKey(key);
        var bodyPath = Path.Combine(_objectsDir, stem + BodyExtension);
        var metaPath = Path.Combine(_objectsDir, stem + MetaExtension);
        var lastModified = DateTimeOffset.UtcNow;

        var model = new OverlayMetadata
        {
            Key = key,
            Size = body.LongLength,
            ContentType = contentType,
            LastModified = lastModified,
            AmzMeta = [.. amzMeta.Select(p => new MetaPair { Name = p.Key, Value = p.Value })],
        };

        await File.WriteAllBytesAsync(bodyPath, body, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(metaPath, JsonSerializer.SerializeToUtf8Bytes(model, s_jsonOptions), cancellationToken).ConfigureAwait(false);

        var entry = new PieceEntry(key, bodyPath, body.LongLength, lastModified, contentType, amzMeta);
        lock (_gate)
        {
            _entries[key] = entry;
            if (_tombstones.Remove(key))
            {
                TryDelete(Path.Combine(_tombstonesDir, stem + TombstoneExtension));
            }
        }
    }

    /// <summary>
    /// Removes an overlay object and/or tombstones a base-only key.
    /// Returns true when something was removed from the overlay or newly hidden.
    /// </summary>
    public bool Delete(string key, bool existsInBase)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var stem = EncodeKey(key);
        lock (_gate)
        {
            var removedOverlay = _entries.Remove(key);
            if (removedOverlay)
            {
                TryDelete(Path.Combine(_objectsDir, stem + BodyExtension));
                TryDelete(Path.Combine(_objectsDir, stem + MetaExtension));
            }

            var hidden = false;
            if (existsInBase && !_tombstones.Contains(key))
            {
                Directory.CreateDirectory(_tombstonesDir);
                // File name encodes the key; content stores it too for human debugging.
                File.WriteAllText(Path.Combine(_tombstonesDir, stem + TombstoneExtension), key, Encoding.UTF8);
                _tombstones.Add(key);
                hidden = true;
            }

            return removedOverlay || hidden;
        }
    }

    internal bool IsTombstoned(string key)
    {
        lock (_gate)
        {
            return _tombstones.Contains(key);
        }
    }

    internal bool TryGet(string key, [MaybeNullWhen(false)] out PieceEntry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out entry);
        }
    }

    internal IReadOnlyList<PieceEntry> SnapshotEntries()
    {
        lock (_gate)
        {
            return _entries.Values.ToList();
        }
    }

    private void Load()
    {
        if (Directory.Exists(_objectsDir))
        {
            foreach (var metaPath in Directory.EnumerateFiles(_objectsDir, "*" + MetaExtension, SearchOption.TopDirectoryOnly))
            {
                var entry = TryReadEntry(metaPath);
                if (entry is not null)
                {
                    _entries[entry.Key] = entry;
                }
            }
        }

        if (Directory.Exists(_tombstonesDir))
        {
            foreach (var tombstonePath in Directory.EnumerateFiles(_tombstonesDir, "*" + TombstoneExtension, SearchOption.TopDirectoryOnly))
            {
                // Recover the exact key by reversing the file-name encoding (content is not
                // parsed, so keys with trailing whitespace survive intact).
                var key = TryDecodeKey(Path.GetFileNameWithoutExtension(tombstonePath));
                if (!string.IsNullOrEmpty(key))
                {
                    _tombstones.Add(key);
                }
            }
        }
    }

    private static PieceEntry? TryReadEntry(string metaPath)
    {
        OverlayMetadata? model;
        try
        {
            model = JsonSerializer.Deserialize<OverlayMetadata>(File.ReadAllBytes(metaPath), s_jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (model is null || string.IsNullOrWhiteSpace(model.Key))
        {
            return null;
        }

        var bodyPath = metaPath[..^MetaExtension.Length] + BodyExtension;
        var bodyInfo = new FileInfo(bodyPath);
        if (!bodyInfo.Exists)
        {
            return null;
        }

        var amzMeta = model.AmzMeta
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value))
            .ToList();

        return new PieceEntry(model.Key, bodyPath, bodyInfo.Length, model.LastModified, model.ContentType, amzMeta);
    }

    private static string EncodeKey(string key) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(key));

    private static string? TryDecodeKey(string stem)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(stem));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the in-memory index is the source of truth.
        }
    }

    private sealed class OverlayMetadata
    {
        public string Key { get; set; } = string.Empty;
        public long Size { get; set; }
        public string? ContentType { get; set; }
        public DateTimeOffset LastModified { get; set; }
        public List<MetaPair> AmzMeta { get; set; } = [];
    }

    private sealed class MetaPair
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
