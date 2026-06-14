using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyGameBuilder.Local.Api.Pieces;
using MyGameBuilder.Local.Api.Updates;

namespace MyGameBuilder.Local.Api.Tests;

public sealed class V1DataImporterTests
{
    [Fact]
    public async Task ImportAsync_WritesV1PiecesToOverlay()
    {
        using var archive = new TempArchive();
        var root = Path.Combine(archive.Root, "v1");
        WritePiece(
            root,
            "alice",
            "project1",
            "tile",
            "Brick",
            Encoding.UTF8.GetBytes("brick"),
            "image/png",
            new Dictionary<string, string>
            {
                ["width"] = "32",
                ["height"] = "32",
                ["content-type"] = "image/png",
                ["comment"] = "imported",
            });

        var importer = NewImporter(archive);
        var scan = await importer.ScanAsync(root);

        Assert.Equal(1, scan.PieceCount);
        Assert.True(scan.ScanComplete);
        Assert.False(scan.RequiresArchiveConfirmation);

        var result = await importer.ImportAsync(root, largeImportConfirmed: false);

        Assert.Equal(1, result.FoundCount);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);

        var store = NewStore(archive);
        var piece = await store.GetAsync("alice/project1/tile/Brick");

        Assert.NotNull(piece);
        Assert.Equal("image/png", piece!.ContentType);
        Assert.Equal("brick", Encoding.UTF8.GetString(await piece.ReadBytesAsync()));
        Assert.Contains(new KeyValuePair<string, string>("width", "32"), piece.AmzMeta);
        Assert.Contains(new KeyValuePair<string, string>("comment", "imported"), piece.AmzMeta);
    }

    [Fact]
    public async Task ImportAsync_RequiresConfirmationAboveThreshold()
    {
        using var archive = new TempArchive();
        var root = Path.Combine(archive.Root, "v1");
        WritePiece(root, "alice", "project1", "tile", "A", [1], "application/octet-stream", new Dictionary<string, string>());
        WritePiece(root, "alice", "project1", "tile", "B", [2], "application/octet-stream", new Dictionary<string, string>());

        var importer = NewImporter(archive, largeImportThreshold: 1);
        var scan = await importer.ScanAsync(root);

        Assert.Equal(2, scan.PieceCount);
        Assert.False(scan.ScanComplete);
        Assert.True(scan.RequiresArchiveConfirmation);

        var exception = await Assert.ThrowsAsync<V1ImportConfirmationRequiredException>(() =>
            importer.ImportAsync(root, largeImportConfirmed: false));
        Assert.True(exception.Scan.RequiresArchiveConfirmation);

        var result = await importer.ImportAsync(root, largeImportConfirmed: true);

        Assert.Equal(2, result.FoundCount);
        Assert.Equal(2, result.ImportedCount);
        Assert.True(result.WasLargeImport);
    }

    private static V1DataImporter NewImporter(TempArchive archive, int largeImportThreshold = V1DataImporter.DefaultLargeImportThreshold) =>
        new(
            new DataPieceStore(archive.OverlayPath),
            NullLogger<V1DataImporter>.Instance,
            largeImportThreshold);

    private static IPieceStore NewStore(TempArchive archive) =>
        new OverlayPieceStore(
            new ArchivePieceStore(archive.ArchivePath),
            new DataPieceStore(archive.OverlayPath));

    private static void WritePiece(
        string root,
        string user,
        string project,
        string pieceType,
        string pieceName,
        byte[] body,
        string contentType,
        IReadOnlyDictionary<string, string> metadata)
    {
        var directory = Path.Combine(root, user, project, pieceType);
        Directory.CreateDirectory(directory);

        var bodyPath = Path.Combine(directory, pieceName);
        File.WriteAllBytes(bodyPath, body);

        var key = string.Join('/', user, project, pieceType, pieceName);
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["size"] = body.Length,
            ["content_type"] = contentType,
            ["etag"] = string.Empty,
            ["last_modified"] = "Fri, 17 Jun 2011 09:04:24 GMT",
            ["amz_meta"] = metadata,
        };

        File.WriteAllText(
            bodyPath + ".meta.json",
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }
}
