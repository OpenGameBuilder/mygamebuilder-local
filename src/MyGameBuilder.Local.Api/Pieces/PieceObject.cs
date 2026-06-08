namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>A resolved piece object: metadata plus deferred access to the body bytes.</summary>
public sealed class PieceObject
{
    private readonly Func<CancellationToken, ValueTask<byte[]>> _bodyLoader;

    public PieceObject(
        string key,
        long size,
        DateTimeOffset lastModified,
        string? contentType,
        IReadOnlyList<KeyValuePair<string, string>> amzMeta,
        Func<CancellationToken, ValueTask<byte[]>> bodyLoader)
    {
        ArgumentNullException.ThrowIfNull(bodyLoader);
        Key = key;
        Size = size;
        LastModified = lastModified;
        ContentType = contentType;
        AmzMeta = amzMeta;
        _bodyLoader = bodyLoader;
    }

    /// <summary>Original S3 key.</summary>
    public string Key { get; }

    /// <summary>Body length in bytes.</summary>
    public long Size { get; }

    /// <summary>Effective last-modified instant.</summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>Known content type, if any.</summary>
    public string? ContentType { get; }

    /// <summary>Stored custom metadata (x-amz-meta-*) pairs, in stored order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> AmzMeta { get; }

    /// <summary>Reads the object body bytes.</summary>
    public ValueTask<byte[]> ReadBytesAsync(CancellationToken cancellationToken = default) => _bodyLoader(cancellationToken);
}
