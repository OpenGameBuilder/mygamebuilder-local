using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdatePaths
{
    private readonly ApplicationPathRoots _paths;
    private readonly IOptions<UpdateOptions> _options;

    public UpdatePaths(ApplicationPathRoots paths, IOptions<UpdateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        _paths = paths;
        _options = options;
    }

    public string ContentRoot => _paths.ContentRoot;

    public string DataRoot => _paths.DataRoot;

    public string StagingRoot => ResolveDataPath(_options.Value.StagingPath);

    public string BackupRoot => ResolveDataPath(_options.Value.BackupPath);

    public string StatePath => Path.Combine(StagingRoot, "state.json");

    public string ResolveDataPath(string configured) => _paths.ResolveDataPath(configured);

    public string CreateOperationStagingDirectory(UpdateTarget target)
    {
        var directory = Path.Combine(
            StagingRoot,
            UpdateTargets.ToId(target),
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string CreateBackupDirectory(UpdateTarget target)
    {
        var directory = Path.Combine(
            BackupRoot,
            UpdateTargets.ToId(target),
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
