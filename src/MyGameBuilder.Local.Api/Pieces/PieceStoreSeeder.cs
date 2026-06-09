using System.Text.Json;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Imports bundled archive-format seed objects into the writable overlay on first run.
/// The source directory may omit root indexes; object sidecars are the source of truth.
/// </summary>
public sealed class PieceStoreSeeder : IHostedService
{
    private const string SidecarSuffix = ".meta.json";

    private readonly DataPieceStore _data;
    private readonly PieceStoreOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PieceStoreSeeder> _logger;

    public PieceStoreSeeder(
        DataPieceStore data,
        IOptions<PieceStoreOptions> options,
        IHostEnvironment environment,
        ILogger<PieceStoreSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _data = data;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.SeedOnFirstRun)
        {
            _logger.LogInformation("Piece-store seed import is disabled.");
            return;
        }

        if (_data.HasAnyState)
        {
            _logger.LogInformation("Writable piece store already contains data or tombstones; skipping seed import.");
            return;
        }

        var seedRoot = ResolvePath(_environment.ContentRootPath, _options.SeedRoot);
        if (!Directory.Exists(seedRoot))
        {
            _logger.LogInformation("No bundled seed-data directory found at {SeedRoot}; starting with an empty writable piece store.", seedRoot);
            return;
        }

        var imported = 0;
        var skipped = 0;
        long bytes = 0;

        foreach (var sidecarPath in Directory.EnumerateFiles(seedRoot, "*" + SidecarSuffix, SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = TryReadMetadata(sidecarPath);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Key))
            {
                skipped++;
                _logger.LogWarning("Skipping seed sidecar with invalid metadata: {SidecarPath}", sidecarPath);
                continue;
            }

            var bodyPath = sidecarPath[..^SidecarSuffix.Length];
            if (!File.Exists(bodyPath))
            {
                skipped++;
                _logger.LogWarning("Skipping seed object {Key}; body file is missing at {BodyPath}", metadata.Key, bodyPath);
                continue;
            }

            try
            {
                var body = await File.ReadAllBytesAsync(bodyPath, cancellationToken).ConfigureAwait(false);
                var amzMeta = metadata.AmzMeta
                    .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))
                    .ToList();

                await _data.PutAsync(metadata.Key, body, metadata.ContentType, amzMeta, cancellationToken).ConfigureAwait(false);
                imported++;
                bytes += body.LongLength;
            }
            catch (IOException exc)
            {
                skipped++;
                _logger.LogWarning(exc, "Skipping seed object {Key}; failed to import from {BodyPath}", metadata.Key, bodyPath);
            }
            catch (UnauthorizedAccessException exc)
            {
                skipped++;
                _logger.LogWarning(exc, "Skipping seed object {Key}; failed to import from {BodyPath}", metadata.Key, bodyPath);
            }
        }

        _logger.LogInformation(
            "Seed import complete from {SeedRoot}: {ImportedCount} objects, {ImportedBytes} bytes imported, {SkippedCount} skipped.",
            seedRoot,
            imported,
            bytes,
            skipped);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static PieceMetadata? TryReadMetadata(string sidecarPath)
    {
        try
        {
            var bytes = StripUtf8Bom(File.ReadAllBytes(sidecarPath));
            return JsonSerializer.Deserialize<PieceMetadata>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolvePath(string contentRoot, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return contentRoot;
        }

        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsSpan(3);
        }

        return bytes;
    }
}
