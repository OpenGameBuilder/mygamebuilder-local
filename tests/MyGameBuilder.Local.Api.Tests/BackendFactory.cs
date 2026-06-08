using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Hosts the backend in-memory for integration tests, pointing the piece store at a
/// caller-provided <see cref="TempArchive"/> so archive reads use real data. The
/// Kestrel URL binding from appsettings.json is overridden to avoid binding port 3000.
/// </summary>
public sealed class BackendFactory : WebApplicationFactory<Program>
{
    private readonly TempArchive _archive;

    public BackendFactory(TempArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PieceStore:ArchiveRoot"] = _archive.ArchiveRoot,
                ["PieceStore:DataRoot"] = _archive.DataRoot,
                // Neutralize the appsettings Kestrel endpoint so the test server binds an ephemeral port.
                ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
            });
        });
    }
}
