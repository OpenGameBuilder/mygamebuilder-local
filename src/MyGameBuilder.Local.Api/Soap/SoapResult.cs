namespace MyGameBuilder.Local.Api.Soap;

/// <summary>A rendered SOAP response envelope plus the HTTP status code to return.</summary>
public readonly record struct SoapResult(string Envelope, int StatusCode)
{
    /// <summary>A successful operation response (HTTP 200).</summary>
    public static SoapResult Operation(string envelope) => new(envelope, StatusCodes.Status200OK);

    /// <summary>A fault response carrying the given HTTP status code.</summary>
    public static SoapResult FaultEnvelope(string envelope, int statusCode) => new(envelope, statusCode);
}
