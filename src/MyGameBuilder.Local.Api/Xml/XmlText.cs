namespace MyGameBuilder.Local.Api.Xml;

/// <summary>
/// Minimal XML text escaping matching the legacy server: escapes
/// <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&quot;</c> and <c>&apos;</c>.
/// </summary>
public static class XmlText
{
    /// <summary>Escapes a value for inclusion in XML element text or an attribute.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Ampersand must be replaced first so later replacements are not double-escaped.
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
