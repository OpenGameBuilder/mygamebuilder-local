using System.Text.Json;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Builds a throwaway on-disk archive snapshot in the documented format (README 3):
/// raw body, <c>.meta.json</c> sidecar, and a per-directory <c>_index.json</c> map.
/// On-disk names mirror the S3 key segments for test readability, but resolution still
/// goes through the generated indexes exactly as the real (opaque-named) archive does.
/// Disposing deletes the temporary directory tree.
/// </summary>
public sealed class TempArchive : IDisposable
{
    // Directory (relative S3 prefix, "" = root) -> entry key -> on-disk name.
    private readonly Dictionary<string, Dictionary<string, string>> _indexes = new(StringComparer.Ordinal);

    public TempArchive()
    {
        Root = Path.Combine(Path.GetTempPath(), "mgb-archive-" + Guid.NewGuid().ToString("N"));
        ArchiveRoot = Path.Combine(Root, "archive");
        DataRoot = Path.Combine(Root, "data");
        Directory.CreateDirectory(ArchiveRoot);
        Directory.CreateDirectory(DataRoot);
        _indexes[string.Empty] = new Dictionary<string, string>(StringComparer.Ordinal);
        WriteIndexes();
    }

    /// <summary>Parent directory containing both the archive and data roots.</summary>
    public string Root { get; }

    /// <summary>Read-only archive snapshot root (<c>PieceStore:ArchiveRoot</c>).</summary>
    public string ArchiveRoot { get; }

    /// <summary>Writable overlay root (<c>PieceStore:DataRoot</c>).</summary>
    public string DataRoot { get; }

    /// <summary>
    /// Writes a base-archive object: the raw body file, its sibling
    /// <c>&lt;name&gt;.meta.json</c> sidecar, and the <c>_index.json</c> entries that
    /// resolve every prefix along the key down to the body's on-disk name.
    /// </summary>
    public void AddObject(string key, byte[] body, string? contentType = null, IDictionary<string, string>? amzMeta = null)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar);
        var bodyPath = Path.Combine(ArchiveRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(bodyPath)!);
        File.WriteAllBytes(bodyPath, body);

        var meta = new
        {
            key,
            size = body.Length,
            content_type = contentType,
            etag = (string?)null,
            last_modified = "Mon, 12 Oct 2009 17:50:00 GMT",
            amz_meta = amzMeta ?? new Dictionary<string, string>(),
        };

        // Write without a BOM so the sidecar mirrors the typical archive output.
        File.WriteAllBytes(bodyPath + ".meta.json", JsonSerializer.SerializeToUtf8Bytes(meta));

        IndexKey(key);
        WriteIndexes();
    }

    /// <summary>
    /// Registers an index entry that points at a body file which is intentionally not
    /// written to disk, to exercise the "index is correct but body missing" 404 path.
    /// </summary>
    public void AddDanglingIndexEntry(string key)
    {
        IndexKey(key);
        WriteIndexes();
    }

    private void IndexKey(string key)
    {
        // Register subdirectory entries for every ancestor prefix, then the leaf body.
        var segments = key.Split('/');
        var parentPrefix = string.Empty;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var childPrefix = parentPrefix.Length == 0 ? segments[i] : parentPrefix + "/" + segments[i];
            DirectoryIndex(parentPrefix)[childPrefix + "/"] = segments[i];
            parentPrefix = childPrefix;
        }

        DirectoryIndex(parentPrefix)[key] = segments[^1];
    }

    private Dictionary<string, string> DirectoryIndex(string prefix)
    {
        if (!_indexes.TryGetValue(prefix, out var index))
        {
            index = new Dictionary<string, string>(StringComparer.Ordinal);
            _indexes[prefix] = index;
        }

        return index;
    }

    private void WriteIndexes()
    {
        foreach (var (prefix, entries) in _indexes)
        {
            var directory = prefix.Length == 0
                ? ArchiveRoot
                : Path.Combine(ArchiveRoot, prefix.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);

            var document = new { prefix, entries };
            File.WriteAllBytes(Path.Combine(directory, "_index.json"), JsonSerializer.SerializeToUtf8Bytes(document));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; ignore transient file locks on CI.
        }
    }
}
