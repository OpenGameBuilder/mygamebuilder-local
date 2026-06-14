using System.Security.Cryptography;

namespace MyGameBuilder.Local.Api.Updates;

public static class Checksum
{
    public static async ValueTask<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async ValueTask EnsureSha256FileAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        var actual = await Sha256FileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch for '{path}'. Expected {expectedSha256}, received {actual}.");
        }
    }
}
