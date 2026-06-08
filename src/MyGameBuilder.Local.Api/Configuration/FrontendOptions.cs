namespace MyGameBuilder.Local.Api.Configuration;

/// <summary>
/// Locations for serving the legacy Flash front-end. The configured <see cref="RootPath"/>
/// directory holds the SWF and any auxiliary assets the client requests; it is served under
/// the <c>/apphost</c> path so the client's hard-coded <c>apphost/...</c> URLs resolve locally.
/// The directory is optional content (the repo never ships it): it is created on startup as a
/// known drop location, so a missing SWF simply 404s until one is placed there.
/// </summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    /// <summary>
    /// Filesystem root for front-end assets (the SWF and anything it loads relatively). Relative
    /// paths resolve against the content root. The directory is mounted at the <c>/apphost</c>
    /// request path.
    /// </summary>
    public string RootPath { get; set; } = "frontend";

    /// <summary>
    /// Flash client file name served at <c>/apphost/{SwfName}</c> and loaded by the Ruffle page.
    /// </summary>
    public string SwfName { get; set; } = "MGB.swf";
}
