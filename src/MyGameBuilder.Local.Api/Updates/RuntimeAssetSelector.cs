using System.Runtime.InteropServices;

namespace MyGameBuilder.Local.Api.Updates;

public static class RuntimeAssetSelector
{
    public static string CurrentRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "osx-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return "osx-arm64";
        }

        throw new PlatformNotSupportedException(
            $"No update asset is defined for {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}.");
    }

    public static string ExecutableNameForRid(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "mygamebuilder-local.exe" : "mygamebuilder-local";
}
