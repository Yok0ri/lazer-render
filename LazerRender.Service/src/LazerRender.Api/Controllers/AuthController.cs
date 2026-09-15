using System.Security.Claims;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace LazerRender.Api.Controllers;

[ApiController]
public sealed class AuthController : ControllerBase
{
    private const string StateCookieName = "lazerrender.oauth.state";

    private readonly OsuOAuthService osu;
    private readonly AuthService auth;
    private readonly ILogger<AuthController> logger;

    public AuthController(OsuOAuthService osu, AuthService auth, ILogger<AuthController> logger)
    {
        this.osu = osu;
        this.auth = auth;
        this.logger = logger;
    }

    [HttpGet("/auth/login")]
    public IActionResult Login()
    {
        var state = Guid.NewGuid().ToString("N");
        Response.Cookies.Append(StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        string authorizeUrl = osu.BuildAuthorizeUrl(state);

        // Logged because a misconfigured authorize request (bad redirect URI, unexpected scope list)
        // fails entirely on osu!'s side, where the browser just shows an error page.
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
        Response.Cookies.Delete(StateCookieName);

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != expectedState)
            return BadRequest(new { error = "invalid_oauth_state" });

        UserEntity user;
        try
        {
            user = await auth.SignInWithOsuAsync(code, ct);
            logger.LogInformation("OAuth sign-in succeeded for osu! user {OsuUserId} ({Username}, role {Role}).", user.OsuUserId, user.Username, user.Role);
        }
        catch (UserNotAllowedException)
        {
            return Redirect("/auth/denied");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new("osu_user_id", user.OsuUserId.ToString()),
            new(ClaimTypes.Role, user.Role),
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
