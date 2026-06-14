using System.Text.Json.Serialization;

namespace MyGameBuilder.Local.Api.Updates;

public sealed record SelfUpdatePlan(
    [property: JsonPropertyName("installDirectory")] string InstallDirectory,
    [property: JsonPropertyName("stagedDirectory")] string StagedDirectory,
    [property: JsonPropertyName("backupDirectory")] string BackupDirectory,
    [property: JsonPropertyName("oldProcessId")] int OldProcessId,
    [property: JsonPropertyName("executableName")] string ExecutableName,
    [property: JsonPropertyName("logPath")] string LogPath);
