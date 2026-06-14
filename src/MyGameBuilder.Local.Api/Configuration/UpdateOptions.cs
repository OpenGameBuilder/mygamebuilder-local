namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>GitHub-backed prompted update settings for the app and archive databases.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    public bool Enabled { get; set; } = true;

    public bool CheckOnStartup { get; set; } = true;

    public int CheckIntervalHours { get; set; } = 24;

    public string StagingPath { get; set; } = ".mygamebuilder-updates";

    public string BackupPath { get; set; } = ".mygamebuilder-backups";

    public string AppRepository { get; set; } = "OpenGameBuilder/mygamebuilder-local";

    public string ArchiveRepository { get; set; } = "OpenGameBuilder/mygamebuilder-archive";

    public bool IncludePrereleases { get; set; }
}
