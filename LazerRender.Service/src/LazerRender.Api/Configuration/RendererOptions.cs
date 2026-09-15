namespace LazerRender.Api.Configuration;

/// <summary>
/// How the worker invokes the LazerRender engine. The RunnerScript is resolved by walking up
/// from the content root to find <c>LazerRender.Game/scripts/run-headless.sh</c> when not configured.
/// </summary>
public sealed class RendererOptions
{
    public const string SectionName = "Renderer";

    public string RunnerScript { get; set; } = "";
    /// <summary>Explicit encoder override. Use "auto" (default) to probe and pick the best available hardware encoder.</summary>
    public string Encoder { get; set; } = "auto";
    public bool DownloadMissing { get; set; } = true;
    public string AvatarApiKey { get; set; } = "";

    /// <summary>
    /// A ready-made osu! API v2 <em>user</em> access token used to sign the render engine in, so
    /// online beatmap leaderboards (the results-screen scoreboard and the <c>scoreboard</c> HUD
    /// element) can fetch scores. Must be a user token: lazer validates it against <c>/me</c>.
    /// Prefer <see cref="OsuBotRefreshToken"/> so the token stays fresh automatically.
    /// </summary>
    public string OsuBotToken { get; set; } = "";

    /// <summary>
    /// A bot account's osu! API v2 refresh token, issued by this application's authorization-code
    /// grant. When set, the service refreshes it on demand and passes the resulting access token to
    /// the engine; the rotated refresh token is persisted (encrypted) under the data directory.
    /// </summary>
    public string OsuBotRefreshToken { get; set; } = "";

    public int ProcessTimeoutSeconds { get; set; } = 7200;
}
