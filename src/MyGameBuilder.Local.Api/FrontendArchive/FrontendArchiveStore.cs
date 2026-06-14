using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Frontend;

/// <summary>
/// Read-only frontend asset store backed by the Wayback SQLite archive produced by
/// MyGameBuilder.Archive.Frontend.
/// </summary>
public sealed class FrontendArchiveStore
{
    private const string ExpectedSchema = "mgb-frontend-wayback-archive";
    private const string ExpectedSchemaVersion = "2";
    private const string S3AppHostBaseUrl = "https://s3.amazonaws.com/apphost/";
    private const string MyGameBuilderBaseUrl = "https://mygamebuilder.com/";

    private static readonly FileExtensionContentTypeProvider s_contentTypes = CreateContentTypes();

    private readonly Lock _schemaGate = new();
    private bool _schemaChecked;

    public FrontendArchiveStore(string archivePath, string captureDateTime)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArchivePath = archivePath;
        CaptureCutoffTimestamp = FrontendOptions.ToWaybackTimestamp(captureDateTime);
    }

    public string ArchivePath { get; }

    public string CaptureCutoffTimestamp { get; }

    public bool IsMissing => !File.Exists(ArchivePath);

    /// <summary>
    /// Validates the required frontend archive.
    /// </summary>
    public FrontendArchiveStatus Initialize()
    {
        if (!File.Exists(ArchivePath))
        {
            return FrontendArchiveStatus.Missing;
        }

        EnsureSupportedArchive();
        return FrontendArchiveStatus.Ready;
    }

    public async ValueTask<FrontendArchiveAsset?> GetAppHostAssetAsync(
        string path,
        string queryString,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var originalUrl = ToOriginalUrl(S3AppHostBaseUrl, path, queryString);
        var asset = await GetAssetAsync(path, originalUrl, applyCaptureCutoff: true, cancellationToken).ConfigureAwait(false);
        if (asset is null && IsLateRecoveredAppHostAsset(path))
        {
            asset = await GetAssetAsync(path, originalUrl, applyCaptureCutoff: false, cancellationToken).ConfigureAwait(false);
        }

        return asset;
    }

    public async ValueTask<FrontendArchiveAsset?> GetMyGameBuilderAssetAsync(
        string path,
        string queryString,
        CancellationToken cancellationToken)
    {
        var originalUrl = ToOriginalUrl(MyGameBuilderBaseUrl, path, queryString);
        return await GetAssetAsync(path, originalUrl, applyCaptureCutoff: true, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSupportedArchive()
    {
        if (_schemaChecked)
        {
            return;
        }

        lock (_schemaGate)
        {
            if (_schemaChecked)
            {
                return;
            }

            try
            {
                using var connection = OpenReadOnly(ArchivePath);
                var schema = ReadArchiveInfo(connection, "schema");
                var schemaVersion = ReadArchiveInfo(connection, "schema_version");
                if (!string.Equals(schema, ExpectedSchema, StringComparison.Ordinal) ||
                    !string.Equals(schemaVersion, ExpectedSchemaVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Unsupported frontend archive schema " +
                        $"'{schema ?? "<missing>"}' version '{schemaVersion ?? "<missing>"}'. " +
                        $"Expected '{ExpectedSchema}' version '{ExpectedSchemaVersion}'.");
                }
            }
            catch (SqliteException exc)
            {
                throw new InvalidOperationException(
                    $"The frontend archive SQLite database could not be read: {ArchivePath}",
                    exc);
            }

            _schemaChecked = true;
        }
    }

    private static string? ReadContentType(SqliteConnection connection, long captureId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT value
            FROM frontend_response_header
            WHERE capture_id = $capture_id
              AND lower(name) = 'content-type'
            ORDER BY header_order
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId);
        return command.ExecuteScalar() as string;
    }

    private static string? ReadArchiveInfo(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM archive_info WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static string InferContentType(string path, string? cdxMimeType)
    {
        if (IsUsefulCdxMimeType(cdxMimeType))
        {
            return cdxMimeType!;
        }

        return s_contentTypes.TryGetContentType(path, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    private async ValueTask<FrontendArchiveAsset?> GetAssetAsync(
        string path,
        string originalUrl,
        bool applyCaptureCutoff,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ArchivePath))
        {
            return null;
        }

        EnsureSupportedArchive();

        var canonicalUrl = Canonicalize(originalUrl);

        await using var connection = OpenReadOnly(ArchivePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                c.capture_id,
                c.cdx_mimetype,
                fc.body
            FROM v_frontend_capture_lookup c
            JOIN frontend_content fc ON fc.content_id = c.content_id
            WHERE c.canonical_url = $canonical_url
              AND ($apply_capture_cutoff = 0 OR c.capture_timestamp <= $capture_timestamp)
              AND c.replay_error IS NULL
              AND c.replay_status_code BETWEEN 200 AND 299
              AND c.replay_content_length_bytes > 0
            ORDER BY
                CASE WHEN $apply_capture_cutoff = 0 THEN c.capture_timestamp END ASC,
                c.capture_timestamp DESC,
                c.capture_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$canonical_url", canonicalUrl);
        command.Parameters.AddWithValue("$capture_timestamp", CaptureCutoffTimestamp);
        command.Parameters.AddWithValue("$apply_capture_cutoff", applyCaptureCutoff ? 1 : 0);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var captureId = reader.GetInt64(0);
        var cdxMimeType = reader.IsDBNull(1) ? null : reader.GetString(1);
        var body = (byte[])reader["body"];
        var storedContentType = ReadContentType(connection, captureId);
        var contentType = IsUsefulContentType(storedContentType)
            ? storedContentType!
            : InferContentType(path, cdxMimeType);

        return new FrontendArchiveAsset(body, contentType);
    }

    private static string ToOriginalUrl(string baseUrl, string path, string queryString)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var normalizedQuery = string.IsNullOrEmpty(queryString) ? string.Empty : queryString;
        return baseUrl + normalizedPath + normalizedQuery;
    }

    private static bool IsLateRecoveredAppHostAsset(string path)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        return normalizedPath.StartsWith("carousel_images/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("mascot_images/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("game_music/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonicalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = "http",
            Host = NormalizeHost(uri.Host),
            Fragment = string.Empty,
        };

        if (IsDefaultPort(uri.Port))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeHost(string host)
    {
        var normalized = host.ToLowerInvariant();
        return normalized.StartsWith("www.", StringComparison.Ordinal)
            ? normalized["www.".Length..]
            : normalized;
    }

    private static bool IsDefaultPort(int port) =>
        port is -1 or 80 or 443;

    private static bool IsUsefulCdxMimeType(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unk", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "warc/revisit", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "application/x-unknown-content-type", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsefulContentType(string? value) =>
        IsUsefulCdxMimeType(value);

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".swf"] = "application/x-shockwave-flash";
        return provider;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed record FrontendArchiveAsset(byte[] Body, string ContentType);

public enum FrontendArchiveStatus
{
    Ready,
    Missing,
}
