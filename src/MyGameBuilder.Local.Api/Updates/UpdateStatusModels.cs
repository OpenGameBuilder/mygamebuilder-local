namespace MyGameBuilder.Local.Api.Updates;

public sealed record UpdateStatusDto(
    bool Enabled,
    DateTimeOffset GeneratedAtUtc,
    bool FirstRunSetupNeeded,
    UpdateTargetStatusDto App,
    UpdateTargetStatusDto S3Archive,
    UpdateTargetStatusDto FrontendArchive);

public sealed record UpdateTargetStatusDto(
    string Target,
    string? InstalledVersion,
    string? AvailableVersion,
    bool Missing,
    bool UpdateAvailable,
    bool CanInstall,
    string? ReleaseName,
    string? ReleaseUrl,
    long? DownloadSizeBytes,
    string State,
    int ProgressPercent,
    string? Message,
    DateTimeOffset? LastCheckedUtc);

internal sealed class UpdateStatusState
{
    public UpdateTargetState App { get; set; } = new();

    public UpdateTargetState S3Archive { get; set; } = new();

    public UpdateTargetState FrontendArchive { get; set; } = new();

    public UpdateTargetState For(UpdateTarget target) => target switch
    {
        UpdateTarget.App => App,
        UpdateTarget.S3Archive => S3Archive,
        UpdateTarget.FrontendArchive => FrontendArchive,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };
}

internal sealed class UpdateTargetState
{
    public string State { get; set; } = "idle";

    public int ProgressPercent { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset? LastCheckedUtc { get; set; }

    public string? AvailableVersion { get; set; }

    public string? InstalledVersion { get; set; }

    public string? ReleaseName { get; set; }

    public string? ReleaseUrl { get; set; }

    public long? DownloadSizeBytes { get; set; }
}
