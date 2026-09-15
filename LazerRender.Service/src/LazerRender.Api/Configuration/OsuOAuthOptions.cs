namespace LazerRender.Api.Configuration;

/// <summary>
/// osu! OAuth v2 client configuration. Bound from the "Osu:OAuth" configuration section.
/// ClientId and ClientSecret must be supplied via environment variables or user secrets —
/// never committed.
/// </summary>
public sealed class OsuOAuthOptions
{
    public const string SectionName = "Osu:OAuth";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "https://osu.ppy.sh/oauth/authorize";
    public string TokenUrl { get; set; } = "https://osu.ppy.sh/oauth/token";
    public string ApiBaseUrl { get; set; } = "https://osu.ppy.sh/api/v2";
    public string RedirectUri { get; set; } = "";
    public string Scopes { get; set; } = "identify";
    public string UserAgent { get; set; } = "LazerRender/0.1";
}
