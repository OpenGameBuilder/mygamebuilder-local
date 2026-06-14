namespace MyGameBuilder.Local.Api.Updates;

public enum UpdateTarget
{
    App,
    S3Archive,
    FrontendArchive,
}

public static class UpdateTargets
{
    public static string ToId(UpdateTarget target) => target switch
    {
        UpdateTarget.App => "app",
        UpdateTarget.S3Archive => "s3",
        UpdateTarget.FrontendArchive => "frontend",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    public static bool TryParseArchiveId(string value, out UpdateTarget target)
    {
        target = value.ToLowerInvariant() switch
        {
            "s3" => UpdateTarget.S3Archive,
            "frontend" => UpdateTarget.FrontendArchive,
            _ => default,
        };
        return target is UpdateTarget.S3Archive or UpdateTarget.FrontendArchive;
    }
}
