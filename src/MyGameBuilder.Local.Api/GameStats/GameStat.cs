namespace MyGameBuilder.Local.Api.GameStats;

/// <summary>
/// A <c>game_stats</c> row keyed by (user, game). Auto-created zeroed on first
/// reference. The accounts/stats DB is not part of the real archive, so this is an
/// in-memory faked store; only the response shapes need to be wire-correct.
/// </summary>
public sealed class GameStat
{
    public required string User { get; init; }

    public required string Game { get; init; }

    public int PlaysCounter { get; set; }

    public int CompletionsCounter { get; set; }

    public double RatingTotalGraphics { get; set; }

    public int RatingCountGraphics { get; set; }

    public double RatingTotalGameplay { get; set; }

    public int RatingCountGameplay { get; set; }

    public int GameStatus { get; set; }

    public int GameType { get; set; }

    public string GameGenre { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Monotonic recency marker used for ordering in list endpoints.</summary>
    public long Sequence { get; set; }

    public double GraphicsAverage => RatingCountGraphics == 0 ? 0 : RatingTotalGraphics / RatingCountGraphics;

    public double GameplayAverage => RatingCountGameplay == 0 ? 0 : RatingTotalGameplay / RatingCountGameplay;
}
