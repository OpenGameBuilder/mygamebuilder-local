using System.Text.RegularExpressions;

namespace MyGameBuilder.Local.Api.Updates;

public static partial class UpdateReleaseSelector
{
    public static IReadOnlyList<T> OrderByLatest<T>(
        IEnumerable<T> releases,
        Func<T, string> tagSelector,
        string prefix,
        string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(tagSelector);

        return releases
            .Select(release => new
            {
                Release = release,
                Parsed = TryParseTaggedVersion(tagSelector(release), prefix, suffix, out var version, out _) ? (SemanticVersion?)version : null,
            })
            .Where(static item => item.Parsed is not null)
            .OrderByDescending(static item => item.Parsed!.Value.Major)
            .ThenByDescending(static item => item.Parsed!.Value.Minor)
            .ThenByDescending(static item => item.Parsed!.Value.Patch)
            .Select(static item => item.Release)
            .ToArray();
    }

    public static bool TryParsePrefixedVersion(string tag, string prefix, out SemanticVersion version, out string normalized)
    {
        return TryParseTaggedVersion(tag, prefix, suffix: string.Empty, out version, out normalized);
    }

    public static bool TryParseTaggedVersion(string tag, string prefix, string suffix, out SemanticVersion version, out string normalized)
    {
        version = default;
        normalized = string.Empty;

        if (!tag.StartsWith(prefix, StringComparison.Ordinal) ||
            !tag.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffixLength = suffix.Length;
        var text = tag[prefix.Length..^suffixLength];
        if (suffixLength == 0)
        {
            text = tag[prefix.Length..];
        }

        var match = SemverRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        version = new SemanticVersion(
            int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, System.Globalization.CultureInfo.InvariantCulture));
        normalized = $"{version.Major}.{version.Minor}.{version.Patch}";
        return true;
    }

    [GeneratedRegex("^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)$")]
    private static partial Regex SemverRegex();
}

public readonly record struct SemanticVersion(int Major, int Minor, int Patch);
