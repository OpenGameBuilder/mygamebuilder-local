using System.Diagnostics;
using System.Text.Json;

namespace MyGameBuilder.Local.Api.Updates;

public static class SelfUpdateApplier
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryGetPlanPath(string[] args, out string planPath)
    {
        planPath = string.Empty;
        if (args.Length != 2 || !string.Equals(args[0], "--apply-update", StringComparison.Ordinal))
        {
            return false;
        }

        planPath = args[1];
        return !string.IsNullOrWhiteSpace(planPath);
    }

    public static bool IsPublishedLayout(string contentRoot)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var processName = Path.GetFileName(processPath);
        if (processName.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
            processName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var processDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
        return !string.IsNullOrWhiteSpace(processDirectory) &&
            string.Equals(Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                processDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(processDirectory, "appsettings.json"));
    }

    public static async Task<int> ApplyAsync(string planPath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false);
        var plan = JsonSerializer.Deserialize<SelfUpdatePlan>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Self-update plan was empty.");

        Directory.CreateDirectory(Path.GetDirectoryName(plan.LogPath) ?? plan.BackupDirectory);
        await using var log = new StreamWriter(new FileStream(plan.LogPath, FileMode.Append, FileAccess.Write, FileShare.Read));
        await LogAsync(log, "Starting app self-update.").ConfigureAwait(false);
        await WaitForOldProcessAsync(plan.OldProcessId, log, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(plan.BackupDirectory);
        BackupReplaceableFiles(plan, log);
        CopyStagedFiles(plan, log);

        var executablePath = Path.Combine(plan.InstallDirectory, plan.ExecutableName);
        await LogAsync(log, "Starting updated app: " + executablePath).ConfigureAwait(false);
        StartDetached(executablePath, plan.InstallDirectory);
        await LogAsync(log, "Self-update completed.").ConfigureAwait(false);
        return 0;
    }

    private static async Task WaitForOldProcessAsync(int oldProcessId, TextWriter log, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(oldProcessId);
            await LogAsync(log, $"Waiting for process {oldProcessId} to exit.").ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (!process.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                process.Refresh();
            }

            if (!process.HasExited)
            {
                throw new InvalidOperationException($"Process {oldProcessId} did not exit before the update timeout.");
            }
        }
        catch (ArgumentException)
        {
            await LogAsync(log, $"Process {oldProcessId} was already gone.").ConfigureAwait(false);
        }
    }

    private static void BackupReplaceableFiles(SelfUpdatePlan plan, TextWriter log)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(plan.StagedDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(plan.StagedDirectory, sourcePath);
            if (ShouldPreserve(relativePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(plan.InstallDirectory, relativePath);
            if (!File.Exists(destinationPath))
            {
                continue;
            }

            var backupPath = Path.Combine(plan.BackupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? plan.BackupDirectory);
            File.Copy(destinationPath, backupPath, overwrite: false);
            log.WriteLine("Backed up " + relativePath);
        }
    }

    private static void CopyStagedFiles(SelfUpdatePlan plan, TextWriter log)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(plan.StagedDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(plan.StagedDirectory, sourcePath);
            if (ShouldPreserve(relativePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(plan.InstallDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? plan.InstallDirectory);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            log.WriteLine("Copied " + relativePath);
        }
    }

    public static bool ShouldPreserve(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return true;
        }

        if (string.Equals(normalized, "overlay.sqlite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "archive.sqlite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "frontend.sqlite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "appsettings.Local.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("archive.sqlite.", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("frontend.sqlite.", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".mygamebuilder-backups/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".mygamebuilder-updates/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("archive/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void StartDetached(string executablePath, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(info);
    }

    private static async Task LogAsync(TextWriter writer, string message)
    {
        await writer.WriteLineAsync(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + message).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
