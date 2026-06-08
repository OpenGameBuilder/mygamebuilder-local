using System.Collections.Concurrent;
using MyGameBuilder.Local.Api.Pieces;

namespace MyGameBuilder.Local.Api.Accounts;

/// <summary>
/// In-memory accounts/stats DB emulation. The real archive does not include the
/// legacy relational DB, so this store is seeded with the three documented default
/// accounts and grows only via archive-driven login (auto-creating empty-password
/// rows for users that exist in the piece store). Interactive sign-up is disabled
/// at the endpoint layer per project scope; <see cref="TryCreate"/> still exists for
/// completeness and tests.
/// </summary>
public sealed class AccountStore
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IPieceStore _pieces;

    public AccountStore(IPieceStore pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        _pieces = pieces;
        SeedDefaults();
    }

    /// <summary>
    /// Authenticates per README 4: empty login becomes guest/guest; archive ghosts
    /// (empty stored password + existing archive user) bypass the password; unknown
    /// logins that exist in the archive auto-create an empty-password row.
    /// </summary>
    public LoginResult Login(string login, string password)
    {
        login = (login ?? string.Empty).Trim();
        password = (password ?? string.Empty).Trim();

        if (login.Length == 0)
        {
            login = "guest";
            password = "guest";
        }

        var archived = _pieces.UserExists(login);

        if (_accounts.TryGetValue(login, out var account))
        {
            var ghost = account.Password.Length == 0 && archived;
            if (!ghost && !string.Equals(account.Password, password, StringComparison.Ordinal))
            {
                return new LoginResult(false, login, 0);
            }

            account.LoginCount++;
            return new LoginResult(true, account.Login, account.LoginCount);
        }

        if (archived)
        {
            var created = new Account { Login = login, Password = string.Empty, LoginCount = 1 };
            _accounts[login] = created;
            return new LoginResult(true, login, 1);
        }

        return new LoginResult(false, login, 0);
    }

    /// <summary>Looks up an account by login (case-insensitive). Null when absent.</summary>
    public Account? Find(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        return _accounts.TryGetValue(login.Trim(), out var account) ? account : null;
    }

    /// <summary>
    /// Inserts a new account. Returns false if the login already exists. Not reachable
    /// from the HTTP surface (sign-up is disabled) but retained for tests/back-compat.
    /// </summary>
    public bool TryCreate(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return _accounts.TryAdd(account.Login, account);
    }

    /// <summary>
    /// Browse list per README 4: union of DB accounts and archive users (count 0 when
    /// not in the DB), sorted by login count desc then login asc, capped at 20.
    /// </summary>
    public IReadOnlyList<(string Login, int LoginCount)> Browse(int cap = 20)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in _accounts.Values)
        {
            counts[account.Login] = account.LoginCount;
        }

        foreach (var user in _pieces.ListUsers())
        {
            if (!counts.ContainsKey(user))
            {
                counts[user] = 0;
            }
        }

        return counts
            .Select(kv => (Login: kv.Key, LoginCount: kv.Value))
            .OrderByDescending(entry => entry.LoginCount)
            .ThenBy(entry => entry.Login, StringComparer.Ordinal)
            .Take(cap)
            .ToList();
    }

    private void SeedDefaults()
    {
        _accounts["foo"] = new Account { Login = "foo", Password = "bar", LoginCount = 0 };
        _accounts["guest"] = new Account { Login = "guest", Password = "guest", LoginCount = 0 };
        _accounts["!system"] = new Account { Login = "!system", Password = "system", LoginCount = 0 };
    }
}
