using System.Globalization;
using System.Security.Claims;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LazerRender.Api.Controllers;

[ApiController]
// Sign-in endpoints are unauthenticated and reach out to osu!, so they carry their own, tighter limit.
[EnableRateLimiting("auth")]
public sealed class AuthController : ControllerBase
{
    private const string StateCookieName = "lazerrender.oauth.state";
    private const string BootstrapCookieName = "lazerrender.oauth.bootstrap";

    private readonly OsuOAuthService osu;
    private readonly AuthService auth;
    private readonly IHostEnvironment environment;
    private readonly ILogger<AuthController> logger;

    public AuthController(
        OsuOAuthService osu,
        AuthService auth,
        IHostEnvironment environment,
        ILogger<AuthController> logger)
    {
        this.osu = osu;
        this.auth = auth;
        this.environment = environment;
        this.logger = logger;
    }

    /// <summary>
    /// Cookies are always <c>Secure</c> in production, where TLS terminates at the proxy. In
    /// development the request scheme decides, so a plain-http localhost run still works.
    /// </summary>
    private bool SecureCookies => !environment.IsDevelopment() || Request.IsHttps;

    [HttpGet("/auth/login")]
    public IActionResult Login([FromQuery] string? bootstrap)
    {
        var state = Guid.NewGuid().ToString("N");
        Response.Cookies.Append(StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = SecureCookies,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        // One-shot admin bootstrap for a fresh instance: the operator visits
        // /auth/login?bootstrap=<token> once, and AuthService promotes them only if no admin exists
        // yet and the token matches. Without a configured token the value is ignored.
        if (!string.IsNullOrWhiteSpace(bootstrap))
        {
            Response.Cookies.Append(BootstrapCookieName, bootstrap, new CookieOptions
            {
                HttpOnly = true,
                Secure = SecureCookies,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10),
            });
        }

        string authorizeUrl = osu.BuildAuthorizeUrl(state);

        // Logged because a misconfigured authorize request (bad redirect URI, unexpected scope list)
        // fails entirely on osu!'s side, where the browser just shows an error page. It contains no
        // secret: client id, redirect URI, response type, state and scopes.
        logger.LogInformation("Redirecting to osu! for authorization: {AuthorizeUrl}", authorizeUrl);

        return Redirect(authorizeUrl);
    }

    [HttpGet("/auth/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        var expectedState = Request.Cookies[StateCookieName];
        var bootstrapToken = Request.Cookies[BootstrapCookieName];
        Response.Cookies.Delete(StateCookieName);
        Response.Cookies.Delete(BootstrapCookieName);

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != expectedState)
            return BadRequest(new { error = "invalid_oauth_state" });

        UserEntity user;
        try
        {
            user = await auth.SignInWithOsuAsync(code, bootstrapToken, ct);
            logger.LogInformation("OAuth sign-in succeeded for osu! user {OsuUserId} ({Username}, role {Role}).", user.OsuUserId, user.Username, user.Role);
        }
        catch (UserNotAllowedException)
        {
            return Redirect("/auth/denied");
        }
        catch (Exception e)
        {
            // Do not let a failed exchange surface as a 500 on a trivially reachable unauthenticated
            // GET, and do not log the remote body (the exception message is already bounded and
            // redacted by OsuOAuthService).
            logger.LogWarning(e, "osu! OAuth sign-in failed for the callback request.");
            return Redirect("/auth/error");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new("osu_user_id", user.OsuUserId.ToString()),
            new(ClaimTypes.Role, user.Role),

            // Absolute session lifetime: the cookie validator rejects a principal once this is older
            // than Auth:SessionLifetimeDays, so a re-issued cookie cannot extend a session forever.
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        logger.LogInformation("Issued auth cookie for {Username}; redirecting to app.", user.Username);

        return Redirect("/");
    }

    [HttpGet("/auth/denied")]
    public IActionResult Denied()
    {
        return Content(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Access denied</title></head>" +
            "<body style=\"font-family:sans-serif;background:#12121a;color:#e8e8f0;display:grid;place-items:center;height:100vh;margin:0\">" +
            "<div style=\"text-align:center\"><h1>Access denied</h1>" +
            "<p>Your osu! account is not on the allowlist for this instance.</p>" +
            "<a href=\"/auth/login\" style=\"color:#ff66aa\">Try a different account</a></div>" +
            "</body></html>",
            "text/html");
    }

    [HttpGet("/auth/error")]
    public IActionResult Error()
    {
        return Content(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign-in failed</title></head>" +
            "<body style=\"font-family:sans-serif;background:#12121a;color:#e8e8f0;display:grid;place-items:center;height:100vh;margin:0\">" +
            "<div style=\"text-align:center\"><h1>Sign-in failed</h1>" +
            "<p>The sign-in could not be completed. Please try again.</p>" +
            "<a href=\"/auth/login\" style=\"color:#ff66aa\">Try again</a></div>" +
            "</body></html>",
            "text/html");
    }

    [HttpPost("/auth/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
            await auth.RemoveRefreshTokenAsync(userId, ct);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
