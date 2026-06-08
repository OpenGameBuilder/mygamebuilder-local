using System.Globalization;
using System.Text;
using MyGameBuilder.Local.Api.Xml;

namespace MyGameBuilder.Local.Api.GameStats;

/// <summary>
/// Renders the reusable <c>&lt;gamestat&gt;</c> fragment exactly as the legacy client
/// expects (README 6): hyphenated counter names, underscore rating names, and
/// averages fixed to 2 decimals using the invariant culture.
/// </summary>
public static class GameStatXml
{
    /// <summary>Formats a rating average as <c>0.00</c> using the invariant culture.</summary>
    public static string Average(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Appends a single <c>&lt;gamestat&gt;</c> block for <paramref name="stat"/>.</summary>
    public static void AppendGameStat(StringBuilder builder, GameStat stat)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stat);

        builder.Append("<gamestat>")
            .Append("<user>").Append(XmlText.Escape(stat.User)).Append("</user>")
            .Append("<game>").Append(XmlText.Escape(stat.Game)).Append("</game>")
            .Append("<plays-counter>").Append(stat.PlaysCounter).Append("</plays-counter>")
            .Append("<completions-counter>").Append(stat.CompletionsCounter).Append("</completions-counter>")
            .Append("<rating_average_graphics>").Append(Average(stat.GraphicsAverage)).Append("</rating_average_graphics>")
            .Append("<rating_count_graphics>").Append(stat.RatingCountGraphics).Append("</rating_count_graphics>")
            .Append("<rating_average_gameplay>").Append(Average(stat.GameplayAverage)).Append("</rating_average_gameplay>")
            .Append("<rating_count_gameplay>").Append(stat.RatingCountGameplay).Append("</rating_count_gameplay>")
            .Append("<gamestatus>").Append(stat.GameStatus).Append("</gamestatus>")
            .Append("<gametype>").Append(stat.GameType).Append("</gametype>")
            .Append("<gamegenre>").Append(XmlText.Escape(stat.GameGenre)).Append("</gamegenre>")
            .Append("<description>").Append(XmlText.Escape(stat.Description)).Append("</description>")
            .Append("</gamestat>");
    }

    /// <summary>Returns a standalone <c>&lt;gamestat&gt;</c> fragment for <paramref name="stat"/>.</summary>
    public static string GameStat(GameStat stat)
    {
        var builder = new StringBuilder();
        AppendGameStat(builder, stat);
        return builder.ToString();
    }
}
