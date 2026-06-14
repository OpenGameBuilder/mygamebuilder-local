using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Configuration;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdatePaths
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<UpdateOptions> _options;

    public UpdatePaths(IHostEnvironment environment, IOptions<UpdateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        _environment = environment;
        _options = options;
    }

    public string ContentRoot => _environment.ContentRootPath;

    public string StagingRoot => Resolve(_options.Value.StagingPath);

    public string BackupRoot => Resolve(_options.Value.BackupPath);

    public string StatePath => Path.Combine(StagingRoot, "state.json");

    public string Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return _environment.ContentRootPath;
        }

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configured));
    }

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
