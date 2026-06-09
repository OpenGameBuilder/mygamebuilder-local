using System.Net;
using System.Text;
using System.Xml.Linq;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Tests the Flex "object" fragment endpoints (README 4/7). Fragments are root-less,
/// so each body is wrapped before parsing. Archive-dependent behavior (ghost login,
/// used-KB) is driven by a real <see cref="TempArchive"/>.
/// </summary>
public sealed class AccountEndpointsTests
{
    [Fact]
    public async Task Healthz_ReturnsOk_WhenNoLaunchToken()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Startup_CreatesWritableDataDirectory()
    {
        using var archive = new TempArchive();
        Directory.Delete(archive.DataRoot, recursive: true);

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.EnsureSuccessStatusCode();
        Assert.True(Directory.Exists(archive.DataRoot));
        Assert.False(Directory.Exists(Path.Combine(archive.DataRoot, "objects")));
        Assert.False(Directory.Exists(Path.Combine(archive.DataRoot, "tombstones")));
    }

    [Fact]
    public async Task FlexLogin_SeededAccount_Succeeds()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flexlogin",
            new() { ["login"] = "foo", ["password"] = "bar" });

        Assert.Equal("1", fragment.Element("status")!.Value);
        Assert.Equal("Welcome back, foo!", fragment.Element("message")!.Value);
        Assert.Equal("1", fragment.Element("logincount")!.Value);
    }

    [Fact]
    public async Task FlexLogin_WithOrigin_ReturnsCorsHeader()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/user/flexlogin")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "foo",
                ["password"] = "bar",
            }),
        };
        request.Headers.Add("Origin", "http://localhost:8080");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal("*", values.Single());
    }

    [Fact]
    public async Task Heartbeat_Preflight_ReturnsCorsHeaders()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/user/flex_heartbeat_safe");
        request.Headers.Add("Origin", "http://localhost:8080");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Equal("*", origins.Single());
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods));
        Assert.Contains("POST", methods.Single());
    }

    [Fact]
    public async Task FlexLogin_WrongPassword_Fails()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flexlogin",
            new() { ["login"] = "foo", ["password"] = "wrong" });

        Assert.Equal("0", fragment.Element("status")!.Value);
        Assert.Equal("Invalid username or password", fragment.Element("message")!.Value);
    }

    [Fact]
    public async Task FlexLogin_ArchiveGhost_BypassesPassword_AndAutoCreates()
    {
        using var archive = new TempArchive();
        // A user that only exists in the archive (no DB row) must log in with any password.
        archive.AddObject("ghostuser/-/profile/user", Encoding.UTF8.GetBytes("profile"));

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flexlogin",
            new() { ["login"] = "ghostuser", ["password"] = "anything" });

        Assert.Equal("1", fragment.Element("status")!.Value);
        Assert.Equal("Welcome back, ghostuser!", fragment.Element("message")!.Value);
    }

    [Fact]
    public async Task FlexCreateUser_IsDisabled()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flexcreateuser",
            new() { ["login"] = "newbie", ["password"] = "pw" });

        Assert.Equal("0", fragment.Element("status")!.Value);
        Assert.Equal("Account creation is disabled", fragment.Element("message")!.Value);
    }

    [Fact]
    public async Task GetUserStats_ReportsArchiveBytesInKb()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/A", new byte[2048]);
        archive.AddObject("alice/p/tile/B", new byte[1024]);

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/get_user_stats",
            new() { ["username"] = "alice" });

        Assert.Equal("1", fragment.Element("status")!.Value);
        Assert.Equal("3", fragment.Element("usedKB")!.Value);
        Assert.Equal("16384", fragment.Element("maxKB")!.Value);
    }

    [Fact]
    public async Task Heartbeat_ReturnsDummyKeys()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await GetFragmentAsync(client, "/user/flex_heartbeat_safe");

        Assert.Equal("DUMMYKEY1&DUMMYKEY2&DUMMYKEY3&DUMMYKEY4", fragment.Element("keyz")!.Value);
        Assert.Equal("ok", fragment.Element("status")!.Value);
    }

    [Fact]
    public async Task BrowseUsers_UnionsDbAndArchiveUsers()
    {
        using var archive = new TempArchive();
        archive.AddObject("carol/p/tile/A", Encoding.UTF8.GetBytes("a"));

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await GetFragmentAsync(client, "/user/flex_browse_users");

        Assert.Equal("1", fragment.Element("status")!.Value);
        var logins = fragment.Element("users")!.Elements("user")
            .Select(u => u.Element("login")!.Value)
            .ToList();
        Assert.Contains("foo", logins);
        Assert.Contains("carol", logins);
    }

    [Fact]
    public async Task DeleteS3Object_RemovesArchiveKey()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/Gone", Encoding.UTF8.GetBytes("x"));

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flex_delete_s3object",
            new() { ["itemname"] = "alice/p/tile/Gone" });

        Assert.Equal("0", fragment.Element("mgb_error")!.Value);
        Assert.Equal("Deleted", fragment.Element("message")!.Value);
    }

    [Fact]
    public async Task StubEndpoint_ReturnsFixedBody()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/user/flex_get_conversations");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<conversations></conversations>", await response.Content.ReadAsStringAsync());
    }

    private static async Task<XElement> PostFormFragmentAsync(HttpClient client, string route, Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        var response = await client.PostAsync(route, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await WrapAsync(response);
    }

    private static async Task<XElement> GetFragmentAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await WrapAsync(response);
    }

    private static async Task<XElement> WrapAsync(HttpResponseMessage response)
    {
        Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        // Fragment responses have no root element; wrap so they can be parsed.
        return XElement.Parse("<root>" + body + "</root>");
    }
}
