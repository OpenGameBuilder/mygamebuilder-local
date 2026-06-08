using System.Text;
using MyGameBuilder.Local.Api.GameStats;
using MyGameBuilder.Local.Api.Http;
using MyGameBuilder.Local.Api.Xml;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// Game-stats and ratings endpoints (README 6), returning Flex "object" fragments.
/// Rows are auto-created zeroed on first reference. This subsystem is faked
/// (in-memory) per project scope; only the response shapes are wire-correct.
/// </summary>
public static class GameStatsEndpoints
{
    public static IEndpointRouteBuilder MapGameStatsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Map(app, "/user/flex_get_game_stats", GetGameStatsAsync);
        Map(app, "/user/flex_bump_play_counter", BumpPlayCounterAsync);
        Map(app, "/user/flex_log_play", BumpPlayCounterAsync);
        Map(app, "/user/flex_update_game_metadata", UpdateMetadataAsync);
        Map(app, "/user/flex_delete_gamestatus_if_exists", DeleteGameStatusAsync);
        Map(app, "/user/flex_record_rating", RecordRatingAsync);
        Map(app, "/user/flex_rate_game", RecordRatingAsync);
        Map(app, "/user/flex_get_ratings", GetRatingsAsync);
        Map(app, "/user/flex_list_games_by5", ListGamesAsync);

        return app;
    }

    private static void Map(IEndpointRouteBuilder app, string pattern, Delegate handler)
        => app.MapMethods(pattern, ["GET", "POST"], handler);

    private static async Task<IResult> GetGameStatsAsync(HttpRequest request, GameStatStore store)
    {
        var (user, game) = await ReadKeyAsync(request).ConfigureAwait(false);
        var stat = store.GetOrCreate(user, game);
        return XmlResults.Xml(GameStatXml.GameStat(stat));
    }

    private static async Task<IResult> BumpPlayCounterAsync(HttpRequest request, GameStatStore store)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var stat = store.GetOrCreate(fields.FormOrQuery("username", "foo"), fields.FormOrQuery("gamename"));

        var plays = ParseInt(fields.FormOrQuery("bumpplayscount"), 0);
        var completions = ParseInt(fields.FormOrQuery("bumpcompletionscount"), 0);
        if (plays != 0 || completions != 0)
        {
            stat.PlaysCounter += plays;
            stat.CompletionsCounter += completions;
            stat.Sequence = store.NextSequence();
        }

        return XmlResults.Xml(GameStatXml.GameStat(stat));
    }

    private static async Task<IResult> UpdateMetadataAsync(HttpRequest request, GameStatStore store)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var stat = store.GetOrCreate(fields.FormOrQuery("username", "foo"), fields.FormOrQuery("gamename"));

        stat.GameStatus = ParseInt(fields.FormOrQuery("gamestatus"), stat.GameStatus);
        stat.GameType = ParseInt(fields.FormOrQuery("gametype"), stat.GameType);
        stat.GameGenre = fields.FormOrQuery("gamegenre", stat.GameGenre);
        stat.Description = fields.FormOrQuery("description", stat.Description);
        stat.Sequence = store.NextSequence();

        return XmlResults.Xml(GameStatXml.GameStat(stat));
    }

    private static async Task<IResult> DeleteGameStatusAsync(HttpRequest request, GameStatStore store)
    {
        var (user, game) = await ReadKeyAsync(request).ConfigureAwait(false);
        var stat = store.Reset(user, game);
        return XmlResults.Xml(GameStatXml.GameStat(stat));
    }

    private static async Task<IResult> RecordRatingAsync(HttpRequest request, GameStatStore store)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var user = fields.FormOrQuery("username", "foo");
        var game = fields.FormOrQuery("gamename");
        var stat = store.GetOrCreate(user, game);

        var graphics = ParseInt(fields.FormOrQuery("graphicsrating"), 0);
        var gameplay = ParseInt(fields.FormOrQuery("gameplayrating"), 0);
        var raterName = fields.FormOrQuery("ratername");

        // Counts only increment for positive ratings; totals always accumulate.
        stat.RatingTotalGraphics += graphics;
        if (graphics > 0)
        {
            stat.RatingCountGraphics++;
        }

        stat.RatingTotalGameplay += gameplay;
        if (gameplay > 0)
        {
            stat.RatingCountGameplay++;
        }

        stat.Sequence = store.NextSequence();

        return XmlResults.Xml(
            "<status>1</status><rating>" +
            $"<user>{XmlText.Escape(stat.User)}</user>" +
            $"<game>{XmlText.Escape(stat.Game)}</game>" +
            $"<ratername>{XmlText.Escape(raterName)}</ratername>" +
            "</rating>");
    }

    private static async Task<IResult> GetRatingsAsync(HttpRequest request, GameStatStore store)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var user = fields.FormOrQuery("username", "foo");
        var game = fields.FormOrQuery("gamename");
        var raterName = fields.FormOrQuery("ratername");
        var stat = store.GetOrCreate(user, game);

        return XmlResults.Xml(
            $"<user>{XmlText.Escape(stat.User)}</user>" +
            $"<game>{XmlText.Escape(stat.Game)}</game>" +
            $"<grme>{XmlText.Escape(raterName)}</grme>" +
            $"<gpme>{XmlText.Escape(raterName)}</gpme>" +
            $"<graphics_average>{GameStatXml.Average(stat.GraphicsAverage)}</graphics_average>" +
            $"<graphics_count>{stat.RatingCountGraphics}</graphics_count>" +
            $"<gameplay_average>{GameStatXml.Average(stat.GameplayAverage)}</gameplay_average>" +
            $"<gameplay_count>{stat.RatingCountGameplay}</gameplay_count>");
    }

    private static async Task<IResult> ListGamesAsync(HttpRequest request, GameStatStore store)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var limit = ParseInt(fields.FormOrQuery("limit"), 5);
        var offset = ParseInt(fields.FormOrQuery("offset"), 0);
        var order = fields.FormOrQuery("order", "plays");
        var statusFilter = fields.FormOrQuery("gamestatus");
        var typeFilter = fields.FormOrQuery("gametype");

        IEnumerable<GameStat> query = store.All();
        query = ApplyFilter(query, statusFilter, stat => stat.GameStatus);
        query = ApplyFilter(query, typeFilter, stat => stat.GameType);

        var filtered = query.ToList();
        var gameCount = filtered.Count;

        IEnumerable<GameStat> ordered = order switch
        {
            "plays" or "mostplays" or "plays_counter" =>
                filtered.OrderByDescending(s => s.PlaysCounter).ThenByDescending(s => s.Sequence),
            "rating" or "rated" =>
                filtered.OrderByDescending(s => s.RatingTotalGraphics + s.RatingTotalGameplay).ThenByDescending(s => s.Sequence),
            _ => filtered.OrderByDescending(s => s.Sequence),
        };

        if (offset > 0)
        {
            ordered = ordered.Skip(offset);
        }

        if (limit >= 0)
        {
            ordered = ordered.Take(limit);
        }

        var rows = ordered.ToList();

        var builder = new StringBuilder();
        builder.Append("<resultcount>").Append(rows.Count).Append("</resultcount>");
        builder.Append("<gamecount>").Append(gameCount).Append("</gamecount>");
        builder.Append("<gamestats>");
        foreach (var stat in rows)
        {
            GameStatXml.AppendGameStat(builder, stat);
        }

        builder.Append("</gamestats>");
        return XmlResults.Xml(builder.ToString());
    }

    private static IEnumerable<GameStat> ApplyFilter(IEnumerable<GameStat> source, string filter, Func<GameStat, int> selector)
    {
        if (string.IsNullOrEmpty(filter) || filter == "-1" || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        if (!int.TryParse(filter, out var wanted))
        {
            return source;
        }

        return source.Where(stat => selector(stat) == wanted);
    }

    private static async Task<(string User, string Game)> ReadKeyAsync(HttpRequest request)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        return (fields.FormOrQuery("username", "foo"), fields.FormOrQuery("gamename"));
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;
}
