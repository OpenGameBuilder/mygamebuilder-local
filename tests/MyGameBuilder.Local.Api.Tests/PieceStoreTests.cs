using System.Text;
using System.IO.Compression;
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
    public async Task DefaultProfileFallback_ReturnsVirtualGuestAndSystemProfiles()
    {
        using var archive = new TempArchive();
        var store = NewStore(archive);

        var guest = await store.GetAsync("guest/-/profile/user");
        var system = await store.GetAsync("!system/-/profile/user");

        Assert.NotNull(guest);
        Assert.NotNull(system);
        Assert.Equal("text/plain", guest!.ContentType);
        Assert.Contains("Default local profile for guest.", DecodeWriteUtfZlib(await guest.ReadBytesAsync()));
        Assert.Contains("Default local profile for !system.", DecodeWriteUtfZlib(await system!.ReadBytesAsync()));
        Assert.False(Directory.Exists(Path.Combine(archive.DataRoot, "objects")));
    }

    [Fact]
    public async Task Overlay_Put_WinsOverDefaultProfileFallback()
    {
        using var archive = new TempArchive();
        var store = NewStore(archive);
        await store.PutAsync("guest/-/profile/user", Encoding.UTF8.GetBytes("real profile"), "text/plain", [], default);

        var obj = await store.GetAsync("guest/-/profile/user");

        Assert.Equal("real profile", Encoding.UTF8.GetString(await obj!.ReadBytesAsync()));
    }

    [Fact]
    public void List_ByPrefix_IncludesDefaultProfileFallback()
    {
        using var archive = new TempArchive();
        var store = NewStore(archive);

        var keys = store.List("guest/-/profile/").Select(item => item.Key).ToList();

        Assert.Equal(["guest/-/profile/user"], keys);
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

    [Fact]
    public void Archive_DoesNotCacheMissingRootIndex()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", Encoding.UTF8.GetBytes("a"));

        var rootIndexPath = Path.Combine(archive.ArchiveRoot, "_index.json");
        var rootIndexBytes = File.ReadAllBytes(rootIndexPath);
        File.Delete(rootIndexPath);

        var store = NewStore(archive);
        Assert.DoesNotContain("alice", store.ListUsers());

        File.WriteAllBytes(rootIndexPath, rootIndexBytes);

        Assert.Contains("alice", store.ListUsers());
    }

    [Fact]
    public async Task Archive_DoesNotCacheMissingNestedIndex()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", Encoding.UTF8.GetBytes("a"), "image/png");

        var leafIndexPath = Path.Combine(archive.ArchiveRoot, "alice", "p", "tile", "_index.json");
        var leafIndexBytes = File.ReadAllBytes(leafIndexPath);
        File.Delete(leafIndexPath);

        var store = NewStore(archive);
        Assert.Null(await store.GetAsync("alice/p/tile/A"));

        File.WriteAllBytes(leafIndexPath, leafIndexBytes);

        var obj = await store.GetAsync("alice/p/tile/A");
        Assert.NotNull(obj);
        Assert.Equal("a", Encoding.UTF8.GetString(await obj!.ReadBytesAsync()));
    }

    private static IPieceStore NewStore(TempArchive archive)
        => new OverlayPieceStore(
            new ArchivePieceStore(archive.ArchiveRoot, new MemoryCache(new MemoryCacheOptions())),
            new DataPieceStore(archive.DataRoot));

    private static string DecodeWriteUtfZlib(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        var payload = output.ToArray();
        Assert.True(payload.Length >= 2);
        var length = (payload[0] << 8) | payload[1];
        Assert.Equal(length, payload.Length - 2);
        return Encoding.UTF8.GetString(payload, 2, length);
    }
}
