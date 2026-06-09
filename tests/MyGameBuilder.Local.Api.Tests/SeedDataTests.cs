using System.Net;
using System.Text;
using System.Xml.Linq;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Tests;

public sealed class SeedDataTests
{
    private static readonly XNamespace s_soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace s_ns1 = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task FreshOverlay_ImportsSeedData_OnStartup()
    {
        using var seed = new TempArchive();
        seed.AddObject(
            "seeduser/-/profile/user",
            Encoding.UTF8.GetBytes("seed-profile"),
            "text/plain",
            new Dictionary<string, string> { ["width"] = "0", ["height"] = "0" });

        using var appData = new TempArchive();
        using var factory = new BackendFactory(appData, seed.ArchiveRoot, seedOnFirstRun: true);
        using var client = factory.CreateClient();

        var browse = await GetFragmentAsync(client, "/user/flex_browse_users");
        var logins = browse.Element("users")!.Elements("user")
            .Select(user => user.Element("login")!.Value)
            .ToList();
        Assert.Contains("seeduser", logins);

        var response = await PostSoapAsync(client, Operation("GetObject", ("Key", "seeduser/-/profile/user")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.Descendants(s_ns1 + "Data").Single().Value;
        Assert.Equal("seed-profile", Encoding.UTF8.GetString(Convert.FromBase64String(data)));
    }

    [Fact]
    public async Task ExistingOverlayState_SkipsSeedImport()
    {
        using var seed = new TempArchive();
        seed.AddObject("seeduser/-/profile/user", Encoding.UTF8.GetBytes("seed-profile"));

        using var appData = new TempArchive();
        var data = new DataPieceStore(appData.DataRoot);
        await data.PutAsync("existing/project1/tile/Brick", Encoding.UTF8.GetBytes("already here"), null, [], default);

        using var factory = new BackendFactory(appData, seed.ArchiveRoot, seedOnFirstRun: true);
        using var client = factory.CreateClient();

        var seeded = await PostSoapAsync(client, Operation("GetObject", ("Key", "seeduser/-/profile/user")));
        Assert.Equal(HttpStatusCode.NotFound, seeded.StatusCode);

        var existing = await PostSoapAsync(client, Operation("GetObject", ("Key", "existing/project1/tile/Brick")));
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostSoapAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "text/xml");
        return await client.PostAsync("/soap", content);
    }

    private static string Operation(string name, params (string Name, string Value)[] parameters)
    {
        var children = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            children.Append($"<{key}>{value}</{key}>");
        }

        return $"<soapenv:Envelope xmlns:soapenv=\"{s_soap}\"><soapenv:Body><{name} xmlns=\"{s_ns1}\">{children}</{name}></soapenv:Body></soapenv:Envelope>";
    }

    private static async Task<XElement> GetFragmentAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return XElement.Parse("<root>" + body + "</root>");
    }
}
