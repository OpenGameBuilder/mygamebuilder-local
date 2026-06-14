using MyGameBuilder.Local.Api.Endpoints;
using MyGameBuilder.Local.Api.Extensions;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Wire-compatible local backend for the legacy MyGameBuilder Flash client, plus a same-origin
// front-end host (landing page + Ruffle launcher + archived client assets) like the old Python
// server. Piece/object data and frontend assets are backed by SQLite archives; every other
// backend subsystem returns faked/defaulted but valid responses.
builder.Services.AddBackend(builder.Configuration, builder.Environment);

var app = builder.Build();

// The legacy client may be hosted as localhost, 127.0.0.1, or via Ruffle from a
// static/file-like origin. Keep local API responses readable across those origins.
app.UseCors();

app.MapHealthEndpoints();
app.MapFrontendEndpoints();
app.MapAccountEndpoints();
app.MapS3SoapEndpoints();
app.MapGameStatsEndpoints();
app.MapStubEndpoints();

// Print the navigable URL and resolved data files once the server is listening.
app.LogStartupBanner();

app.Run();
