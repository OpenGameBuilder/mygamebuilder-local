namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>Resolves filesystem roots for normal development runs and published single-file apps.</summary>
public static class ApplicationPaths
{
    private const string OrganizationDirectoryName = "OpenGameBuilder";
    private const string ProductDirectoryName = "MyGameBuilder Local";

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

    /// <summary>Resolves the per-user writable data root for runtime data, downloaded archives, and update state.</summary>
    public static string ResolveDataRoot(
        ApplicationDataPlatform? platform = null,
        string? localAppData = null,
        string? xdgDataHome = null,
        string? homeDirectory = null)
    {
        var resolvedPlatform = platform ?? CurrentPlatform();
        var baseDirectory = resolvedPlatform switch
        {
            ApplicationDataPlatform.Windows => FirstNonEmpty(
                localAppData,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                HomeFallback(homeDirectory)),
            ApplicationDataPlatform.MacOS => Path.Combine(HomeFallback(homeDirectory), "Library", "Application Support"),
            ApplicationDataPlatform.Linux => FirstNonEmpty(
                RootedOrNull(xdgDataHome ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME")),
                Path.Combine(HomeFallback(homeDirectory), ".local", "share")),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), resolvedPlatform, null),
        };

        return Path.GetFullPath(Path.Combine(baseDirectory, OrganizationDirectoryName, ProductDirectoryName));
    }

    private static ApplicationDataPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ApplicationDataPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return ApplicationDataPlatform.MacOS;
        }

        return ApplicationDataPlatform.Linux;
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

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static string HomeFallback(string? configuredHome)
    {
        var home = FirstNonEmpty(
            configuredHome,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetEnvironmentVariable("USERPROFILE"));
        return home;
    }

    private static string? RootedOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) ? path : null;
}

public sealed class ApplicationPathRoots
{
    public ApplicationPathRoots(string contentRoot, string? dataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ContentRoot = Path.GetFullPath(contentRoot);
        DataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot) ? ApplicationPaths.ResolveDataRoot() : dataRoot);
    }

    public string ContentRoot { get; }

    public string DataRoot { get; }

    public string ResolveDataPath(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DataRoot;
        }

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(DataRoot, configured));
    }
}

public enum ApplicationDataPlatform
{
    Windows,
    MacOS,
    Linux,
}
