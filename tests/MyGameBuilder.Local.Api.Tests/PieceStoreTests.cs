using System.Text;
using Microsoft.Extensions.Caching.Memory;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Unit tests for the archive/overlay piece store with no web host involved. These
/// isolate store behavior (index resolution, overlay precedence, tombstones, user
/// sizing, caching) from configuration plumbing.
/// </summary>
public sealed class PieceStoreTests
{
    [Fact]
    public async Task Archive_ResolvesViaIndex_AndReadsBody()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/Brick", Encoding.UTF8.GetBytes("brick"), "image/png");

        var store = NewStore(archive);

        var obj = await store.GetAsync("alice/p/tile/Brick");
        Assert.NotNull(obj);
        Assert.Equal("image/png", obj!.ContentType);
        Assert.Equal("brick", Encoding.UTF8.GetString(await obj.ReadBytesAsync()));
    }

    [Fact]
    public async Task Overlay_Put_WinsOverBase()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/Brick", Encoding.UTF8.GetBytes("old"));

        var store = NewStore(archive);
        await store.PutAsync("alice/p/tile/Brick", Encoding.UTF8.GetBytes("new"), null, [], default);

        var obj = await store.GetAsync("alice/p/tile/Brick");
        Assert.Equal("new", Encoding.UTF8.GetString(await obj!.ReadBytesAsync()));
    }

    [Fact]
    public async Task Delete_BaseKey_IsTombstoned()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/Brick", Encoding.UTF8.GetBytes("x"));

        var store = NewStore(archive);
        Assert.True(await store.DeleteAsync("alice/p/tile/Brick"));
        Assert.Null(await store.GetAsync("alice/p/tile/Brick"));
        Assert.False(await store.DeleteAsync("alice/p/tile/Brick"));
    }

    [Fact]
    public void UserSizeBytes_SumsOnlyThatUsersBodies()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[100]);
        archive.AddObject("alice/p/tile/B", new byte[200]);
        archive.AddObject("bob/p/tile/C", new byte[50]);

        var store = NewStore(archive);

        Assert.Equal(300, store.UserSizeBytes("alice"));
        Assert.Equal(50, store.UserSizeBytes("bob"));
    }

    [Fact]
    public void UserExists_TrueForArchiveUser()
    {
        using var archive = new TempArchive();
        archive.AddObject("carol/p/tile/A", new byte[1]);

        var store = NewStore(archive);

        Assert.True(store.UserExists("carol"));
        Assert.False(store.UserExists("nobody"));
    }

    [Fact]
    public async Task Archive_ToleratesUtf8BomInSidecar()
    {
        using var archive = new TempArchive();
        // Create the body, sidecar, and index normally, then rewrite only the sidecar with a BOM.
        archive.AddObject("alice/p/tile/Bom", Encoding.UTF8.GetBytes("bom-body"), "image/png");

        var sidecarPath = Path.Combine(archive.ArchiveRoot, "alice", "p", "tile", "Bom.meta.json");
        var json = "{\"key\":\"alice/p/tile/Bom\",\"size\":8,\"content_type\":\"image/png\"}";
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        await File.WriteAllBytesAsync(sidecarPath, withBom);

        var store = NewStore(archive);

        var obj = await store.GetAsync("alice/p/tile/Bom");
        Assert.NotNull(obj);
        Assert.Equal("bom-body", Encoding.UTF8.GetString(await obj!.ReadBytesAsync()));
    }

    [Fact]
    public async Task Archive_IndexPointsToMissingBody_ReturnsNull()
    {
        using var archive = new TempArchive();
        // The index lists the key, but no body file is written for it.
        archive.AddDanglingIndexEntry("alice/p/tile/Ghost");

        var store = NewStore(archive);

        Assert.Null(await store.GetAsync("alice/p/tile/Ghost"));
    }

    [Fact]
    public async Task Archive_UnindexedKey_ReturnsNull()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/Real", Encoding.UTF8.GetBytes("x"));

        var store = NewStore(archive);

        // A sibling key that is not in any index must not resolve.
        Assert.Null(await store.GetAsync("alice/p/tile/Missing"));
    }

    [Fact]
    public void List_ByPrefix_ReturnsIndexedKeys()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[10]);
        archive.AddObject("alice/p/tile/B", new byte[20]);
        archive.AddObject("alice/q/tile/C", new byte[30]);
        archive.AddObject("bob/p/tile/D", new byte[40]);

        var store = NewStore(archive);

        var keys = store.List("alice/p/").Select(i => i.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(["alice/p/tile/A", "alice/p/tile/B"], keys);
    }

    [Fact]
    public void ListUsers_ComesFromRootIndex()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[1]);
        archive.AddObject("bob/p/tile/B", new byte[1]);

        var store = NewStore(archive);

        var users = store.ListUsers();
        Assert.Contains("alice", users);
        Assert.Contains("bob", users);
    }

    [Fact]
    public async Task Archive_CachesIndexAcrossRepeatedLookups()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", Encoding.UTF8.GetBytes("a"), "image/png");

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new OverlayPieceStore(new ArchivePieceStore(archive.ArchiveRoot, cache), new DataPieceStore(archive.DataRoot));

        Assert.NotNull(await store.GetAsync("alice/p/tile/A"));

        // Delete the root index from disk; a cached store should still resolve the key.
        File.Delete(Path.Combine(archive.ArchiveRoot, "_index.json"));

        Assert.NotNull(await store.GetAsync("alice/p/tile/A"));
    }

    private static IPieceStore NewStore(TempArchive archive)
        => new OverlayPieceStore(
            new ArchivePieceStore(archive.ArchiveRoot, new MemoryCache(new MemoryCacheOptions())),
            new DataPieceStore(archive.DataRoot));
}
