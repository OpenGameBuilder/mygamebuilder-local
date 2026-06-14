using System.Text;
using System.Text.RegularExpressions;

namespace MyGameBuilder.Local.Api.Frontend;

internal static partial class FrontendUrlRewriter
{
    private static readonly UTF8Encoding s_utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] RewriteIfInspectable(byte[] body, string contentType, string path, string serverBaseUrl)
    {
        if (!IsInspectable(contentType, path))
        {
            return body;
        }

        string text;
        try
        {
            text = s_utf8Strict.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return body;
        }

        var rewritten = ArchiveUrlRegex().Replace(text, match =>
        {
            var pathAndQuery = match.Groups["path"].Success ? match.Groups["path"].Value : string.Empty;
            return serverBaseUrl.TrimEnd('/') + pathAndQuery;
        });

        return string.Equals(text, rewritten, StringComparison.Ordinal)
            ? body
            : s_utf8NoBom.GetBytes(rewritten);
    }

    private static bool IsInspectable(string contentType, string path)
    {
        var mediaType = contentType.Split(';', 2)[0].Trim();
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"(?:(?:https?:)?//)(?:www\.)?(?:mygamebuilder\.com|s3\.amazonaws\.com)(?<path>/[^\s""'<>)]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveUrlRegex();
}
