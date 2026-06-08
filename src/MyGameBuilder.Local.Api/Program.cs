using MyGameBuilder.Local.Api.Endpoints;
using MyGameBuilder.Local.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Wire-compatible local backend for the legacy MyGameBuilder Flash client.
// Only the piece store (S3 archive emulation) is backed by real data; every other
// subsystem returns faked/defaulted but valid responses. Static/front-end serving
// is intentionally out of scope and owned by a separate front-end host project.
builder.Services.AddBackend(builder.Configuration, builder.Environment);

var app = builder.Build();

app.MapHealthEndpoints();
app.MapAccountEndpoints();
app.MapS3SoapEndpoints();
app.MapGameStatsEndpoints();
app.MapStubEndpoints();

app.Run();
