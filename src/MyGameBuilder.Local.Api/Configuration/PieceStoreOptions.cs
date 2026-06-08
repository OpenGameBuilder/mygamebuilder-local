namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>
/// Filesystem locations for the S3 piece-store emulation: a read-only archive
/// snapshot (base) overlaid by a writable data directory. Reads fall through the
/// overlay to the archive; writes and tombstones always land in the overlay.
/// </summary>
public sealed class PieceStoreOptions
{
    public const string SectionName = "PieceStore";

    /// <summary>
    /// Read-only archive snapshot root: the directory that contains the top-level
    /// <c>_index.json</c> (i.e. the bucket directory, typically <c>.../JGI_test1</c>).
    /// May be absent (treated as an empty base). Relative paths resolve against the content root.
    /// </summary>
    public string ArchiveRoot { get; set; } = "archive";

    /// <summary>
    /// Writable overlay root. Newly written objects and per-key tombstones live here.
    /// Relative paths resolve against the content root.
    /// </summary>
    public string DataRoot { get; set; } = "data";

    /// <summary>
    /// Upper bound on the archive store's in-memory cache size (sum of cached entry
    /// sizes: parsed index entry counts and resolved listing counts). Bounds memory for
    /// very large archives while keeping repeat lookups for active users fast.
    /// </summary>
    public long CacheSizeLimit { get; set; } = 200_000;
}
