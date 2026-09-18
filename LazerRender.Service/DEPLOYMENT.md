# LazerRender Service — Deployment

## 1. Execution modes

Three modes are supported. This section covers the two bare-metal ones; the container stack is §11.

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

### Containers (Docker / Podman)

`docker compose up -d --build` from the repo root builds one image holding both the service and the
engine and runs it with GPU passthrough. See §11 for the compose stack, volumes, GPU device nodes and
the reverse-proxy/forwarded-headers consequences of running behind a container network.

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
[`WEB_GUI_GUIDE.md`](../LazerRender.Game/WEB_GUI_GUIDE.md) §6 and
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

- The .NET 10 runtime and the API binaries (self-contained, linux-x64).
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

## 11. Containers (Docker / Podman + compose)

The repository root carries a two-stage [`Dockerfile`](../Dockerfile) and a
[`docker-compose.yml`](../docker-compose.yml) stack. One image holds both halves: the service (API and
render worker) published into `/app`, and the engine published as a *prebuilt*, framework-dependent
application under `/app/LazerRender.Game/publish/`. The render host therefore needs only the ASP.NET
Core runtime — no SDK, no source tree and no osu! submodule (see §4 for what a publish bundle carries,
and [`run-headless.sh`](../LazerRender.Game/scripts/run-headless.sh) for the `LAZERRENDER_ENGINE` path
that runs a published engine instead of `dotnet run`).

### Quick start

```bash
docker compose up -d --build
curl -fsS http://127.0.0.1:5180/health
```

The stack serves on **host port 5180** — deliberately not 5080, so it cannot collide with another
service on the host. `docker compose` and `podman-compose` both work.

### Environment

The compose file maps the same settings §2 documents, from a `.env` beside it (or straight into the
Portainer stack):

| compose variable | app setting |
|---|---|
| `OSU_CLIENT_ID` / `OSU_CLIENT_SECRET` | `Osu:OAuth:ClientId` / `:ClientSecret` |
| `OSU_REDIRECT_URI` | `Osu:OAuth:RedirectUri` — required once `ClientId` is set |
| `ALLOWED_HOSTS` | `AllowedHosts` — the public hostname the instance is served under |
| `ADMIN_OSU_USER_IDS` | `Admin:OsuUserIds` |
| `KNOWN_PROXIES` / `KNOWN_NETWORKS` | `Proxy:KnownProxies` / `Proxy:KnownNetworks` — see below |
| `OSU_BOT_REFRESH_TOKEN` | `Renderer:OsuBotRefreshToken` (optional fallback credential) |

Nothing above is baked into the image; every value is supplied per deployment.

### GPU passthrough

Only the **render** nodes are passed, and only the ones belonging to the GPU you want to render on.
Mesa enumerates every render node it can see and does not necessarily pick the discrete card — on a
host with both an integrated and a discrete AMD GPU, exposing both made the engine render on the
iGPU. Map the nodes on the host first:

```bash
ls -l /dev/dri/by-path/                        # which renderD* is which PCI device
cat /sys/class/drm/renderD128/device/uevent    # confirm with PCI_ID / PCI_SLOT_NAME
```

The shipped compose file passes `/dev/dri/renderD128` alone; change that line if your card is on a
different node. The nodes are mode `crw-rw-rw-`, so the container user needs no extra group.

### Shared memory

compose sets `shm_size: 1gb`. The default 64 MB `/dev/shm` is too small once the compositor and its
client are sharing buffers — a single 3840x2160 RGBA frame is already ~33 MB — so 1440p and 4K renders
can fail without it. A bare `docker run` needs the equivalent `--shm-size=1g`.

### Volumes and the key ring

The two named volumes are mandatory and never image layers:

- `lazerrender-data` → `/app/data` — SQLite database, staged uploads, results, and the engine's Realm
storage (`data/realm`);
- `lazerrender-keys` → `/app/keys` — the Data Protection key ring that decrypts every stored osu!
credential.

The image creates `/app/keys` as `0700` and the service writes each key file as `0600`. Replacing the
image never touches either volume, and `docker image history` cannot reveal a credential. Do not bind
this path to a directory on a shared volume, and do not check it into an image build.

### Reverse proxy in the container topology

Keep the §10 rule in mind, because containers change the proxy's apparent address. Forwarded headers
are only trusted from an address listed in `Proxy:KnownProxies` (or covered by
`Proxy:KnownNetworks`), which defaults to `127.0.0.1,::1`:

- a proxy on the **host** reaches the container from the docker bridge gateway (usually `172.17.0.1`);
- a proxy in the **same compose network** reaches it from its own container address, in which case list
  that subnet in `Proxy:KnownNetworks` (e.g. `172.18.0.0/16`).

Get this wrong and the symptoms are exactly the ones in §10: cookies issued without `Secure`, and
every request counted as one client by the rate limiter. The reverse-proxy chain itself is unchanged:
Cloudflare domain → NGinx → `host:5180`, and the container's `/health` is suitable for both the
docker/podman healthcheck and the proxy's upstream check.

### PID 1 and shutdown

compose sets `init: true`, so a small init (tini) is PID 1 and reaps the engine/compositor/FFmpeg
children the render loop leaves behind. With a bare `docker run`, pass `--init` for the same behaviour.
Process-group cancellation of a running render works either way — the runner signals the whole group,
not just the direct child.

### The base image is Ubuntu 24.04 (noble)

The .NET 10 images are Ubuntu 24.04 "noble", not Debian bookworm (the base used when 8.1 was verified).
That changes the container's system packages: `libasound2` is now `libasound2t64`, and noble's Weston 13
already understands `--backend=headless --renderer=gl`, so `run-headless.sh`'s flag probe succeeds and no
compositor backport is needed.

The original image pulled Mesa 25.x and Weston 14.x from `bookworm-backports` because bookworm's Mesa
22.3 cannot drive an AMD RDNA4 (`gfx1200`) part at all and Weston 10 wedged the capture pipeline on the
first frame. That workaround is obsolete with the noble base, but the underlying caveat is not: Mesa now
comes from the distro, and **if your GPU needs a newer Mesa than noble ships, rebuild on a base that
carries it** — in practice `noble-updates` currently ships Mesa 25.x, which is exactly what the earlier
image needed for RDNA4/`gfx1200`, so a current pull should already cover it. Re-run the render-path
check below on the target GPU after any base change.

### Verifying the render path

A render is the only honest test of GPU passthrough. Run one inside the container against a test replay
and confirm the frames come out at hardware speed:

```bash
docker run --rm --shm-size=1g \
  --device /dev/dri/renderD128 \
  -v "$PWD/LazerRender.Game/tests:/tests:ro" \
  -v /tmp/lr-storage:/storage -v /tmp/lr-out:/out \
  --entrypoint /app/LazerRender.Game/scripts/run-headless.sh \
  lazerrender:latest --replay /tests/replay_nm_short.osr \
      --storage /storage --output /out --encoder cpu --download-missing
```

Healthy output is a `{"type":"progress",...,"fps":200+}` stream followed by `output.mp4`. If it
stalls on `frame:0`, the engine is almost certainly not on a working GL path: the engine logs
`Capture renderer backend: osu.Framework.Graphics.OpenGL.GLRenderer` and the GPU it picked in
`LazerRender.Game/storage/logs/*.runtime.log`. Notes:

- `radeonsi` may print `'gfxNNNN' is not a recognized processor ... LLVM doesn't support gfxNNNN,
  bailing out` on very new GPUs, because the backports Mesa is still built against LLVM 15. It is
  noise as long as a GL context comes up, but it is a real fallback path.
- If hardware GL cannot be brought up at all, `LAZERRENDER_MESA_DRIVER=zink` (documented in
  [`run-headless.sh`](../LazerRender.Game/scripts/run-headless.sh)) runs the engine's OpenGL on top of
  Vulkan/RADV instead. `vulkaninfo` and `glxinfo` are *not* installed in the released image; add
  `vulkan-tools` / `mesa-utils` for a one-off debugging container.
- The image is `linux/amd64` only, and the engine publishes for `linux-x64`.

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
