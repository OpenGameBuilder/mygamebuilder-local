using Microsoft.Data.Sqlite;

namespace MyGameBuilder.Local.Api.Updates;

public static class ArchiveVersionReader
{
    public static string? ReadReleaseVersion(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = archivePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM archive_info WHERE name = 'release_version' LIMIT 1;";
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
