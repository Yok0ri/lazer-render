using LazerRender.Api.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

/// <summary>
/// Supplies the osu! API v2 <em>user</em> access token the engine uses to sign itself in, so online
/// beatmap leaderboards work during a render.
///
/// The token must belong to a user context: lazer validates whatever it is given against
/// <c>/me</c>, which a client-credentials token cannot satisfy (no resource owner).
///
/// Configure either <c>Renderer:OsuBotToken</c> (a ready-made access token) or, preferably,
/// <c>Renderer:OsuBotRefreshToken</c> (a bot account's refresh token from this application's
/// authorization-code grant). With the latter the service refreshes on demand and persists the
/// rotated refresh token under the data directory, encrypted with Data Protection.
/// </summary>
public sealed class OsuBotAuthService
{
    /// <summary>Serializes refresh requests; osu! rotates the refresh token on every use.</summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly RendererOptions options;
    private readonly OsuOAuthService osu;
    private readonly StorageService storage;
    private readonly IDataProtector protector;
    private readonly ILogger<OsuBotAuthService> logger;

    private OsuAccessToken? cachedToken;
    private DateTimeOffset cachedExpiresAt = DateTimeOffset.MinValue;
    private string? refreshToken;
    private bool refreshTokenLoaded;

    public OsuBotAuthService(
        IOptions<RendererOptions> options,
        OsuOAuthService osu,
        StorageService storage,
        IDataProtectionProvider protectionProvider,
        ILogger<OsuBotAuthService> logger)
    {
        this.options = options.Value;
        this.osu = osu;
        this.storage = storage;
        protector = protectionProvider.CreateProtector("OsuBot.RefreshToken");
        this.logger = logger;
    }

    /// <summary>Whether any bot credential is configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.OsuBotToken) || !string.IsNullOrWhiteSpace(options.OsuBotRefreshToken);

    /// <summary>
    /// Returns a currently valid access token, or <c>null</c> when no credential is configured or the
    /// refresh failed. A <c>null</c> result is not fatal — the render simply runs without online
    /// leaderboards (and without the scoreboard element populating from the API).
    /// </summary>
    public async Task<OsuAccessToken?> GetTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.OsuBotToken))
        {
            // A static token's remaining lifetime is unknown; assume a conservative hour.
            return new OsuAccessToken(options.OsuBotToken, 3600);
        }

        if (string.IsNullOrWhiteSpace(options.OsuBotRefreshToken))
            return null;

        if (hasUsableToken())
            return remaining();

        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (hasUsableToken())
                return remaining();

            string current = loadRefreshToken();
            var token = await osu.RefreshAsync(current, ct);

            cachedToken = new OsuAccessToken(token.AccessToken, token.ExpiresIn);
            cachedExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);

            // osu! rotates the refresh token; persist the new one so a restart does not strand us.
            if (!string.IsNullOrWhiteSpace(token.RefreshToken) && token.RefreshToken != current)
                saveRefreshToken(token.RefreshToken);

            logger.LogInformation("Refreshed the osu! bot token (valid for {Seconds}s).", token.ExpiresIn);

            return cachedToken;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to refresh the osu! bot token; the render will run without online leaderboards.");

            cachedToken = null;
            cachedExpiresAt = DateTimeOffset.MinValue;

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool hasUsableToken() =>
        cachedToken is not null && cachedExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5);

    /// <summary>The cached token with its remaining lifetime, not the lifetime it had when issued.</summary>
    private OsuAccessToken remaining() =>
        cachedToken! with { ExpiresIn = (long)Math.Max(60, (cachedExpiresAt - DateTimeOffset.UtcNow).TotalSeconds) };

    private string loadRefreshToken()
    {
        if (refreshTokenLoaded)
            return refreshToken!;

        refreshTokenLoaded = true;
        refreshToken = options.OsuBotRefreshToken;

        string path = refreshTokenPath;

        if (File.Exists(path))
        {
            try
            {
                refreshToken = protector.Unprotect(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not read the persisted osu! bot refresh token; using the configured value.");
            }
        }

        return refreshToken!;
    }

    private void saveRefreshToken(string token)
    {
        refreshToken = token;

        try
        {
            Directory.CreateDirectory(storage.DataDirectory);
            File.WriteAllText(refreshTokenPath, protector.Protect(token));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not persist the rotated osu! bot refresh token.");
        }
    }

    private string refreshTokenPath => Path.Combine(storage.DataDirectory, "osu-bot-refresh-token");
}