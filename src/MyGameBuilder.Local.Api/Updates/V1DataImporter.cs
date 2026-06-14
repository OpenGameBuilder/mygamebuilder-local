using System.Text.Json;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class V1DataImporter
{
    public const int DefaultLargeImportThreshold = 100_000;

    private const string MetadataSuffix = ".meta.json";

    private static readonly EnumerationOptions s_metadataEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly DataPieceStore _dataStore;
    private readonly ILogger<V1DataImporter> _logger;
    private readonly int _largeImportThreshold;

    public V1DataImporter(DataPieceStore dataStore, ILogger<V1DataImporter> logger)
        : this(dataStore, logger, DefaultLargeImportThreshold)
    {
    }

    public V1DataImporter(DataPieceStore dataStore, ILogger<V1DataImporter> logger, int largeImportThreshold)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(logger);
        if (largeImportThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(largeImportThreshold), largeImportThreshold, "The threshold must be positive.");
        }

        _dataStore = dataStore;
        _logger = logger;
        _largeImportThreshold = largeImportThreshold;
    }

    public Task<V1ImportScanResult> ScanAsync(string? directoryPath, CancellationToken cancellationToken = default)
    {
        var root = NormalizeDirectoryPath(directoryPath);
        var count = 0;
        var scanComplete = true;

        foreach (var _ in EnumerateMetadataFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > _largeImportThreshold)
            {
                scanComplete = false;
                break;
            }
        }

        return Task.FromResult(new V1ImportScanResult(
            root,
            count,
            scanComplete,
            count > _largeImportThreshold,
            ScanMessage(count, scanComplete)));
    }

    public async Task<V1ImportResult> ImportAsync(string? directoryPath, bool largeImportConfirmed, CancellationToken cancellationToken = default)
    {
        var root = NormalizeDirectoryPath(directoryPath);
        if (!largeImportConfirmed)
        {
            var scan = await ScanAsync(root, cancellationToken).ConfigureAwait(false);
            if (scan.RequiresArchiveConfirmation)
            {
                throw new V1ImportConfirmationRequiredException(scan);
            }
        }

        var counters = new V1ImportCounters();
        foreach (var metadataPath in EnumerateMetadataFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.FoundCount++;

            var bodyPath = metadataPath[..^MetadataSuffix.Length];
            if (!File.Exists(bodyPath))
            {
                counters.MissingBodyCount++;
                continue;
            }

            if (!TryBuildKey(root, bodyPath, out var key))
            {
                counters.InvalidKeyCount++;
                continue;
            }

            V1PieceMetadata metadata;
            try
            {
                metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
            {
                counters.InvalidMetadataCount++;
                _logger.LogWarning(exc, "Skipping v1 metadata file {MetadataPath}; it could not be read.", metadataPath);
                continue;
            }

            byte[] body;
            try
            {
                body = await File.ReadAllBytesAsync(bodyPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                counters.MissingBodyCount++;
                _logger.LogWarning(exc, "Skipping v1 piece body {BodyPath}; it could not be read.", bodyPath);
                continue;
            }

            await _dataStore.PutAsync(key, body, metadata.ContentType, metadata.AmzMeta, cancellationToken).ConfigureAwait(false);
            counters.ImportedCount++;
        }

        return new V1ImportResult(
            root,
            counters.FoundCount,
            counters.ImportedCount,
            counters.SkippedCount,
            counters.MissingBodyCount,
            counters.InvalidMetadataCount,
            counters.InvalidKeyCount,
            counters.FoundCount > _largeImportThreshold,
            ImportMessage(counters));
    }

    private static IEnumerable<string> EnumerateMetadataFiles(string root) =>
        Directory.EnumerateFiles(root, "*" + MetadataSuffix, s_metadataEnumerationOptions);

    private static string NormalizeDirectoryPath(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Choose a v1 data folder before importing.", nameof(directoryPath));
        }

        var root = Path.GetFullPath(directoryPath.Trim());
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The folder was not found: {root}");
        }

        return root;
    }

    private static bool TryBuildKey(string root, string bodyPath, out string key)
    {
        var relative = Path.GetRelativePath(root, bodyPath);
        if (Path.IsPathFullyQualified(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            string.Equals(relative, "..", StringComparison.Ordinal))
        {
            key = string.Empty;
            return false;
        }

        key = relative
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        var parts = key.Split('/', 4);
        return parts.Length == 4 && parts.All(static part => part.Length > 0);
    }

    private static async Task<V1PieceMetadata> ReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(metadataPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var metadata = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty("amz_meta", out var amzMeta) && amzMeta.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in amzMeta.EnumerateObject())
            {
                metadata.Add(new KeyValuePair<string, string>(property.Name, JsonValueToString(property.Value)));
            }
        }

        var contentType = TryGetString(root, "content_type");
        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = metadata
                .FirstOrDefault(static pair => string.Equals(pair.Key, "Content-Type", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        return new V1PieceMetadata(
            string.IsNullOrWhiteSpace(contentType) ? null : contentType,
            metadata);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string JsonValueToString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText(),
        };

    private static string ScanMessage(int count, bool scanComplete)
    {
        if (!scanComplete)
        {
            return "Found more than 100,000 v1 pieces. Confirm this is personal user data before importing.";
        }

        return count == 1
            ? "Found 1 v1 piece."
            : $"Found {count:N0} v1 pieces.";
    }

    private static string ImportMessage(V1ImportCounters counters)
    {
        if (counters.SkippedCount == 0)
        {
            return counters.ImportedCount == 1
                ? "Imported 1 v1 piece."
                : $"Imported {counters.ImportedCount:N0} v1 pieces.";
        }

        return $"Imported {counters.ImportedCount:N0} v1 pieces and skipped {counters.SkippedCount:N0}.";
    }

    private sealed record V1PieceMetadata(
        string? ContentType,
        IReadOnlyList<KeyValuePair<string, string>> AmzMeta);

    private sealed class V1ImportCounters
    {
        public int FoundCount { get; set; }

        public int ImportedCount { get; set; }

        public int MissingBodyCount { get; set; }

        public int InvalidMetadataCount { get; set; }

        public int InvalidKeyCount { get; set; }

        public int SkippedCount => MissingBodyCount + InvalidMetadataCount + InvalidKeyCount;
    }
}

public sealed record V1ImportRequest(
    string? DirectoryPath,
    bool LargeImportConfirmed = false);

public sealed record V1ImportScanResult(
    string DirectoryPath,
    int PieceCount,
    bool ScanComplete,
    bool RequiresArchiveConfirmation,
    string Message);

public sealed record V1ImportResult(
    string DirectoryPath,
    int FoundCount,
    int ImportedCount,
    int SkippedCount,
    int MissingBodyCount,
    int InvalidMetadataCount,
    int InvalidKeyCount,
    bool WasLargeImport,
    string Message);

public sealed record V1ImportError(
    string Message,
    V1ImportScanResult? Scan = null);

public sealed class V1ImportConfirmationRequiredException : Exception
{
    public V1ImportConfirmationRequiredException(V1ImportScanResult scan)
        : base(scan.Message)
    {
        Scan = scan;
    }

    public V1ImportScanResult Scan { get; }
}
