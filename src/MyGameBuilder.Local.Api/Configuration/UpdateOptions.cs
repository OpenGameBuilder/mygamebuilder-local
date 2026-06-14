namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>GitHub-backed prompted update settings for the app and archive databases.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    public bool Enabled { get; set; } = true;

    public bool CheckOnStartup { get; set; } = true;

    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>Update staging directory. Relative paths resolve under the per-user data root.</summary>
    public string StagingPath { get; set; } = "updates";

    /// <summary>Update backup directory. Relative paths resolve under the per-user data root.</summary>
    public string BackupPath { get; set; } = "backups";

    public string AppRepository { get; set; } = "OpenGameBuilder/mygamebuilder-local";

    public string ArchiveRepository { get; set; } = "OpenGameBuilder/mygamebuilder-archive";

    public bool IncludePrereleases { get; set; }
}
