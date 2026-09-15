namespace LazerRender.Api.Services;

/// <summary>
/// Security response headers for a cookie-authenticated SPA.
///
/// <c>script-src</c> is held at <c>'self'</c> (the SPA has no inline handlers or inline scripts), and
/// <c>frame-ancestors</c>/<c>base-uri</c>/<c>object-src</c> are pinned. <c>style-src</c> also stays at
/// <c>'self'</c> because the only dynamic style the SPA needs — the progress-bar width — is applied
/// through the CSSOM rather than an inline <c>style</c> attribute.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// Avatars are served from <c>a.ppy.sh</c>, so <c>img-src</c> has to allow that host (and
    /// <c>data:</c>, which the SPA uses for inline icons).
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; "
        + "img-src 'self' data: https://a.ppy.sh; connect-src 'self'; font-src 'self'; "
        + "form-action 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'";

    public static void Apply(IHeaderDictionary headers)
    {
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
    }
}
