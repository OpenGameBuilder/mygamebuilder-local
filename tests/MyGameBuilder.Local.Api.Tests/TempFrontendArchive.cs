using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Builds a throwaway frontend Wayback SQLite archive for endpoint tests.
/// </summary>
public sealed class TempFrontendArchive : IDisposable
{
    private const string DefaultSchema = "mgb-frontend-wayback-archive";
    private const string DefaultSchemaVersion = "2";

    public TempFrontendArchive(
        string schema = DefaultSchema,
        string schemaVersion = DefaultSchemaVersion)
    {
        Root = Path.Join(Path.GetTempPath(), "mgb-frontend-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ArchivePath = Path.Join(Root, "frontend.sqlite");
        CreateArchive(ArchivePath, schema, schemaVersion);
    }

    public string Root { get; }

    public string ArchivePath { get; }

    public static void CreateArchive(
        string archivePath,
        string schema = DefaultSchema,
        string schemaVersion = DefaultSchemaVersion)
    {
        var directory = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open(archivePath);
        InitializeArchive(connection, schema, schemaVersion);
    }

    public void AddAppHostCapture(
        string path,
        byte[] body,
        string timestamp = "20120101000000",
        string? contentType = null,
        string? cdxMimeType = null,
        params (string Name, string Value)[] responseHeaders)
    {
        var normalizedPath = path.TrimStart('/');
        AddCapture(
            "https://s3.amazonaws.com/apphost/" + normalizedPath,
            body,
            timestamp,
            contentType,
            cdxMimeType,
            "200",
            200,
            "OK",
            responseHeaders);
    }

    public void AddMyGameBuilderCapture(
        string path,
        byte[] body,
        string timestamp = "20120101000000",
        string? contentType = null,
        string? cdxMimeType = null,
        string cdxStatusCode = "200",
        int replayStatusCode = 200,
        string replayReasonPhrase = "OK",
        params (string Name, string Value)[] responseHeaders)
    {
        var normalizedPath = path.TrimStart('/');
        AddCapture(
            "https://mygamebuilder.com/" + normalizedPath,
            body,
            timestamp,
            contentType,
            cdxMimeType,
            cdxStatusCode,
            replayStatusCode,
            replayReasonPhrase,
            responseHeaders);
    }

    private void AddCapture(
        string originalUrl,
        byte[] body,
        string timestamp,
        string? contentType,
        string? cdxMimeType,
        string cdxStatusCode,
        int replayStatusCode,
        string replayReasonPhrase,
        IReadOnlyList<(string Name, string Value)> responseHeaders)
    {
        using var connection = Open(ArchivePath);
        using var transaction = connection.BeginTransaction();

        var canonicalUrl = Canonicalize(originalUrl);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO frontend_resource(canonical_url, sample_original_url)
            VALUES ($canonical_url, $sample_original_url)
            ON CONFLICT(canonical_url) DO NOTHING;
            """,
            new SqliteParameter("$canonical_url", canonicalUrl),
            new SqliteParameter("$sample_original_url", originalUrl));

        var resourceId = ScalarLong(
            connection,
            transaction,
            "SELECT resource_id FROM frontend_resource WHERE canonical_url = $canonical_url;",
            new SqliteParameter("$canonical_url", canonicalUrl));

        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO frontend_content(body_sha256, content_length_bytes, body)
            VALUES ($body_sha256, $content_length_bytes, $body)
            ON CONFLICT(body_sha256) DO NOTHING;
            """,
            new SqliteParameter("$body_sha256", sha),
            new SqliteParameter("$content_length_bytes", body.LongLength),
            new SqliteParameter("$body", body));

        var contentId = ScalarLong(
            connection,
            transaction,
            "SELECT content_id FROM frontend_content WHERE body_sha256 = $body_sha256;",
            new SqliteParameter("$body_sha256", sha));

        Execute(
            connection,
            transaction,
            """
            INSERT INTO frontend_capture(
                resource_id,
                capture_timestamp,
                original_url,
                cdx_mimetype,
                cdx_status_code,
                cdx_raw_json,
                replay_url,
                replay_status_code,
                replay_reason_phrase,
                replayed_utc,
                content_id,
                replay_content_length_bytes,
                replay_body_sha256
            )
            VALUES (
                $resource_id,
                $capture_timestamp,
                $original_url,
                $cdx_mimetype,
                $cdx_status_code,
                '{}',
                $replay_url,
                $replay_status_code,
                $replay_reason_phrase,
                '2012-01-01T00:00:00.000Z',
                $content_id,
                $content_length_bytes,
                $body_sha256
            );
            """,
            new SqliteParameter("$resource_id", resourceId),
            new SqliteParameter("$capture_timestamp", timestamp),
            new SqliteParameter("$original_url", originalUrl),
            new SqliteParameter("$cdx_mimetype", (object?)cdxMimeType ?? DBNull.Value),
            new SqliteParameter("$cdx_status_code", cdxStatusCode),
            new SqliteParameter("$replay_url", "https://web.archive.org/web/" + timestamp + "id_/" + originalUrl),
            new SqliteParameter("$replay_status_code", replayStatusCode),
            new SqliteParameter("$replay_reason_phrase", replayReasonPhrase),
            new SqliteParameter("$content_id", contentId),
            new SqliteParameter("$content_length_bytes", body.LongLength),
            new SqliteParameter("$body_sha256", sha));

        var captureId = ScalarLong(
            connection,
            transaction,
            """
            SELECT capture_id
            FROM frontend_capture
            WHERE capture_timestamp = $capture_timestamp
              AND original_url = $original_url;
            """,
            new SqliteParameter("$capture_timestamp", timestamp),
            new SqliteParameter("$original_url", originalUrl));

        var headers = new List<(string Name, string Value)>();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            headers.Add(("Content-Type", contentType));
        }

        headers.AddRange(responseHeaders);
        for (var i = 0; i < headers.Count; i++)
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO frontend_response_header(capture_id, header_order, name, value)
                VALUES ($capture_id, $header_order, $name, $value);
                """,
                new SqliteParameter("$capture_id", captureId),
                new SqliteParameter("$header_order", i),
                new SqliteParameter("$name", headers[i].Name),
                new SqliteParameter("$value", headers[i].Value));
        }

        transaction.Commit();
    }

    private static void InitializeArchive(SqliteConnection connection, string schema, string schemaVersion)
    {
        Execute(
            connection,
            null,
            """
            PRAGMA encoding = 'UTF-8';
            PRAGMA foreign_keys = ON;

            CREATE TABLE archive_info (
                name TEXT PRIMARY KEY COLLATE BINARY,
                value TEXT NOT NULL
            ) STRICT, WITHOUT ROWID;

            INSERT INTO archive_info(name, value)
            VALUES ('schema', $schema), ('schema_version', $schema_version);

            CREATE TABLE frontend_resource (
                resource_id INTEGER PRIMARY KEY,
                canonical_url TEXT NOT NULL COLLATE BINARY,
                sample_original_url TEXT NOT NULL COLLATE BINARY,
                UNIQUE(canonical_url)
            ) STRICT;

            CREATE TABLE frontend_content (
                content_id INTEGER PRIMARY KEY,
                body_sha256 TEXT NOT NULL COLLATE BINARY,
                content_length_bytes INTEGER NOT NULL,
                body BLOB NOT NULL,
                UNIQUE(body_sha256)
            ) STRICT;

            CREATE TABLE frontend_capture (
                capture_id INTEGER PRIMARY KEY,
                resource_id INTEGER NOT NULL REFERENCES frontend_resource(resource_id),
                capture_timestamp TEXT NOT NULL COLLATE BINARY,
                original_url TEXT NOT NULL COLLATE BINARY,
                cdx_mimetype TEXT NULL COLLATE BINARY,
                cdx_status_code TEXT NULL COLLATE BINARY,
                cdx_digest TEXT NULL COLLATE BINARY,
                cdx_length INTEGER NULL,
                cdx_redirect_url TEXT NULL COLLATE BINARY,
                cdx_raw_json TEXT NOT NULL,
                replay_url TEXT NULL COLLATE BINARY,
                replay_status_code INTEGER NULL,
                replay_reason_phrase TEXT NULL,
                replayed_utc TEXT NULL COLLATE BINARY,
                replay_error TEXT NULL,
                content_id INTEGER NULL REFERENCES frontend_content(content_id),
                replay_content_length_bytes INTEGER NULL,
                replay_body_sha256 TEXT NULL COLLATE BINARY,
                UNIQUE(capture_timestamp, original_url)
            ) STRICT;

            CREATE TABLE frontend_response_header (
                capture_id INTEGER NOT NULL REFERENCES frontend_capture(capture_id),
                header_order INTEGER NOT NULL,
                name TEXT NOT NULL COLLATE BINARY,
                value TEXT NOT NULL,
                PRIMARY KEY(capture_id, header_order)
            ) STRICT, WITHOUT ROWID;

            CREATE VIEW v_frontend_capture_lookup AS
            SELECT
                r.canonical_url,
                c.capture_id,
                c.capture_timestamp,
                c.original_url,
                c.cdx_mimetype,
                c.cdx_status_code,
                c.cdx_digest,
                c.cdx_length,
                c.cdx_redirect_url,
                c.replay_url,
                c.replay_status_code,
                c.replay_reason_phrase,
                c.replayed_utc,
                c.replay_error,
                c.replay_content_length_bytes,
                c.replay_body_sha256,
                c.content_id
            FROM frontend_capture c
            JOIN frontend_resource r ON r.resource_id = c.resource_id;
            """,
            new SqliteParameter("$schema", schema),
            new SqliteParameter("$schema_version", schemaVersion));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; ignore transient SQLite file locks on CI.
        }
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

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }
}
