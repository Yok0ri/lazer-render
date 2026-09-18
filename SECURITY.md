# SECURITY.md

**What this is.** The durable security model and audit record for LazerRender. A full security audit was
performed in Phase 7 against the original threat model; its high/medium/low findings were remediated as
part of that phase, and the remediation checklist (with per-item verification) is preserved in
[`ROADMAP.md`](ROADMAP.md:129) §7.1. This document is what a reader needs *without* the original audit
workings: the assets, the trust boundaries, the finding register and its status, the deliberate
acceptances, and the posture that must not be regressed.

**Scope note.** Only *our* interaction with `extern/osu` (the pinned upstream submodule) is in scope;
the submodule internals are third-party code.

---

## 1. Executive summary

LazerRender is in good shape for its size. The engine **never** builds a shell string — every
service-side child process uses `ProcessStartInfo.ArgumentList` — SQL is uniformly parameterised, the
only raw SQL is a compile-time constant, all storage paths are GUID-scoped, the replay header parser is
bounds-checked, the render-config validator rejects out-of-range and unknown values, the SPA has a single
escaping primitive (`esc()`) applied at every `innerHTML` interpolation of an external value, no secrets
are committed, and the shipped `systemd` unit applies meaningful sandboxing.

The residual risk was concentrated in the **deployment trust boundary** (a public
Cloudflare/NGinx/Caddy frontend the app must be told about) and in **credential handling** around the
osu! user token. Five High findings drove the Phase 7 P0 work; all are now closed, with the deployment-side
operating steps called out in §7.

---

## 2. Threat model

### 2.1 Assets

1. Each user's **osu! refresh token** (long-lived, acts on the user's behalf against osu!).
2. The **osu! OAuth client secret** (environment only) and the **bot refresh/access token**.
3. The **Data Protection key ring** — the effective master key for every stored refresh token.
4. **Render output** (`.mp4`) and uploaded replays, beatmaps and skins.
5. **Render host CPU/GPU/disk** — one serialised render semaphore, one GPU.
6. **Admin capability** (allow/revoke users, purge the shared library, read the render host's logs).

### 2.2 Trust boundaries

```
 Internet
    │  TLS
    ▼
 Cloudflare ──▶ NGinx/Caddy ──▶ [127.0.0.1]  ASP.NET service        ◀── "B1"
                                     │
                                     │ ProcessStartInfo.ArgumentList (setsid)
                                     ▼
                              LazerRender engine (unauthenticated local CLI, GPU)
                                     │
                                     │ HTTPS
                                     ▼
                              osu.ppy.sh  +  public beatmap mirrors (osu.direct, catboy.best)
```

- **B1 (proxy → app).** The app cannot see the real client or scheme unless `Proxy:KnownProxies`/
  `KnownNetworks` are configured. Without them, cookies lose `Secure`, the HTTPS redirect is inert and
  the rate limiter collapses to one shared bucket. This is a **deployment configuration requirement**,
  not an optional extra (see §7).
- **B2 (app → engine).** The osu! access token / avatar key cross this boundary. They now travel in an
  owner-only `0600` secrets file whose path is the only thing on the command line; the engine deletes it
  after reading and clears the value from its config on exit.
- **B3 (engine → third-party mirrors).** Deliberate egress; the only client-controlled input is an MD5
  validated as 32 hex characters before it becomes a URL segment.

### 2.3 Actors

| Actor | Trust |
|---|---|
| Unauthenticated internet client | Untrusted. Can reach `/health`, `/auth/*`, static SPA. |
| Authenticated but **not allowed** user | Completes osu! OAuth; the session is refused, but the identity row is persisted for an admin to allow later. |
| Authenticated allowed user | Queue jobs, upload skins/beatmaps, manage own presets, read own jobs. |
| Admin | All of the above + user allow/revoke, library purge, Render PC + console logs. |
| Local unprivileged host user | **Treated as untrusted.** |
| Render host operator | Trusted. |

---

## 3. Finding register

Severity and one-line description; the full remediation evidence (a test, a reproduction or a written
justification) lives in [`ROADMAP.md`](ROADMAP.md:129) §7.1.

### High (P0 — before public exposure)

| ID | Finding | Status |
|---|---|---|
| H-1 | `JobsHub.Subscribe` had no ownership check — any authenticated user could watch another user's job | Fixed: ownership + id-shape check (`JobsHubAuthorizationTests`). |
| H-2 | No forwarded-headers handling broke `Secure` cookies, the HTTPS redirect and per-client rate limiting | Fixed: explicit `Proxy:KnownProxies`/`KnownNetworks` trust list; production cookies always `Secure` (`ProxyConfigurationTests`). |
| H-3 | Data Protection key ring unencrypted and world-readable in the shipped deployment | Fixed: created `0700`, key files tightened, optional certificate encryption (`FilePermissionsTests`). |
| H-4 | osu! access token + avatar key crossed the process boundary via `argv` and disk | Fixed: owner-only secrets file, deleted after read, token cleared from engine config, log redaction (`SecretsChannelTests`). |
| H-5 | First user became admin on a fresh public deployment (and a personal admin id shipped) | Fixed: `Admin:OsuUserIds` empty; constant-time, one-shot, token-gated bootstrap (`BootstrapAdminTests`). |

### Medium (P1 — hardening)

| ID | Finding | Status |
|---|---|---|
| M-1 | Preset `ConfigJson` unbounded (≤220 MB) | Fixed: validated, capped 16 KB, 50 presets/user (`PresetGuardTests`). |
| M-2 | Job creation could pin a request 25 s and spawn unbounded background work | Fixed: real deadline, cancelled rather than orphaned under the render lock; `POST /jobs` gets its own limiter partition. |
| M-3 | No security headers; inline `onclick` blocked a strict CSP | Fixed: CSP / `nosniff` / `Referrer-Policy` / `X-Frame-Options`; HSTS outside Development; inline handler + inline style removed (`SecurityResponseTests`). |
| M-4 | osu! token-endpoint error body reached an exception message and the log | Fixed: only the short `error` code; failed exchange redirects to `/auth/error`. |
| M-5 | No antiforgery defence beyond `SameSite` | Fixed: state-changing requests require the `X-LazerRender-Request` header (second CSRF layer). |
| M-6 | Skin deletion had no ownership check and orphaned engine storage | Fixed: deletion restricted to uploader or admin; Realm divergence documented (**accepted** as a shared library). |
| M-7 | Engine stdout/stderr forwarded verbatim into the service log | Fixed: bounded per line and per render, redacted; carried into the Phase 8.2 pipeline (`RendererProcessRunnerLogTests`, `LoggingPipelineTests`). |
| M-8 | Untrusted `.osk`/`.osz`/`.osr` parsed with GPU privileges, unsandboxed | Mitigated: per-file cap and archive-bomb guard; `Renderer:DownloadMissing=false` and engine-user isolation documented (**residual** is inherent to a renderer). |
| M-9 | `AllowedHosts: "*"` with no host filtering | **Partial:** startup warns while unrestricted; setting the real hostname is a deployment step. |
| M-10 | Quota caps were check-then-act races | Fixed: `JobCreationGate` serialises check+insert; `DisplayNumber` unique index. |
| M-11 | Session cookie had no absolute lifetime | Fixed: 7-day absolute lifetime, no sliding, plus an `auth_time` check. |

### Low (P2 — hygiene)

L-1…L-11 (engine `.osr` hash bound; `CSS.escape`; token-helper output; runner-script search confined
outside Development; Debug-only Swagger; listing/name visibility documented; tighter `/auth` limiter;
`RedirectUri` required; `AllowUser` no longer echoes exceptions; systemd/mirror docs; central package
management + lock files and the transitive inventory). All closed — see `ROADMAP.md` §7.1.

---

## 4. Positive observations (do not "fix" these)

1. **No shell anywhere in our code.** Every service-side spawn uses `ArgumentList` with
   `UseShellExecute = false`; the only concatenated command line is `FrameSink`'s FFmpeg invocation,
   whose interpolated values are engine-generated GUID/temp paths.
2. **Parameterised SQL.** All EF Core queries are parameterised; the only `ExecuteSqlRaw` calls are
   `DatabaseInitializer`'s `ALTER TABLE`/`CREATE TABLE` built from the compile-time `ColumnPatches`
   table under `#pragma warning disable EF1002`.
3. **GUID-scoped filesystem.** `StorageService` composes every path from a server-generated GUID;
   uploads use a GUID prefix with only the extension taken from the client.
4. **Bounds-checked untrusted parsing.** `ReplayFileParser` caps string length and rejects oversized
   varints; `IsMd5Hash` enforces 32 hex characters before the value becomes an argument or a URL segment.
5. **Config validation at the boundary.** `RenderConfigValidator` rejects unknown enum values, the full
   HUD vocabulary and every numeric range on every job creation.
6. **SPA escaping is consistent.** `esc()` is applied wherever an external string meets `innerHTML`; no
   exploitable XSS was found.
7. **No secrets in the tree.** Both `.gitignore` files exclude `keys/`, `data/`, uploads/jobs/results,
   `storage/`, `publish/`, `*.env`; `appsettings.json` ships every credential field empty.
8. **Server-side authorization.** `[Authorize]` per controller, `[Authorize(Roles = "admin")]` on
   `AdminController`, owner filtering on every job read; the admin role is read from the **database** on
   the quota path, not the cookie.
9. **systemd sandboxing already present.** `NoNewPrivileges`, `PrivateTmp`, `ProtectSystem=strict`,
   `ProtectHome`, a dedicated unprivileged user and a narrow `ReadWritePaths` set.
10. **Rate limiting exists** (global fixed-window + `jobs`/`auth` partitions); it is only effective once
    B1 is correctly configured.

---

## 5. Accepted risks and non-findings

Considered and deliberately **not** changed:

| Item | Rationale |
|---|---|
| Engine CLI has no authentication | Inherent to a local tool; reachable only via the child-process boundary or an operator shell. |
| `--download-missing` fetches from public mirrors | Documented feature; the only client input is a validated 32-hex MD5. No SSRF. |
| No CORS policy | SPA is same-origin; the ASP.NET default (no CORS headers) is the safe choice. |
| `[Authorize]` decisions on the admin role | Read from the DB (`QuotaService`) or the cookie at `[Authorize(Roles)]` time, as documented. |
| Swagger present in the source | Mapped only under `IsDevelopment()`, and `#if DEBUG` — not in Release bundles. |
| `dev/` directory | Gitignored, historical, not shipped. |
| `extern/osu` submodule internals | Third-party; out of scope. |
| `DatabaseInitializer` `EnsureCreated` + `ALTER TABLE` stopgap | No security impact; a schema-divergence risk only. |
| Single render semaphore / single-instance assumption | Explicit product constraint. |
| SQLite as a local single file | No network database surface. |
| `LAZERRENDER_FLATFILL` | Development-only capture diagnostic, env-gated. |
| `--osu-user-token` uses `ArgumentList` | No command-injection risk (confidentiality is the separate H-4 concern). |
| Skin names are client-controlled and visible to all authenticated users | The shared-skin model is a product decision (M-6). |
| Eve/observability buffers hold recent logs in memory | Redacted before storage; bounded; not persisted; emptied when no admin is watching (Phase 8.2/8.3). |

---

## 6. Dependency posture

| Item | Assessment |
|---|---|
| Service packages | Pinned centrally (`LazerRender.Service/Directory.Packages.props` + per-project `packages.lock.json`): EF Core `10.0.12`, Swashbuckle `6.6.2` (Debug-only), test SDK `18.10.1`, xunit `2.9.3` / runner `3.1.5`. |
| Engine dependencies | Five `ProjectReference`s into `extern/osu`; no `PackageReference` of its own. The closure (`ManagedBass*`, `ImageSharp`, `Silk.NET`, `Veldrid`) arrives transitively and must be re-derived on every submodule bump (`dotnet list ... --include-transitive`). |
| osu! submodule | `ppy/osu` @ `2026.918.0-tachyon` (gitlink), `net10.0`. The .NET 8 → .NET 10 migration is recorded in [`MAINTENANCE.md`](MAINTENANCE.md:1) §4.2; the pin cannot advance further without repeating that kind of toolchain check. |
| `AutoMapper 13.0.1` | `NU1903` silenced upstream ("does not affect us"). **Unverified upstream claim** — re-assess on every bump. |
| `Sentry 6.6.0` | Present transitively; no initialisation found in our engine code. Re-confirm on a logging/bump change. |
| FFmpeg / `setsid` / `weston` | Resolved at runtime; the one concatenated FFmpeg arg string is engine-generated. |

---

## 7. Security-relevant deployment requirements

These are operating steps, not code — get them wrong and the hardened code is nullified. Details in
[`LazerRender.Service/DEPLOYMENT.md`](LazerRender.Service/DEPLOYMENT.md).

- Set `Proxy:KnownProxies` (or `Proxy:KnownNetworks`) to the real proxy address(es); otherwise B1 is
  unprotected.
- Set `AllowedHosts` to the deployment hostname (M-9).
- Protect the key ring: run with `DataProtection:CertificatePath` when the host is shared, and never bake
  `keys/` into a container layer.
- Supply the OAuth client secret and any fallback credentials via the environment only.
- Keep `/api/v1/admin/*` reachable only through the authenticated app; the role check is server-side, but
  the endpoints are not a public API.

---

## 8. Endpoint authorization map

| Endpoint(s) | Controller | Attribute |
|---|---|---|
| `GET /auth/login`, `/auth/callback`, `/auth/denied`, `POST /auth/logout` | `AuthController` | *(none — intentional)* |
| `GET /api/v1/me` | `MeController` | `[Authorize]` |
| `POST\|GET /api/v1/jobs`, `GET\|DELETE /api/v1/jobs/{id}`, `GET /api/v1/jobs/{id}/result` | `JobsController` | `[Authorize]` + owner filter |
| `POST\|GET /api/v1/skins`, `DELETE /api/v1/skins/{id}`, `POST /api/v1/beatmaps`, `GET /api/v1/beatmaps/cache/{md5}` | `AssetsController` | `[Authorize]` (skin delete: uploader or admin) |
| `GET /api/v1/capabilities`, `/api/v1/render-config/defaults` | `MetaController` | `[Authorize]` |
| `GET\|POST /api/v1/presets`, `DELETE /api/v1/presets/{id}` | `PresetsController` | `[Authorize]` + owner filter |
| `POST /api/v1/admin/purge`, `GET /api/v1/admin/queue`, `/users`, `POST /users/allow`, `/users/revoke`, `GET /render-pc`, `GET /logs`, `POST /logs/close` | `AdminController` | `[Authorize(Roles = "admin")]` |
| `/hubs/jobs` (`Subscribe`, `Unsubscribe`) | `JobsHub` | `[Authorize]` + ownership check |
| `/health` | `Program.cs` | *(none — health probe)* |

Regression control: `AdminObservabilityTests` reflects over every `AdminController` action and asserts the
admin-role requirement, so a new admin endpoint cannot ship unprotected.

---

## 9. Verification commands

```bash
# No secrets / key material / runtime data tracked
git ls-files | grep -iE '\.env|secret|credential|password|keys/|\.key$|\.pfx$|\.pem$'

# Hardening primitives present where expected
grep -rInE 'UseForwardedHeaders|UseHsts|UseRateLimiter' LazerRender.Service/src/LazerRender.Api/Program.cs
grep -rInE 'X-LazerRender-Request' LazerRender.Service/src/LazerRender.Api

# Submodule pin
git ls-tree HEAD LazerRender.Game/extern/osu

# Tests covering the authz/credential/log surfaces
dotnet test LazerRender.Service/LazerRender.Service.sln -c Debug --filter \
  "FullyQualifiedName~JobsHubAuthorizationTests|FullyQualifiedName~SecretsChannelTests|FullyQualifiedName~AdminObservabilityTests|FullyQualifiedName~ProxyConfigurationTests"