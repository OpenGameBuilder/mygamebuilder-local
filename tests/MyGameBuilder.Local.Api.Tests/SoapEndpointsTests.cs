using System.Net;
using System.Text;
using System.Xml.Linq;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// End-to-end SOAP piece-store tests: the only subsystem backed by real archive data.
/// Exercises PUT to the overlay, GET from base + overlay, ListBucket prefix/delimiter,
/// and DeleteObject tombstone semantics over the three identical SOAP routes.
/// </summary>
public sealed class SoapEndpointsTests
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Ns1 = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task GetObject_ReturnsArchiveBody_AndMetadata()
    {
        using var archive = new TempArchive();
        archive.AddObject(
            "alice/project1/tile/Brick",
            Encoding.UTF8.GetBytes("brick-bytes"),
            contentType: "image/png",
            amzMeta: new Dictionary<string, string> { ["width"] = "32", ["height"] = "32" });

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await PostSoapAsync(client, "/soap", Operation("GetObject", ("Key", "alice/project1/tile/Brick")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.Descendants(Ns1 + "Data").Single().Value;
        Assert.Equal("brick-bytes", Encoding.UTF8.GetString(Convert.FromBase64String(data)));

        var metaPairs = doc.Descendants(Ns1 + "Metadata")
            .Select(m => (Name: m.Element(Ns1 + "Name")!.Value, Value: m.Element(Ns1 + "Value")!.Value))
            .ToList();
        Assert.Contains(("width", "32"), metaPairs);
        Assert.Contains(("height", "32"), metaPairs);
        Assert.Contains(("Content-Type", "image/png"), metaPairs);
    }

    [Fact]
    public async Task GetObject_Missing_Returns404Fault()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await PostSoapAsync(client, "/s3soap", Operation("GetObject", ("Key", "nobody/x/tile/y")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Client.NoSuchKey", doc.Descendants("faultcode").Single().Value);
    }

    [Fact]
    public async Task PutThenGet_RoundTripsThroughOverlay()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("new-actor"));
        var put = await PostSoapAsync(client, "/apphost/soap", PutObject("bob/proj/actor/Hero", payload, ("Content-Type", "text/plain"), ("comment", "hi")));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await PostSoapAsync(client, "/soap", Operation("GetObject", ("Key", "bob/proj/actor/Hero")));
        var doc = XDocument.Parse(await get.Content.ReadAsStringAsync());
        var data = doc.Descendants(Ns1 + "Data").Single().Value;
        Assert.Equal("new-actor", Encoding.UTF8.GetString(Convert.FromBase64String(data)));

        var metaPairs = doc.Descendants(Ns1 + "Metadata")
            .Select(m => (Name: m.Element(Ns1 + "Name")!.Value, Value: m.Element(Ns1 + "Value")!.Value));
        Assert.Contains(("comment", "hi"), metaPairs);
    }

    [Fact]
    public async Task ListBucket_FiltersByPrefix_AndSortsAscending()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/B", Encoding.UTF8.GetBytes("b"));
        archive.AddObject("alice/p/tile/A", Encoding.UTF8.GetBytes("a"));
        archive.AddObject("zoe/p/tile/C", Encoding.UTF8.GetBytes("c"));

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await PostSoapAsync(client, "/soap", Operation("ListBucket", ("Prefix", "alice/")));
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());

        var keys = doc.Descendants(Ns1 + "Contents").Select(c => c.Element(Ns1 + "Key")!.Value).ToList();
        Assert.Equal(["alice/p/tile/A", "alice/p/tile/B"], keys);
    }

    [Fact]
    public async Task ListBucket_WithDelimiter_EmitsCommonPrefixes()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/project1/tile/A", Encoding.UTF8.GetBytes("a"));
        archive.AddObject("alice/project2/tile/B", Encoding.UTF8.GetBytes("b"));

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await PostSoapAsync(client, "/soap", Operation("ListBucket", ("Prefix", "alice/"), ("Delimiter", "/")));
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());

        var prefixes = doc.Descendants(Ns1 + "CommonPrefixes")
            .Select(p => p.Element(Ns1 + "Prefix")!.Value)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["alice/project1/", "alice/project2/"], prefixes);
    }

    [Fact]
    public async Task DeleteObject_TombstonesBaseKey_ThenGetReturns404()
    {
        using var archive = new TempArchive();
        archive.AddObject("alice/p/tile/Doomed", Encoding.UTF8.GetBytes("x"));

        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var delete = await PostSoapAsync(client, "/soap", Operation("DeleteObject", ("Key", "alice/p/tile/Doomed")));
        var deleteDoc = XDocument.Parse(await delete.Content.ReadAsStringAsync());
        Assert.Equal("204", deleteDoc.Descendants(Ns1 + "Code").Single().Value);

        var get = await PostSoapAsync(client, "/soap", Operation("GetObject", ("Key", "alice/p/tile/Doomed")));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        // A second delete finds nothing left to remove.
        var deleteAgain = await PostSoapAsync(client, "/soap", Operation("DeleteObject", ("Key", "alice/p/tile/Doomed")));
        var deleteAgainDoc = XDocument.Parse(await deleteAgain.Content.ReadAsStringAsync());
        Assert.Equal("404", deleteAgainDoc.Descendants(Ns1 + "Code").Single().Value);
    }

    [Fact]
    public async Task UnparseableRequest_ReturnsInvalidRequestFault()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        using var content = new StringContent("not xml at all", Encoding.UTF8, "text/xml");
        var response = await client.PostAsync("/soap", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Invalid request", doc.Descendants("faultstring").Single().Value);
    }

    [Fact]
    public async Task UnknownOperation_ReturnsUnsupportedFault()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var response = await PostSoapAsync(client, "/soap", Operation("Frobnicate"));
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Unsupported operation: Frobnicate", doc.Descendants("faultstring").Single().Value);
    }

    private static async Task<HttpResponseMessage> PostSoapAsync(HttpClient client, string route, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "text/xml");
        return await client.PostAsync(route, content);
    }

    private static string Operation(string name, params (string Name, string Value)[] parameters)
    {
        var children = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            children.Append($"<{key}>{value}</{key}>");
        }

        return Envelope($"<{name} xmlns=\"{Ns1}\">{children}</{name}>");
    }

    private static string PutObject(string key, string base64Data, params (string Name, string Value)[] metadata)
    {
        var metaXml = new StringBuilder();
        foreach (var (name, value) in metadata)
        {
            metaXml.Append($"<Metadata><Name>{name}</Name><Value>{value}</Value></Metadata>");
        }

        return Envelope(
            $"<PutObjectInline xmlns=\"{Ns1}\">" +
            "<Bucket>JGI_test1</Bucket>" +
            $"<Key>{key}</Key>" +
            metaXml +
            $"<Data>{base64Data}</Data>" +
            "</PutObjectInline>");
    }

    private static string Envelope(string operationXml) =>
        $"<soapenv:Envelope xmlns:soapenv=\"{Soap}\"><soapenv:Body>{operationXml}</soapenv:Body></soapenv:Envelope>";
}
