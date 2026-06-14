using MyGameBuilder.Local.Api.Configuration;

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
