using System.Text;
using MyGameBuilder.Local.Api.Pieces;
using MyGameBuilder.Local.Api.Time;
using MyGameBuilder.Local.Api.Xml;

namespace MyGameBuilder.Local.Api.Soap;

/// <summary>
/// Executes the four supported S3 SOAP operations against the piece store and
/// renders their response bodies (README 5). Bodies are returned without the
/// surrounding envelope; <see cref="SoapResult"/> carries the HTTP status so the
/// endpoint layer can apply it. Indentation matches the legacy server output.
/// </summary>
public sealed class SoapOperationHandler
{
    private const string DefaultBucket = "JGI_test1";

    private readonly IPieceStore _pieces;

    public SoapOperationHandler(IPieceStore pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        _pieces = pieces;
    }

    /// <summary>Dispatches a parsed request to the matching operation by local name.</summary>
    public async Task<SoapResult> ExecuteAsync(SoapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Operation switch
        {
            "PutObjectInline" => await PutObjectInlineAsync(request, cancellationToken).ConfigureAwait(false),
            "GetObject" => await GetObjectAsync(request, cancellationToken).ConfigureAwait(false),
            "ListBucket" => ListBucket(request),
            "DeleteObject" => await DeleteObjectAsync(request, cancellationToken).ConfigureAwait(false),
            _ => SoapResult.FaultEnvelope(SoapEnvelope.Fault($"Unsupported operation: {request.Operation}"), StatusCodes.Status200OK),
        };
    }

    private async Task<SoapResult> PutObjectInlineAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var key = request.Param("Key");
        var data = request.Param("Data");

        byte[] body;
        try
        {
            body = data.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            body = Array.Empty<byte>();
        }

        var contentType = request.Metadata
            .FirstOrDefault(pair => string.Equals(pair.Key, "Content-Type", StringComparison.Ordinal)).Value;

        try
        {
            await _pieces.PutAsync(key, body, string.IsNullOrEmpty(contentType) ? null : contentType, request.Metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exc)
        {
            return SoapResult.FaultEnvelope(SoapEnvelope.Fault($"Archive write failed: {exc.Message}"), StatusCodes.Status500InternalServerError);
        }

        var bodyXml = $"""
                <ns1:PutObjectInlineResponse xmlns:ns1="{SoapEnvelope.AwsNamespace}">
                  <ns1:PutObjectInlineResponse>
                    <ns1:Timestamp>{SoapDateTime.Now()}</ns1:Timestamp>
                  </ns1:PutObjectInlineResponse>
                </ns1:PutObjectInlineResponse>
            """;

        return SoapResult.Operation(SoapEnvelope.Wrap(bodyXml));
    }

    private async Task<SoapResult> GetObjectAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var key = request.Param("Key");
        var obj = await _pieces.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (obj is null)
        {
            var fault = SoapEnvelope.Fault("The specified key does not exist", "Client.NoSuchKey");
            return SoapResult.FaultEnvelope(fault, StatusCodes.Status404NotFound);
        }

        var bytes = await obj.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
        var dataBase64 = Convert.ToBase64String(bytes);

        // Echo every stored metadata pair, then add Content-Type if known and not already present.
        var metadata = new List<KeyValuePair<string, string>>(obj.AmzMeta);
        if (!string.IsNullOrEmpty(obj.ContentType) &&
            !metadata.Any(pair => string.Equals(pair.Key, "Content-Type", StringComparison.Ordinal)))
        {
            metadata.Add(new KeyValuePair<string, string>("Content-Type", obj.ContentType));
        }

        var builder = new StringBuilder();
        builder.Append("    <ns1:GetObjectResponse xmlns:ns1=\"").Append(SoapEnvelope.AwsNamespace).Append("\">\n");
        builder.Append("      <ns1:GetObjectResponse>\n");
        builder.Append("        <ns1:Data>").Append(XmlText.Escape(dataBase64)).Append("</ns1:Data>\n");
        foreach (var pair in metadata)
        {
            builder.Append("        <ns1:Metadata>\n");
            builder.Append("          <ns1:Name>").Append(XmlText.Escape(pair.Key)).Append("</ns1:Name>\n");
            builder.Append("          <ns1:Value>").Append(XmlText.Escape(pair.Value)).Append("</ns1:Value>\n");
            builder.Append("        </ns1:Metadata>\n");
        }

        builder.Append("        <ns1:LastModified>").Append(SoapDateTime.Format(obj.LastModified)).Append("</ns1:LastModified>\n");
        builder.Append("      </ns1:GetObjectResponse>\n");
        builder.Append("    </ns1:GetObjectResponse>");

        return SoapResult.Operation(SoapEnvelope.Wrap(builder.ToString()));
    }

    private SoapResult ListBucket(SoapRequest request)
    {
        var bucket = request.Param("Bucket", DefaultBucket);
        var prefix = request.Param("Prefix");
        var marker = request.Param("Marker");
        var delimiter = request.Param("Delimiter");
        var maxKeys = ParseInt(request.Param("MaxKeys"), 1000);

        var items = _pieces.List(prefix);
        var byKey = items.ToDictionary(item => item.Key, item => item, StringComparer.Ordinal);

        var keys = byKey.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (marker.Length > 0)
        {
            keys = [.. keys.Where(k => string.CompareOrdinal(k, marker) > 0)];
        }

        var isTruncated = keys.Count > maxKeys;
        if (isTruncated)
        {
            keys = [.. keys.Take(maxKeys)];
        }

        var entries = new StringBuilder();
        if (delimiter.Length > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var remainder = key.Length >= prefix.Length ? key[prefix.Length..] : string.Empty;
                var delimPos = remainder.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimPos > 0)
                {
                    var commonPrefix = prefix + remainder[..(delimPos + delimiter.Length)];
                    if (seen.Add(commonPrefix))
                    {
                        entries.Append("      <ns1:CommonPrefixes>\n");
                        entries.Append("        <ns1:Prefix>").Append(XmlText.Escape(commonPrefix)).Append("</ns1:Prefix>\n");
                        entries.Append("      </ns1:CommonPrefixes>");
                    }
                }
                else
                {
                    AppendContents(entries, byKey[key]);
                }
            }
        }
        else
        {
            foreach (var key in keys)
            {
                AppendContents(entries, byKey[key]);
            }
        }

        var builder = new StringBuilder();
        builder.Append("    <ns1:ListBucketResponse xmlns:ns1=\"").Append(SoapEnvelope.AwsNamespace).Append("\">\n");
        builder.Append("      <ns1:ListBucketResponse>\n");
        builder.Append("        <ns1:Name>").Append(XmlText.Escape(bucket)).Append("</ns1:Name>\n");
        builder.Append("        <ns1:Prefix>").Append(XmlText.Escape(prefix)).Append("</ns1:Prefix>\n");
        builder.Append("        <ns1:Marker>").Append(XmlText.Escape(marker)).Append("</ns1:Marker>\n");
        builder.Append("        <ns1:MaxKeys>").Append(maxKeys).Append("</ns1:MaxKeys>\n");
        builder.Append("        <ns1:IsTruncated>").Append(isTruncated ? "true" : "false").Append("</ns1:IsTruncated>\n");
        builder.Append(entries);
        if (entries.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append("      </ns1:ListBucketResponse>\n");
        builder.Append("    </ns1:ListBucketResponse>");

        return SoapResult.Operation(SoapEnvelope.Wrap(builder.ToString()));
    }

    private async Task<SoapResult> DeleteObjectAsync(SoapRequest request, CancellationToken cancellationToken)
    {
        var key = request.Param("Key");
        var removed = await _pieces.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        var code = removed ? 204 : 404;

        var bodyXml = $"""
                <ns1:DeleteObjectResponse xmlns:ns1="{SoapEnvelope.AwsNamespace}">
                  <ns1:DeleteObjectResponse><ns1:Code>{code}</ns1:Code></ns1:DeleteObjectResponse>
                </ns1:DeleteObjectResponse>
            """;

        return SoapResult.Operation(SoapEnvelope.Wrap(bodyXml));
    }

    private static void AppendContents(StringBuilder builder, PieceListItem item)
    {
        builder.Append("      <ns1:Contents>\n");
        builder.Append("        <ns1:Key>").Append(XmlText.Escape(item.Key)).Append("</ns1:Key>\n");
        builder.Append("        <ns1:LastModified>").Append(SoapDateTime.Format(item.LastModified)).Append("</ns1:LastModified>\n");
        builder.Append("        <ns1:Size>").Append(item.Size).Append("</ns1:Size>\n");
        builder.Append("      </ns1:Contents>");
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;
}
