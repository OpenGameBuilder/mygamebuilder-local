using MyGameBuilder.Local.Api.Http;
using MyGameBuilder.Local.Api.Soap;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// S3 SOAP piece-storage endpoints (README 5). All three routes dispatch identically:
/// the raw body is parsed, the operation is executed against the piece store, and the
/// resulting envelope is returned as <c>text/xml</c> with the operation's HTTP status.
/// </summary>
public static class S3SoapEndpoints
{
    private static readonly string[] s_routes = ["/", "/soap", "/s3soap", "/apphost/soap"];

    public static IEndpointRouteBuilder MapS3SoapEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var route in s_routes)
        {
            app.MapPost(route, HandleAsync);
        }

        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        SoapOperationHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        string xml;
        using (var reader = new StreamReader(request.Body))
        {
            xml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var parsed = SoapRequest.TryParse(xml);
        if (parsed is null)
        {
            loggerFactory.CreateLogger("MyGameBuilder.Local.Api.Soap.Endpoint")
                .LogWarning("SOAP request could not be parsed at {Path}; request length was {Length} characters.", request.Path, xml.Length);
            return XmlResults.Xml(SoapEnvelope.Fault("Invalid request"));
        }

        var result = await handler.ExecuteAsync(parsed, cancellationToken).ConfigureAwait(false);
        return XmlResults.Xml(result.Envelope, result.StatusCode);
    }
}
