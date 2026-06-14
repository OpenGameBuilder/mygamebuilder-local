using System.Security.Cryptography;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdateSecurityToken
{
    public string Value { get; } = CreateToken();

    public bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(Value),
            System.Text.Encoding.UTF8.GetBytes(value));

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }
}
