using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Local.Api.Pieces;

internal static class PieceStoreSqlite
{
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    internal static SqliteConnection OpenReadWriteCreate(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    internal static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static byte[] KeyBytes(string key) => Encoding.UTF8.GetBytes(key);

    internal static void AddPrefixRangeParameters(SqliteCommand command, string prefix)
    {
        var prefixBytes = KeyBytes(prefix);
        command.Parameters.Add("$prefix_utf8", SqliteType.Blob).Value = prefixBytes;
        command.Parameters.Add("$prefix_end_utf8", SqliteType.Blob).Value =
            PrefixUpperBound(prefixBytes) is { } end ? end : DBNull.Value;
    }

    internal static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static byte[]? PrefixUpperBound(byte[] prefix)
    {
        if (prefix.Length == 0)
        {
            return null;
        }

        var upper = prefix.ToArray();
        for (var i = upper.Length - 1; i >= 0; i--)
        {
            if (upper[i] == byte.MaxValue)
            {
                continue;
            }

            upper[i]++;
            Array.Resize(ref upper, i + 1);
            return upper;
        }

        return null;
    }
}
