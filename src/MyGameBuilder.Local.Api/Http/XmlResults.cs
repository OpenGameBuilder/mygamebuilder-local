using System.Text;

namespace MyGameBuilder.Local.Api.Http;

/// <summary>
/// Produces byte-exact responses for the legacy client. The XML payloads are
/// hand-built fragments / SOAP envelopes and must not be reformatted, so they are
/// returned verbatim as <c>text/xml; charset=utf-8</c>.
/// </summary>
public static class XmlResults
{
    private const string XmlContentType = "text/xml";
    private const string PlainContentType = "text/plain";

    /// <summary>Returns a hand-built XML fragment or SOAP envelope as text/xml.</summary>
    public static IResult Xml(string content, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(content, XmlContentType, Encoding.UTF8, statusCode);

    /// <summary>Returns a plain-text body (used by <c>/healthz</c>).</summary>
    public static IResult Plain(string content, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(content, PlainContentType, Encoding.UTF8, statusCode);
}
