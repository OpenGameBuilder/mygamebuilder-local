using System.IO.Compression;
using System.Text;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// In-memory fallback profile pieces for special accounts the legacy client assumes
/// exist. These are never written to the overlay; real data/archive objects win.
/// </summary>
internal static class DefaultProfilePieces
{
    private static readonly DateTimeOffset s_lastModified = new(2009, 10, 12, 17, 50, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<KeyValuePair<string, string>> s_profileMeta =
    [
        new("width", "0"),
        new("height", "0"),
        new("tilename", "null"),
        new("blobencoding", "0"),
        new("comment", "null"),
        new("acl", ""),
    ];

    private static readonly IReadOnlyDictionary<string, PieceObject> s_profiles = new Dictionary<string, PieceObject>(StringComparer.Ordinal)
    {
        ["!system/-/profile/user"] = CreateProfile("!system/-/profile/user", BuildProfileText("!system", "Built-in MyGameBuilder system account")),
        ["guest/-/profile/user"] = CreateProfile("guest/-/profile/user", BuildProfileText("guest", "Local guest account")),
    };

    internal static bool TryGet(string key, out PieceObject? profile) => s_profiles.TryGetValue(key, out profile);

    internal static IReadOnlyCollection<PieceListItem> List(string prefix) =>
        s_profiles.Values
            .Where(profile => profile.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(profile => new PieceListItem(profile.Key, profile.Size, profile.LastModified))
            .ToList();

    private static PieceObject CreateProfile(string key, string profileText)
    {
        var body = EncodeWriteUtfZlib(profileText);
        return new PieceObject(
            key,
            body.LongLength,
            s_lastModified,
            "text/plain",
            s_profileMeta,
            _ => ValueTask.FromResult(body.ToArray()));
    }

    private static string BuildProfileText(string login, string status)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["userStatusComment"] = status,
            ["profile-general_info"] = $"Default local profile for {login}.",
            ["profile-general_info--privateflag"] = "false",
            ["maxQuotaKB"] = "16384",
            ["tutorialLevelCompleted"] = "",
            ["lastLoginDate"] = "00:00:00 01/01/2000 (GMT+0)",
            ["skillLevelTileMaker"] = "14",
            ["skillLevelActorMaker"] = "1",
            ["skillLevelMapMaker"] = "10",
            ["skillLevelGameMaker"] = "1",
            ["skillLevelGamePlayer"] = "1",
            ["skillLevelTutorialMaker"] = "1",
            ["skillLevelCurrentTileMaker"] = "14",
            ["skillLevelCurrentMapMaker"] = "8",
            ["mainBackgroundColor"] = "0xD0D0D0",
            ["informationpanelBackgroundColor"] = "0xD0D0D0",
            ["adpanelBackgroundColor"] = "0xD0D0D0",
            ["accountmanagementBackgroundColor"] = "0xcccccc",
            ["tilemakerBackgroundColor"] = "0x33ccff",
            ["actormakerBackgroundColor"] = "0x00cc00",
            ["mapmakerBackgroundColor"] = "0x3399ff",
            ["gamemakerBackgroundColor"] = "0x009933",
            ["gameplayerBackgroundColor"] = "0xD0D0D0",
            ["tutorialmakerBackgroundColor"] = "0x009933",
        };

        var builder = new StringBuilder();
        foreach (var (key, value) in fields)
        {
            builder.Append('#').Append(key).Append('|').Append(value);
        }

        return builder.ToString();
    }

    private static byte[] EncodeWriteUtfZlib(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Default profile is too large for Flash writeUTF encoding.");
        }

        using var payload = new MemoryStream();
        payload.WriteByte((byte)(utf8.Length >> 8));
        payload.WriteByte((byte)(utf8.Length & 0xff));
        payload.Write(utf8);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            payload.Position = 0;
            payload.CopyTo(zlib);
        }

        return compressed.ToArray();
    }
}
