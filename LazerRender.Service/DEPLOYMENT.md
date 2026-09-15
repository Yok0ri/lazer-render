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
| `Osu__OAuth__RedirectUri` | Must match the registered redirect URI, e.g. `https://example.com/auth/callback`. **Required** whenever `ClientId` is set: the service refuses to start with an empty value rather than shipping a localhost default |
| `Admin__OsuUserIds` | Comma-separated osu! user ids granted admin (and always allowed). Ships **empty** — set it to your own id |
| `Admin__BootstrapToken` | One-shot secret that claims the initial admin account on a fresh instance (see §8). Empty disables the bootstrap path |
| `Storage__DataDirectory` | Override the runtime data root (default `{contentRoot}/data`) |
| `Storage__RealmDirectory` | LazerRender `--storage` directory (default `{contentRoot}/data/realm`) |
| `Proxy__KnownProxies` | Proxy addresses allowed to set `X-Forwarded-For`/`-Proto` (default `127.0.0.1,::1`). Required for `Secure` cookies and per-client rate limiting behind the proxy — see §10 |
| `Proxy__KnownNetworks` | CIDR ranges, e.g. `172.18.0.0/16`, for a proxy on a container network |
| `DataProtection__CertificatePath` | Optional PFX (or PEM certificate) used to encrypt the key ring at rest |
| `DataProtection__CertificateKeyPath` | PEM private key, when the certificate is supplied as a PEM pair |
| `DataProtection__CertificatePassword` | Password for the supplied PFX |
| `Auth__SessionLifetimeDays` | Absolute session lifetime in days (default 7). Sliding expiration is off, so a session cannot be kept alive indefinitely by use |
| `Quota__MaxBeatmapBytes` | Largest beatmap package accepted (default 104857600 = 100 MB). Separate from the replay cap because packages are expanded by third-party parsers |
| `AllowedHosts` | Host-header allowlist. Ships as `*`; **set it to your deployment hostname** — see §12 |
| `Renderer__RunnerScript` | Absolute path to `LazerRender.Game/scripts/run-headless.sh`. Auto-detected inside the content root; in Development the search also walks parent directories (repo layout). Outside Development the search is confined to the content root, so a writable ancestor cannot substitute the executed script |
| `Renderer__Encoder` | `auto` (default; probes AMD VAAPI → NVIDIA NVENC → Intel QSV → CPU), or explicit `cpu`/`amd`/`nvidia`/`intel` |
| `Renderer__AvatarApiKey` | osu! API v2 token used to fetch replay-player avatars (public endpoint; a client-credentials token is enough) |
| `Renderer__OsuBotToken` | ready-made osu! API v2 **user** access token, used as a *fallback* to sign the engine in so online beatmap leaderboards / the `scoreboard` HUD element work |
| `Renderer__OsuBotRefreshToken` | fallback bot account refresh token (preferred over `OsuBotToken`): the service refreshes it per render and persists the rotated value, encrypted, under `data/` |

Example `/opt/lazerrender/lazerrender.env` (chmod 600, owned by the service user):

```bash
Osu__OAuth__ClientId=12345
Osu__OAuth__ClientSecret=your-secret
Osu__OAuth__RedirectUri=https://render.example.com/auth/callback
Admin__OsuUserIds=your-osu-user-id
Renderer__Encoder=auto
# The public name this instance is served under (the Host header). Not a bind address, and not set in
# the source: it is read from configuration and overridden here per deployment.
AllowedHosts=render.example.com
```

The service warns at startup while no admin account exists, and tells you which of the two bootstrap
paths applies (§8).

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

# Credentials live in this file, not in the bundle. Create it 0600 and owned by the service user.
sudo install -m 600 -o lazerrender -g lazerrender /dev/null /opt/lazerrender/lazerrender.env
sudo nano /opt/lazerrender/lazerrender.env

sudo chown -R lazerrender:lazerrender /opt/lazerrender
sudo systemctl daemon-reload
sudo systemctl enable --now lazerrender
```

**Key-ring permissions.** The service creates `keys/` as `0700` and tightens any key file it finds (it
used to inherit the process umask, which left the ring readable by every local account). Confirm it,
because the ring decrypts every stored refresh token:

```bash
sudo ls -la /opt/lazerrender/keys    # expect drwx------ and -rw------- on the *.xml files
sudo chmod 700 /opt/lazerrender/keys && sudo chmod 600 /opt/lazerrender/keys/*.xml
```

For defence in depth, encrypt the ring itself with a certificate that is *not* stored next to it — see
`DataProtection__*` in §2. The service logs at startup whether the ring is encrypted.

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
3. **Behind the proxy (the audit's H-2 check):** with the proxy in front, confirm the session cookie is
   issued `Secure` and that the rate limiter sees distinct clients:

   ```bash
   curl -sI https://render.example.com/auth/login | grep -i '^set-cookie'   # expect: Secure; HttpOnly
   ```

   A `Secure` cookie that is *missing* means `Proxy:KnownProxies` does not cover the address the proxy
   connects from (`docker inspect` / `ss -tnp` on the listeners will show it).
4. **Credential channel (H-4):** start a render and confirm the osu! token is not on the engine's
   command line, and that no secrets file outlives the job:

   ```bash
   tr '\0' ' ' < /proc/<engine-pid>/cmdline | grep -c osu-user-token   # expect 0
   ls /opt/lazerrender/data/jobs/*/secrets.json 2>/dev/null            # expect nothing
   ```

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
  when `Renderer:OsuBotRefreshToken` is used. **Treat the backup as a secret**: together with the
  database it is every user's osu! credential in one archive.
- `data/realm/` — the LazerRender Realm library (imported beatmaps/skins). Rebuildable from the
  mirrors, so backup is optional but saves re-download time.

Stop the service or use `sqlite3 .backup` for a consistent database snapshot.

## 8. Allowlist authorization (whitelist-only beta)

Authentication (osu! OAuth) is mandatory but not sufficient: each user must also be explicitly
allowed. Configured admins (`Admin:OsuUserIds`) are always allowed. Other accounts must be allowed by
an admin via the web UI (Admin → Users) or the API:

- `POST /api/v1/admin/users/allow` with `{"osuUserId": 123}` or `{"username": "peppy"}`
- `POST /api/v1/admin/users/revoke` with `{"osuUserId": 123}`

Disallowed users are recorded but never receive an application session; they are redirected to
`/auth/denied`.

### Claiming the first admin

There is no "first account to log in becomes admin" behaviour — that let any visitor take admin on an
instance that was reachable before its operator had signed in. Choose one of:

1. **Set `Admin:OsuUserIds`** to your own osu! user id (comma-separated for several). Simplest, and
the recommended production setup.
2. **One-shot bootstrap token**, for when you do not know your id yet: set
`Admin:BootstrapToken` to a random secret, then visit
`/auth/login?bootstrap=<token>` **once** while the database has no admin. The token is carried in a
short-lived HttpOnly cookie to the OAuth callback, compared in constant time, and only promotes the
account when no admin exists yet. Delete the variable and restart afterwards.

Until an admin exists the service logs a warning at startup naming whichever path is available, or
saying that none is configured.

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

## 10. TLS and forwarded headers

Terminate TLS at a reverse proxy (Caddy/nginx) in front of `127.0.0.1:5080`. A Caddyfile example is
in [`deploy/Caddyfile`](deploy/Caddyfile). The API does not terminate HTTPS in production.

The proxy must forward `X-Forwarded-Proto` and `X-Forwarded-For`, and the proxy's own address must be
listed in `Proxy:KnownProxies` (or covered by `Proxy:KnownNetworks`). Without that the app sees plain
HTTP and the proxy's address, which means:

- the session and OAuth-state cookies are issued **without `Secure`**;
- `UseHttpsRedirection` does nothing;
- the rate limiter treats every user as one client, so one caller can exhaust the shared budget.

The app never trusts forwarded headers from an address it was not told about, so a client connecting
directly cannot spoof them to evade the limiter.

## 12. Hardening notes

**Direct API calls need the CSRF header.** Every state-changing request (`POST`/`PUT`/`PATCH`/`DELETE`)
must carry `X-LazerRender-Request: 1`. A cross-site page cannot set a custom header without a CORS
preflight and this service sends no CORS headers, so the requirement is what stops a third-party page
from driving the API with your session cookie. Safe methods (`GET`/`HEAD`/`OPTIONS`) do not need it,
and the bundled SPA sends it automatically.

```bash
curl -X POST https://render.example.com/api/v1/jobs \
  -H "X-LazerRender-Request: 1" \
  -b cookies.txt \
  -F "file=@replay.osr"
```

Requests without it are rejected with `400 {"error":"csrf_header_required"}`. This includes
Swagger's "Try it out" for unsafe methods in Development — add the header there, or use curl.

If a client is ever wired to the SignalR progress hub, note that the `/hubs/jobs` **negotiate** request
is a `POST` and therefore also needs the header (`HubConnectionBuilder.withUrl(..., { headers: {...} })`).
The bundled SPA does not use the hub today; it polls.

**Restrict `AllowedHosts`.** The default `*` accepts any `Host` header. Set it to the hostname the
service is served under (`AllowedHosts=render.example.com`, or a `;`-separated list) and/or enforce the
host at the proxy. The service logs a warning at startup while it is unrestricted in Production.

**Consider disabling mirror downloads.** `Renderer:DownloadMissing=false` removes the outbound path to
the public beatmap mirrors entirely, so only beatmaps already in the Realm library can be rendered.
That is the right setting for a locked-down instance; it means uploads must supply their own beatmaps.

**Isolate the engine.** The renderer parses untrusted replay, beatmap and skin packages in the process
that holds the GPU context and (for the duration of a render) the osu! credential. Uploads are capped
and archives are checked for bomb shapes before import, but that is a mitigation and not a sandbox. On a
host with other tenants, run the service (and therefore the engine child) under its own unprivileged
account with a private data directory — the shipped systemd unit already does this, along with
`NoNewPrivileges`, `PrivateTmp`, `ProtectSystem=strict` and `ProtectHome`.

**Swagger is compiled out of Release builds.** It is a `#if DEBUG` feature and its package reference is
Debug-only, so a published bundle carries neither the code nor the dependency. A published instance
serving `ASPNETCORE_ENVIRONMENT=Development` will not expose it — run a Debug build if you need it.

**Skin deletion.** Skins are a shared library that anyone can use, but a user may only delete a skin
they uploaded; admins may delete any. Deleting the row does not remove the copy from the engine's Realm
store — that only happens with `--purge skins`.

## 13. Schema changes

The MVP uses EF `EnsureCreated`, which does not migrate existing databases. After pulling a change
that adds/removes columns, delete `data/lazerrender.db` (development only) or back up and recreate
it. Replace with EF Core migrations before the first production data migration.
