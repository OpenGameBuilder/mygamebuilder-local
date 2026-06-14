using System.Net;
using System.Text;

namespace MyGameBuilder.Local.Api.Tests;

public sealed class FrontendEndpointsTests
{
    [Fact]
    public async Task Root_ServesArchivedMyGameBuilderHomePage()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddMyGameBuilderCapture(
            string.Empty,
            Encoding.UTF8.GetBytes("""<a href="https://s3.amazonaws.com/apphost/MGB.swf">Play</a>"""),
            contentType: "text/html; charset=utf-8");

        using var factory = new BackendFactory(pieces, frontend.ArchivePath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""<a href="http://localhost/apphost/MGB.swf">Play</a>""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Startup_WhenFrontendArchiveMissing_ShowsSetupError()
    {
        using var pieces = new TempArchive();
        var missingFrontendArchive = Path.Combine(pieces.Root, "missing-frontend.sqlite");
        using var factory = new BackendFactory(pieces, missingFrontendArchive);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MyGameBuilder Local needs frontend.sqlite", body);
        Assert.Contains("frontend.sqlite", body);
    }

    [Fact]
    public async Task AppHostAsset_ServesSwfFromFrontendArchive()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        var swfBytes = new byte[] { 0x46, 0x57, 0x53, 0x09 };
        frontend.AddAppHostCapture(
            "MGB.swf",
            swfBytes,
            contentType: "application/x-shockwave-flash",
            cdxMimeType: "application/x-shockwave-flash");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2025-08-19T17:56:51");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/MGB.swf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-shockwave-flash", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(swfBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AppHostAsset_UsesNewestCapture()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "scripts/app.js",
            Encoding.UTF8.GetBytes("old"),
            timestamp: "20110101000000",
            contentType: "application/javascript");
        frontend.AddAppHostCapture(
            "scripts/app.js",
            Encoding.UTF8.GetBytes("new"),
            timestamp: "20130101000000",
            contentType: "application/javascript");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2025-08-19T17:56:51");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/scripts/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AppHostAsset_UsesNewestCaptureAtOrBeforeConfiguredDate()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "scripts/app.js",
            Encoding.UTF8.GetBytes("may 3"),
            timestamp: "20170503235959",
            contentType: "application/javascript");
        frontend.AddAppHostCapture(
            "scripts/app.js",
            Encoding.UTF8.GetBytes("may 4"),
            timestamp: "20170504000000",
            contentType: "application/javascript");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2017-05-03");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/scripts/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("may 3", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AppHostAsset_UsesConfiguredTimeAsExactCutoff()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "scripts/app.js",
            Encoding.UTF8.GetBytes("early"),
            timestamp: "20170503123000",
            contentType: "application/javascript");
        frontend.AddAppHostCapture(
            "scripts/app.js",
            Encoding.UTF8.GetBytes("late"),
            timestamp: "20170503123001",
            contentType: "application/javascript");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2017-05-03T12:30:00");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/scripts/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("early", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LateRecoveredCarouselAsset_FallsBackToEarliestCapture()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "carousel_images/X2.PNG",
            Encoding.UTF8.GetBytes("first recovered"),
            timestamp: "20240717210150",
            contentType: "image/png");
        frontend.AddAppHostCapture(
            "carousel_images/X2.PNG",
            Encoding.UTF8.GetBytes("later recovered"),
            timestamp: "20250717210150",
            contentType: "image/png");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2017-05-03");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/carousel_images/X2.PNG");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("first recovered", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LateRecoveredGameMusicAsset_FallsBackToEarliestCapture()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "game_music/McLeod9/MindGear.mp3",
            Encoding.UTF8.GetBytes("first recovered"),
            timestamp: "20260613000000",
            contentType: "application/x-unknown-content-type",
            cdxMimeType: "application/x-unknown-content-type");
        frontend.AddAppHostCapture(
            "game_music/McLeod9/MindGear.mp3",
            Encoding.UTF8.GetBytes("later recovered"),
            timestamp: "20260614000000",
            contentType: "audio/mpeg");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2017-05-03");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/game_music/McLeod9/MindGear.mp3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("first recovered", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonFallbackAppHostAsset_RemainsBoundedByConfiguredDate()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "MGB.swf",
            Encoding.UTF8.GetBytes("future swf"),
            timestamp: "20240717210150",
            contentType: "application/x-shockwave-flash");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2017-05-03");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/MGB.swf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Root_SkipsNewerEmptyReplayRedirects()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddMyGameBuilderCapture(
            string.Empty,
            Encoding.UTF8.GetBytes("usable home"),
            timestamp: "20170623133506",
            contentType: "text/html; charset=utf-8");
        frontend.AddMyGameBuilderCapture(
            string.Empty,
            [],
            timestamp: "20250819175651",
            contentType: "text/plain",
            cdxStatusCode: "301",
            replayStatusCode: 302,
            replayReasonPhrase: "FOUND");

        using var factory = new BackendFactory(
            pieces,
            frontend.ArchivePath,
            frontendCaptureDateTime: "2025-08-19T17:56:51");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("usable home", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MyGameBuilderAsset_ServesFromFrontendArchive()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddMyGameBuilderCapture(
            "scripts/site.js",
            Encoding.UTF8.GetBytes("console.log('site');"),
            contentType: "application/javascript");

        using var factory = new BackendFactory(pieces, frontend.ArchivePath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/scripts/site.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("console.log('site');", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TextAsset_RewritesArchiveUrlsToServerBaseUrl()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "index.html",
            Encoding.UTF8.GetBytes(
                """
                <script src="https://www.mygamebuilder.com/scripts/site.js"></script>
                <link href="http://mygamebuilder.com/styles/site.css" rel="stylesheet">
                <img src="https://s3.amazonaws.com/apphost/images/logo.png">
                """),
            contentType: "text/html; charset=utf-8");

        using var factory = new BackendFactory(pieces, frontend.ArchivePath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("http://localhost/scripts/site.js", body);
        Assert.Contains("http://localhost/styles/site.css", body);
        Assert.Contains("http://localhost/apphost/images/logo.png", body);
        Assert.DoesNotContain("mygamebuilder.com", body);
        Assert.DoesNotContain("s3.amazonaws.com", body);
    }

    [Fact]
    public async Task CssAsset_RewritesArchiveUrlsToServerBaseUrl()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddMyGameBuilderCapture(
            "styles/site.css",
            Encoding.UTF8.GetBytes("body { background: url(https://s3.amazonaws.com/apphost/images/bg.png); }"),
            contentType: "text/css");

        using var factory = new BackendFactory(pieces, frontend.ArchivePath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/styles/site.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "body { background: url(http://localhost/apphost/images/bg.png); }",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AppHostAsset_IncludesQueryStringInFrontendArchiveLookup()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive();
        frontend.AddAppHostCapture(
            "scripts/cache.js?v=1",
            Encoding.UTF8.GetBytes("cached"),
            contentType: "application/javascript");

        using var factory = new BackendFactory(pieces, frontend.ArchivePath);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/apphost/scripts/cache.js?v=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Startup_WithUnsupportedFrontendArchiveSchema_FailsClearly()
    {
        using var pieces = new TempArchive();
        using var frontend = new TempFrontendArchive(schema: "mgb-jgi-test1-unversioned-archive");
        using var factory = new BackendFactory(pieces, frontend.ArchivePath);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Unsupported frontend archive schema", ex.ToString());
        Assert.Contains("mgb-frontend-wayback-archive", ex.ToString());
    }
}
