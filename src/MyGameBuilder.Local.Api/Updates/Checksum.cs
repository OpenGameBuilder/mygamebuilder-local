using System.Security.Cryptography;

namespace MyGameBuilder.Local.Api.Updates;

public static class Checksum
{
    public static async ValueTask<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        return await Sha256FileAsync(path, bytesProgress: null, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<string> Sha256FileAsync(
        string path,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var total = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            total += read;
            bytesProgress?.Report(total);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async ValueTask EnsureSha256FileAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        await EnsureSha256FileAsync(path, expectedSha256, bytesProgress: null, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask EnsureSha256FileAsync(
        string path,
        string expectedSha256,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        var actual = await Sha256FileAsync(path, bytesProgress, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch for '{path}'. Expected {expectedSha256}, received {actual}.");
        }
    }
}
