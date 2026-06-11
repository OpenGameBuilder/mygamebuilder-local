using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Writable overlay half of the piece store. Local objects and tombstones are stored
/// in a current-state SQLite database; there is no local version history.
/// </summary>
public sealed class DataPieceStore
{
    private static readonly (string Column, string Name)[] s_knownMetadata =
    [
        ("meta_width", "width"),
        ("meta_height", "height"),
        ("meta_tilename", "tilename"),
        ("meta_blobencoding", "blobencoding"),
        ("meta_comment", "comment"),
        ("meta_acl", "acl"),
    ];

    private readonly string _overlayPath;
    private readonly Lock _writeGate = new();

    public DataPieceStore(string overlayPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(overlayPath);
        _overlayPath = overlayPath;
        Initialize();
    }

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_overlayPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
        PieceStoreSqlite.ExecuteNonQuery(connection, SchemaSql);
    }

    /// <summary>Writes (or replaces) an overlay object and clears any tombstone for its key.</summary>
    public Task PutAsync(string key, byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> amzMeta, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(amzMeta);
        cancellationToken.ThrowIfCancellationRequested();

        var keyBytes = PieceStoreSqlite.KeyBytes(key);
        var updatedUtc = PieceStoreSqlite.FormatUtc(DateTimeOffset.UtcNow);
        var etag = ComputeETag(body);
        var known = KnownMetadata(amzMeta);

        lock (_writeGate)
        {
            using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO local_object_overlay (
                        key_utf8,
                        key_text,
                        is_delete_marker,
                        updated_utc,
                        content_type,
                        etag,
                        content_length_bytes,
                        body,
                        meta_width,
                        meta_height,
                        meta_tilename,
                        meta_blobencoding,
                        meta_comment,
                        meta_acl
                    )
                    VALUES (
                        $key_utf8,
                        $key_text,
                        0,
                        $updated_utc,
                        $content_type,
                        $etag,
                        $content_length_bytes,
                        $body,
                        $meta_width,
                        $meta_height,
                        $meta_tilename,
                        $meta_blobencoding,
                        $meta_comment,
                        $meta_acl
                    )
                    ON CONFLICT(key_utf8) DO UPDATE SET
                        key_text = excluded.key_text,
                        is_delete_marker = 0,
                        updated_utc = excluded.updated_utc,
                        content_type = excluded.content_type,
                        etag = excluded.etag,
                        content_length_bytes = excluded.content_length_bytes,
                        body = excluded.body,
                        meta_width = excluded.meta_width,
                        meta_height = excluded.meta_height,
                        meta_tilename = excluded.meta_tilename,
                        meta_blobencoding = excluded.meta_blobencoding,
                        meta_comment = excluded.meta_comment,
                        meta_acl = excluded.meta_acl;
                    """;
                command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = keyBytes;
                command.Parameters.AddWithValue("$key_text", key);
                command.Parameters.AddWithValue("$updated_utc", updatedUtc);
                command.Parameters.AddWithValue("$content_type", (object?)contentType ?? DBNull.Value);
                command.Parameters.AddWithValue("$etag", etag);
                command.Parameters.AddWithValue("$content_length_bytes", body.LongLength);
                command.Parameters.Add("$body", SqliteType.Blob).Value = body;
                foreach (var (column, name) in s_knownMetadata)
                {
                    command.Parameters.AddWithValue("$" + column, known.TryGetValue(name, out var value) ? value : DBNull.Value);
                }

                command.ExecuteNonQuery();
            }

            ReplaceMetadata(connection, transaction, keyBytes, amzMeta);
            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes an overlay object and/or tombstones a base-only key.
    /// Returns true when something was removed from the overlay or newly hidden.
    /// </summary>
    public bool Delete(string key, bool existsInBase)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var keyBytes = PieceStoreSqlite.KeyBytes(key);

        lock (_writeGate)
        {
            using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
            using var transaction = connection.BeginTransaction();

            var existing = GetOverlayState(connection, transaction, keyBytes);
            if (existing == OverlayState.Tombstone)
            {
                transaction.Commit();
                return false;
            }

            if (!existsInBase)
            {
                if (existing == OverlayState.Live)
                {
                    DeleteOverlayRow(connection, transaction, keyBytes);
                    transaction.Commit();
                    return true;
                }

                transaction.Commit();
                return false;
            }

            UpsertTombstone(connection, transaction, key, keyBytes);
            transaction.Commit();
            return true;
        }
    }

    internal bool IsTombstoned(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM local_object_overlay
            WHERE key_utf8 = $key_utf8
              AND is_delete_marker = 1
            LIMIT 1;
            """;
        command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = PieceStoreSqlite.KeyBytes(key);
        return command.ExecuteScalar() is not null;
    }

    internal bool TryGet(string key, [MaybeNullWhen(false)] out PieceEntry entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT key_text, content_length_bytes, updated_utc, content_type
            FROM local_object_overlay
            WHERE key_utf8 = $key_utf8
              AND is_delete_marker = 0
            LIMIT 1;
            """;
        command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = PieceStoreSqlite.KeyBytes(key);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var keyText = reader.GetString(0);
        var size = reader.GetInt64(1);
        var updatedUtc = PieceStoreSqlite.ParseUtc(reader.GetString(2));
        var contentType = reader.IsDBNull(3) ? null : reader.GetString(3);
        var metadata = ReadMetadata(keyText);
        entry = new PieceEntry(
            keyText,
            size,
            updatedUtc,
            contentType,
            metadata,
            cancellationToken => LoadBodyAsync(keyText, cancellationToken));
        return true;
    }

    internal IReadOnlyList<PieceEntry> SnapshotEntries()
    {
        using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT key_text, content_length_bytes, updated_utc, content_type
            FROM local_object_overlay
            WHERE is_delete_marker = 0
            ORDER BY key_text COLLATE BINARY;
            """;

        var entries = new List<PieceEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var size = reader.GetInt64(1);
            var updatedUtc = PieceStoreSqlite.ParseUtc(reader.GetString(2));
            var contentType = reader.IsDBNull(3) ? null : reader.GetString(3);
            entries.Add(new PieceEntry(
                key,
                size,
                updatedUtc,
                contentType,
                [],
                cancellationToken => LoadBodyAsync(key, cancellationToken)));
        }

        return entries;
    }

    private IReadOnlyList<KeyValuePair<string, string>> ReadMetadata(string key)
    {
        using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, value
            FROM local_object_metadata
            WHERE key_utf8 = $key_utf8
            ORDER BY ordinal;
            """;
        command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = PieceStoreSqlite.KeyBytes(key);

        var metadata = new List<KeyValuePair<string, string>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            metadata.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }

        return metadata;
    }

    private async ValueTask<byte[]> LoadBodyAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = PieceStoreSqlite.OpenReadWriteCreate(_overlayPath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT body
            FROM local_object_overlay
            WHERE key_utf8 = $key_utf8
              AND is_delete_marker = 0
            LIMIT 1;
            """;
        command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = PieceStoreSqlite.KeyBytes(key);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] body
            ? body
            : throw new InvalidOperationException($"Overlay object body was not found for key '{key}'.");
    }

    private static void ReplaceMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] keyBytes,
        IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM local_object_metadata WHERE key_utf8 = $key_utf8;";
            delete.Parameters.Add("$key_utf8", SqliteType.Blob).Value = keyBytes;
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO local_object_metadata(key_utf8, ordinal, name, value)
            VALUES ($key_utf8, $ordinal, $name, $value);
            """;
        insert.Parameters.Add("$key_utf8", SqliteType.Blob);
        insert.Parameters.Add("$ordinal", SqliteType.Integer);
        insert.Parameters.Add("$name", SqliteType.Text);
        insert.Parameters.Add("$value", SqliteType.Text);

        for (var i = 0; i < metadata.Count; i++)
        {
            insert.Parameters["$key_utf8"].Value = keyBytes;
            insert.Parameters["$ordinal"].Value = i;
            insert.Parameters["$name"].Value = metadata[i].Key;
            insert.Parameters["$value"].Value = metadata[i].Value;
            insert.ExecuteNonQuery();
        }
    }

    private static OverlayState GetOverlayState(SqliteConnection connection, SqliteTransaction transaction, byte[] keyBytes)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT is_delete_marker
            FROM local_object_overlay
            WHERE key_utf8 = $key_utf8
            LIMIT 1;
            """;
        command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = keyBytes;
        var value = command.ExecuteScalar();
        if (value is null)
        {
            return OverlayState.None;
        }

        return Convert.ToInt64(value) == 1 ? OverlayState.Tombstone : OverlayState.Live;
    }

    private static void DeleteOverlayRow(SqliteConnection connection, SqliteTransaction transaction, byte[] keyBytes)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM local_object_overlay WHERE key_utf8 = $key_utf8;";
        command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = keyBytes;
        command.ExecuteNonQuery();
    }

    private static void UpsertTombstone(SqliteConnection connection, SqliteTransaction transaction, string key, byte[] keyBytes)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO local_object_overlay (
                    key_utf8,
                    key_text,
                    is_delete_marker,
                    updated_utc,
                    content_type,
                    etag,
                    content_length_bytes,
                    body,
                    meta_width,
                    meta_height,
                    meta_tilename,
                    meta_blobencoding,
                    meta_comment,
                    meta_acl
                )
                VALUES (
                    $key_utf8,
                    $key_text,
                    1,
                    $updated_utc,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL
                )
                ON CONFLICT(key_utf8) DO UPDATE SET
                    key_text = excluded.key_text,
                    is_delete_marker = 1,
                    updated_utc = excluded.updated_utc,
                    content_type = NULL,
                    etag = NULL,
                    content_length_bytes = NULL,
                    body = NULL,
                    meta_width = NULL,
                    meta_height = NULL,
                    meta_tilename = NULL,
                    meta_blobencoding = NULL,
                    meta_comment = NULL,
                    meta_acl = NULL;
                """;
            command.Parameters.Add("$key_utf8", SqliteType.Blob).Value = keyBytes;
            command.Parameters.AddWithValue("$key_text", key);
            command.Parameters.AddWithValue("$updated_utc", PieceStoreSqlite.FormatUtc(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }

        using var deleteMetadata = connection.CreateCommand();
        deleteMetadata.Transaction = transaction;
        deleteMetadata.CommandText = "DELETE FROM local_object_metadata WHERE key_utf8 = $key_utf8;";
        deleteMetadata.Parameters.Add("$key_utf8", SqliteType.Blob).Value = keyBytes;
        deleteMetadata.ExecuteNonQuery();
    }

    private static Dictionary<string, string> KnownMetadata(IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            foreach (var (_, name) in s_knownMetadata)
            {
                if (string.Equals(pair.Key, name, StringComparison.Ordinal))
                {
                    known[name] = pair.Value;
                    break;
                }
            }
        }

        return known;
    }

    private static string ComputeETag(byte[] body) => Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant();

    private enum OverlayState
    {
        None,
        Live,
        Tombstone,
    }

    private const string SchemaSql =
        """
        PRAGMA encoding = 'UTF-8';
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS local_object_overlay (
            key_utf8 BLOB PRIMARY KEY,
            key_text TEXT NOT NULL COLLATE BINARY,
            is_delete_marker INTEGER NOT NULL DEFAULT 0 CHECK (is_delete_marker IN (0, 1)),
            updated_utc TEXT NOT NULL COLLATE BINARY,
            content_type TEXT NULL COLLATE BINARY,
            etag TEXT NULL COLLATE BINARY,
            content_length_bytes INTEGER NULL CHECK (
                content_length_bytes IS NULL OR content_length_bytes >= 0
            ),
            body BLOB NULL,
            meta_width        TEXT NULL,
            meta_height       TEXT NULL,
            meta_tilename     TEXT NULL,
            meta_blobencoding TEXT NULL,
            meta_comment      TEXT NULL,
            meta_acl          TEXT NULL,
            CHECK (key_utf8 = CAST(key_text AS BLOB)),
            CHECK (
                (
                    is_delete_marker = 1
                    AND body IS NULL
                    AND content_length_bytes IS NULL
                    AND content_type IS NULL
                    AND etag IS NULL
                )
                OR
                (
                    is_delete_marker = 0
                    AND body IS NOT NULL
                    AND content_length_bytes IS NOT NULL
                    AND content_length_bytes = length(body)
                    AND etag IS NOT NULL
                )
            )
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_local_object_overlay_key_text
            ON local_object_overlay(key_text COLLATE BINARY);

        CREATE TABLE IF NOT EXISTS local_object_metadata (
            key_utf8 BLOB NOT NULL
                REFERENCES local_object_overlay(key_utf8)
                ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            name TEXT NOT NULL COLLATE BINARY,
            value TEXT NOT NULL,
            PRIMARY KEY(key_utf8, ordinal),
            CHECK (length(name) > 0)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_local_object_metadata_name
            ON local_object_metadata(name, value);
        """;
}
