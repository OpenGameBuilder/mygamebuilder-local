namespace MyGameBuilder.Local.Api.Accounts;

/// <summary>Outcome of a login attempt: success flag, the resolved login, and its login count.</summary>
public readonly record struct LoginResult(bool Success, string Login, int LoginCount);
