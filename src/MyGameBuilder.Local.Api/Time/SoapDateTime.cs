using System.Globalization;

namespace MyGameBuilder.Local.Api.Time;

/// <summary>
/// Formats timestamps in the exact shape the legacy client expects:
/// <c>yyyy-MM-ddTHH:mm:ss.000Z</c> (UTC, always a literal <c>.000</c> millisecond
/// component). Used for SOAP timestamps and object <c>LastModified</c> values.
/// </summary>
public static class SoapDateTime
{
    // The '.000Z' is a quoted literal so real sub-second precision is never rendered.
    private const string Pattern = "yyyy-MM-ddTHH:mm:ss'.000Z'";

    /// <summary>Current UTC time in the SOAP datetime format.</summary>
    public static string Now() => Format(DateTimeOffset.UtcNow);

    /// <summary>Formats the given instant (converted to UTC) in the SOAP datetime format.</summary>
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);
}
