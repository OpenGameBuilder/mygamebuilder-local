using System.Text;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Accounts;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.Http;
using MyGameBuilder.Local.Api.Pieces;
using MyGameBuilder.Local.Api.Time;
using MyGameBuilder.Local.Api.Xml;

namespace MyGameBuilder.Local.Api.Endpoints;

/// <summary>
/// Auth, account, and per-user endpoints returning Flex "object" fragments
/// (concatenated sibling elements, no root, <c>text/xml</c>) per README 4. New-account
/// registration is intentionally disabled for this build; the archive-driven login
/// path (which auto-creates empty-password rows for archived users) remains active.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/user/flexlogin", LoginAsync);
        app.MapPost("/user/flexcreateuser", CreateUserAsync);
        app.MapPost("/user/flexlogout", () => XmlResults.Xml("<status>1</status><message>Logged out</message>"));
        app.MapMethods("/user/flex_heartbeat_safe", ["GET", "POST"], Heartbeat);
        app.MapMethods("/user/get_user_stats", ["GET", "POST"], UserStatsAsync);
        app.MapMethods("/user/flex_browse_users", ["GET", "POST"], BrowseUsers);
        app.MapPost("/user/flexrecoveryquestionrequest", RecoveryQuestionAsync);
        app.MapPost("/user/flexrecoverpassword", RecoverPasswordAsync);
        app.MapPost("/user/flexchangepassword", ChangePasswordAsync);
        app.MapMethods("/log/logbug", ["GET", "POST"], LogBugAsync);
        app.MapPost("/user/flex_delete_s3object", DeleteS3ObjectAsync);

        return app;
    }

    private static async Task<IResult> LoginAsync(HttpRequest request, AccountStore accounts)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var result = accounts.Login(fields.Form("login"), fields.Form("password"));

        if (result.Success)
        {
            return XmlResults.Xml(
                $"<status>1</status><message>Welcome back, {XmlText.Escape(result.Login)}!</message><logincount>{result.LoginCount}</logincount>");
        }

        return XmlResults.Xml("<status>0</status><message>Invalid username or password</message><logincount>0</logincount>");
    }

    private static Task<IResult> CreateUserAsync()
    {
        // Sign-up is disabled for this build (project scope). Returning a well-formed
        // failure fragment keeps the client happy without creating accounts.
        return Task.FromResult(XmlResults.Xml("<status>0</status><message>Account creation is disabled</message>"));
    }

    private static IResult Heartbeat() =>
        XmlResults.Xml($"<keyz>DUMMYKEY1&amp;DUMMYKEY2&amp;DUMMYKEY3&amp;DUMMYKEY4</keyz><dt>{SoapDateTime.Now()}</dt><status>ok</status>");

    private static async Task<IResult> UserStatsAsync(HttpRequest request, IPieceStore pieces, IOptions<ServerOptions> options)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var username = fields.FormOrQuery("username", "guest");
        var usedKb = pieces.UserSizeBytes(username) / 1024;
        return XmlResults.Xml($"<status>1</status><usedKB>{usedKb}</usedKB><maxKB>{options.Value.MaxQuotaKb}</maxKB>");
    }

    private static IResult BrowseUsers(AccountStore accounts)
    {
        var users = accounts.Browse();
        if (users.Count == 0)
        {
            return XmlResults.Xml("<status>1</status><users></users>");
        }

        var builder = new StringBuilder("<status>1</status><users>");
        foreach (var (login, loginCount) in users)
        {
            builder.Append("<user><login>").Append(XmlText.Escape(login)).Append("</login>")
                .Append("<logincount>").Append(loginCount).Append("</logincount></user>");
        }

        builder.Append("</users>");
        return XmlResults.Xml(builder.ToString());
    }

    private static async Task<IResult> RecoveryQuestionAsync(HttpRequest request, AccountStore accounts)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var account = accounts.Find(fields.Form("login"));
        if (account is null)
        {
            return XmlResults.Xml("<status>0</status><message>User not found</message>");
        }

        return XmlResults.Xml($"<status>1</status><question>{XmlText.Escape(account.SecretQuestion)}</question>");
    }

    private static async Task<IResult> RecoverPasswordAsync(HttpRequest request, AccountStore accounts)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var account = accounts.Find(fields.Form("login"));
        var answer = fields.Form("answer");

        if (account is not null && string.Equals(account.SecretAnswer, answer, StringComparison.OrdinalIgnoreCase))
        {
            return XmlResults.Xml($"<status>1</status><password>{XmlText.Escape(account.Password)}</password><message>Password recovered</message>");
        }

        return XmlResults.Xml("<status>0</status><message>Incorrect answer</message>");
    }

    private static async Task<IResult> ChangePasswordAsync(HttpRequest request, AccountStore accounts)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var account = accounts.Find(fields.Form("login"));
        var oldPassword = fields.Form("oldpassword");
        var newPassword = fields.Form("newpassword");

        if (account is not null && string.Equals(account.Password, oldPassword, StringComparison.Ordinal))
        {
            account.Password = newPassword;
            return XmlResults.Xml("<status>1</status><message>Password changed</message>");
        }

        return XmlResults.Xml("<status>0</status><message>Invalid old password</message>");
    }

    private static async Task<IResult> LogBugAsync(HttpRequest request, ILoggerFactory loggerFactory)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var message = fields.FormOrQuery("message");
        loggerFactory.CreateLogger("MyGameBuilder.Local.Api.BugLog").LogInformation("Client bug report: {Message}", message);
        return XmlResults.Xml("<status>ok</status>");
    }

    private static async Task<IResult> DeleteS3ObjectAsync(HttpRequest request, IPieceStore pieces, CancellationToken cancellationToken)
    {
        var fields = await RequestFields.ReadAsync(request).ConfigureAwait(false);
        var itemName = fields.Form("itemname");
        var deleted = await pieces.DeleteAsync(itemName, cancellationToken).ConfigureAwait(false);

        return deleted
            ? XmlResults.Xml("<mgb_error>0</mgb_error><message>Deleted</message>")
            : XmlResults.Xml("<mgb_error>404</mgb_error><mgb_error_msg>Not found</mgb_error_msg>");
    }
}
