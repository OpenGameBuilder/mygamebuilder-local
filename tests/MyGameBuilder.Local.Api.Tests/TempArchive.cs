using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Local.Api.Tests;

/// <summary>
/// Builds a throwaway unversioned SQLite S3 archive plus a sibling writable overlay path.
/// Disposing deletes the temporary directory tree.
/// </summary>
public sealed class TempArchive : IDisposable
{
    private long _nextObjectId = 1;
    private long _nextSourceOrdinal;

    public TempArchive(bool createArchive = true)
    {
        Root = Path.Join(Path.GetTempPath(), "mgb-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ArchivePath = Path.Join(Root, "archive.sqlite");
        OverlayPath = Path.Join(Root, "overlay.sqlite");
        if (createArchive)
        {
            InitializeArchive("mgb-jgi-test1-unversioned-archive");
        }
    }

    /// <summary>Parent directory containing both SQLite files.</summary>
    public string Root { get; }

    /// <summary>Read-only unversioned archive database (<c>PieceStore:ArchivePath</c>).</summary>
    public string ArchivePath { get; }

    /// <summary>Writable overlay database (<c>PieceStore:OverlayPath</c>).</summary>
    public string OverlayPath { get; }

    /// <summary>Writes an archive object row and its MyGameBuilder key projection.</summary>
    public void AddObject(string key, byte[] body, string? contentType = null, IDictionary<string, string>? amzMeta = null)
    {
        var part = ParseMgbKey(key);
        var metadata = amzMeta ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var objectId = _nextObjectId++;
        var ordinal = _nextSourceOrdinal++;

        using var connection = Open(ArchivePath);
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO s3_object (
                    object_id,
                    key_text,
                    key_utf8,
                    last_modified_utc,
                    content_type,
                    etag,
                    storage_class,
                    content_length_bytes,
                    body_sha256,
                    body,
                    source_list_ordinal,
                    source_list_xml,
                    meta_width,
                    meta_height,
                    meta_tilename,
                    meta_blobencoding,
                    meta_comment,
                    meta_acl
                )
                VALUES (
                    $object_id,
                    $key_text,
                    $key_utf8,
                    '2011-09-15T22:58:53.000Z',
                    $content_type,
                    $etag,
                    'STANDARD',
                    $content_length_bytes,
                    $body_sha256,
                    $body,
                    $source_list_ordinal,
                    $source_list_xml,
                    $meta_width,
                    $meta_height,
                    $meta_tilename,
                    $meta_blobencoding,
                    $meta_comment,
                    $meta_acl
                );
                """;
            command.Parameters.AddWithValue("$object_id", objectId);
            command.Parameters.AddWithValue("$key_text", key);
            command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(key);
            command.Parameters.AddWithValue("$content_type", (object?)contentType ?? DBNull.Value);
            command.Parameters.AddWithValue("$etag", ComputeHash(body, MD5.HashData));
            command.Parameters.AddWithValue("$content_length_bytes", body.LongLength);
            command.Parameters.AddWithValue("$body_sha256", ComputeHash(body, SHA256.HashData));
            command.Parameters.Add("$body", SqliteType.Blob).Value = body;
            command.Parameters.AddWithValue("$source_list_ordinal", ordinal);
            command.Parameters.AddWithValue("$source_list_xml", $"<Contents><Key>{key}</Key></Contents>");
            command.Parameters.AddWithValue("$meta_width", ValueOrDbNull(metadata, "width"));
            command.Parameters.AddWithValue("$meta_height", ValueOrDbNull(metadata, "height"));
            command.Parameters.AddWithValue("$meta_tilename", ValueOrDbNull(metadata, "tilename"));
            command.Parameters.AddWithValue("$meta_blobencoding", ValueOrDbNull(metadata, "blobencoding"));
            command.Parameters.AddWithValue("$meta_comment", ValueOrDbNull(metadata, "comment"));
            command.Parameters.AddWithValue("$meta_acl", ValueOrDbNull(metadata, "acl"));
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO mgb_key_part(object_id, user_name, project_name, piece_type, piece_name)
                VALUES ($object_id, $user_name, $project_name, $piece_type, $piece_name);
                """;
            command.Parameters.AddWithValue("$object_id", objectId);
            command.Parameters.AddWithValue("$user_name", part.UserName);
            command.Parameters.AddWithValue("$project_name", part.ProjectName);
            command.Parameters.AddWithValue("$piece_type", part.PieceType);
            command.Parameters.AddWithValue("$piece_name", part.PieceName);
            command.ExecuteNonQuery();
        }

        foreach (var (name, value) in metadata)
        {
            if (IsKnownMetadata(name))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO s3_user_metadata_extra(object_id, name, value)
                VALUES ($object_id, $name, $value);
                """;
            command.Parameters.AddWithValue("$object_id", objectId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetArchiveSchema(string schema)
    {
        using var connection = Open(ArchivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE archive_info
            SET value = $schema
            WHERE name = 'schema';
            """;
        command.Parameters.AddWithValue("$schema", schema);
        command.ExecuteNonQuery();
    }

    private void InitializeArchive(string schema)
    {
        using var connection = Open(ArchivePath);
        Execute(
            connection,
            """
            PRAGMA encoding = 'UTF-8';
            PRAGMA foreign_keys = ON;

            CREATE TABLE archive_info (
                name TEXT PRIMARY KEY COLLATE BINARY,
                value TEXT NOT NULL
            ) STRICT, WITHOUT ROWID;

            INSERT INTO archive_info(name, value)
            VALUES ('schema', $schema), ('schema_version', '2');

            CREATE TABLE s3_object (
                object_id INTEGER PRIMARY KEY,
                key_text TEXT NOT NULL COLLATE BINARY,
                key_utf8 BLOB NOT NULL,
                last_modified_utc TEXT NOT NULL COLLATE BINARY,
                content_type TEXT NULL COLLATE BINARY,
                etag TEXT NOT NULL COLLATE BINARY,
                storage_class TEXT NULL COLLATE BINARY,
                content_length_bytes INTEGER NOT NULL,
                body_sha256 TEXT NOT NULL COLLATE BINARY,
                body BLOB NOT NULL,
                source_list_ordinal INTEGER NOT NULL,
                source_list_xml TEXT NOT NULL,
                meta_width        TEXT NULL,
                meta_height       TEXT NULL,
                meta_tilename     TEXT NULL,
                meta_blobencoding TEXT NULL,
                meta_comment      TEXT NULL,
                meta_acl          TEXT NULL,
                UNIQUE(key_utf8),
                CHECK (key_utf8 = CAST(key_text AS BLOB)),
                CHECK (content_length_bytes = length(body))
            ) STRICT;

            CREATE UNIQUE INDEX ux_s3_object_key_text
                ON s3_object(key_text COLLATE BINARY);

            CREATE TABLE mgb_key_part (
                object_id INTEGER PRIMARY KEY
                    REFERENCES s3_object(object_id)
                    ON DELETE RESTRICT,
                user_name TEXT NOT NULL COLLATE BINARY,
                project_name TEXT NOT NULL COLLATE BINARY,
                piece_type TEXT NOT NULL COLLATE BINARY,
                piece_name TEXT NOT NULL COLLATE BINARY
            ) STRICT;

            CREATE INDEX ix_mgb_key_project_piece
                ON mgb_key_part(user_name, project_name, piece_type, piece_name);

            CREATE TABLE s3_user_metadata_extra (
                object_id INTEGER NOT NULL
                    REFERENCES s3_object(object_id)
                    ON DELETE RESTRICT,
                name TEXT NOT NULL COLLATE BINARY,
                value TEXT NOT NULL,
                PRIMARY KEY(object_id, name)
            ) STRICT, WITHOUT ROWID;

            CREATE TABLE s3_response_header (
                object_id INTEGER NOT NULL
                    REFERENCES s3_object(object_id)
                    ON DELETE RESTRICT,
                name TEXT NOT NULL COLLATE BINARY,
                value TEXT NOT NULL,
                PRIMARY KEY(object_id, name, value)
            ) STRICT, WITHOUT ROWID;

            CREATE VIEW v_s3_bodies AS
            SELECT *
            FROM s3_object;

            CREATE VIEW v_mgb_pieces AS
            SELECT
                m.user_name,
                m.project_name,
                m.piece_type,
                m.piece_name,
                o.key_text,
                o.key_utf8,
                o.object_id,
                o.last_modified_utc,
                o.content_type,
                o.etag,
                o.storage_class,
                o.content_length_bytes,
                o.body_sha256,
                o.source_list_ordinal,
                o.source_list_xml,
                o.meta_width,
                o.meta_height,
                o.meta_tilename,
                o.meta_blobencoding,
                o.meta_comment,
                o.meta_acl
            FROM s3_object o
            JOIN mgb_key_part m ON m.object_id = o.object_id;
            """,
            new SqliteParameter("$schema", schema));
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

    private static (string UserName, string ProjectName, string PieceType, string PieceName) ParseMgbKey(string key)
    {
        var parts = key.Split('/', 4);
        if (parts.Length != 4 || parts.Any(static part => part.Length == 0))
        {
            throw new ArgumentException("Test archive keys must use user/project/piece_type/piece_name shape.", nameof(key));
        }

        return (parts[0], parts[1], parts[2], parts[3]);
    }

    private static object ValueOrDbNull(IDictionary<string, string> metadata, string name) =>
        metadata.TryGetValue(name, out var value) ? value : DBNull.Value;

    private static bool IsKnownMetadata(string name) =>
        name is "width" or "height" or "tilename" or "blobencoding" or "comment" or "acl";

    private static string ComputeHash(byte[] body, Func<byte[], byte[]> hash) =>
        Convert.ToHexString(hash(body)).ToLowerInvariant();

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }
}
