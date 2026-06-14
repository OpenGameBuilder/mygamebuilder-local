namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>Resolves filesystem roots for normal development runs and published single-file apps.</summary>
public static class ApplicationPaths
{
    /// <summary>
    /// Uses the executable directory when it contains appsettings.json; otherwise falls back
    /// to AppContext.BaseDirectory so dotnet run and test hosts keep their existing behavior.
    /// </summary>
    public static string ResolveContentRoot(string? processPath = null, string? baseDirectory = null)
    {
        var processDirectory = DirectoryFromFile(processPath ?? Environment.ProcessPath);
        if (ContainsAppSettings(processDirectory))
        {
            return processDirectory!;
        }

        return baseDirectory ?? AppContext.BaseDirectory;
    }

    private static string? DirectoryFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetDirectoryName(Path.GetFullPath(path));
    }

    private static bool ContainsAppSettings(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, "appsettings.json"));
}
