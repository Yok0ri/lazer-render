namespace LazerRender.Api.Services;

/// <summary>
/// CSRF defence for the cookie-authenticated API. State-changing requests must carry
/// <see cref="HeaderName"/>; a cross-site page cannot set a custom header without a CORS preflight, and
/// this service sends no CORS headers, so a forged request never reaches a handler. This is the second
/// layer behind the session cookie's <c>SameSite</c> policy rather than the only control.
/// </summary>
internal static class RequestGuards
{
    public const string HeaderName = "X-LazerRender-Request";

    private static readonly string[] SafeMethods = { "GET", "HEAD", "OPTIONS", "TRACE" };

    /// <summary>Whether <paramref name="method"/> can change state and therefore needs the header.</summary>
    public static bool RequiresHeader(string method) =>
        !SafeMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    public static bool HasHeader(HttpRequest request) => request.Headers.ContainsKey(HeaderName);
}
