namespace MyGameBuilder.Local.Api.Accounts;

/// <summary>
/// A user account row from the legacy accounts DB. Mutable because login counts and
/// passwords change at runtime; persistence is intentionally in-memory only (the
/// accounts/stats DB is not part of the real archive and is faked per project scope).
/// </summary>
public sealed class Account
{
    public required string Login { get; init; }

    public string Password { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Dob { get; set; } = "01/01/2000";

    public string SecretQuestion { get; set; } = "Default?";

    public string SecretAnswer { get; set; } = "Yes";

    public int LoginCount { get; set; }
}
