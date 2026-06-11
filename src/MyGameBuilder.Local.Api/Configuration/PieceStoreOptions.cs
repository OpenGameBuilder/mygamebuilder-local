namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>
/// SQLite locations for the S3 piece-store emulation: a read-only archive
/// database (base) overlaid by a writable local database. Reads fall through the
/// overlay to the archive; writes and tombstones always land in the overlay DB.
/// </summary>
public sealed class PieceStoreOptions
{
    public const string SectionName = "PieceStore";

    /// <summary>
    /// Read-only unversioned SQLite archive produced by MyGameBuilder.Archive.S3.
    /// May be absent (treated as an empty base). Relative paths resolve against the content root.
    /// </summary>
    public string ArchivePath { get; set; } = "archive.sqlite";

    /// <summary>
    /// Writable SQLite overlay. Newly written objects and per-key tombstones live here.
    /// Relative paths resolve against the content root.
    /// </summary>
    public string OverlayPath { get; set; } = "overlay.sqlite";
}
