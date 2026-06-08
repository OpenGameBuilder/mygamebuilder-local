using System.Text;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Extensions;
using MyGameBuilder.Local.Api.Http;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// Front-end browser endpoints: a landing page at <c>/</c>, the Ruffle launcher at
/// <c>/apphost/MGB.html</c> (the old Python client used <c>/play</c>), the Flash
/// <c>crossdomain.xml</c> policy, and a friendly 404 for a missing SWF. The SWF and any
/// auxiliary assets are served as static files from the configured front-end directory
/// (see <see cref="WebApplicationExtensions.UseFrontend"/>); these endpoints only provide
/// the server-generated wrapper pages.
/// </summary>
public static class FrontendEndpoints
{
    public static IEndpointRouteBuilder MapFrontendEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ServiceProvider.GetRequiredService<IOptions<FrontendOptions>>().Value;
        var environment = app.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var frontendRoot = WebApplicationExtensions.ResolveContentPath(environment.ContentRootPath, options.RootPath);
        var swfUrl = $"{WebApplicationExtensions.FrontendRequestPath}/{options.SwfName}";

        // Simple landing page linking to the Ruffle launcher.
        app.MapGet("/", () => Html(BuildLandingPage(options.SwfName)));

        // Ruffle launcher. A user-supplied MGB.html in the front-end dir would be served by the
        // static-files middleware first; otherwise this server-generated wrapper is used.
        app.MapGet(WebApplicationExtensions.PlayPath, () => Html(BuildRufflePage(swfUrl)));

        // Flash cross-domain policy (allow-all is fine for a local single-origin host).
        app.MapGet("/crossdomain.xml", () => XmlResults.Xml(CrossDomainPolicy));

        // Static files serves the SWF when present; this only runs when it is missing, so it
        // points the user at the exact drop location instead of a bare 404.
        app.MapGet(swfUrl, () =>
        {
            var swfPath = Path.Combine(frontendRoot, options.SwfName);
            if (File.Exists(swfPath))
            {
                return Results.File(swfPath, "application/x-shockwave-flash");
            }

            return Results.Text(
                $"{options.SwfName} is missing. Place the Flash client at {swfPath} and reload.",
                "text/plain",
                Encoding.UTF8,
                StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static IResult Html(string content) => Results.Text(content, "text/html", Encoding.UTF8);

    private static string BuildLandingPage(string swfName) =>
        LandingPageTemplate
            .Replace("__PLAY_URL__", WebApplicationExtensions.PlayPath, StringComparison.Ordinal)
            .Replace("__SWF_NAME__", swfName, StringComparison.Ordinal);

    private static string BuildRufflePage(string swfUrl) =>
        RufflePageTemplate.Replace("__MGB_SWF_URL__", swfUrl, StringComparison.Ordinal);

    private const string CrossDomainPolicy =
        """
        <?xml version="1.0"?>
        <cross-domain-policy>
          <allow-access-from domain="*" />
        </cross-domain-policy>
        """;

    private const string LandingPageTemplate =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>MyGameBuilder Local</title>
          <style>
            body { margin: 0; padding: 2rem; background: #1e1e1e; color: #eee; font-family: Arial, sans-serif; line-height: 1.6; }
            main { max-width: 40rem; margin: 0 auto; }
            a { color: #8ec5ff; }
            code { background: #2d2d2d; padding: 0.1rem 0.35rem; border-radius: 4px; }
            .cta { display: inline-block; margin: 1rem 0; padding: 0.6rem 1.1rem; background: #0a64c8; color: #fff; border-radius: 6px; text-decoration: none; }
          </style>
        </head>
        <body>
        <main>
          <h1>MyGameBuilder Local</h1>
          <p>Local backend and Flash front-end for the legacy MyGameBuilder client.</p>
          <p><a class="cta" href="__PLAY_URL__">Launch MyGameBuilder (Ruffle)</a></p>
          <p>Seeded accounts: <code>foo</code> / <code>bar</code>, <code>guest</code> / <code>guest</code>.</p>
          <p>The Flash client (<code>__SWF_NAME__</code>) is served from the configured front-end directory under <code>/apphost</code>.</p>
        </main>
        </body>
        </html>
        """;

    private const string RufflePageTemplate =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>MyGameBuilder Local</title>
          <style>
            html, body { height: 100%; margin: 0; background: #1e1e1e; color: #eee; font-family: Arial, sans-serif; }
            body { display: flex; align-items: center; justify-content: center; }
            main { width: min(100vw, 1366px); height: min(100vh, 768px); display: flex; align-items: center; justify-content: center; }
            ruffle-player { display: block; width: 100%; height: 100%; max-width: 1366px; max-height: 768px; }
            a { color: #8ec5ff; }
          </style>
        </head>
        <body>
          <main id="player"></main>
          <script>
            window.RufflePlayer = window.RufflePlayer || {};
            window.RufflePlayer.config = {
              autoplay: 'on',
              unmuteOverlay: 'hidden',
              splashScreen: false,
              allowScriptAccess: true,
              socketProxy: [],
              upgradeToHttps: false,
              urlRewriteRules: [
                ['https://s3.amazonaws.com/soap', '/soap'],
                ['http://s3.amazonaws.com/soap', '/soap'],
                ['http://50.18.54.95:3000/user/flexlogin', '/user/flexlogin'],
                ['http://50.18.54.95:3000/user/flexcreateuser', '/user/flexcreateuser'],
                ['http://50.18.54.95:3000/user/flexlogout', '/user/flexlogout'],
                ['http://50.18.54.95:3000/user/flex_heartbeat_safe', '/user/flex_heartbeat_safe'],
                ['http://50.18.54.95:3000/user/get_user_stats', '/user/get_user_stats'],
                ['http://50.18.54.95:3000/user/flex_browse_users', '/user/flex_browse_users'],
                ['http://50.18.54.95:3000/log/logbug', '/log/logbug'],
                [/^http:\/\/50\.18\.54\.95:3000\/(.*)$/i, '/$1'],
                [/^https?:\/\/s3\.amazonaws\.com\/apphost\/(.*)$/i, '/apphost/$1'],
              ],
            };
          </script>
          <script src="https://unpkg.com/@ruffle-rs/ruffle"></script>
          <script>
            const ruffle = window.RufflePlayer.newest();
            const player = ruffle.createPlayer();
            document.getElementById('player').appendChild(player);
            player.load({ url: '__MGB_SWF_URL__' });
          </script>
        </body>
        </html>
        """;
}
