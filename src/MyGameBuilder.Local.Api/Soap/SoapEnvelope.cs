using System.Text;

namespace MyGameBuilder.Local.Api.Soap;

/// <summary>
/// Builds the SOAP envelopes the S3 endpoints return. The envelope namespaces and
/// layout match the legacy server byte-for-byte (README 2B / 5). Operation response
/// bodies are supplied pre-rendered and inserted verbatim.
/// </summary>
public static class SoapEnvelope
{
    /// <summary>S3 operation namespace (<c>ns1</c>) used by all operation responses.</summary>
    public const string AwsNamespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private const string Header = """
        <?xml version="1.0" encoding="UTF-8"?>
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                          xmlns:SOAP-ENC="http://schemas.xmlsoap.org/soap/encoding/"
                          xmlns:xsi="http://www.w3.org/1999/XMLSchema-instance"
                          xmlns:xsd="http://www.w3.org/1999/XMLSchema"
                          soapenv:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <soapenv:Body>
        """;

    private const string Footer = """
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    /// <summary>Wraps a pre-rendered operation response body in a SOAP envelope.</summary>
    public static string Wrap(string body)
    {
        var builder = new StringBuilder(Header.Length + Footer.Length + (body?.Length ?? 0));
        builder.Append(Header).Append(body).Append(Footer);
        return builder.ToString();
    }

    /// <summary>Builds a SOAP fault envelope with the given fault string (and optional fault code).</summary>
    public static string Fault(string faultString, string? faultCode = null)
    {
        var inner = new StringBuilder("    <soapenv:Fault>");
        if (!string.IsNullOrEmpty(faultCode))
        {
            inner.Append("<faultcode>").Append(Xml.XmlText.Escape(faultCode)).Append("</faultcode>");
        }

        inner.Append("<faultstring>").Append(Xml.XmlText.Escape(faultString)).Append("</faultstring>");
        inner.Append("</soapenv:Fault>");
        return Wrap(inner.ToString());
    }
}
