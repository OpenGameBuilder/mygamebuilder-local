using MyGameBuilder.Local.Api.Http;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// Fixed-response stub endpoints (README 7). These exist only to prevent client 404s
/// and return constant <c>text/xml</c> bodies. Messaging, friendships, wallposts,
/// highscores, and badges are not modeled.
/// </summary>
public static class StubEndpoints
{
    private static readonly (string Path, string Body)[] s_fixed =
    [
        ("/user/flex_get_conversations", "<conversations></conversations>"),
        ("/user/flex_get_message_thread", "<messages></messages>"),
        ("/user/flex_delete_message_thread", "<status>1</status>"),
        ("/user/flex_send_message", "<status>1</status>"),
        ("/user/flex_list_friendships", "<friendships></friendships>"),
        ("/user/flex_add_friendship", "<status>1</status>"),
        ("/user/flex_delete_friendship", "<status>1</status>"),
        ("/user/flex_get_wallposts", "<wallposts></wallposts>"),
        ("/user/flex_add_wallpost", "<status>1</status>"),
        ("/user/flex_delete_wallpost", "<status>1</status>"),
        ("/user/flex_get_highscores", "<highscores></highscores>"),
        ("/user/flex_submit_highscore", "<status>1</status>"),
        ("/user/flex_award_tutorial_badge", "<status>1</status>"),
        ("/user/flex_get_badges", "<badges></badges>"),
    ];

    public static IEndpointRouteBuilder MapStubEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var (path, body) in s_fixed)
        {
            app.MapMethods(path, ["GET", "POST"], () => XmlResults.Xml(body));
        }

        // Image upload is POST-only and returns a placeholder URL.
        app.MapPost("/user/uploadUserImageFile", () => XmlResults.Xml("<status>1</status><url>/placeholder.png</url>"));

        return app;
    }
}
