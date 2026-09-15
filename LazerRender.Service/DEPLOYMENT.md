# LazerRender Service — Deployment

## 1. Two execution modes

### Development (framework-dependent)

Requires the ASP.NET Core 8 runtime (`Microsoft.AspNetCore.App` 8.0.x). On Arch:

```bash
sudo pacman -S --needed aspnet-runtime-8.0
```

Run from the repo root:

```bash
dotnet run --project LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj --launch-profile http
```

This listens on `http://localhost:5080` and writes runtime data to
`LazerRender.Service/src/LazerRender.Api/data/` (gitignored).

### Production (self-contained publish)

Requires no .NET runtime on the target machine — the bundle carries it. From the repo root:

```bash
LazerRender.Service/scripts/publish-service.sh linux-x64
```

The deployment directory is `LazerRender.Service/publish/`. Copy it to the server (e.g.
`/opt/lazerrender`) and run from **inside** that directory so content-root-relative runtime data
(`data/`, `keys/`) is created next to the binary:

```bash
cd /opt/lazerrender && ./LazerRender.Api
```

## 2. Configuration

The app reads `appsettings.json` for non-secret defaults. Secrets and environment-specific values
are supplied via environment variables (ASP.NET configuration maps `Section__Key` → nested keys).

| Environment variable | Purpose |
|---|---|
| `ASPNETCORE_URLS` | Listen address (default for prod: `http://127.0.0.1:5080`) |
| `ASPNETCORE_ENVIRONMENT` | `Development` or `Production` |
| `ASPNETCORE_CONTENTROOT` | Content root (set to the publish dir under systemd) |
| `ConnectionStrings__Default` | SQLite connection string (optional; default `data/lazerrender.db`) |
| `Osu__OAuth__ClientId` | osu! OAuth v2 client id (required for login) |
| `Osu__OAuth__ClientSecret` | osu! OAuth v2 client secret (required for login) |
| `Osu__OAuth__RedirectUri` | Must match the registered redirect URI, e.g. `https://example.com/auth/callback` |
| `Admin__OsuUserIds` | Comma-separated osu! user ids granted admin (and always allowed) |
| `Storage__DataDirectory` | Override the runtime data root (default `{contentRoot}/data`) |
| `Storage__RealmDirectory` | LazerRender `--storage` directory (default `{contentRoot}/data/realm`) |
| `Renderer__RunnerScript` | Path to `LazerRender.Game/scripts/run-headless.sh` (auto-detected if unset) |
| `Renderer__Encoder` | `auto` (default; probes AMD VAAPI → NVIDIA NVENC → Intel QSV → CPU), or explicit `cpu`/`amd`/`nvidia`/`intel` |
| `Renderer__AvatarApiKey` | osu! API v2 token used to fetch replay-player avatars (public endpoint; a client-credentials token is enough) |
| `Renderer__OsuBotToken` | ready-made osu! API v2 **user** access token, used as a *fallback* to sign the engine in so online beatmap leaderboards / the `scoreboard` HUD element work |
| `Renderer__OsuBotRefreshToken` | fallback bot account refresh token (preferred over `OsuBotToken`): the service refreshes it per render and persists the rotated value, encrypted, under `data/` |

Example `/opt/lazerrender/lazerrender.env` (chmod 600, owned by the service user):

```bash
Osu__OAuth__ClientId=12345
Osu__OAuth__ClientSecret=your-secret
Osu__OAuth__RedirectUri=https://render.example.com/auth/callback
Renderer__Encoder=auto
```

**Online leaderboards need no bot account.** Each render is signed into the osu! API with the
identity of the player who queued it, using the refresh token the service already stores when that
player signs in through the web UI (see `UserOsuTokenService`). The bot variables are only a
fallback for jobs whose owner has no usable stored credential, and renders work fine without them:
avatars fall back to the id embedded in the replay, and the scoreboard shows only the replaying
player.

If you do configure a fallback, `Renderer__OsuBotRefreshToken` must be a **user** refresh token
issued by this application's authorization-code grant — see
[`WEB_GUI_GUIDE.md`](LazerRender.Game/WEB_GUI_GUIDE.md) §6 and
`LazerRender.Game/scripts/fetch-user-token.sh`. Client-credentials tokens are guest-scoped and will
not enable the scoreboard.

⚠️ `Osu__OAuth__Scopes` must include `public` (the shipped default is `identify public`). `identify`
alone is enough for lazer to sign in, but the beatmap-leaderboard fetch will fail. Because the refresh
token inherits the scopes of the original grant, **users who signed in before the scopes changed must
sign in once more** for their stored token to carry `public`.

The configured list may be space-, comma- or `+`-separated; the service always emits it
`+`-separated in the authorize URL, because osu! rejects the percent-encoded (`%20`) form with
"Invalid request parameter / The client is not authorized". The full authorize URL is logged on every
`/auth/login`, so a mismatch (bad redirect URI, unexpected scopes) is visible in the service log
instead of only as an error page in the browser.

Never put credentials in `appsettings.json`; the publish bundle intentionally ships with empty
OAuth values, and `appsettings.Development.json` is excluded from publish.

## 3. systemd

```bash
sudo useradd --system --home /opt/lazerrender --shell /usr/sbin/nologin lazerrender
sudo mkdir -p /opt/lazerrender
sudo cp -r LazerRender.Service/publish/. /opt/lazerrender/
sudo cp LazerRender.Service/deploy/lazerrender.service /etc/systemd/system/
sudo cp /opt/lazerrender/lazerrender.env /opt/lazerrender/lazerrender.env  # secrets, chmod 600
sudo chown -R lazerrender:lazerrender /opt/lazerrender
sudo systemctl daemon-reload
sudo systemctl enable --now lazerrender
```

TLS is terminated by a reverse proxy (Caddy/nginx) in front of `127.0.0.1:5080`; the app itself
does not terminate HTTPS in production.

## 4. What the publish bundle contains

- The .NET 8 runtime and the API binaries (self-contained, linux-x64).
- `appsettings.json` only (no Development overrides, no PDBs).
- No OAuth credentials, no tokens. Refresh tokens are encrypted at rest in the SQLite database with
  a Data Protection key ring kept in `keys/`; both live outside the bundle and are gitignored.

## 5. Verification

Both paths must be exercised before shipping:

1. **Development:** `dotnet run --project ... --launch-profile http` → `GET /health` returns 200 and
   `data/lazerrender.db` is created.
2. **Published:** `LazerRender.Service/scripts/publish-service.sh` then `cd LazerRender.Service/publish && ./LazerRender.Api`
   → same health check and runtime data created in the publish directory.

## 6. Rate limiting

The API applies a global fixed-window limiter keyed by client IP (default 120 requests/minute, HTTP
429 when exceeded). Per-user abuse protection is separate: `Quota:MaxActiveJobs` and
`Quota:MaxJobsPerDay` cap concurrent and daily renders. Tune both in `appsettings.json` or via
environment variables.

## 7. Backups

Two directories must be backed up regularly:

- `data/lazerrender.db` — users, jobs, history, encrypted refresh tokens.
- `keys/` — the Data Protection key ring. Losing it makes stored refresh tokens unrecoverable
  (users simply re-login; no render data is lost). It also decrypts `data/osu-bot-refresh-token`
  when `Renderer:OsuBotRefreshToken` is used.
- `data/realm/` — the LazerRender Realm library (imported beatmaps/skins). Rebuildable from the
  mirrors, so backup is optional but saves re-download time.

Stop the service or use `sqlite3 .backup` for a consistent database snapshot.

## 8. Allowlist authorization (whitelist-only beta)

Authentication (osu! OAuth) is mandatory but not sufficient: each user must also be explicitly
allowed. Configured admins (`Admin:OsuUserIds`) are always allowed. The first account to log in
(when no users exist yet) is automatically granted admin + allowed when `Admin:AllowFirstUser` is
true (the default) — this is the self-hosted bootstrap. Other accounts must be allowed by an admin
via the web UI (Admin → Users) or the API:

- `POST /api/v1/admin/users/allow` with `{"osuUserId": 123}` or `{"username": "peppy"}`
- `POST /api/v1/admin/users/revoke` with `{"osuUserId": 123}`

Disallowed users are recorded but never receive an application session; they are redirected to
`/auth/denied`.

## 9. Encoder auto-detection

With `Renderer:Encoder=auto` the service probes FFmpeg once at startup (cached) in this order and
selects the first working backend:

1. AMD VAAPI (`h264_vaapi`)
2. NVIDIA NVENC (`h264_nvenc`)
3. Intel QSV (`h264_qsv`)
4. CPU (`libx264`)

A backend is chosen only when a real FFmpeg null-encode probe succeeds, so the service never claims
a hardware encoder merely because a device node exists. Set `Renderer:Encoder` explicitly to bypass
detection.

## 10. TLS

Terminate TLS at a reverse proxy (Caddy/nginx) in front of `127.0.0.1:5080`. A Caddyfile example is
in [`deploy/Caddyfile`](deploy/Caddyfile). The API does not terminate HTTPS in production.

## 11. Schema changes

The MVP uses EF `EnsureCreated`, which does not migrate existing databases. After pulling a change
that adds/removes columns, delete `data/lazerrender.db` (development only) or back up and recreate
it. Replace with EF Core migrations before the first production data migration.
