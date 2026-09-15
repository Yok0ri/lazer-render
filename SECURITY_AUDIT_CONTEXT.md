# SECURITY_AUDIT_CONTEXT.md

**Purpose.** This document is a map plus evidence for a security audit of LazerRender (Roadmap Phase 7 —
the audit itself, and the §7.1 hardening backlog it produces; the admin observability panel is §8.3 and
out of scope). It is written to be read **instead of** cloning the repo. Every claim below is grounded in
a file path and, where it matters, the actual code.

**Auditor expectations.** Assume you are a skilled reviewer. Prose is deliberately minimal; file paths
and code excerpts are the payload. Where a snippet is small or security-relevant, it is pasted in full.

**Baseline.** Line numbers, paths and code excerpts refer to commit `28072c17` ("Initial commit:
LazerRender headless replay recorder and web service", 106 files) — the tree this briefing was written
against. Runtime data (`storage/`, `data/`, `keys/`; ~425 MB, and the engine's Realm/ini files can
transiently hold the injected token), build output, editor config and the private `dev/` notes are
excluded from that commit via `.gitignore`: they exist on a real render host but are not part of the
tracked tree.

**Repo root (project-relative, as referenced throughout):** `lazer-render/`

**Document scope / non-goals for this file**

- ✅ Authn/authz, tokens/credentials, outbound network, IPC/process boundaries, filesystem, user input.
- ❌ Admin-panel and observability UI work (that is Roadmap §8.3, a separate task).
- ❌ Performance, correctness, or rendering-quality review.
- ❌ The `dev/` directory (historical prompts/reports, not shipped) and `LazerRender.Game/extern/osu`
  (the pinned upstream `ppy/osu` submodule — third-party code, not ours to harden).

---

## 1. Project overview

LazerRender is a headless, faster-than-realtime **osu!lazer replay recorder** plus an o!rdr-style web
service. The **engine** (`LazerRender.Game/`) wraps a pinned `ppy/osu` submodule, drives gameplay with a
manual clock, captures the GPU framebuffer, decodes audio offline, and muxes the result through FFmpeg;
it has a purely local command-line interface and **no authentication of any kind**. The **service**
(`LazerRender.Service/`) is an ASP.NET Core 8 application that authenticates users with osu! OAuth v2,
queues render jobs in SQLite, runs the engine as a child process behind a single-render semaphore, and
streams progress over SignalR to a vanilla-JS SPA. Access is allowlist-gated: signing in proves osu!
identity, and a database `IsAllowed`/`Role` check authorizes use. The service deliberately signs the
engine into lazer's API **as the queuing player** (using that player's stored, encrypted refresh token)
so online beatmap leaderboards work during a render.

---

## 2. Inventory of security-relevant surfaces

### 2.1 Authentication and login

#### 2.1.1 Complete endpoint authorization map

Endpoints are authorized via **per-controller attributes only** — nothing is registered with
`RequireAuthorization` at the route-builder level.

| Endpoint | File | Attribute |
| --- | --- | --- |
| `GET /auth/login`, `GET /auth/callback`, `GET /auth/denied`, `POST /auth/logout` | `LazerRender.Service/src/LazerRender.Api/Controllers/AuthController.cs` | *(none — intentional)* |
| `GET /api/v1/me` | `.../Controllers/MeController.cs` | `[Authorize]` |
| `POST|GET /api/v1/jobs`, `GET|DELETE /api/v1/jobs/{id}`, `GET /api/v1/jobs/{id}/result` | `.../Controllers/JobsController.cs` | `[Authorize]` |
| `POST|GET /api/v1/skins`, `DELETE /api/v1/skins/{id}`, `POST /api/v1/beatmaps`, `GET /api/v1/beatmaps/cache/{md5}` | `.../Controllers/AssetsController.cs` | `[Authorize]` |
| `GET /api/v1/capabilities`, `GET /api/v1/render-config/defaults` | `.../Controllers/MetaController.cs` | `[Authorize]` |
| `GET|POST /api/v1/presets`, `DELETE /api/v1/presets/{id}` | `.../Controllers/PresetsController.cs` | `[Authorize]` |
| `POST /api/v1/admin/purge`, `GET /api/v1/admin/queue`, `GET /api/v1/admin/users`, `POST /api/v1/admin/users/allow`, `POST /api/v1/admin/users/revoke` | `.../Controllers/AdminController.cs` | `[Authorize(Roles = "admin")]` |
| `/hubs/jobs` SignalR hub (`Subscribe`, `Unsubscribe`) | `.../Hubs/JobsHub.cs` | `[Authorize]` |
| `/health` | `Program.cs` | *(none — health probe)* |

#### 2.1.2 CSRF `state` and the login redirect

`LazerRender.Service/src/LazerRender.Api/Controllers/AuthController.cs`:

```csharp
private const string StateCookieName = "lazerrender.oauth.state";

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
```

#### 2.1.3 Callback: state check, claims, session cookie

`.../Controllers/AuthController.cs`:

```csharp
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
```

#### 2.1.4 Session cookie configuration

`LazerRender.Service/src/LazerRender.Api/Program.cs`:

```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lazerrender.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
```

#### 2.1.5 Allowlist gate + first-user-becomes-admin

`LazerRender.Service/src/LazerRender.Api/Services/AuthService.cs` (constructor and the gate):

```csharp
this.protector = protectionProvider.CreateProtector("OsuOAuth.RefreshToken");
adminIds = ParseAdminIds(adminOptions.Value.OsuUserIds);
allowFirstUser = adminOptions.Value.AllowFirstUser;
scopes = osuOptions.Value.Scopes;
```

```csharp
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
    // ...existing-user update omitted (never demotes an existing admin)...

    // Persist the identity record even for disallowed users, so an admin can allow them later.
    await db.SaveChangesAsync(ct);

    userTokens.Invalidate(user.Id);

    if (!user.IsAllowed)
        throw new UserNotAllowedException();

    return user;
}
```

Relevant configuration defaults, `LazerRender.Service/src/LazerRender.Api/appsettings.json`:

```json
  "Admin": {
    "OsuUserIds": "11566111",
    "AllowFirstUser": true
  },
  "Osu": {
    "OAuth": {
      "ClientId": "",
      "ClientSecret": "",
      "AuthorizeUrl": "https://osu.ppy.sh/oauth/authorize",
      "TokenUrl": "https://osu.ppy.sh/oauth/token",
      "ApiBaseUrl": "https://osu.ppy.sh/api/v2",
      "RedirectUri": "http://localhost:5080/auth/callback",
      "Scopes": "identify public",
      "UserAgent": "LazerRender/0.1"
    }
  },
```

> Note for the auditor: a **real osu! user id (11566111) is hard-coded** as an admin in the committed
> `appsettings.json`. Decide whether that is acceptable for the public release (Phase 8.5 also asks
> for a "published tree contains no personal leftovers" check).

Logout (`AuthController.Logout`) calls `auth.RemoveRefreshTokenAsync(userId, ct)` (deletes the encrypted
refresh-token row and invalidates the in-memory access token) and then `SignOutAsync`.

---

### 2.2 API tokens and credentials

#### 2.2.1 OAuth token exchange / refresh (client secret in a form body)

`.../Services/OsuOAuthService.cs`:

```csharp
public async Task<OsuTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
{
    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = options.ClientId,
        ["client_secret"] = options.ClientSecret,
        ["redirect_uri"] = options.RedirectUri,
        ["code"] = code,
    });

    return await PostTokenAsync(content, ct);
}

public async Task<OsuTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
{
    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = options.ClientId,
        ["client_secret"] = options.ClientSecret,
        ["refresh_token"] = refreshToken,
    });

    return await PostTokenAsync(content, ct);
}
```

```csharp
private async Task<OsuTokenResponse> PostTokenAsync(FormUrlEncodedContent content, CancellationToken ct)
{
    using var response = await http.PostAsync(options.TokenUrl, content, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException(
            $"osu! token endpoint returned {(int)response.StatusCode}: {body}");

    return JsonSerializer.Deserialize<OsuTokenResponse>(body)
        ?? throw new InvalidOperationException("Empty token response from osu!.");
}
```

> The failure path embeds the full response body in an exception message; confer with the service's
> logging to decide whether a token-endpoint error body can ever contain a secret.

#### 2.2.2 Refresh token encryption at rest (user sessions)

`Program.cs`:

```csharp
// --- Data Protection (refresh-token encryption key ring) ---
var keysDirectory = Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("LazerRender.Api");
```

`AuthService` writes the encrypted refresh token:

```csharp
if (!string.IsNullOrEmpty(token.RefreshToken))
{
    user.OAuthToken ??= new OAuthTokenEntity { UserId = user.Id };
    user.OAuthToken.RefreshTokenEncrypted = protector.Protect(token.RefreshToken);
    user.OAuthToken.Scopes = scopes;
    user.OAuthToken.IssuedAt = DateTimeOffset.UtcNow;
    user.OAuthToken.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
}
```

The token row, `.../Data/Entities.cs`:

```csharp
public sealed class OAuthTokenEntity
{
    public string UserId { get; set; } = "";
    public string RefreshTokenEncrypted { get; set; } = "";
    public string Scopes { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    public UserEntity? User { get; set; }
}
```

> **Key-ring exposure:** the Data Protection key ring is a plaintext-wrapped directory at
> `{contentRoot}/keys` with no DPAPI/KeyVault/Certificate protection. Anyone who reads that directory
> can decrypt every stored refresh token. It is **gitignored** (both `.gitignore` files ignore `keys/`)
> and excluded from the baseline commit, so the exposure is at-rest/on-host rather than in source
> control: the auditor should confirm the key ring is not baked into a container image layer and is not
> weakly permissioned on the render host.

#### 2.2.3 Supplying the engine with the queuing player's token

`.../Services/UserOsuTokenService.cs` — exchanges the stored (encrypted) refresh token for a fresh access
token, serialising refreshes per user:

```csharp
// Same purpose string as AuthService, so tokens written at sign-in can be read back here.
protector = protectionProvider.CreateProtector("OsuOAuth.RefreshToken");
```

```csharp
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
        return token;
    }
    // ...OCE rethrow + catch-all warning log omitted...
    finally
    {
        gate.Release();
    }
}
```

#### 2.2.4 The bot/fallback credential

`.../Services/OsuBotAuthService.cs` — configured `Renderer:OsuBotToken` (a static access token) or
`Renderer:OsuBotRefreshToken`; a rotated refresh token is persisted encrypted:

```csharp
protector = protectionProvider.CreateProtector("OsuBot.RefreshToken");
```

```csharp
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
        if (hasUsableToken())
            return remaining();

        string current = loadRefreshToken();
        var token = await osu.RefreshAsync(current, ct);

        cachedToken = new OsuAccessToken(token.AccessToken, token.ExpiresIn);
        cachedExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);

        // osu! rotates the refresh token; persist the new one so a restart does not strand us.
        if (!string.IsNullOrWhiteSpace(token.RefreshToken) && token.RefreshToken != current)
            saveRefreshToken(token.RefreshToken);
        // ...logging omitted...
        return cachedToken;
    }
    // ...failure handling omitted...
    finally
    {
        gate.Release();
    }
}
```

```csharp
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
```

#### 2.2.5 **Tokens are passed to the engine as command-line arguments**

This is the single highest-signal item in this section. `.../Services/RendererProcessRunner.cs`:

```csharp
private static IEnumerable<string> BuildArgs(RenderInvocation invocation)
{
    yield return "--replay";
    yield return invocation.ReplayPath;
    yield return "--output";
    yield return invocation.OutputDirectory;
    yield return "--storage";
    yield return invocation.StorageDirectory;
    yield return "--render-config";
    yield return invocation.RenderConfigPath;
    yield return "--encoder";
    yield return invocation.Encoder;

    if (invocation.DownloadMissing)
        yield return "--download-missing";

    if (!string.IsNullOrWhiteSpace(invocation.AvatarApiKey))
    {
        yield return "--avatar-api-key";
        yield return invocation.AvatarApiKey;
    }

    // A user token signs the engine into lazer's API provider, which is what lets online beatmap
    // leaderboards (the results screen and the `scoreboard` HUD element) fetch scores.
    if (!string.IsNullOrWhiteSpace(invocation.OsuUserToken))
    {
        yield return "--osu-user-token";
        yield return invocation.OsuUserToken;
        yield return "--osu-user-token-expires-in";
        yield return invocation.OsuUserTokenExpiresIn.ToString(CultureInfo.InvariantCulture);
    }
}
```

The token is assembled by `RenderWorker.RunJobAsync` (`.../Services/RenderWorker.cs`):

```csharp
OsuAccessToken? osuToken = await userOsuTokens.GetTokenAsync(job.OwnerUserId, jobCts.Token)
                          ?? await osuBotAuth.GetTokenAsync(jobCts.Token);
```

```csharp
var invocation = new RenderInvocation(
    job.Id,
    storage.StagedReplayPath(job.Id),
    storage.RenderConfigPath(job.Id),
    storage.OutputDirectory(job.Id),
    storage.RealmDirectory,
    job.Encoder.ToString().ToLowerInvariant(),
    rendererOptions.DownloadMissing,
    string.IsNullOrWhiteSpace(rendererOptions.AvatarApiKey) ? null : rendererOptions.AvatarApiKey,
    osuToken?.AccessToken,
    osuToken?.ExpiresIn ?? 3600);
```

> **Confidentiality impact:** because `ProcessStartInfo.ArgumentList` is used (not a shell string), there
> is **no command-injection risk**, but every other process on the render host can read the arguments via
> `ps` / `/proc/<pid>/cmdline` for the duration of the render. The engine also writes the token into its
> own config (see §2.2.6). The auditor should assess this against the deployment model.

#### 2.2.6 The engine persists the token into its own on-disk config

`LazerRender.Game/LazerRenderGame.cs`:

```csharp
private void applyOsuUserToken()
{
    if (string.IsNullOrWhiteSpace(options.OsuUserToken))
    {
        Logger.Log(@"osu! API login: no --osu-user-token provided; online leaderboards are disabled.");
        return;
    }

    try
    {
        var token = new OAuthToken { AccessToken = options.OsuUserToken! };
        token.ExpiresIn = Math.Max(60, options.OsuUserTokenExpiresIn);

        // A one-shot render should not "remember" the token: clearing SavePassword makes
        // lazer's own token-changed handler blank the persisted value as soon as APIAccess
        // has consumed it. Set it before the token, because toggling SavePassword off resets
        // the stored token.
        LocalConfig.SetValue(OsuSetting.SavePassword, false);
        LocalConfig.SetValue(OsuSetting.Token, token.ToString());

        Logger.Log($@"osu! API login: injected user token (valid for {options.OsuUserTokenExpiresIn}s).");
    }
    catch (Exception ex)
    {
        Logger.Log($@"osu! API login: failed to inject the user token: {ex.Message}");
    }
}
```

> The intent is that `SavePassword = false` causes lazer to blank the persisted value after `APIAccess`
> consumes it. The engine's config manager writes an ini file under the engine storage directory
> (`LazerRender.Game/storage/...`, or the service's `RealmDirectory`). **The auditor should verify that
> the token is not recoverable from disk after a render** (this is a stated known trade-off, not an
> oversight — see §3 and §6).

#### 2.2.7 Credential helper scripts

`LazerRender.Game/scripts/fetch-bearer-token.sh` and `LazerRender.Game/scripts/fetch-user-token.sh` take
`OSU_OAUTH_CLIENT_ID` / `OSU_OAUTH_CLIENT_SECRET` from the environment (never committed) and `curl` the
token endpoint. `fetch-user-token.sh` prints the refresh token that goes into
`Renderer:OsuBotRefreshToken`. They use `set -euo pipefail` and never write secrets to disk.

`OSU_API_KEY` is an accepted environment fallback for the avatar-only API key in two places:
`LazerRender.Game/Program.cs` (`options.AvatarApiKey ??= Environment.GetEnvironmentVariable(@"OSU_API_KEY");`)
and `.../Controllers/AdminController.cs` (username → osu! id resolution).

---

### 2.3 Outbound network requests

#### 2.3.1 Service → osu! (`OsuOAuthService`)

`GetMeAsync` / `GetPublicUserAsync` (`.../Services/OsuOAuthService.cs`):

```csharp
public async Task<OsuUserResponse> GetMeAsync(string accessToken, CancellationToken ct)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, options.ApiBaseUrl + "/me");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Headers.UserAgent.ParseAdd(options.UserAgent);

    using var response = await http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    return await JsonSerializer.DeserializeAsync<OsuUserResponse>(stream, cancellationToken: ct)
        ?? throw new InvalidOperationException("Empty response from osu! /me.");
}
```

```csharp
public async Task<OsuUserResponse> GetPublicUserAsync(string username, string bearerToken, CancellationToken ct)
{
    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        options.ApiBaseUrl + "/users/" + Uri.EscapeDataString(username));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    request.Headers.UserAgent.ParseAdd(options.UserAgent);
    // ...same deserialize pattern...
}
```

`OsuOAuthService` is registered as a typed `HttpClient` (`Program.cs`:
`builder.Services.AddHttpClient<OsuOAuthService>();`) — no explicit `BaseAddress`, no handler
customisation, default redirect/proxy behaviour.

#### 2.3.2 Engine → osu! (`LazerRender.Game/LazerRenderGame.cs`)

Avatar/identity lookup (the `username` is URL-escaped):

```csharp
private static async Task enrichAvatarFromApiAsync(APIUser user, string token)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        string url = $@"https://osu.ppy.sh/api/v2/users/{Uri.EscapeDataString(user.Username)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(@"Bearer", token);

        Logger.Log($@"Avatar: GET {url}");

        using var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Logger.Log($@"Avatar: HTTP {(int)response.StatusCode} {response.StatusCode} ({(response.IsSuccessStatusCode ? "success" : "failure")}).");

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($@"Avatar: response body: {truncate(body)}");
            return;
        }

        using var json = JsonDocument.Parse(body);

        if (json.RootElement.TryGetProperty(@"id", out JsonElement idElement) && idElement.TryGetInt32(out int apiUserId) && apiUserId > 1)
            user.Id = apiUserId;
        // ...avatar_url and country_code parsed here...
    }
    catch (Exception ex)
    {
        Logger.Log($@"Avatar: lookup failed for ""{user.Username}"": {ex.Message}");
    }
}
```

> The failure path logs up to `truncate(body)` of the response into the engine log, which the service
> forwards (`RendererProcessRunner.logEngineLine`). Ensure an osu! error body cannot echo the bearer
> token.

#### 2.3.3 Engine → public beatmap mirrors

`LazerRender.Game/LazerRenderGame.cs` — writes a downloaded archive to `Path.GetTempPath()`:

```csharp
private static async Task<string> downloadBeatmapAsync(string hash)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // catboy.best (and some Cloudflare-fronted mirrors) reject requests without a
    // User-Agent; osu.direct tolerates the default. Set one explicitly so the fallback works.
    http.DefaultRequestHeaders.UserAgent.ParseAdd(@"LazerRender/1.0");

    string tempPath = Path.Combine(Path.GetTempPath(), $@"lazerrender-{hash}.osz");

    // Primary: osu.direct. ...
    try
    {
        string metadata = await http.GetStringAsync($@"https://osu.direct/api/v2/md5/{hash}");

        using (JsonDocument doc = JsonDocument.Parse(metadata))
        {
            if (doc.RootElement.TryGetProperty(@"beatmapset_id", out JsonElement setIdElement)
                && setIdElement.TryGetInt32(out int setId)
                && setId > 0)
            {
                byte[] data = await http.GetByteArrayAsync($@"https://osu.direct/d/{setId}");

                if (data.Length > 100)
                {
                    await File.WriteAllBytesAsync(tempPath, data);
                    return tempPath;
                }
            }
        }
    }
    catch (Exception e)
    {
        Logger.Log($@"osu.direct download failed for {hash}: {e.Message}");
    }

    // Fallback: catboy.best (independent mirror). ...
    try
    {
        string metadata = await http.GetStringAsync($@"https://catboy.best/api/md5/{hash}");
        // ...same pattern, key @"ParentSetID", then /d/{setId}...
    }
    catch (Exception e)
    {
        Logger.Log($@"catboy.best download failed for {hash}: {e.Message}");
    }

    throw new InvalidOperationException($@"Could not download beatmap {hash} from any mirror.");
}
```

> **Trust boundary:** the downloaded `.osz` is imported into the engine's Realm DB and the beatmap is
> rendered (including its storyboard/video/skin). The `.osr` MD5 is validated as 32 hex chars before it
> reaches this method (`ReplayFileParser.IsMd5Hash`), so the hash cannot be a path/injection payload.
> Whether a malicious beatmap package is a meaningful threat for a render service is a judgement call
> for the auditor.

Also engine-side: lazer's own `APIAccess` / `OsuWebRequest` talks to the osu! API (that is the code path
the user token feeds), and lazer's `TrustedDomainOnlineStore` restricts online asset downloads to
`*.ppy.sh` hosts.

---

### 2.4 IPC / process boundaries

#### 2.4.1 The render process: `setsid` + process-group kill

`.../Services/RendererProcessRunner.cs`:

```csharp
public async Task<RenderRunResult> RunAsync(
    RenderInvocation invocation,
    CancellationToken ct,
    Func<RenderProgress, Task>? onProgress)
{
    string script = ResolveRunnerScript();

    var psi = new ProcessStartInfo
    {
        FileName = ResolveSetsid(),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    psi.ArgumentList.Add(script);
    foreach (var arg in BuildArgs(invocation))
        psi.ArgumentList.Add(arg);

    using var process = new Process { StartInfo = psi };
    process.Start();
    // ...stdout progress reader + stderr drain tasks...
}
```

```csharp
bool cancelled = false;

using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.ProcessTimeoutSeconds));

try
{
    await process.WaitForExitAsync(timeoutCts.Token);
}
catch (OperationCanceledException)
{
    cancelled = true;
    logger.LogWarning("Render {JobId} cancelled or timed out; sending SIGTERM to process group.", invocation.JobId);

    SendSignal(process.Id, SigTerm, processGroup: true);

    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        await process.WaitForExitAsync(killCts.Token);
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Render {JobId} did not exit after SIGTERM; sending SIGKILL.", invocation.JobId);
        SendSignal(process.Id, SigKill, processGroup: true);
        await process.WaitForExitAsync();
    }
}
```

The kill path (native P/Invoke):

```csharp
[DllImport("libc", SetLastError = true)]
private static extern int kill(int pid, int sig);

private static void SendSignal(int pid, int sig, bool processGroup)
{
    _ = kill(processGroup ? -pid : pid, sig);
}
```

`ResolveSetsid()` tries `/usr/bin/setsid`, `/bin/setsid`, `/usr/local/bin/setsid`, else the bare name
(`setsid` on `PATH`). `ResolveRunnerScript()` walks up from the content root looking for
`LazerRender.Game/scripts/run-headless.sh` unless `Renderer:RunnerScript` is set.

> Auditor notes: (a) the executable/script path is server-controlled, not user-controlled;
> (b) `kill(-pid, …)` targets the process group — verify this cannot signal the service's own group if
> the child fails to create a new session; (c) the same pattern is duplicated in `AssetImportRunner`.

#### 2.4.2 Asset import: same spawn pattern

`.../Services/AssetImportRunner.cs` — `RunAsync` is gated by the shared render lock
(`if (!await renderLock.Gate.WaitAsync(0, ct)) throw new AssetImportBusyException();`), and
`CaptureAsync` (used by `MapMetadataService`) is explicitly documented as *not* acquiring the lock
("The caller is responsible for acquiring the shared render lock"). Both use `ArgumentList` and
`setsid`.

#### 2.4.3 HTTP surface coupled to the engine's stdout

The engine's real IPC contract is **line-delimited JSON on stdout** plus SIGINT/SIGTERM. The service
turns stdout into DB rows and SignalR events. This is where user-visible job state is derived from a
child process, so a compromised/rogue engine could inject arbitrary progress values — low impact, but
it is the trust direction: service trusts its own child.

---

### 2.5 Filesystem access

#### 2.5.1 Service storage layout (all GUID-based)

`.../Services/StorageService.cs`:

```csharp
public string DataDirectory => Resolve(options.DataDirectory, "data");
public string UploadsDirectory => Resolve(options.UploadsDirectory, Path.Combine(DataDirectory, "uploads"));
public string JobsDirectory => Resolve(options.JobsDirectory, Path.Combine(DataDirectory, "jobs"));
public string ResultsDirectory => Resolve(options.ResultsDirectory, Path.Combine(DataDirectory, "results"));
public string RealmDirectory => Resolve(options.RealmDirectory, Path.Combine(DataDirectory, "realm"));

public string StagedReplayPath(string jobId) => Path.Combine(UploadsDirectory, $"{jobId}.osr");
public string JobDirectory(string jobId) => Path.Combine(JobsDirectory, jobId);
public string RenderConfigPath(string jobId) => Path.Combine(JobDirectory(jobId), "render-config.json");
public string OutputDirectory(string jobId) => Path.Combine(JobDirectory(jobId), "output");
public string ResultPath(string jobId) => Path.Combine(ResultsDirectory, jobId, "output.mp4");
```

```csharp
private string Resolve(string configured, string fallback)
{
    var basePath = string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    var full = Path.IsPathRooted(basePath)
        ? basePath
        : Path.Combine(environment.ContentRootPath, basePath);

    return Path.GetFullPath(full);
}
```

All `jobId` values are server-generated GUIDs (`JobEntity.Id = Guid.NewGuid().ToString("N")`), so there
is no traversal surface in these helpers. Directory roots come from configuration
(`Storage:*`), i.e. operator-controlled.

#### 2.5.2 Upload staging (temp names are GUIDs; extensions come from the client)

`.../Controllers/AssetsController.cs`:

```csharp
var tempPath = Path.Combine(storage.UploadsDirectory, $"{Guid.NewGuid():N}.osk");
```

```csharp
var tempPath = Path.Combine(storage.UploadsDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(name)}");
```

The `.osu/.osz` extension is taken from the client-supplied filename but prefixed by a server GUID, so
it cannot traverse. Files are deleted in a `finally` block.

#### 2.5.3 Secrets at rest inside the engine storage

Engine config (including the injected osu! token, §2.2.6) and Realm DB live under the engine storage
directory. The service points the engine there via `--storage <RealmDirectory>`. That directory is the
shared Realm DB for beatmaps/skins, so it is both a data store and (transiently) a secret store. It is
gitignored and excluded from the baseline commit; on a real render host it holds `client*.realm`,
`game.ini` / `game.dev.ini` and `online.db`.

#### 2.5.4 The engine CLI has no auth

`LazerRender.Game/Program.cs` is a local CLI. Anyone with shell access on the render host can render,
import, purge, and read the storage. That is inherent to the design; it is only reachable through the
service's child-process boundary.

---

### 2.6 User-supplied input

#### 2.6.1 Job upload: `JobsController.Create`

`.../Controllers/JobsController.cs`:

```csharp
var file = Request.Form.Files.GetFile("file");
var config = Request.Form["config"].ToString();
var skin = Request.Form["skin"].ToString();

if (file is null || file.Length == 0)
    return BadRequest(new ErrorResponse("replay_required", "An .osr file must be uploaded."));

if (!file.FileName.EndsWith(".osr", StringComparison.OrdinalIgnoreCase))
    return BadRequest(new ErrorResponse("invalid_extension", "Only .osr replay files are accepted."));

if (file.Length > quota.MaxUploadBytes)
    return BadRequest(new ErrorResponse(
        "file_too_large",
        $"The replay exceeds the {quota.MaxUploadBytes / (1024 * 1024)} MB limit."));

RenderConfig renderConfig;
if (!string.IsNullOrWhiteSpace(config))
{
    try
    {
        renderConfig = JsonSerializer.Deserialize<RenderConfig>(config, JsonOptions) ?? new RenderConfig();
    }
    catch (JsonException e)
    {
        return BadRequest(new ErrorResponse("invalid_config", e.Message));
    }
}
else
{
    renderConfig = new RenderConfig();
}

if (!string.IsNullOrWhiteSpace(skin))
    renderConfig.Skin = skin;

var configErrors = RenderConfigValidator.Validate(renderConfig);
if (configErrors.Count > 0)
    return BadRequest(new ErrorResponse("invalid_config", string.Join(' ', configErrors)));
```

Then the replay is staged and parsed, and metadata resolution is given a bounded wait:

```csharp
await storage.StageReplayAsync(job.Id, file.OpenReadStream(), ct);

var header = ReplayFileParser.TryParse(storage.StagedReplayPath(job.Id));
if (header is null || !ReplayFileParser.IsMd5Hash(header.BeatmapMd5))
{
    storage.DeleteStagedReplay(job.Id);
    return BadRequest(new ErrorResponse("invalid_replay", "The uploaded file is not a valid lazer .osr replay."));
}

job.ReplayMd5 = header.BeatmapMd5;
job.PlayerUsername = header.PlayerUsername;
```

```csharp
// ...the shared render lock is free for the lookup... The wait is
// bounded so a slow beatmap download can never hang the request; if it does not finish in
// time the resolution keeps running in the background and updates the job when it completes.
var resolve = mapMetadata.ResolveAndUpdateAsync(job.Id, header.BeatmapMd5);
await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(25)));
```

> The request can be held for up to 25 s while metadata resolves (which may include a beatmap download
> from a public mirror). Combine that with the global rate limiter to reason about resource exhaustion.

#### 2.6.2 Replay header parser: bounds-checked, catches everything

`.../Services/ReplayFileParser.cs`:

```csharp
public static ReplayHeader? TryParse(string path)
{
    try
    {
        using var stream = File.OpenRead(path);

        ReadVarint(stream); // ruleset id
        ReadVarint(stream); // format version

        string beatmapMd5 = ReadString(stream);
        string? username = null;

        try
        {
            username = ReadString(stream);
        }
        catch
        {
            // The username is optional for validation purposes.
        }

        if (string.IsNullOrWhiteSpace(beatmapMd5))
            return null;

        return new ReplayHeader(beatmapMd5, username);
    }
    catch
    {
        return null;
    }
}
```

```csharp
private static string ReadString(Stream stream)
{
    if (stream.ReadByte() != 0x0b)
        throw new InvalidDataException("Missing string marker in .osr header.");

    int length = ReadVarint(stream);

    if (length is < 0 or > 256)
        throw new InvalidDataException($"Implausible string length {length} in .osr header.");

    var buffer = new byte[length];
    stream.ReadExactly(buffer);
    return System.Text.Encoding.UTF8.GetString(buffer);
}
```

```csharp
private static int ReadVarint(Stream stream)
{
    int result = 0;
    int shift = 0;

    while (shift < 32)
    {
        int b = stream.ReadByte();
        if (b < 0)
            throw new EndOfStreamException("Unexpected end of .osr header.");

        result |= (b & 0x7f) << shift;

        if ((b & 0x80) == 0)
            return result;

        shift += 7;
    }

    throw new InvalidDataException("Varint too long in .osr header.");
}
```

#### 2.6.3 Render config validation

`.../Services/RenderConfigValidator.cs` validates every enum/range and the HUD whitelist vocabulary
(`LeaderboardScopeValues`, `HudComponentKeys`, `SupportedFpsValues`, `SupportedResolutions`, plus
numeric ranges). Unknown HUD keys are rejected, and it is called on every job create.

#### 2.6.4 Skin upload: filename becomes a display name

`.../Controllers/AssetsController.cs`:

```csharp
db.Skins.Add(new SkinEntity
{
    Name = Path.GetFileNameWithoutExtension(file.FileName),
    UploadedBy = userId,
});
```

> `Name` is client-controlled and is rendered in the SPA. The SPA has a single escaping helper,
> `esc()` in `LazerRender.Service/src/LazerRender.Api/wwwroot/app.js`, applied at every `innerHTML`
> interpolation site; the SPA has no build step, so `esc()` is the reviewable primitive:

```javascript
function esc(value) {
  return String(value ?? "")
    .replace(/&/g, "\u0026amp;")
    .replace(/</g, "\u0026lt;")
    .replace(/>/g, "\u0026gt;")
    .replace(/"/g, "\u0026quot;")
    .replace(/'/g, "\u0026#39;");
}
```

> Auditor task: confirm that **every** user-, owner-, or engine-controlled value that reaches
> `innerHTML`/`insertAdjacentHTML` is wrapped in `esc()`. This is a review of `app.js` (the only
> frontend file with logic); it is not asserted to be buggy here.

`DeleteSkin(id)` has no ownership check — skins are a shared library by design, and any authenticated
user may delete any skin. Confirm whether that matches the intended trust model.

#### 2.6.5 Upload size limits

`Program.cs`:

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 220L * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 220L * 1024 * 1024;
});
```

Per-endpoint caps: skins ≤ 200 MB (`AssetsController`), beatmaps rely on the global 220 MB body limit,
replays ≤ `Quota:MaxUploadBytes` (default 20 971 520 = 20 MiB).

#### 2.6.6 SQL construction

All EF Core queries are parameterised. The only raw SQL is in `.../Data/DatabaseInitializer.cs`, built
from compile-time constant identifiers and guarded with `#pragma warning disable EF1002`:

```csharp
private static readonly (string Table, string Column, string Ddl)[] ColumnPatches =
{
    ("jobs", "DisplayNumber", "INTEGER NOT NULL DEFAULT 0"),
    ("users", "IsAllowed", "INTEGER NOT NULL DEFAULT 0"),
    // ...more columns...
};

// ...

// Identifiers come from the compile-time ColumnPatches table above, never from user input.
#pragma warning disable EF1002
db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl};");
#pragma warning restore EF1002
```

---

### 2.7 Authorization logic worth auditing end-to-end

#### 2.7.1 Quota reads the role from the database (not the cookie)

`.../Services/QuotaService.cs`:

```csharp
public async Task<string?> ValidateAsync(string userId, DateTimeOffset now, CancellationToken ct)
{
    // Administrators are trusted and exempt from per-user quota limits. The role is read from
    // the database rather than the auth cookie so a promotion takes effect immediately (the
    // cookie's role claim is only refreshed at the next sign-in).
    var role = await db.Users
        .Where(u => u.Id == userId)
        .Select(u => u.Role)
        .SingleOrDefaultAsync(ct);

    if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
        return null;
    // ...active-job and per-day caps...
}
```

#### 2.7.2 **SignalR group subscription has no ownership check**

`.../Hubs/JobsHub.cs` (entire file):

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LazerRender.Api.Hubs;

/// <summary>
/// Realtime progress hub. Clients subscribe to a job id and the worker broadcasts progress
/// to that group.
/// </summary>
[Authorize]
public sealed class JobsHub : Hub
{
    public async Task Subscribe(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
    }

    public async Task Unsubscribe(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }
}
```

The worker broadcasts to that group — `.../Services/RenderWorker.cs`:

```csharp
await hub.Clients.Group(jobId).SendAsync("progress", new
{
    jobId,
    phase = progress.ParsedPhase.ToWire(),
    frame = progress.Frame,
    total = progress.Total,
    fps = progress.Fps,
});
```

Contrast with the REST endpoints, which **do** scope by owner — `JobsController.Get`:

```csharp
var job = await db.Jobs.AsNoTracking().Include(j => j.Owner)
    .SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == userId, ct);
```

and `JobsController.DownloadResult`:

```csharp
var job = await db.Jobs.AsNoTracking()
    .SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == userId, ct);
```

> **Finding to assess:** any authenticated user who learns/guesses another user's `jobId` can
> `Subscribe` to it and observe that job's progress and metadata. Job ids are 32-char GUID hex strings,
> which limits practical exploitability, but the authorization model is inconsistent between the hub and
> REST. Recommend requiring the hub to verify ownership (e.g. resolve the job and compare
> `OwnerUserId`, or broadcast to a per-user group instead of a per-job group).

#### 2.7.3 Admin endpoints are role-gated server-side

`AdminController` carries `[Authorize(Roles = "admin")]` at the class level; `AllowUser`/`RevokeUser` bind
`[FromBody]` models and the controller is `[ApiController]` (so invalid bodies 400 automatically).
`AllowUser` can resolve a username to an osu! id using `Renderer:AvatarApiKey` / `OSU_API_KEY`.

---

## 3. Things I am personally unsure about / shortcuts left in

These are flagged honestly so the auditor can prioritise. Some are known gaps rather than doubts.

1. **No `ForwardedHeaders` anywhere.** `Program.cs` never calls `UseForwardedHeaders`, and no
   `X-Forwarded-*` handling exists. Behind Cloudflare + NGinx this means:
   - the global rate limiter partitions on `HttpContext.Connection.RemoteIpAddress`, which will be the
     **proxy's** address (so the 120 req/min budget is effectively shared by all users);
   - `Request.IsHttps` / `CookieSecurePolicy.SameAsRequest` / `UseHttpsRedirection` depend on proxy
     forwarding being plumbed correctly.

   This is likely the highest-impact deployment finding.

2. **No antiforgery tokens.** No `[ValidateAntiForgeryToken]`, no `AddAntiforgery`, anywhere. Cookie auth
   is `SameSite=Lax`, and state-changing endpoints are `POST`/`DELETE` (JSON or multipart). Whether Lax
   plus the absence of CORS is sufficient is a judgement call — but there is no explicit CSRF defence.

3. **No CORS policy.** No `AddCors`/`UseCors`. The SPA is same-origin, so this is likely intentional;
   confirm nothing needs cross-origin and that the default (no CORS headers) is what you want.

4. **No HSTS.** `UseHttpsRedirection()` is present; `UseHsts()` is not.

5. **SignalR `Subscribe` ownership gap** (§2.7.2).

6. **Tokens on the command line** (§2.2.5) and **token persisted into engine config** (§2.2.6). Both are
   known/accepted trade-offs during development, but they are real confidentiality exposures on a shared
   render host.

7. **Data Protection key ring is unencrypted at rest** (`{contentRoot}/keys`, §2.2.2). Whoever reads it
   can decrypt all stored refresh tokens.

8. **A personal osu! user id is hard-coded** as an admin in `appsettings.json` (§2.1.5).

9. **`DatabaseInitializer` is an explicitly documented stopgap** ("until EF Core migrations replace it").
   It `EnsureCreated()`s, then `ALTER TABLE ADD COLUMN`s from a hand-maintained list. No migrations exist.
   Not a direct security hole, but the schema can silently diverge.

10. **`DeleteSkin` has no per-user ownership check** (§2.6.4) — appears intentional for a shared library.

11. **Engine response bodies can be logged to disk** (`enrichAvatarFromApiAsync`, §2.3.2) and forwarded
    into the service log. Low risk, but worth a look.

12. **`JobsController.Create` holds a request for up to 25 s** (§2.6.1) while metadata resolution may
    download a beatmap from a public mirror. Resource-exhaustion angle.

13. **Shortcuts/TODOs:** a repository-wide search found **no** `TODO`, `FIXME`, `HACK`, `XXX` or
    `WARNING:` comments in tracked source. There are therefore no "we know this is wrong" markers to
    surface. The only explicit "temporary" markers are the `DatabaseInitializer` doc comment and the
    `saveRefreshToken`/`PersistKeysToFileSystem` comments.

14. **Non-goal markers that look like holes but are not** (so you do not spend budget on them):
    - the engine CLI has no auth by design;
    - `--download-missing` deliberately fetches from public mirrors;
    - `MapMetadataService` passes a validated 32-hex MD5 as an argument;
    - `dev/` is historical, gitignored and not shipped.

---

## 4. The osu!lazer login-gating quirk (a nonstandard trust boundary)

This is *not* the service's own auth. It is a constraint imposed by lazer's internals that shapes how
LazerRender obtains and uses credentials, so it belongs in the threat model.

**The rule:** lazer's `LeaderboardManager.FetchWithCriteria` refuses to fetch online scores unless
`api.IsLoggedIn`, and additionally gates on:

- `BeatmapOnlineID > 0` and `Status > BeatmapOnlineStatus.Pending`,
- `scope.RequiresSupporter(...) && !api.LocalUser.Value.IsSupporter`,
- `scope == Team && api.LocalUser.Value.Team == null`.

Its failure states are `NetworkFailure = -1, BeatmapUnavailable = -2, RulesetUnavailable = -3,
NoneSelected = -4, NotLoggedIn = -5, NotSupporter = -6, NoTeam = -7`. `RequiresSupporter` is `false` for
`Local`, **`true` for `Country` and `Friend`**, otherwise the `filterMods` flag.

**Why a client-credentials token cannot be used:** lazer validates whatever token it is given against
`/me`, which is `requires user` + `identify`; a client-credentials token has no resource owner and so
cannot sign lazer in. A user token that lacks the `public` scope still passes `/me` but the scores fetch
401s (surfacing as `NetworkFailure`).

**What LazerRender does about it:** the service signs the engine in **as the queuing player**, using that
player's stored (encrypted) refresh token, so no bot account is required; `Renderer:OsuBot*` is only a
fallback. Tokens are refreshed server-side, then handed to the local child process via argv (and written
into engine config). The service explicitly warns at startup if the configured scopes lack `public`
(`Program.cs`), and the engine logs the diagnosis when the score fetch cannot proceed
(`LazerRenderGame.warmLeaderboardAsync`).

**Trust-boundary implications for you to assess:**

- A user's long-lived refresh token is used to act on their behalf against osu! (read-only public data,
  but still an impersonation-adjacent capability held server-side).
- The token crosses a local process boundary via argv and disk, not via any authenticated channel.
- The service stores one credential per user, decryptable from the unencrypted key ring.
- Note also `LazerRenderGame.cs` overrides `public override bool UseDevelopmentServer => false;` —
  a deliberate hard pin to production osu! endpoints. (Historically, a debug build of lazer otherwise
  defaults to `dev.ppy.sh`, where a production user token 401s; this override fixed it.)

Related deliberate behaviour: `ExtendedResultsScreen.FetchScores()` is overridden to skip the fetch when
not logged in, which prevents a noisy `NotLoggedIn` failure from the results screen tail:

```csharp
protected override Task<ScoreInfo[]> FetchScores()
    => api.IsLoggedIn ? base.FetchScores() : Task.FromResult(Array.Empty<ScoreInfo>());
```

---

## 5. Third-party dependencies

### 5.1 Our own projects (fully pinned, no central management)

| Project | Package | Version | Notes |
| --- | --- | --- | --- |
| `LazerRender.Api` (`LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj`) | Microsoft.EntityFrameworkCore.Design | 8.0.11 | `PrivateAssets=all`; build-time only |
| | Microsoft.EntityFrameworkCore.Sqlite | 8.0.11 | Database access |
| | Swashbuckle.AspNetCore | 6.6.2 | Enabled **Development only** (`Program.cs`: `if (app.Environment.IsDevelopment())`) |
| `LazerRender.Contracts` | *(none)* | — | Plain DTOs |
| `LazerRender.Worker.Tests` | Microsoft.NET.Test.Sdk | 17.8.0 | |
| | xunit | 2.6.6 | |
| | xunit.runner.visualstudio | 2.5.6 | |
| | Microsoft.EntityFrameworkCore.Sqlite | 8.0.11 | |

There is **no `Directory.Packages.props`, no `nuget.config`, and no `packages.lock.json`** anywhere in
the repo → no central version pinning and no locked package feed. Any transitive dependency can drift
within the ranges the top-level packages allow.

### 5.2 The engine's direct dependency is a git submodule

`LazerRender.Game/LazerRender.Game.csproj` has **no `<PackageReference>` of its own**; it references five
projects inside the pinned submodule:

```xml
<ProjectReference Include="extern/osu/osu.Game/osu.Game.csproj" />
<ProjectReference Include="extern/osu/osu.Game.Rulesets.Osu/osu.Game.Rulesets.Osu.csproj" />
<ProjectReference Include="extern/osu/osu.Game.Rulesets.Catch/osu.Game.Rulesets.Catch.csproj" />
<ProjectReference Include="extern/osu/osu.Game.Rulesets.Mania/osu.Game.Rulesets.Mania.csproj" />
<ProjectReference Include="extern/osu/osu.Game.Rulesets.Taiko/osu.Game.Rulesets.Taiko.csproj" />
```

The submodule (`.gitmodules` → `https://github.com/ppy/osu.git`) is pinned to
**`2026.821.0-tachyon`** (gitlink `e9451fe70b91292c482bab203ea87bd727eaa237`); the submodule's
`global.json` requires SDK **8.0.100** (`rollForward: latestFeature`, `allowPrerelease: false`).

### 5.3 Dependencies that arrive through the submodule

From `LazerRender.Game/extern/osu/osu.Game/osu.Game.csproj` (and `osu.Desktop`):

ppy.osu.Framework 2026.807.0, ppy.osu.Game.Resources 2026.710.0, Realm 20.1.0, SharpCompress 0.49.1,
Newtonsoft.Json 13.0.4, MessagePack 3.1.7, Microsoft.Data.Sqlite.Core 10.0.9,
SQLitePCLRaw.bundle_e_sqlite3 3.0.3, Sentry 6.6.0, HtmlAgilityPack 1.12.4, TagLibSharp 2.3.0,
**AutoMapper 13.0.1** (see below), Humanizer 2.14.1, DiffPlex 1.9.0, SignalR.Client 10.0.9,
Microsoft.Extensions.Configuration.Abstractions 10.0.9, Microsoft.Toolkit.HighPerformance 7.1.2,
System.ComponentModel.Annotations 5.0.0, System.IO.FileSystem.Primitives 4.3.0,
System.Runtime.InteropServices 4.3.0, System.Runtime.Handles 4.3.0, NUnit 4.5.1,
ppy.LocalisationAnalyser 2026.611.0, System.IO.Packaging 10.0.9, DiscordRichPresence 1.5.0.51,
Velopack 0.0.1298.

**Flags:**

- **`AutoMapper 13.0.1` has a silenced vulnerability advisory.** Verbatim from the submodule csproj:

  ```xml
  <ItemGroup Label="Package References">
    <!-- Held back due to licencing change stupidness. Silenced vulnerability does not affect us. -->
    <PackageReference Include="AutoMapper" Version="13.0.1">
      <NoWarn>NU1903</NoWarn>
    </PackageReference>
  ```

  `NU1903` is "Package has a known high severity vulnerability". Someone (upstream ppy) decided it does
  not apply; verify that claim for our usage. AutoMapper is held back deliberately (licence change).

- **`Humanizer 2.14.1` is held back** because 3.x requires .NET 9 (comment in the same file). This is not
  a `NoWarn` — it is just an older pin.
- **Media/rendering natives** (`ManagedBass`/`ManagedBass.Fx`/`ManagedBass.Mix`, `ImageSharp`,
  `Silk.NET`, `Veldrid`) arrive **transitively** through `ppy.osu.Framework` with **no explicit version
  pins in this repo**, so they cannot be audited by reading our csproj files. `ManagedBass` is used
  directly by `LazerRender.Game/BassTrackDecoder.cs` and `HitsoundMixer.cs`.
- `Sentry 6.6.0` is present in the submodule's dependency set. **Verify whether any telemetry is
  actually initialised in our engine** (I did not find Sentry initialisation in `LazerRender.Game/*.cs`);
  if it is active, that is an outbound-data-flow item the audit must cover.
- `Velopack 0.0.1298` (auto-updater) is an `osu.Desktop` dependency; our engine does not use
  `osu.Desktop`, so it should not be part of the build, but confirm.

### 5.4 External binaries

- **FFmpeg** on `PATH`: the engine pipes raw frames to it (`LazerRender.Game/FrameSink.cs`) and the
  service probes it (`.../Services/EncoderResolver.cs`). `EncoderResolver` and
  `RendererProcessRunner`/`AssetImportRunner` build arguments as a list (`psi.ArgumentList`).
  `FrameSink` is the exception: it passes **one interpolated `Arguments` string** with
  `UseShellExecute = false` (so .NET's own argument splitting, never a shell), quoting the audio FIFO
  and the output path (`LazerRender.Game/FrameSink.cs:164-177`):

  ```csharp
  string arguments =
      $@"-y -loglevel info -hide_banner {hardwareInitArgs}-f rawvideo -pix_fmt rgba -s {width}x{height} -r {fps} -i pipe:0 " +
      $@"-f s16le -ar {audioSampleRate} -ac {audioChannels} -i ""{audioFifoPath}"" " +
      $@"-map 0:v -map 1:a {videoFilterArgs}{encoderArgs}-c:a aac -b:a 192k ""{outputPath}""";
  ```

  `outputPath` and `audioFifoPath` are engine-generated (job GUID + temp dir), not client-supplied, so
  there is no injection vector today — but this is the one place a future user-controlled value would be
  dangerous, so note it.
- **`mkfifo`** is spawned the same way from `FrameSink` with a single quoted path (also
  `UseShellExecute = false`).
- **Weston**: `LazerRender.Game/scripts/run-headless.sh` starts a throwaway headless compositor on a
  per-PID socket. It requires `weston` and honours `LAZERRENDER_MESA_DRIVER`.
- **`setsid`** from util-linux.

---

## 6. Explicit non-goals (do not spend audit budget here)

These are intentional design properties of a local/offline-capable single-operator renderer:

1. **The engine CLI is unauthenticated.** `LazerRender.Game/Program.cs` is a local tool; it is only
   reachable through the service's child-process boundary or by an operator with shell access.
2. **`--download-missing` fetches beatmaps from public third-party mirrors** (osu.direct, catboy.best).
   This is a documented feature, not an SSRF surface: the only client-controlled input is an MD5 that is
   validated as 32 hex characters first.
3. **Renders are serialised** (one GPU, one worker) behind a semaphore; the service is explicitly
   **single-instance** (`DisplayNumber` uses `MAX+1`; `ClaimNextAsync` is single-instance-atomic only).
   Multi-host coordination is out of scope.
4. **SQLite** is a single local file; there is no network database.
5. **Allowlist-gated sign-in** is intentional: unknown osu! accounts are recorded but rejected
   (`UserNotAllowedException`).
6. **`LAZERRENDER_FLATFILL`** is a development-only capture diagnostic
   (`Environment.GetEnvironmentVariable("LAZERRENDER_FLATFILL") == "1"` in `LazerRenderGame.cs`).
7. **`GetBeatmapCache` / `ListSkins` / `ListPresets` are read-only** and shared-library by design.
8. **Swagger is Development-only.** Do not flag its presence in the source.
9. **`dev/`** (historical prompts and reports) is gitignored and is not part of the shipped product.
10. **The `extern/osu` submodule** is upstream third-party code. Auditing ppy's codebase is out of scope;
    audit only how *we* call into it (reflection, config injection, API endpoints).

---

## 7. Suggested reading order

1. `LazerRender.Service/src/LazerRender.Api/Program.cs` — the entire composition and middleware order.
2. `Controllers/AuthController.cs`, `Services/AuthService.cs`, `Services/OsuOAuthService.cs` — authn.
3. `Services/UserOsuTokenService.cs`, `Services/OsuBotAuthService.cs` — credential handling.
4. `Services/RendererProcessRunner.cs`, `Services/RenderWorker.cs` — the process boundary and token flow.
5. `Controllers/JobsController.cs`, `Controllers/AssetsController.cs`, `Hubs/JobsHub.cs` — input and authz.
6. `Services/StorageService.cs`, `Services/ReplayFileParser.cs`, `Data/DatabaseInitializer.cs`.
7. `LazerRender.Game/LazerRenderGame.cs` (`applyOsuUserToken`, `warmLeaderboardAsync`, `downloadBeatmapAsync`,
   `enrichAvatarFromApiAsync`).
8. `wwwroot/app.js` — the `esc()` review (§2.6.4).
