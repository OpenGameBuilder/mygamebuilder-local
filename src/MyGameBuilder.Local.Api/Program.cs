using MyGameBuilder.Local.Api.Endpoints;
using MyGameBuilder.Local.Api.Extensions;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Wire-compatible local backend for the legacy MyGameBuilder Flash client, plus a same-origin
// front-end host (landing page + Ruffle launcher + static client assets) like the old Python
// server. Only the piece store (S3 archive emulation) is backed by real data; every other
// backend subsystem returns faked/defaulted but valid responses.
builder.Services.AddBackend(builder.Configuration, builder.Environment);

var app = builder.Build();

// Serve the Flash client bundle (and any assets it requests) under /apphost.
app.UseFrontend();

app.MapHealthEndpoints();
app.MapFrontendEndpoints();
app.MapAccountEndpoints();
app.MapS3SoapEndpoints();
app.MapGameStatsEndpoints();
app.MapStubEndpoints();

// Print the navigable URL and resolved data directories once the server is listening.
app.LogStartupBanner();

app.Run();
