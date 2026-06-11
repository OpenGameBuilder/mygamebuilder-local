using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>
/// Read-only base half of the piece store, backed by the unversioned SQLite archive
/// produced by MyGameBuilder.Archive.S3.
/// </summary>
public sealed class ArchivePieceStore
{
    private const string ExpectedSchema = "mgb-jgi-test1-unversioned-archive";

    private static readonly (string Column, string Name)[] s_knownMetadata =
    [
        ("meta_width", "width"),
        ("meta_height", "height"),
        ("meta_tilename", "tilename"),
        ("meta_blobencoding", "blobencoding"),
        ("meta_comment", "comment"),
        ("meta_acl", "acl"),
    ];

    private readonly string _archivePath;
    private readonly Lock _schemaGate = new();
    private bool _schemaChecked;

    public ArchivePieceStore(string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        _archivePath = archivePath;
    }

    /// <summary>
    /// Validates an existing archive. A missing archive is allowed and behaves as an
    /// empty base so users can start the local server before downloading content.
    /// </summary>
    public void Initialize()
    {
        if (File.Exists(_archivePath))
        {
            EnsureSupportedArchive();
        }
    }

    internal bool TryGet(string key, [MaybeNullWhen(false)] out PieceEntry entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(key) || !File.Exists(_archivePath))
        {
            return false;
        }

        EnsureSupportedArchive();
        using var connection = PieceStoreSqlite.OpenReadOnly(_archivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                key_text,
                object_id,
                content_length_bytes,
                last_modified_utc,
                content_type,
                meta_width,
                meta_height,
                meta_tilename,
                meta_blobencoding,
                meta_comment,
                meta_acl
            FROM v_mgb_pieces
            WHERE key_text = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        entry = ReadEntry(_archivePath, reader, includeMetadata: true);
        return true;
    }

    internal IReadOnlyList<PieceEntry> ListEntries(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || !File.Exists(_archivePath))
        {
            return [];
        }

        EnsureSupportedArchive();
        using var connection = PieceStoreSqlite.OpenReadOnly(_archivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                key_text,
                object_id,
                content_length_bytes,
                last_modified_utc,
                content_type,
                meta_width,
                meta_height,
                meta_tilename,
                meta_blobencoding,
                meta_comment,
                meta_acl
            FROM v_mgb_pieces
            WHERE key_utf8 >= $prefix_utf8
              AND ($prefix_end_utf8 IS NULL OR key_utf8 < $prefix_end_utf8)
            ORDER BY key_text COLLATE BINARY;
            """;
        PieceStoreSqlite.AddPrefixRangeParameters(command, prefix);

        var entries = new List<PieceEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(_archivePath, reader, includeMetadata: false));
        }

        return entries;
    }

    internal bool UserExists(string user)
    {
        if (string.IsNullOrEmpty(user) || !File.Exists(_archivePath))
        {
            return false;
        }

        EnsureSupportedArchive();
        using var connection = PieceStoreSqlite.OpenReadOnly(_archivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM v_mgb_pieces
            WHERE user_name = $user
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$user", user);
        return command.ExecuteScalar() is not null;
    }

    internal IReadOnlyCollection<string> ListUsers()
    {
        if (!File.Exists(_archivePath))
        {
            return [];
        }

        EnsureSupportedArchive();
        using var connection = PieceStoreSqlite.OpenReadOnly(_archivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT user_name
            FROM v_mgb_pieces
            ORDER BY user_name COLLATE BINARY;
            """;

        var users = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            users.Add(reader.GetString(0));
        }

        return users;
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
                using var connection = PieceStoreSqlite.OpenReadOnly(_archivePath);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM archive_info WHERE name = 'schema';";
                var schema = command.ExecuteScalar() as string;
                if (!string.Equals(schema, ExpectedSchema, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Unsupported piece archive schema '{schema ?? "<missing>"}'. Expected '{ExpectedSchema}'.");
                }
            }
            catch (SqliteException exc)
            {
                throw new InvalidOperationException(
                    $"The piece archive SQLite database could not be read: {_archivePath}",
                    exc);
            }

            _schemaChecked = true;
        }
    }

    private static PieceEntry ReadEntry(string archivePath, SqliteDataReader reader, bool includeMetadata)
    {
        var key = reader.GetString(0);
        var objectId = reader.GetInt64(1);
        var size = reader.GetInt64(2);
        var lastModified = PieceStoreSqlite.ParseUtc(reader.GetString(3));
        var contentType = reader.IsDBNull(4) ? null : reader.GetString(4);
        var metadata = includeMetadata ? ReadMetadata(archivePath, reader, objectId) : [];

        return new PieceEntry(
            key,
            size,
            lastModified,
            contentType,
            metadata,
            cancellationToken => LoadBodyAsync(archivePath, key, cancellationToken));
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ReadMetadata(
        string archivePath,
        SqliteDataReader reader,
        long objectId)
    {
        var metadata = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < s_knownMetadata.Length; i++)
        {
            var ordinal = 5 + i;
            if (!reader.IsDBNull(ordinal))
            {
                metadata.Add(new KeyValuePair<string, string>(s_knownMetadata[i].Name, reader.GetString(ordinal)));
            }
        }

        using var connection = PieceStoreSqlite.OpenReadOnly(archivePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, value
            FROM s3_user_metadata_extra
            WHERE object_id = $object_id
            ORDER BY name COLLATE BINARY, value COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$object_id", objectId);

        using var extraReader = command.ExecuteReader();
        while (extraReader.Read())
        {
            metadata.Add(new KeyValuePair<string, string>(extraReader.GetString(0), extraReader.GetString(1)));
        }

        return metadata;
    }

    private static async ValueTask<byte[]> LoadBodyAsync(string archivePath, string key, CancellationToken cancellationToken)
    {
        await using var connection = PieceStoreSqlite.OpenReadOnly(archivePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT body
            FROM v_s3_bodies
            WHERE key_text = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] body
            ? body
            : throw new InvalidOperationException($"Archived object body was not found for key '{key}'.");
    }
}
