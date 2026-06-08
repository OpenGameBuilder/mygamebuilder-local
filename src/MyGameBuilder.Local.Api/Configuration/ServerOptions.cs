namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>General backend settings unrelated to piece storage.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// Per-run liveness token returned by <c>/healthz</c> (sourced from the
    /// <c>MGB_LAUNCH_TOKEN</c> environment variable). When empty, <c>/healthz</c> returns <c>ok</c>.
    /// </summary>
    public string LaunchToken { get; set; } = string.Empty;

    /// <summary>Reported storage quota (KB) for <c>/user/get_user_stats</c>.</summary>
    public int MaxQuotaKb { get; set; } = 16384;
}
