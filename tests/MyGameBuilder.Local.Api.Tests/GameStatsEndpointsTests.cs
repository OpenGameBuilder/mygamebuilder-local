using System.Net;
using System.Xml.Linq;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Tests the game-stats fragment endpoints (README 6). The store is in-memory and
/// faked per project scope, so these assert response shape and accumulation rules
/// rather than persistence.
/// </summary>
public sealed class GameStatsEndpointsTests
{
    [Fact]
    public async Task GetGameStats_AutoCreatesZeroedRow()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flex_get_game_stats",
            new() { ["username"] = "foo", ["gamename"] = "Pong" });

        var stat = fragment.Element("gamestat")!;
        Assert.Equal("foo", stat.Element("user")!.Value);
        Assert.Equal("Pong", stat.Element("game")!.Value);
        Assert.Equal("0", stat.Element("plays-counter")!.Value);
        Assert.Equal("0.00", stat.Element("rating_average_graphics")!.Value);
    }

    [Fact]
    public async Task GetGameStats_WhenUsernameMissing_UsesGuest()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        var fragment = await PostFormFragmentAsync(client, "/user/flex_get_game_stats",
            new() { ["gamename"] = "Pong" });

        Assert.Equal("guest", fragment.Element("gamestat")!.Element("user")!.Value);
    }

    [Fact]
    public async Task BumpPlayCounter_AddsToPlays()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        await PostFormFragmentAsync(client, "/user/flex_bump_play_counter",
            new() { ["username"] = "foo", ["gamename"] = "Pong", ["bumpplayscount"] = "3" });
        var fragment = await PostFormFragmentAsync(client, "/user/flex_bump_play_counter",
            new() { ["username"] = "foo", ["gamename"] = "Pong", ["bumpplayscount"] = "2" });

        Assert.Equal("5", fragment.Element("gamestat")!.Element("plays-counter")!.Value);
    }

    [Fact]
    public async Task RecordRating_CountsOnlyPositiveRatings()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        await PostFormFragmentAsync(client, "/user/flex_record_rating",
            new() { ["username"] = "foo", ["gamename"] = "Pong", ["graphicsrating"] = "4", ["gameplayrating"] = "0", ["ratername"] = "bob" });

        var fragment = await PostFormFragmentAsync(client, "/user/flex_get_ratings",
            new() { ["username"] = "foo", ["gamename"] = "Pong", ["ratername"] = "bob" });

        Assert.Equal("4.00", fragment.Element("graphics_average")!.Value);
        Assert.Equal("1", fragment.Element("graphics_count")!.Value);
        Assert.Equal("0.00", fragment.Element("gameplay_average")!.Value);
        Assert.Equal("0", fragment.Element("gameplay_count")!.Value);
    }

    [Fact]
    public async Task ListGamesBy5_ReturnsCountsAndRows()
    {
        using var archive = new TempArchive();
        using var factory = new BackendFactory(archive);
        using var client = factory.CreateClient();

        await PostFormFragmentAsync(client, "/user/flex_bump_play_counter",
            new() { ["username"] = "foo", ["gamename"] = "A", ["bumpplayscount"] = "1" });
        await PostFormFragmentAsync(client, "/user/flex_bump_play_counter",
            new() { ["username"] = "foo", ["gamename"] = "B", ["bumpplayscount"] = "5" });

        var fragment = await PostFormFragmentAsync(client, "/user/flex_list_games_by5",
            new() { ["order"] = "plays" });

        Assert.Equal("2", fragment.Element("resultcount")!.Value);
        Assert.Equal("2", fragment.Element("gamecount")!.Value);

        var games = fragment.Element("gamestats")!.Elements("gamestat")
            .Select(g => g.Element("game")!.Value)
            .ToList();
        Assert.Equal(["B", "A"], games);
    }

    private static async Task<XElement> PostFormFragmentAsync(HttpClient client, string route, Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        var response = await client.PostAsync(route, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        return XElement.Parse("<root>" + body + "</root>");
    }
}
