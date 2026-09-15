using LazerRender.Api.Controllers;
using LazerRender.Api.Data;
using LazerRender.Api.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// M-3 / M-5 / M-6 (audit): security response headers, the CSRF custom-header requirement, and the
/// skin-deletion rule.
/// </summary>
public sealed class SecurityResponseTests
{
    [Fact]
    public void Security_headers_are_set_on_a_response()
    {
        var headers = new HeaderDictionary();

        SecurityHeaders.Apply(headers);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, headers["Content-Security-Policy"]);
    }

    /// <summary>
    /// The whole point of removing the inline handler is to keep <c>script-src</c> strict, so the policy
    /// must never grow an <c>unsafe-inline</c>.
    /// </summary>
    [Fact]
    public void The_csp_does_not_allow_unsafe_inline()
    {
        Assert.DoesNotContain("unsafe-inline", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("script-src 'self'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("base-uri 'none'", SecurityHeaders.ContentSecurityPolicy);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("get", false)]
    [InlineData("POST", true)]
    [InlineData("DELETE", true)]
    [InlineData("PATCH", true)]
    public void Only_state_changing_methods_need_the_csrf_header(string method, bool expected)
    {
        Assert.Equal(expected, RequestGuards.RequiresHeader(method));
    }

    [Fact]
    public void The_csrf_header_is_detected()
    {
        var context = new DefaultHttpContext();
        Assert.False(RequestGuards.HasHeader(context.Request));

        context.Request.Headers[RequestGuards.HeaderName] = "1";
        Assert.True(RequestGuards.HasHeader(context.Request));
    }

    /// <summary>
    /// M-6, decided by the project owner: skins stay a shared library anyone can *use*, but a user may
    /// only delete a skin they uploaded themselves (an admin may delete any).
    /// </summary>
    [Fact]
    public void A_user_may_delete_their_own_skin()
    {
        var skin = new SkinEntity { UploadedBy = "user-a" };

        Assert.True(AssetsController.MayDelete(skin, "user-a", isAdmin: false));
    }

    [Fact]
    public void A_user_may_not_delete_someone_elses_skin()
    {
        var skin = new SkinEntity { UploadedBy = "user-a" };

        Assert.False(AssetsController.MayDelete(skin, "user-b", isAdmin: false));
    }

    [Fact]
    public void An_admin_may_delete_any_skin()
    {
        var skin = new SkinEntity { UploadedBy = "user-a" };

        Assert.True(AssetsController.MayDelete(skin, "user-b", isAdmin: true));
    }

    [Fact]
    public void A_skin_with_no_recorded_uploader_is_not_user_deletable()
    {
        var skin = new SkinEntity { UploadedBy = null };

        Assert.False(AssetsController.MayDelete(skin, "user-a", isAdmin: false));
        Assert.True(AssetsController.MayDelete(skin, "user-a", isAdmin: true));
    }
}
