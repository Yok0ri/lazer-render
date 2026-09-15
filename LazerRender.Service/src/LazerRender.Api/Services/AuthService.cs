using LazerRender.Api.Configuration;
using LazerRender.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Thrown when the osu! identity is valid (authentication succeeded) but the account is not on
/// the allowlist (authorization failed). The caller must not create a session.
/// </summary>
public sealed class UserNotAllowedException : Exception
{
    public UserNotAllowedException() : base("This osu! account is not allowed to use this instance.") { }
}

/// <summary>
/// Turns a successful osu! OAuth exchange into a local user account. The refresh token is
/// encrypted at rest with ASP.NET Data Protection; the short-lived access token is never
/// persisted. Access is allowlist-gated: only explicitly allowed users (and configured admins)
/// may sign in.
/// </summary>
public sealed class AuthService
{
    private readonly AppDbContext db;
    private readonly OsuOAuthService osu;
    private readonly UserOsuTokenService userTokens;
    private readonly IDataProtector protector;
    private readonly HashSet<long> adminIds;
    private readonly bool allowFirstUser;
    private readonly string scopes;

    public AuthService(
        AppDbContext db,
        OsuOAuthService osu,
        UserOsuTokenService userTokens,
        IDataProtectionProvider protectionProvider,
        IOptions<AdminOptions> adminOptions,
        IOptions<OsuOAuthOptions> osuOptions)
    {
        this.db = db;
        this.osu = osu;
        this.userTokens = userTokens;
        protector = protectionProvider.CreateProtector("OsuOAuth.RefreshToken");
        adminIds = ParseAdminIds(adminOptions.Value.OsuUserIds);
        allowFirstUser = adminOptions.Value.AllowFirstUser;
        scopes = osuOptions.Value.Scopes;
    }

    private static HashSet<long> ParseAdminIds(string raw)
    {
        var set = new HashSet<long>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, out var id))
                set.Add(id);
        }

        return set;
    }

    public async Task<UserEntity> SignInWithOsuAsync(string code, CancellationToken ct)
    {
        var token = await osu.ExchangeCodeAsync(code, ct);
        var me = await osu.GetMeAsync(token.AccessToken, ct);

        var isFirstUser = allowFirstUser && !await db.Users.AnyAsync(ct);
        var isAdmin = adminIds.Contains(me.Id) || isFirstUser;

        var user = await db.Users
            .Include(u => u.OAuthToken)
            .SingleOrDefaultAsync(u => u.OsuUserId == me.Id, ct);

        if (user is null)
        {
            user = new UserEntity
            {
                OsuUserId = me.Id,
                Username = me.Username,
                AvatarUrl = me.AvatarUrl,
                CountryCode = me.CountryCode,
                Role = isAdmin ? "admin" : "user",
                IsAllowed = isAdmin,
            };
            db.Users.Add(user);
        }
        else
        {
            user.Username = me.Username;
            user.AvatarUrl = me.AvatarUrl;
            user.CountryCode = me.CountryCode;

            // Never demote an existing admin just because they are not in the configured list;
            // the allowlist flag is also left intact for non-admins (an admin manages it).
            if (isAdmin)
            {
                user.Role = "admin";
                user.IsAllowed = true;
            }
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            user.OAuthToken ??= new OAuthTokenEntity { UserId = user.Id };
            user.OAuthToken.RefreshTokenEncrypted = protector.Protect(token.RefreshToken);
            user.OAuthToken.Scopes = scopes;
            user.OAuthToken.IssuedAt = DateTimeOffset.UtcNow;
            user.OAuthToken.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        }

        // Persist the identity record even for disallowed users, so an admin can allow them later.
        await db.SaveChangesAsync(ct);

        // A re-login may have widened the granted scopes (e.g. adding `public` for leaderboards), so
        // drop any token cached from the previous grant.
        userTokens.Invalidate(user.Id);

        if (!user.IsAllowed)
            throw new UserNotAllowedException();

        return user;
    }

    public async Task RemoveRefreshTokenAsync(string userId, CancellationToken ct)
    {
        var token = await db.OAuthTokens.SingleOrDefaultAsync(t => t.UserId == userId, ct);
        if (token is not null)
        {
            db.OAuthTokens.Remove(token);
            await db.SaveChangesAsync(ct);
        }

        userTokens.Invalidate(userId);
    }
}
