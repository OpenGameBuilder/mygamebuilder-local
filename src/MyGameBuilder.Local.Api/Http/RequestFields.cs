namespace MyGameBuilder.Local.Api.Http;

/// <summary>
/// Reads request fields the way the legacy server did: POST form fields first,
/// with an optional query-string fallback for the stat/game endpoints. The form is
/// read asynchronously up front to avoid synchronous request-body access.
/// </summary>
public readonly struct RequestFields
{
    private readonly IFormCollection? _form;
    private readonly IQueryCollection _query;

    private RequestFields(IFormCollection? form, IQueryCollection query)
    {
        _form = form;
        _query = query;
    }

    /// <summary>Buffers the form (when present) and captures the query collection.</summary>
    public static async Task<RequestFields> ReadAsync(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        IFormCollection? form = null;
        if (request.HasFormContentType)
        {
            form = await request.ReadFormAsync().ConfigureAwait(false);
        }

        return new RequestFields(form, request.Query);
    }

    /// <summary>Form value for <paramref name="name"/>, or <paramref name="defaultValue"/> when absent.</summary>
    public string Form(string name, string defaultValue = "")
        => _form is not null && _form.TryGetValue(name, out var value) ? value.ToString() : defaultValue;

    /// <summary>Form value, falling back to the query string, then <paramref name="defaultValue"/>.</summary>
    public string FormOrQuery(string name, string defaultValue = "")
    {
        if (_form is not null && _form.TryGetValue(name, out var formValue))
        {
            return formValue.ToString();
        }

        return _query.TryGetValue(name, out var queryValue) ? queryValue.ToString() : defaultValue;
    }
}
