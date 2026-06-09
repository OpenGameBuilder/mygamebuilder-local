using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Read-only base half of the piece store, backed by the frozen archive's per-directory
/// <c>_index.json</c> map (README 3.3/4). Object lookups walk the index segment by
/// segment from the archive root to the opaque on-disk leaf, then read that body's
/// sibling <c>.meta.json</c> sidecar for size/content-type/last-modified. The index is
/// authoritative; if it points at a body file that is not present on disk the lookup
/// returns null (treated as a 404 upstream).
///
/// Designed for very large archives (tens of thousands of users): nothing is scanned
/// eagerly, the (large) root index is parsed once, and parsed indexes plus resolved
/// per-prefix listings are cached in <see cref="IMemoryCache"/> with size limits so
/// repeated requests for the same user stay cheap without unbounded memory growth.
/// </summary>
public sealed class ArchivePieceStore
{
    private const string IndexFileName = "_index.json";
    private const string SidecarSuffix = ".meta.json";
    private const string PrefixCacheTag = "archive-prefix::";
    private const string IndexCacheTag = "archive-index::";

    private readonly string _archiveRoot;
    private readonly IMemoryCache _cache;

    public ArchivePieceStore(string archiveRoot, IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(archiveRoot);
        ArgumentNullException.ThrowIfNull(cache);
        _archiveRoot = archiveRoot;
        _cache = cache;
    }

    /// <summary>
    /// Resolves a single object by its bucket-relative S3 key via the index walk.
    /// Returns false when the key is not indexed or the indexed body file is missing.
    /// </summary>
    internal bool TryGet(string key, [MaybeNullWhen(false)] out PieceEntry entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(key) || !Directory.Exists(_archiveRoot))
        {
            return false;
        }

        var directory = ResolveDirectoryForKey(_archiveRoot, key, out var index);
        if (directory is null || index is null || !index.Entries.TryGetValue(key, out var onDiskName))
        {
            return false;
        }

        entry = TryReadBody(directory, key, onDiskName);
        return entry is not null;
    }

    /// <summary>
    /// Lists every object whose key starts with <paramref name="prefix"/> by resolving
    /// the prefix's directory and recursively descending its indexes. The empty prefix
    /// would walk the entire archive, so it is rejected (returns empty); callers that
    /// need user enumeration should use <see cref="ListUsers"/>. Results are cached
    /// per prefix because a client session typically lists within a single user/project.
    /// </summary>
    internal IReadOnlyList<PieceEntry> ListEntries(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || !Directory.Exists(_archiveRoot))
        {
            return [];
        }

        var cacheKey = PrefixCacheTag + prefix;
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<PieceEntry>? cached))
        {
            return cached ?? [];
        }

        var startDirectory = ResolveDirectoryForPrefix(_archiveRoot, prefix);
        if (startDirectory is null)
        {
            // A missing prefix can be transient while an archive is being copied into
            // place. Do not cache the miss; let the next request re-check the disk.
            return [];
        }

        var results = new List<PieceEntry>();
        CollectEntries(startDirectory, prefix, results);

        using var cacheEntry = _cache.CreateEntry(cacheKey);
        cacheEntry.Size = results.Count + 1;
        cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(10);
        cacheEntry.Value = results;

        return results;
    }

    /// <summary>True when <paramref name="user"/> has a top-level entry in the root index and its directory exists.</summary>
    internal bool UserExists(string user)
    {
        if (string.IsNullOrEmpty(user) || !Directory.Exists(_archiveRoot))
        {
            return false;
        }

        var rootIndex = ReadIndex(_archiveRoot);
        if (rootIndex is null || !rootIndex.Entries.TryGetValue(user + "/", out var onDiskDir))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(_archiveRoot, onDiskDir));
    }

    /// <summary>Enumerates the user prefixes listed in the root index (trailing slash stripped).</summary>
    internal IReadOnlyCollection<string> ListUsers()
    {
        if (!Directory.Exists(_archiveRoot))
        {
            return [];
        }

        var rootIndex = ReadIndex(_archiveRoot);
        if (rootIndex is null)
        {
            return [];
        }

        var users = new List<string>(rootIndex.Entries.Count);
        foreach (var entryKey in rootIndex.Entries.Keys)
        {
            // Root entries are user prefixes ending in '/'; ignore any stray non-prefix entries.
            if (entryKey.EndsWith('/'))
            {
                users.Add(entryKey[..^1]);
            }
        }

        return users;
    }

    /// <summary>
    /// Recursively collects body entries under <paramref name="directory"/>. Subdirectory
    /// entries (keys ending in '/') are descended; body entries whose key starts with
    /// <paramref name="filterPrefix"/> are resolved through their sidecars.
    /// </summary>
    private void CollectEntries(string directory, string filterPrefix, List<PieceEntry> results)
    {
        var index = ReadIndex(directory);
        if (index is null)
        {
            return;
        }

        foreach (var (entryKey, onDiskName) in index.Entries)
        {
            if (entryKey.EndsWith('/'))
            {
                // Descend only into subdirectories on the path to (or under) the filter prefix.
                if (OnPrefixPath(entryKey, filterPrefix))
                {
                    var childDirectory = Path.Combine(directory, onDiskName);
                    if (Directory.Exists(childDirectory))
                    {
                        CollectEntries(childDirectory, filterPrefix, results);
                    }
                }

                continue;
            }

            if (!entryKey.StartsWith(filterPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var entry = TryReadBody(directory, entryKey, onDiskName);
            if (entry is not null)
            {
                results.Add(entry);
            }
        }
    }

    /// <summary>
    /// Walks the index tree from <paramref name="archiveRoot"/> to the directory that
    /// directly contains <paramref name="key"/>, returning that directory and its parsed
    /// index. Returns null when any segment is missing from the indexes.
    /// </summary>
    private string? ResolveDirectoryForKey(string archiveRoot, string key, out ArchiveIndexDocument? containingIndex)
    {
        containingIndex = null;

        var lastSlash = key.LastIndexOf('/');
        var parentPrefix = lastSlash < 0 ? string.Empty : key[..lastSlash];

        var directory = parentPrefix.Length == 0
            ? archiveRoot
            : ResolveDirectoryForPrefix(archiveRoot, parentPrefix + "/");

        if (directory is null)
        {
            return null;
        }

        containingIndex = ReadIndex(directory);
        return containingIndex is null ? null : directory;
    }

    /// <summary>
    /// Walks the index tree from <paramref name="archiveRoot"/> to the on-disk directory
    /// representing <paramref name="prefix"/> (which must end with '/'). Returns null when
    /// any segment is not present in the indexes.
    /// </summary>
    private string? ResolveDirectoryForPrefix(string archiveRoot, string prefix)
    {
        var trimmed = prefix.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return archiveRoot;
        }

        var segments = trimmed.Split('/');
        var directory = archiveRoot;
        var accumulated = string.Empty;

        foreach (var segment in segments)
        {
            var index = ReadIndex(directory);
            if (index is null)
            {
                return null;
            }

            accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
            var lookup = accumulated + "/";
            if (!index.Entries.TryGetValue(lookup, out var onDiskName))
            {
                return null;
            }

            directory = Path.Combine(directory, onDiskName);
            if (!Directory.Exists(directory))
            {
                return null;
            }
        }

        return directory;
    }

    /// <summary>Reads and caches a directory's <c>_index.json</c>. Returns null when absent or unparseable.</summary>
    private ArchiveIndexDocument? ReadIndex(string directory)
    {
        var cacheKey = IndexCacheTag + directory;
        if (_cache.TryGetValue(cacheKey, out ArchiveIndexDocument? cached))
        {
            return cached;
        }

        var path = Path.Combine(directory, IndexFileName);
        ArchiveIndexDocument? document = null;
        try
        {
            var bytes = StripUtf8Bom(File.ReadAllBytes(path));
            document = JsonSerializer.Deserialize<ArchiveIndexDocument>(bytes);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        if (document is null)
        {
            // Treat absent, incomplete, or temporarily unreadable indexes as a
            // request-local miss. Caching null here makes an archive copied into
            // place after startup look empty until the cache entry expires.
            return null;
        }

        using var cacheEntry = _cache.CreateEntry(cacheKey);
        // Size scales with entry count so the large root index is weighted heavily.
        cacheEntry.Size = document.Entries.Count + 1;
        cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(10);
        cacheEntry.Value = document;
        return document;
    }

    /// <summary>Reads the sidecar for an indexed body and builds a <see cref="PieceEntry"/>; null when the body is missing.</summary>
    private static PieceEntry? TryReadBody(string directory, string key, string onDiskName)
    {
        var bodyPath = Path.Combine(directory, onDiskName);
        var bodyInfo = new FileInfo(bodyPath);
        if (!bodyInfo.Exists)
        {
            // The index is assumed correct, but a missing body is treated as not-found.
            return null;
        }

        var meta = TryReadSidecar(bodyPath + SidecarSuffix);

        var amzMeta = meta?.AmzMeta
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .ToList() ?? [];

        var lastModified = ParseHttpDate(meta?.LastModified)
            ?? new DateTimeOffset(bodyInfo.LastWriteTimeUtc, TimeSpan.Zero);

        return new PieceEntry(key, bodyPath, bodyInfo.Length, lastModified, meta?.ContentType, amzMeta);
    }

    private static PieceMetadata? TryReadSidecar(string sidecarPath)
    {
        try
        {
            var bytes = StripUtf8Bom(File.ReadAllBytes(sidecarPath));
            return JsonSerializer.Deserialize<PieceMetadata>(bytes);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="entryKey"/> is on the path to (or under) <paramref name="filterPrefix"/>.</summary>
    private static bool OnPrefixPath(string entryKey, string filterPrefix)
        => entryKey.StartsWith(filterPrefix, StringComparison.Ordinal)
            || filterPrefix.StartsWith(entryKey, StringComparison.Ordinal);

    private static DateTimeOffset? ParseHttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Sidecars record last_modified as an RFC1123 HTTP date (e.g. "Mon, 12 Oct 2009 17:50:00 GMT").
        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
        {
            return exact;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes)
    {
        // A UTF-8 BOM is the three-byte sequence EF BB BF.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsSpan(3);
        }

        return bytes;
    }
}
