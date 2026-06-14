using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Updates;

namespace MyGameBuilder.Local.Api.Tests;

public sealed class ApplicationPathsTests
{
    [Fact]
    public void ResolveContentRoot_UsesProcessDirectory_WhenItContainsAppSettings()
    {
        using var paths = new TempPaths();
        var processDirectory = paths.CreateDirectory("published");
        var baseDirectory = paths.CreateDirectory("base");
        File.WriteAllText(Path.Combine(processDirectory, "appsettings.json"), "{}");

        var result = ApplicationPaths.ResolveContentRoot(
            Path.Combine(processDirectory, "mygamebuilder-local.exe"),
            baseDirectory);

        Assert.Equal(processDirectory, result);
    }

    [Fact]
    public void ResolveContentRoot_FallsBackToBaseDirectory_WhenProcessDirectoryHasNoAppSettings()
    {
        using var paths = new TempPaths();
        var processDirectory = paths.CreateDirectory("dotnet");
        var baseDirectory = paths.CreateDirectory("base");

        var result = ApplicationPaths.ResolveContentRoot(
            Path.Combine(processDirectory, "dotnet.exe"),
            baseDirectory);

        Assert.Equal(baseDirectory, result);
    }

    [Fact]
    public void ResolveDataRoot_UsesWindowsLocalAppData()
    {
        using var paths = new TempPaths();
        var localAppData = paths.CreateDirectory("LocalAppData");

        var result = ApplicationPaths.ResolveDataRoot(
            ApplicationDataPlatform.Windows,
            localAppData: localAppData);

        Assert.Equal(
            Path.Combine(localAppData, "OpenGameBuilder", "MyGameBuilder Local"),
            result);
    }

    [Fact]
    public void ResolveDataRoot_UsesMacApplicationSupport()
    {
        using var paths = new TempPaths();
        var home = paths.CreateDirectory("home");

        var result = ApplicationPaths.ResolveDataRoot(
            ApplicationDataPlatform.MacOS,
            homeDirectory: home);

        Assert.Equal(
            Path.Combine(home, "Library", "Application Support", "OpenGameBuilder", "MyGameBuilder Local"),
            result);
    }

    [Fact]
    public void ResolveDataRoot_UsesLinuxXdgDataHome()
    {
        using var paths = new TempPaths();
        var xdgDataHome = paths.CreateDirectory("xdg-data-home");
        var home = paths.CreateDirectory("home");

        var result = ApplicationPaths.ResolveDataRoot(
            ApplicationDataPlatform.Linux,
            xdgDataHome: xdgDataHome,
            homeDirectory: home);

        Assert.Equal(
            Path.Combine(xdgDataHome, "OpenGameBuilder", "MyGameBuilder Local"),
            result);
    }

    [Fact]
    public void ResolveDataRoot_UsesLinuxHomeFallback_WhenXdgDataHomeIsMissingOrRelative()
    {
        using var paths = new TempPaths();
        var home = paths.CreateDirectory("home");

        var result = ApplicationPaths.ResolveDataRoot(
            ApplicationDataPlatform.Linux,
            xdgDataHome: "relative-xdg",
            homeDirectory: home);

        Assert.Equal(
            Path.Combine(home, ".local", "share", "OpenGameBuilder", "MyGameBuilder Local"),
            result);
    }

    [Fact]
    public void ApplicationPathRoots_ResolveRuntimeDataRelativeToDataRoot()
    {
        using var paths = new TempPaths();
        var contentRoot = paths.CreateDirectory("install");
        var dataRoot = paths.CreateDirectory("data");
        var roots = new ApplicationPathRoots(contentRoot, dataRoot);

        Assert.Equal(contentRoot, roots.ContentRoot);
        Assert.Equal(dataRoot, roots.DataRoot);
        Assert.Equal(Path.Combine(dataRoot, "archive.sqlite"), roots.ResolveDataPath("archive.sqlite"));
        Assert.Equal(Path.Combine(dataRoot, "frontend.sqlite"), roots.ResolveDataPath("frontend.sqlite"));
        Assert.Equal(Path.Combine(dataRoot, "overlay.sqlite"), roots.ResolveDataPath("overlay.sqlite"));
    }

    [Fact]
    public void ApplicationPathRoots_PreserveAbsoluteRuntimeDataPaths()
    {
        using var paths = new TempPaths();
        var contentRoot = paths.CreateDirectory("install");
        var dataRoot = paths.CreateDirectory("data");
        var absolutePath = Path.Combine(paths.CreateDirectory("override"), "archive.sqlite");
        var roots = new ApplicationPathRoots(contentRoot, dataRoot);

        Assert.Equal(absolutePath, roots.ResolveDataPath(absolutePath));
    }

    [Fact]
    public void UpdatePaths_ResolveRelativeStagingAndBackupsUnderDataRoot()
    {
        using var paths = new TempPaths();
        var contentRoot = paths.CreateDirectory("install");
        var dataRoot = paths.CreateDirectory("data");
        var updatePaths = new UpdatePaths(
            new ApplicationPathRoots(contentRoot, dataRoot),
            Options.Create(new UpdateOptions()));

        Assert.Equal(Path.Combine(dataRoot, "updates"), updatePaths.StagingRoot);
        Assert.Equal(Path.Combine(dataRoot, "backups"), updatePaths.BackupRoot);
        Assert.Equal(Path.Combine(dataRoot, "updates", "state.json"), updatePaths.StatePath);
    }

    private sealed class TempPaths : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mgb-paths-" + Guid.NewGuid().ToString("N"));

        public string CreateDirectory(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
