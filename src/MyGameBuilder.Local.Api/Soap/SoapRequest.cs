using System.Xml.Linq;

namespace MyGameBuilder.Local.Api.Soap;

/// <summary>
/// Parsed SOAP request: the operation's local name, its direct child parameters
/// (keyed by local element name), and every <c>&lt;Metadata&gt;</c> Name/Value pair
/// found anywhere in the request (README 5).
/// </summary>
public sealed class SoapRequest
{
    private SoapRequest(string operation, IReadOnlyDictionary<string, string> parameters, IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        Operation = operation;
        Parameters = parameters;
        Metadata = metadata;
    }

    /// <summary>The operation element's local name (e.g. <c>PutObjectInline</c>).</summary>
    public string Operation { get; }

    /// <summary>Direct child parameters of the operation element, keyed by local name.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>All <c>Metadata</c> Name/Value pairs found in the request, in document order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Metadata { get; }

    /// <summary>Returns the parameter value for <paramref name="name"/>, or <paramref name="defaultValue"/>.</summary>
    public string Param(string name, string defaultValue = "")
        => Parameters.TryGetValue(name, out var value) ? value : defaultValue;

    /// <summary>
    /// Parses a SOAP request body. Returns null when the body cannot be parsed or has
    /// no operation element; callers should respond with an "Invalid request" fault.
    /// Matching is by local name so namespaced and bare requests both work.
    /// </summary>
    public static SoapRequest? TryParse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var body = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        var operationElement = body?.Elements().FirstOrDefault();
        if (operationElement is null)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in operationElement.Elements())
        {
            // Direct children become params keyed by local name; last write wins.
            parameters[child.Name.LocalName] = child.Value;
        }

        var metadata = new List<KeyValuePair<string, string>>();
        foreach (var meta in document.Descendants().Where(e => e.Name.LocalName == "Metadata"))
        {
            var name = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            var value = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                metadata.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            }
        }

        return new SoapRequest(operationElement.Name.LocalName, parameters, metadata);
    }
}
