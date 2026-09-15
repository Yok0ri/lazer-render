using System.Collections.Concurrent;
using LazerRender.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LazerRender.Api.Services;

/// <summary>
/// Supplies the osu! API v2 <em>user</em> access token belonging to the player who queued a render,
/// so the engine can be signed in with that identity and online beatmap leaderboards work — without
/// requiring a separate bot account.
///
/// The refresh token is the one <see cref="AuthService"/> already stores (encrypted) during a normal
/// web sign-in, so this service never needs a credential of its own. osu! rotates refresh tokens on
/// use, so refreshes are serialized per user and the rotated value is written straight back to the
/// database; access tokens are cached in memory until they are close to expiring.
/// </summary>
public sealed class UserOsuTokenService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly OsuOAuthService osu;
    private readonly IDataProtector protector;
    private readonly ILogger<UserOsuTokenService> logger;

    /// <summary>One gate per user, so two renders cannot rotate the same refresh token concurrently.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();

    private readonly ConcurrentDictionary<string, CachedToken> cache = new();

    private sealed record CachedToken(OsuAccessToken Token, DateTimeOffset ExpiresAt);

    public UserOsuTokenService(
        IServiceScopeFactory scopeFactory,
        OsuOAuthService osu,
        IDataProtectionProvider protectionProvider,
        ILogger<UserOsuTokenService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.osu = osu;
        // Same purpose string as AuthService, so tokens written at sign-in can be read back here.
        protector = protectionProvider.CreateProtector("OsuOAuth.RefreshToken");
        this.logger = logger;
    }

    /// <summary>
    /// A valid access token for <paramref name="userId"/>, or <c>null</c> when that user has no stored
    /// credential or it could not be refreshed. A null result is not fatal: callers fall back to the
    /// configured bot credential and then to rendering without online leaderboards.
    /// </summary>
    public async Task<OsuAccessToken?> GetTokenAsync(string? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        if (tryGetCached(userId, out var cached))
            return cached;

        SemaphoreSlim gate = gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            // Another caller may have refreshed while this one waited for the gate.
            if (tryGetCached(userId, out cached))
                return cached;

            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            OAuthTokenEntity? stored = await db.OAuthTokens.SingleOrDefaultAsync(t => t.UserId == userId, ct);

            if (stored is null || string.IsNullOrWhiteSpace(stored.RefreshTokenEncrypted))
                return null;

            string refreshToken;

            try
            {
                refreshToken = protector.Unprotect(stored.RefreshTokenEncrypted);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not decrypt the stored osu! credential for user {UserId}.", userId);
                return null;
            }

            OsuTokenResponse refreshed = await osu.RefreshAsync(refreshToken, ct);

            // osu! rotates the refresh token; persist the new one so the web session stays usable.
            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken) && refreshed.RefreshToken != refreshToken)
            {
                stored.RefreshTokenEncrypted = protector.Protect(refreshed.RefreshToken);
                stored.IssuedAt = DateTimeOffset.UtcNow;
                stored.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
                await db.SaveChangesAsync(ct);
            }

            var token = new OsuAccessToken(refreshed.AccessToken, refreshed.ExpiresIn);
            cache[userId] = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn));

            logger.LogInformation(
                "Using the queuing player's osu! identity for this render (user {UserId}, valid for {Seconds}s).",
                userId, refreshed.ExpiresIn);

            return token;
        }
        catch (OperationCanceledException)
        {
            // The job was cancelled while refreshing; let the worker's cancellation handling take over
            // instead of falling through to the bot credential.
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(
                e,
                "Could not refresh the stored osu! credential for user {UserId}; falling back to the configured bot credential.",
                userId);

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached access token for a user. Called when they sign out or re-authenticate, so a
    /// token issued under an older scope set is never reused.
    /// </summary>
    public void Invalidate(string userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
            cache.TryRemove(userId, out _);
    }

    private bool tryGetCached(string userId, out OsuAccessToken? token)
    {
        token = null;

        if (!cache.TryGetValue(userId, out CachedToken? cached) || cached.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
            return false;

        // Report the remaining lifetime, not the lifetime the token originally had.
        token = cached.Token with
        {
            ExpiresIn = (long)Math.Max(60, (cached.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
        };

        return true;
    }
}
