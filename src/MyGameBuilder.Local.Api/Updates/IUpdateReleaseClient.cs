namespace MyGameBuilder.Local.Api.Updates;

public interface IUpdateReleaseClient
{
    Task<UpdateRelease?> GetLatestReleaseAsync(UpdateTarget target, CancellationToken cancellationToken);

    Task DownloadAssetAsync(
        GithubReleaseAsset asset,
        string destinationPath,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken);
}
