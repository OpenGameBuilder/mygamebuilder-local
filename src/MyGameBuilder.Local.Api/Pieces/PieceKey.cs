namespace MyGameBuilder.Local.Api.Pieces;

/// <summary>Helpers for interpreting S3 piece keys shaped as <c>user/project/piecetype/name</c>.</summary>
public static class PieceKey
{
    /// <summary>The owning user (first path segment) of a key, or empty when none.</summary>
    public static string UserOf(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var slash = key.IndexOf('/');
        return slash < 0 ? key : key[..slash];
    }
}
