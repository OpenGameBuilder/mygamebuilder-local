using System.IO.Compression;
using System.Text;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Unit tests for the SQLite archive/overlay piece store with no web host involved.
/// </summary>
public sealed class PieceStoreTests
{
    [Fact]
    public async Task Archive_ResolvesFromSqlite_AndReadsBody()
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
    public async Task Archive_ReturnsKnownAndExtraMetadata()
    {
        using var archive = new TempArchive();
        archive.AddObject(
            "alice/p/tile/Brick",
            Encoding.UTF8.GetBytes("brick"),
            "image/png",
            new Dictionary<string, string>
            {
                ["width"] = "32",
                ["height"] = "32",
                ["custom"] = "kept",
            });

        var store = NewStore(archive);

        var obj = await store.GetAsync("alice/p/tile/Brick");

        Assert.Contains(new KeyValuePair<string, string>("width", "32"), obj!.AmzMeta);
        Assert.Contains(new KeyValuePair<string, string>("height", "32"), obj.AmzMeta);
        Assert.Contains(new KeyValuePair<string, string>("custom", "kept"), obj.AmzMeta);
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
    public async Task Overlay_Put_OverwritesPreviousOverlayRow()
    {
        using var archive = new TempArchive();
        var store = NewStore(archive);

        await store.PutAsync("alice/p/tile/Brick", Encoding.UTF8.GetBytes("old"), null, [new("comment", "old")], default);
        await store.PutAsync("alice/p/tile/Brick", Encoding.UTF8.GetBytes("new"), "image/png", [new("comment", "new")], default);

        var obj = await store.GetAsync("alice/p/tile/Brick");

        Assert.Equal("image/png", obj!.ContentType);
        Assert.Equal("new", Encoding.UTF8.GetString(await obj.ReadBytesAsync()));
        Assert.Equal([new KeyValuePair<string, string>("comment", "new")], obj.AmzMeta);
    }

    [Fact]
    public async Task Overlay_PreservesMetadataOrder()
    {
        using var archive = new TempArchive();
        var store = NewStore(archive);

        await store.PutAsync(
            "alice/p/actor/Hero",
            Encoding.UTF8.GetBytes("actor"),
            "text/plain",
            [new("Content-Type", "text/plain"), new("comment", "hi"), new("width", "0")],
            default);

        var obj = await store.GetAsync("alice/p/actor/Hero");

        Assert.Equal(
            [new("Content-Type", "text/plain"), new("comment", "hi"), new("width", "0")],
            obj!.AmzMeta);
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
    public async Task Delete_OverlayOnlyKey_RemovesObject()
    {
        using var archive = new TempArchive();
        var store = NewStore(archive);
        await store.PutAsync("alice/p/tile/Brick", Encoding.UTF8.GetBytes("x"), null, [], default);

        Assert.True(await store.DeleteAsync("alice/p/tile/Brick"));
        Assert.Null(await store.GetAsync("alice/p/tile/Brick"));
        Assert.False(await store.DeleteAsync("alice/p/tile/Brick"));
    }

    [Fact]
    public async Task UserSizeBytes_UsesEffectiveOverlayRows()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[100]);
        archive.AddObject("alice/p/tile/B", new byte[200]);
        archive.AddObject("bob/p/tile/C", new byte[50]);

        var store = NewStore(archive);
        await store.PutAsync("alice/p/tile/A", new byte[10], null, [], default);

        Assert.Equal(210, store.UserSizeBytes("alice"));
        Assert.Equal(50, store.UserSizeBytes("bob"));
    }

    [Fact]
    public async Task UserSizeBytes_ExcludesTombstonedArchiveRows()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[100]);
        archive.AddObject("alice/p/tile/B", new byte[200]);

        var store = NewStore(archive);
        await store.DeleteAsync("alice/p/tile/A");

        Assert.Equal(200, store.UserSizeBytes("alice"));
    }

    [Fact]
    public async Task UserExists_TrueForArchiveAndOverlayUsers()
    {
        using var archive = new TempArchive();
        archive.AddObject("carol/p/tile/A", new byte[1]);

        var store = NewStore(archive);
        await store.PutAsync("dana/p/tile/B", new byte[1], null, [], default);

        Assert.True(store.UserExists("carol"));
        Assert.True(store.UserExists("dana"));
        Assert.False(store.UserExists("nobody"));
    }

    [Fact]
    public void List_ByPrefix_ReturnsEffectiveKeys()
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
    public async Task ListUsers_UnionsArchiveOverlayAndFallbackUsers()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[1]);

        var store = NewStore(archive);
        await store.PutAsync("bob/p/tile/B", new byte[1], null, [], default);

        var users = store.ListUsers();
        Assert.Contains("alice", users);
        Assert.Contains("bob", users);
        Assert.Contains("guest", users);
        Assert.Contains("!system", users);
    }

    [Fact]
    public async Task MissingArchiveBehavesAsEmptyBase()
    {
        using var archive = new TempArchive(createArchive: false);

        var store = NewStore(archive);

        Assert.Null(await store.GetAsync("alice/p/tile/A"));
        Assert.DoesNotContain("alice", store.ListUsers());
    }

    [Fact]
    public void UnsupportedArchiveSchemaFailsClearly()
    {
        using var archive = new TempArchive();
        archive.SetArchiveSchema("mgb-jgi-test1-canonical-archive");
        var archiveStore = new ArchivePieceStore(archive.ArchivePath);

        var ex = Assert.Throws<InvalidOperationException>(archiveStore.Initialize);

        Assert.Contains("Unsupported piece archive schema", ex.Message);
    }

    private static IPieceStore NewStore(TempArchive archive)
        => new OverlayPieceStore(
            new ArchivePieceStore(archive.ArchivePath),
            new DataPieceStore(archive.OverlayPath));

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
