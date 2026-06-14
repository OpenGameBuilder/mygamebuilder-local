using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Hosts the backend in-memory for integration tests, pointing the piece store at a
/// caller-provided <see cref="TempArchive"/> so archive reads use real SQLite data. The
/// optional frontend archive path lets frontend tests serve captured assets from SQLite.
/// The Kestrel URL binding from appsettings.json is overridden to avoid binding port 3000.
/// </summary>
public sealed class BackendFactory : WebApplicationFactory<Program>
{
    private readonly TempArchive _archive;
    private readonly string? _frontendArchivePath;
    private readonly string? _frontendCaptureDateTime;

    public BackendFactory(
        TempArchive archive,
        string? frontendArchivePath = null,
        string? frontendCaptureDateTime = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;
        _frontendArchivePath = frontendArchivePath;
        _frontendCaptureDateTime = frontendCaptureDateTime;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var frontendArchivePath = _frontendArchivePath ?? Path.Combine(_archive.Root, "frontend.sqlite");
            if (_frontendArchivePath is null && !File.Exists(frontendArchivePath))
            {
                TempFrontendArchive.CreateArchive(frontendArchivePath);
            }

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PieceStore:ArchivePath"] = _archive.ArchivePath,
                ["PieceStore:OverlayPath"] = _archive.OverlayPath,
                // Most tests use a tiny valid frontend archive so startup validation can run.
                ["Frontend:ArchivePath"] = frontendArchivePath,
                ["Frontend:CaptureDateTime"] = _frontendCaptureDateTime,
                // Neutralize the appsettings Kestrel endpoint so the test server binds an ephemeral port.
                ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
            });
        });
    }
}
