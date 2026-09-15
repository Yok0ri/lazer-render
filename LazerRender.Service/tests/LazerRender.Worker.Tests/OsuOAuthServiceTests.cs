using System.Net.Http;
using LazerRender.Api.Configuration;
using LazerRender.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// The authorize URL is built by hand for the scope parameter. osu! rejects a space-encoded scope
/// list ("Invalid request parameter / The client is not authorized"), which is what
/// <see cref="Microsoft.AspNetCore.Http.QueryString.Create"/> produces, so the separator matters.
/// </summary>
public sealed class OsuOAuthServiceTests
{
    [Fact]
    public void Authorize_url_separates_scopes_with_plus_not_percent_encoding()
    {
        string url = Create("identify public").BuildAuthorizeUrl("state123");

        Assert.Contains("scope=identify+public", url);
        Assert.DoesNotContain("%20", url);
    }

    [Theory]
    [InlineData("identify,public")]
    [InlineData("identify+public")]
    [InlineData("identify public")]
    [InlineData("  identify   public  ")]
    public void Authorize_url_normalises_the_configured_separators(string configured)
    {
        Assert.Contains("scope=identify+public", Create(configured).BuildAuthorizeUrl("s"));
    }

    [Fact]
    public void Authorize_url_keeps_a_single_scope_unchanged()
    {
        Assert.Contains("scope=identify", Create("identify").BuildAuthorizeUrl("s"));
    }

    [Fact]
    public void Authorize_url_passes_the_wildcard_scope_through()
    {
        string url = Create("*").BuildAuthorizeUrl("s");

        Assert.Contains("scope=*", url);
        Assert.DoesNotContain("%2A", url);
    }

    [Fact]
    public void Authorize_url_escapes_the_redirect_uri()
    {
        Assert.Contains(
            "redirect_uri=http%3A%2F%2Flocalhost%3A5080%2Fauth%2Fcallback",
            Create("identify").BuildAuthorizeUrl("s"));
    }

    private static OsuOAuthService Create(string scopes) => new(
        Options.Create(new OsuOAuthOptions
        {
            ClientId = "12345",
            ClientSecret = "secret",
            Scopes = scopes,
            RedirectUri = "http://localhost:5080/auth/callback",
        }),
        new HttpClient());
}
