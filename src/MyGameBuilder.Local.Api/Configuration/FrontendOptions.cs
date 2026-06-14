namespace MyGameBuilder.Local.Api.Configuration;

using System.Globalization;

/// <summary>
/// Settings for serving the archived legacy Flash front-end. The configured
/// <see cref="ArchivePath"/> database is the frontend archive produced by
/// MyGameBuilder.Archive.Frontend and is read under the <c>/apphost</c> path so the
/// client's hard-coded <c>apphost/...</c> URLs resolve locally.
/// </summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";
    public const string DefaultCaptureDateTime = "2017-05-03";

    /// <summary>
    /// SQLite archive for front-end captures. Relative paths resolve against the content root.
    /// When this file is missing, the server stays online and serves a friendly setup error.
    /// </summary>
    public string ArchivePath { get; set; } = "frontend.sqlite";

    /// <summary>
    /// Serves the latest archived frontend file at or before this date/time. Date-only values
    /// include the whole day.
    /// </summary>
    public string CaptureDateTime { get; set; } = DefaultCaptureDateTime;

    /// <summary>
    /// Flash client file name served at <c>/apphost/{SwfName}</c> and loaded by the Ruffle page.
    /// </summary>
    public string SwfName { get; set; } = "MGB.swf";

    public static string ToWaybackTimestamp(string? configuredDateTime)
    {
        var value = string.IsNullOrWhiteSpace(configuredDateTime)
            ? DefaultCaptureDateTime
            : configuredDateTime.Trim();

        if (DateOnly.TryParseExact(
                value,
                ["yyyy-MM-dd", "yyyyMMdd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateOnly))
        {
            return dateOnly.ToDateTime(TimeOnly.MaxValue).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        if (!LooksLikeDateTime(value) &&
            DateOnly.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out dateOnly))
        {
            return dateOnly.ToDateTime(TimeOnly.MaxValue).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime))
        {
            return dateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException(
            $"Frontend:{nameof(CaptureDateTime)} must be a date or date/time, for example '{DefaultCaptureDateTime}' or '2017-05-03T12:30:00'.");
    }

    private static bool LooksLikeDateTime(string value) =>
        value.Contains(':', StringComparison.Ordinal) ||
        value.Contains('T', StringComparison.Ordinal);
}
