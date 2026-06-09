using System.Collections.Concurrent;

namespace MyGameBuilder.Local.Api.GameStats;

/// <summary>
/// In-memory <c>game_stats</c> store. Rows are auto-created zeroed on first reference
/// (README 6). A monotonic sequence stamps recency so ordering endpoints are stable.
/// Faked per project scope: not persisted and not backed by the archive.
/// </summary>
public sealed class GameStatStore
{
    private readonly ConcurrentDictionary<(string User, string Game), GameStat> _stats = new();
    private long _sequence;

    /// <summary>Gets the row for (user, game), creating a zeroed row stamped with recency if missing.</summary>
    public GameStat GetOrCreate(string user, string game)
    {
        user = Normalize(user, "guest");
        game ??= string.Empty;

        return _stats.GetOrAdd((user, game), key => new GameStat
        {
            User = key.User,
            Game = key.Game,
            Sequence = Interlocked.Increment(ref _sequence),
        });
    }

    /// <summary>Removes the row then re-creates it zeroed (README flex_delete_gamestatus_if_exists).</summary>
    public GameStat Reset(string user, string game)
    {
        user = Normalize(user, "guest");
        game ??= string.Empty;
        _stats.TryRemove((user, game), out _);
        return GetOrCreate(user, game);
    }

    /// <summary>Snapshot of all rows (for list endpoints).</summary>
    public IReadOnlyList<GameStat> All() => _stats.Values.ToList();

    /// <summary>Next recency value; bump when a row is meaningfully updated.</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    private static string Normalize(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }
}
