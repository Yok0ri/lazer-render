# LazerRender Security Audit Report

**Roadmap Phase 7 — Security Audit & Hardening**
**Deliverable of Phase 7.1 (hardening backlog)**

| | |
|---|---|
| **Tree under review** | `3f45d66` (HEAD) — "Docs: sync the Opus briefings with the baseline commit and renumbered phases" |
| **Code baseline equivalent** | `28072c17` — "Initial commit: LazerRender headless replay recorder and web service" |
| **Briefing followed** | [`SECURITY_AUDIT_CONTEXT.md`](SECURITY_AUDIT_CONTEXT.md) |
| **Audit type** | Source review (white-box), static. No live penetration testing was performed. |
| **Scope** | Authn/authz, credentials, outbound network, IPC/process boundaries, filesystem, user input, deployment topology, dependency posture. |
| **Explicit non-goals** | The `LazerRender.Game/extern/osu` submodule internals; performance/rendering-quality; admin observability UI (Phase 8.3); the `dev/` directory. |

---

## 0. Verification of the briefing and scope

`3f45d66` is a documentation-only commit. The entire code tree is byte-identical to the
briefing's baseline:

```
$ git diff --stat 28072c1 3f45d66
 ARCHITECTURE.md              |  3 +++
 MAINTENANCE_INFRA_CONTEXT.md | 32 ++++++++++++++++++++------------
 README.md                    |  2 +-
 SECURITY_AUDIT_CONTEXT.md    | 34 ++++++++++++++++++++++++------------
 4 files changed, 46 insertions(+), 25 deletions(-)
$ git status --short        # clean
```

Consequences:

- Every path, line number and code excerpt in `SECURITY_AUDIT_CONTEXT.md` is valid at HEAD.
- No hardening was implemented between the briefing and this audit, so **all seeded backlog
  items in Roadmap §7.1 are still open** (see §7).
- There are **no** `TODO` / `FIXME` / `HACK` / `XXX` markers in tracked first-party source, so the
  only signals are the comments the briefing already names (`DatabaseInitializer`, key-ring
  persistence, `saveRefreshToken`).

**Method.** Each section of the briefing was re-read against the actual HEAD source (not the
excerpt), then the tree was searched for the standard ASP.NET Core hardening primitives and for
user-input sinks. All findings below were reproduced by reading the code path end to end; the
"Evidence" line names the file and the relevant construct at HEAD.

**Confidence.** Findings marked *Confirmed* were read in full. Findings marked *Config-dependent*
are correct for the shipped defaults in [`appsettings.json`](LazerRender.Service/src/LazerRender.Api/appsettings.json)
or for the shipped deployment examples, and are called out as such.

---

## 1. Executive summary

LazerRender is in good shape for its size. The engine **never** builds a shell string — every
service-side child process uses `ProcessStartInfo.ArgumentList` — SQL is uniformly parameterised,
the only raw SQL is compile-time constant, all storage paths are GUID-scoped, the replay header
parser is bounds-checked, the render-config validator rejects out-of-range and unknown values, the
SPA has a single escaping primitive that is applied at every `innerHTML` interpolation of an
external value, no secrets are committed, and the shipped `systemd` unit already applies meaningful
sandboxing (`ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `NoNewPrivileges`).

The residual risk is concentrated in the **deployment trust boundary** (a public Cloudflare/NGinx/Caddy
frontend that the app is not yet aware of) and in the **credential handling** around the osu! user
token. Five findings are rated High.

| ID | Finding | Severity | Roadmap §7.1 seed |
|---|---|---|---|
| [H-1](#h-1--signalr-jobs-hub-group-subscription-has-no-ownership-check) | SignalR `JobsHub.Subscribe` has no ownership check | **High** | ✔ SignalR authorization gap |
| [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting) | No forwarded-headers handling → cookies not `Secure`, HTTPS redirect no-op, rate limiter collapses to one bucket | **High** | ✔ No forwarded-headers handling |
| [H-3](#h-3--data-protection-key-ring-is-unencrypted-at-rest-and-world-readable-in-the-shipped-deployment) | Data Protection key ring unencrypted and world-readable on the host | **High** | ✔ Key ring unprotected |
| [H-4](#h-4--osu-user-access-token-and-avatar-api-key-cross-the-process-boundary-via-argv-and-disk) | osu! access token + avatar key passed via `argv` and persisted into engine config | **High** | ✔ Token on the command line |
| [H-5](#h-5--first-user-becomes-admin-on-a-fresh-public-deployment) | `AllowFirstUser=true` + hard-coded personal admin id | **High** | (new — not in the seeds) |
| [M-1](#m-1--preset-configjson-is-unbounded-up-to-the-global-220-mb-body-limit) | Preset `ConfigJson` unbounded (≤220 MB) → SQLite/DB bloat | Medium | (new) |
| [M-2](#m-2--job-creation-can-pin-a-request-for-25-s-and-spawn-background-work-unbounded-per-client) | `POST /api/v1/jobs` holds up to 25 s and spawns background downloads | Medium | ✔ 25 s hold |
| [M-3](#m-3--no-security-headers-and-an-inline-event-handler-blocks-a-strict-csp) | No CSP/HSTS/nosniff/frame-ancestors; inline `onclick` | Medium | ✔ no HSTS |
| [M-4](#m-4--oauth-token-endpoint-error-body-lands-in-an-exception-message-and-the-log) | osu! token-error body in exception message + log | Medium | ✔ body-in-exception |
| [M-5](#m-5--no-antiforgery-defence-the-cookie-is-the-only-csrf-control-and-it-is-not-secure) | No antiforgery; `SameSite=Lax` is the only CSRF control (and cookie is not `Secure`) | Medium | ✔ no antiforgery |
| [M-6](#m-6--skin-deletion-has-no-ownership-check-and-never-purges-engine-storage) | `DeleteSkin` has no ownership check and orphans engine storage | Medium | ✔ DeleteSkin |
| [M-7](#m-7--engine-stdoutstderr-is-forwarded-verbatim-into-the-service-log) | Engine output forwarded verbatim into the service log | Medium | ✔ engine logging |
| [M-8](#m-8--untrusted-package-parsing-runs-with-gpu-privileges-and-no-sandbox) | Untrusted `.osk`/`.osz`/`.osr` parsed with GPU privileges, unsandboxed | Medium | (new) |
| [M-9](#m-9--allowedhosts--with-no-host-filtering) | `AllowedHosts: "*"` | Medium | (new) |
| [M-10](#m-10--quota-caps-are-check-then-act-races) | Quota caps are check-then-act races | Medium | (new) |
| [M-11](#m-11--session-cookie-has-no-absolute-lifetime) | 14-day sliding session, no absolute lifetime | Medium | (new) |
| [L-1](#l-1--engine-side-osr-header-parser-has-an-unbounded-allocation-defense-in-depth) … [L-11](#l-11--no-central-package-management-and-a-silenced-transitive-advisory) | Eleven low-severity hardening items | Low | mixed |

---

## 2. Threat model

### 2.1 Assets

1. Each user's **osu! refresh token** (long-lived, acts on the user's behalf against osu!).
2. The **osu! OAuth client secret** (env only) and the **bot refresh/access token**.
3. The **Data Protection key ring** — the effective master key for every stored refresh token.
4. **Render output** (`.mp4`) and uploaded replays, beatmaps and skins.
5. **Render host CPU/GPU/disk** — single render semaphore, single GPU.
6. **Admin capability** (allow/revoke users, purge the shared library).

### 2.2 Trust boundaries

```
 Internet
    │  TLS
    ▼
 Cloudflare ──▶ NGinx/Caddy ──▶ [127.0.0.1:5080]  ASP.NET service   ◀── "B1"
                                     │
                                     │ ProcessStartInfo.ArgumentList (setsid)
                                     ▼
                              LazerRender engine (unauthenticated local CLI, GPU)
                                     │
                                     │ HTTPS
                                     ▼
                              osu.ppy.sh  +  public beatmap mirrors (osu.direct, catboy.best)
```

- **B1 (proxy → app)** is the boundary the app is currently blind to: no `ForwardedHeaders`, so the
  app cannot tell HTTPS from HTTP, or one user from another (see [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting)).
- **B2 (app → engine)** carries a bearer token over `argv` and disk, with no credential channel of
  its own (see [H-4](#h-4--osu-user-access-token-and-avatar-api-key-cross-the-process-boundary-via-argv-and-disk)).
- **B3 (engine → third-party mirrors)** is a deliberate egress path; the only client-controlled
  input is an MD5 validated as 32 hex characters.

### 2.3 Actors

| Actor | Trust |
|---|---|
| Unauthenticated internet client | Untrusted. Can reach `/health`, `/auth/login`, `/auth/callback`, `/auth/denied`, static SPA. |
| Authenticated but **not allowed** user | Can complete osu! OAuth; the session is refused (`UserNotAllowedException`), but the identity row is persisted. |
| Authenticated allowed user | Can queue jobs, upload skins/beatmaps, manage own presets, read own jobs. |
| Admin | All of the above + user allow/revoke + library purge. |
| Local unprivileged host user | **Treated as untrusted** by this audit (see H-3/H-4). |
| Render host operator | Trusted. |

---

## 3. Confirmed briefing findings

The following were independently re-verified at HEAD. Each links to the full finding below or is
noted as accepted.

| Briefing item | Status at HEAD |
|---|---|
| §2.7.2 SignalR `Subscribe` ownership gap | **Confirmed** → [H-1](#h-1--signalr-jobs-hub-group-subscription-has-no-ownership-check) |
| §3.1 No `ForwardedHeaders` | **Confirmed** → [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting) |
| §2.2.2 / §3.7 Key ring unencrypted | **Confirmed** (and worse than stated — see [H-3](#h-3--data-protection-key-ring-is-unencrypted-at-rest-and-world-readable-in-the-shipped-deployment)) |
| §2.2.5/§2.2.6 Token on `argv` and in engine config | **Confirmed** → [H-4](#h-4--osu-user-access-token-and-avatar-api-key-cross-the-process-boundary-via-argv-and-disk) |
| §2.1.5 Hard-coded personal admin id | **Confirmed, escalated** → [H-5](#h-5--first-user-becomes-admin-on-a-fresh-public-deployment) |
| §2.6.1 25 s hold in `JobsController.Create` | **Confirmed** → [M-2](#m-2--job-creation-can-pin-a-request-for-25-s-and-spawn-background-work-unbounded-per-client) |
| §3.4 No HSTS | **Confirmed** → [M-3](#m-3--no-security-headers-and-an-inline-event-handler-blocks-a-strict-csp) |
| §2.2.1 Token-endpoint body in the exception message | **Confirmed** → [M-4](#m-4--oauth-token-endpoint-error-body-lands-in-an-exception-message-and-the-log) |
| §3.2 No antiforgery | **Confirmed** → [M-5](#m-5--no-antiforgery-defence-the-cookie-is-the-only-csrf-control-and-it-is-not-secure) |
| §2.6.4 `DeleteSkin` has no ownership check | **Confirmed** → [M-6](#m-6--skin-deletion-has-no-ownership-check-and-never-purges-engine-storage) |
| §2.3.2 Engine response bodies logged | **Confirmed** → [M-7](#m-7--engine-stdoutstderr-is-forwarded-verbatim-into-the-service-log) |
| §2.6.6 SQL construction | **Confirmed safe** (parameterised; raw SQL is compile-time constant) |
| §5.3 AutoMapper `NU1903` silenced | **Present** → [L-11](#l-11--no-central-package-management-and-a-silenced-transitive-advisory) |
| §3.3 No CORS | **Confirmed, accepted** — same-origin SPA; absence is the safe default |
| §3.9 `DatabaseInitializer` stopgap | **Confirmed, accepted** — no security impact |
| §3.14 Non-goals (engine CLI auth, `--download-missing`, MD5 argument, `dev/`) | **Confirmed as designed** |

**Search evidence** (zero hits in `LazerRender.Service/**/*.cs`):

```
AddAntiforgery | ValidateAntiForgeryToken | AutoValidateAntiforgery
AddCors | UseCors | UseForwardedHeaders | UseHsts
AllowAnonymous | RequireAuthorization | X-Forwarded
```

---

## 4. Findings

### H-1 — SignalR Jobs Hub group subscription has no ownership check

**Severity:** High · **Category:** Broken access control (IDOR) · **Roadmap §7.1:** ✔

**Evidence.** [`Hubs/JobsHub.cs`](LazerRender.Service/src/LazerRender.Api/Hubs/JobsHub.cs:13) — the
entire hub:

```csharp
[Authorize]
public sealed class JobsHub : Hub
{
    public async Task Subscribe(string jobId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, jobId);

    public async Task Unsubscribe(string jobId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
}
```

The worker broadcasts to that group in
[`RenderWorker.BroadcastProgressAsync`](LazerRender.Service/src/LazerRender.Api/Services/RenderWorker.cs:322):

```csharp
await hub.Clients.Group(jobId).SendAsync("progress", new { jobId, phase, frame, total, fps });
```

The REST endpoints, by contrast, are owner-scoped —
[`JobsController.Get`](LazerRender.Service/src/LazerRender.Api/Controllers/JobsController.cs:192)
and [`DownloadResult`](LazerRender.Service/src/LazerRender.Api/Controllers/JobsController.cs:239)
both filter on `j.OwnerUserId == userId`.

**Impact.** Any authenticated (even **not allowed**) user who obtains another user's `jobId` can
`Subscribe` to it and observe that job's phase, frame, total and instantaneous FPS in real time.
`jobId` is a 32-char GUID hex string, which is not guessable in practice, so this is an
authorization-consistency defect rather than a practical mass-disclosure. It is nevertheless a real
BAC gap: the hub trusts a caller-supplied identifier where every other surface verifies ownership.
Note also that neither `jobId` length nor format is validated, so a caller can create arbitrarily
many groups.

**Recommendation.** Resolve the job and compare `OwnerUserId` before `AddToGroupAsync`, or
(preferable) broadcast to a **per-user group** derived from
`ClaimTypes.NameIdentifier` so the client never supplies the routing key:

```csharp
public async Task Subscribe(string jobId)
{
    var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
    // look up the job in a scoped DbContext; only join if it belongs to userId
    await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
}
```

**Verification.** Integration test: authenticate as user A, `Subscribe` to a job owned by user B,
assert no `progress` message is delivered. Add a unit test over the hub method with a mocked
`IHubContext`.

---

### H-2 — No forwarded-headers handling breaks secure cookies, HTTPS redirect and rate limiting

**Severity:** High (deployment) · **Category:** Security misconfiguration · **Roadmap §7.1:** ✔

**Evidence.** [`Program.cs`](LazerRender.Service/src/LazerRender.Api/Program.cs:161) middleware chain:

```csharp
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
```

There is no `UseForwardedHeaders`, and no `X-Forwarded-*` handling anywhere in the service
(confirmed by search). The rate limiter partitions on the raw socket address
([`Program.cs:95`](LazerRender.Service/src/LazerRender.Api/Program.cs:95)):

```csharp
context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
```

and both cookies are gated on the request scheme:

- auth cookie: `options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;`
  ([`Program.cs:80`](LazerRender.Service/src/LazerRender.Api/Program.cs:80))
- OAuth state cookie: `Secure = Request.IsHttps`
  ([`AuthController.cs:33`](LazerRender.Service/src/LazerRender.Api/Controllers/AuthController.cs:33))

The shipped systemd unit listens on plain HTTP behind a proxy
([`deploy/lazerrender.service:14`](LazerRender.Service/deploy/lazerrender.service:14)):

```
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
```

and the shipped proxy example adds no headers of its own
([`deploy/Caddyfile`](LazerRender.Service/deploy/Caddyfile:3)).

**Impact.**

1. **Cookies are issued without `Secure` in production.** `Request.IsHttps` is `false` behind the
   TLS-terminating proxy (the app speaks HTTP to Caddy/NGinx). Both the 14-day session cookie and
   the OAuth state cookie therefore lack the `Secure` flag. If the site is ever reachable over
   plaintext (a misconfigured proxy, an HTTP vhost, an HSTS-less first visit), the session cookie
   can leak, defeating the `HttpOnly` protection.
2. **`UseHttpsRedirection` is effectively inert.** With no `https_port` configured and no HTTPS
   address in `IServerAddressesFeature`, ASP.NET Core logs a warning and does not redirect. The
   redirect is not a security control here.
3. **The rate limiter collapses to a single global bucket.** Every request arrives with the
   proxy's address, so the 120 req/min budget is shared across all users. One abusive client can
   starve everyone; conversely the limiter provides no per-client protection.
4. **The OAuth `state` cookie is not `Secure`**, weakening the login-CSRF defence that is the
   *only* CSRF control (see [M-5](#m-5--no-antiforgery-defence-the-cookie-is-the-only-csrf-control-and-it-is-not-secure)).

**Recommendation.**

1. Add `ForwardedHeaders` **before everything else**, restricted to the actual proxy hop(s):

   ```csharp
   builder.Services.Configure<ForwardedHeadersOptions>(o =>
   {
       o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
       o.KnownNetworks.Clear();          // do NOT trust XFF from the whole internet
       o.KnownProxies.Add(IPAddress.Loopback);   // or the container/proxy address
   });
   app.UseForwardedHeaders();
   ```

   Configure `KnownProxies`/`KnownNetworks` explicitly so a direct client cannot spoof
   `X-Forwarded-For` and evade the limiter.
2. Set `CookieSecurePolicy.Always` on the auth cookie and `Secure = true` on the state cookie, then
   terminate TLS at the proxy as the only ingress.
3. Re-evaluate the rate limiter once `RemoteIpAddress` is meaningful (consider a per-user partition
   on the authenticated identity, and a tighter dedicated limit on `/auth/*`).
4. Add `UseHsts()` behind a production-scheme guard, and send `Strict-Transport-Security` from the
   proxy (see [M-3](#m-3--no-security-headers-and-an-inline-event-handler-blocks-a-strict-csp)).

**Verification.** Assert in an integration test (with `X-Forwarded-Proto: https` and
`X-Forwarded-For` set from a known proxy) that the `Set-Cookie` carries `Secure` and that the
limiter partitions on the forwarded address.

---

### H-3 — Data Protection key ring is unencrypted at rest and world-readable in the shipped deployment

**Severity:** High · **Category:** Sensitive data exposure at rest · **Roadmap §7.1:** ✔

**Evidence.** [`Program.cs:66`](LazerRender.Service/src/LazerRender.Api/Program.cs:66):

```csharp
var keysDirectory = Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("LazerRender.Api");
```

There is no `ProtectKeysWithDpapi` / `ProtectKeysWithCertificate` / Key Vault. The keys are stored
as plaintext XML under `{contentRoot}/keys`. The refresh token and the rotated bot refresh token are
encrypted with the protector created from this ring
([`AuthService.cs:45`](LazerRender.Service/src/LazerRender.Api/Services/AuthService.cs:45),
[`UserOsuTokenService.cs:41`](LazerRender.Service/src/LazerRender.Api/Services/UserOsuTokenService.cs:41),
[`OsuBotAuthService.cs:45`](LazerRender.Service/src/LazerRender.Api/Services/OsuBotAuthService.cs:45)),
so **whoever reads `keys/` can decrypt every stored osu! refresh token.**

The briefing correctly notes the ring is gitignored. The audit found the at-rest exposure is
concretely worse than the briefing implies, because of the *shipped* deployment instructions:

- [`DEPLOYMENT.md:105`](LazerRender.Service/DEPLOYMENT.md:105) does
  `sudo chown -R lazerrender:lazerrender /opt/lazerrender` but never tightens permissions.
  `Directory.CreateDirectory` creates `0755` under a default `umask 022`, so `keys/` and its key
  files are **world-readable** on the render host. Any other local account on that host can read
  the entire key ring and decrypt every user's refresh token.
- The systemd unit explicitly whitelists the key ring for write
  ([`deploy/lazerrender.service:25`](LazerRender.Service/deploy/lazerrender.service:25)):
  `ReadWritePaths=/opt/lazerrender/data /opt/lazerrender/keys`. That is correct for the app, but it
  also documents the directory as long-lived state that must be protected.

**Impact.** Full compromise of every stored osu! credential by any local user (or by anyone who
obtains a container-image layer or a backup that includes `keys/` without also protecting the
database). The refresh tokens are long-lived and act on the victim's behalf against osu!'s API.

**Recommendation.**

1. Protect the ring with a key that is not stored next to it: `ProtectKeysWithCertificate(...)`
   with the certificate supplied from the environment, or an external key store. At minimum,
   `ProtectKeysWithDpapi()` on Windows.
2. Tighten filesystem permissions as part of deployment:
   `chmod 700 /opt/lazerrender/keys` and `chmod 600` on the key files, owned by the service user.
   Add this to `DEPLOYMENT.md` and to the systemd `ExecStartPre` or the publish script.
3. Never bake `keys/` into a container image layer; mount it as a volume with restricted
   permissions (Phase 8.1 must state this explicitly).
4. Treat the key ring as a credential in the backup runbook (it already is listed in
   `DEPLOYMENT.md` §7 — add "and treat the backup as a secret").

**Verification.** `ls -la /opt/lazerrender/keys` on the target host shows `drwx------`; a second
local account cannot read the key files. A unit test can assert `ProtectKeysWith*` is configured
when a certificate is supplied.

---

### H-4 — osu! user access token and avatar API key cross the process boundary via `argv` and disk

**Severity:** High · **Category:** Sensitive data exposure / insecure credential channel · **Roadmap §7.1:** ✔

**Evidence.** [`RendererProcessRunner.BuildArgs`](LazerRender.Service/src/LazerRender.Api/Services/RendererProcessRunner.cs:212)
appends the live access token and the avatar API key to the child's argument list:

```csharp
if (!string.IsNullOrWhiteSpace(invocation.AvatarApiKey))
{
    yield return "--avatar-api-key";
    yield return invocation.AvatarApiKey;
}

if (!string.IsNullOrWhiteSpace(invocation.OsuUserToken))
{
    yield return "--osu-user-token";
    yield return invocation.OsuUserToken;
    yield return "--osu-user-token-expires-in";
    yield return invocation.OsuUserTokenExpiresIn.ToString(CultureInfo.InvariantCulture);
}
```

The token is minted from the queuing player's stored credential in
[`RenderWorker.RunJobAsync`](LazerRender.Service/src/LazerRender.Api/Services/RenderWorker.cs:254):

```csharp
OsuAccessToken? osuToken = await userOsuTokens.GetTokenAsync(job.OwnerUserId, jobCts.Token)
                          ?? await osuBotAuth.GetTokenAsync(jobCts.Token);
```

and the engine writes it into its own config
([`LazerRenderGame.cs:782`](LazerRender.Game/LazerRenderGame.cs:782)):

```csharp
LocalConfig.SetValue(OsuSetting.SavePassword, false);
LocalConfig.SetValue(OsuSetting.Token, token.ToString());
```

**Impact.** No command injection (`ArgumentList` is used, not a shell). But for the entire duration
of a render — up to `Renderer:ProcessTimeoutSeconds`, default **7200 s** — any local process or user
can read the token from `ps auxww` or `/proc/<pid>/cmdline`. The avatar API key is exposed the same
way on every job. The engine also writes the token into lazer's ini config under the shared Realm
directory; the intent (`SavePassword=false` → blank on consume) is a good-faith mitigation, but the
audit could not verify at rest that the value is always blanked before the process exits (a crash
or `SIGKILL` in the window between write and blank leaves it on disk).

Corroborating evidence that this is a known-risk area: Roadmap §8.2 explicitly requires the future
logging pipeline to redact credentials "because the osu! user token arrives via argv".

**Recommendation.**

1. Pass the token over a channel that is not world-readable: an anonymous pipe or a file descriptor
   inherited by the child (e.g. write it to the engine's stdin or to a `0600` file whose path is the
   only argument). This is the single highest-value change in this section.
2. If `argv` must remain, shorten the exposure: reduce the token lifetime handed to the engine to
   the render's actual need, and ensure the engine clears its config in a `finally`/signal handler
   on every exit path, including `SIGTERM`/`SIGKILL` (SIGKILL cannot be handled, so prefer the pipe).
3. Ensure `keys/`, `data/realm/*.ini` and `data/osu-bot-refresh-token` are `0600` and owned by the
   service user (overlaps with [H-3](#h-3--data-protection-key-ring-is-unencrypted-at-rest-and-world-readable-in-the-shipped-deployment)).
4. Redact `--osu-user-token`, `--avatar-api-key` and the engine's `osu! API login:` lines in the
   service log pipeline from the outset (Phase 8.2).

**Verification.** Start a render and confirm the token is **not** present in
`tr '\0' ' ' </proc/<engine-pid>/cmdline`; confirm no ini file under the Realm directory contains a
token after a normal and a cancelled render.

---

### H-5 — First user becomes admin on a fresh public deployment

**Severity:** High (config-dependent) · **Category:** Broken access control / privilege escalation · **Roadmap §7.1:** (new)

**Evidence.** Shipped defaults in
[`appsettings.json:29`](LazerRender.Service/src/LazerRender.Api/appsettings.json:29):

```json
"Admin": {
  "OsuUserIds": "11566111",
  "AllowFirstUser": true
}
```

and the gate in
[`AuthService.SignInWithOsuAsync`](LazerRender.Service/src/LazerRender.Api/Services/AuthService.cs:63):

```csharp
var isFirstUser = allowFirstUser && !await db.Users.AnyAsync(ct);
var isAdmin = adminIds.Contains(me.Id) || isFirstUser;
```

**Impact.** Two distinct problems:

1. **Bootstrap race.** On a fresh database, whichever osu! account completes OAuth first becomes a
   permanent admin (`IsAllowed = true`, `Role = "admin"`). `AllowFirstUser` cannot be turned off
   before the first sign-in, because there is no other way to create the first admin. On a public
   deployment that is exposed before the operator performs their own first login, an attacker (or a
   crawl of the login endpoint) can seize admin. Once an admin exists the flag is harmless, but
   nothing forces the operator to win the race.
2. **Personal data in the published tree.** The hard-coded `11566111` is a real osu! user id. It is
   not a credential, but it is a personal identifier that ships to every deployment and is exactly
   what Roadmap Phase 8.5's "published tree contains no personal leftovers" check targets. It also
   silently grants admin to that account on **every** instance built from this tree, regardless of
   who deploys it.

**Recommendation.**

1. Change the shipped default to `"OsuUserIds": ""` and document bootstrap via
   `Admin__OsuUserIds`. Rework `AllowFirstUser` into an explicit, one-shot bootstrap: e.g. require
   `Admin__BootstrapToken` to accompany the first login, or a CLI/`--bootstrap-admin` step, so
   first-come-first-served cannot win.
2. Log a prominent startup warning when `AllowFirstUser` is true **and** `db.Users` is empty, and
   instruct operators (in `DEPLOYMENT.md`) to complete the first login before exposing the service.
3. Remove the personal id from the committed tree to satisfy Phase 8.5.

**Verification.** From a fresh database, confirm that a login without the bootstrap secret does not
grant admin. Add a test asserting the first user is only promoted when the bootstrap condition is
satisfied. Add a repo check for hard-coded numeric osu! ids in `appsettings*.json`.

---

### M-1 — Preset `ConfigJson` is unbounded (up to the global 220 MB body limit)

**Severity:** Medium · **Category:** Resource exhaustion · **Roadmap §7.1:** (new)

**Evidence.** [`PresetsController.Save`](LazerRender.Service/src/LazerRender.Api/Controllers/PresetsController.cs:39)
caps `Name` at 64 chars but stores `ConfigJson` verbatim with no length or schema check; the column
is `IsRequired()` with no max length
([`AppDbContext.cs:97`](LazerRender.Service/src/LazerRender.Api/Data/AppDbContext.cs:97)). The only
ceiling is the process-wide body limit
([`Program.cs:20`](LazerRender.Service/src/LazerRender.Api/Program.cs:20), 220 MB).

**Impact.** An authenticated user can persist up to ~220 MB of arbitrary text per preset (with
overwrite, repeatedly) into the SQLite file, growing the database and backups without bound and
inflating every `GET /api/v1/presets` response (the DTO returns `ConfigJson` per preset). This is a
straightforward storage/bandwidth DoS by a low-privileged account.

**Recommendation.** Validate `ConfigJson` with `RenderConfigValidator` at save time (it is not
currently validated, only at job creation) and reject payloads above a small cap (e.g. 16 KB).
Optionally add a per-user preset count limit and a SQLite `CHECK`/max-length.

**Verification.** `POST /api/v1/presets` with a 10 MB `configJson` is rejected with 400; the
database size is unchanged.

---

### M-2 — Job creation can pin a request for 25 s and spawn background work unbounded per client

**Severity:** Medium · **Category:** Resource exhaustion · **Roadmap §7.1:** ✔

**Evidence.** [`JobsController.Create`](LazerRender.Service/src/LazerRender.Api/Controllers/JobsController.cs:141):

```csharp
var resolve = mapMetadata.ResolveAndUpdateAsync(job.Id, header.BeatmapMd5);
await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(25)));
```

`Task.WhenAny` does **not** cancel `resolve`: if it outlives the 25 s window it keeps running (and
keeps holding the shared render lock) while the worker has already returned 202. The resolution path
may perform an outbound beatmap download
([`MapMetadataService`](LazerRender.Service/src/LazerRender.Api/Services/MapMetadataService.cs:64),
`--map-info … --download-missing` with a 30 s HTTP timeout) while holding the render lock.

**Impact.** Each `POST /api/v1/jobs` can occupy a request thread for 25 s and leave behind orphaned
background work. Because [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting)
collapses the 120 req/min limiter into one global bucket, a single client can occupy a large share
of the service's concurrency and time budget, delaying every other user. Quota enforcement
(`Quota:MaxActiveJobs = 1`) happens before this hold, so it does not bound the in-flight requests.

**Recommendation.** Pass a linked `CancellationTokenSource` with a 25 s deadline into
`ResolveAndUpdateAsync` so the work is actually cancelled, and/or move metadata resolution fully
off the request path (enqueue and let the worker resolve it, as it already does for backfill).
Consider a dedicated limiter partition for `POST /api/v1/jobs` on top of `Quota`.

**Verification.** With a deliberately slow mirror, confirm the request returns promptly, the
background task is cancelled, and the render lock is released.

---

### M-3 — No security headers, and an inline event handler blocks a strict CSP

**Severity:** Medium · **Category:** Security misconfiguration / XSS defence-in-depth · **Roadmap §7.1:** ✔ (HSTS)

**Evidence.** Neither the app nor the shipped proxy sets any security header. The Caddy example is
only ([`deploy/Caddyfile`](LazerRender.Service/deploy/Caddyfile:3)):

```
render.example.com {
    reverse_proxy 127.0.0.1:5080
    encode gzip
}
```

So there is no `Content-Security-Policy`, `Strict-Transport-Security`, `X-Content-Type-Options`,
`X-Frame-Options`/`frame-ancestors`, or `Referrer-Policy` — for a cookie-authenticated admin UI.
`UseHttpsRedirection()` is present but [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting)
shows it is inert; `UseHsts()` is absent. Separately, the SPA injects an inline handler
([`app.js:70`](LazerRender.Service/src/LazerRender.Api/wwwroot/app.js:70)):

```js
`<a class="btn ghost" href="/auth/logout" onclick="event.preventDefault(); doLogout();">Log out</a>`
```

which would require `'unsafe-inline'` for `script-src` unless it is converted to an
`addEventListener` binding.

**Impact.** No defence-in-depth against XSS (see the escaping review in §6.1 — the SPA is clean
today, but the primary control is a single hand-written helper), no transport hardening for repeat
visits, no MIME-sniffing protection, and the admin UI can be framed (limited in practice because the
`SameSite=Lax` cookie is withheld from cross-site iframe subrequests, but `frame-ancestors` should
still be set).

**Recommendation.** Set `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
`Content-Security-Policy: default-src 'self'; img-src 'self' https://a.ppy.sh data:; frame-ancestors 'none'; base-uri 'none'`
(adjust `img-src` for avatars), and `Strict-Transport-Security` at the proxy (or via `UseHsts()` once
[H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting) is
fixed). Replace the inline `onclick` with an event listener so `script-src 'self'` is achievable.

**Verification.** A response-headers assertion test (`/` and `/api/v1/me`) covers the app-set
headers; a CSP report-only run in staging must show no violations during a full user flow.

---

### M-4 — osu! token-endpoint error body lands in an exception message and the log

**Severity:** Medium · **Category:** Sensitive information in logs / error handling · **Roadmap §7.1:** ✔

**Evidence.** [`OsuOAuthService.PostTokenAsync`](LazerRender.Service/src/LazerRender.Api/Services/OsuOAuthService.cs:131):

```csharp
if (!response.IsSuccessStatusCode)
    throw new InvalidOperationException(
        $"osu! token endpoint returned {(int)response.StatusCode}: {body}");
```

`AuthController.Callback`
([`AuthController.cs:59`](LazerRender.Service/src/LazerRender.Api/Controllers/AuthController.cs:59))
catches only `UserNotAllowedException`; anything else propagates, so the framework logs the
exception (including the full osu! response body) at Error before returning 500. The same
`InvalidOperationException` is caught and logged by
[`UserOsuTokenService`](LazerRender.Service/src/LazerRender.Api/Services/UserOsuTokenService.cs:113)
and [`OsuBotAuthService`](LazerRender.Service/src/LazerRender.Api/Services/OsuBotAuthService.cs:93).

**Impact.** osu! token errors normally describe `invalid_grant`/`invalid_client` and do **not** echo
the client secret or the submitted refresh token, so this is medium rather than high. But the body
is unbounded and attacker-influenceable only in the sense that a user can trigger many failing
exchanges; the real risk is that a future osu! error format that echoes request parameters would put
the client secret or a refresh token straight into the service log. It also produces noisy 500s on a
trivially reachable unauthenticated GET endpoint.

**Recommendation.** Do not embed the body in the exception; log a bounded, redacted excerpt
(status code + `error` field) instead, and catch the generic failure in `Callback` to return a
`400`/redirect rather than a 500.

**Verification.** Force a 400 from the token endpoint and confirm the log contains the status class
and error code but not the raw body or any credential.

---

### M-5 — No antiforgery defence; the cookie is the only CSRF control and it is not `Secure`

**Severity:** Medium · **Category:** CSRF · **Roadmap §7.1:** ✔

**Evidence.** No `AddAntiforgery` / `[ValidateAntiForgeryToken]` / `[AutoValidateAntiforgeryToken]`
anywhere. The only control is the auth cookie's `SameSite = SameSiteMode.Lax`
([`Program.cs:79`](LazerRender.Service/src/LazerRender.Api/Program.cs:79)). The OAuth `state` cookie
is also `Lax` ([`AuthController.cs:34`](LazerRender.Service/src/LazerRender.Api/Controllers/AuthController.cs:34)).

**Impact.** `Lax` withholds the cookie from cross-site POSTs, so the multipart `POST /api/v1/jobs`,
JSON `POST /api/v1/presets`, `DELETE` endpoints and `POST /auth/logout` are not reachable with the
victim's session from a third-party page — the practical CSRF surface is small. Two residual gaps
remain: (a) `Lax` sends cookies on cross-site **top-level GET navigations**, and the two GET
endpoints that touch session state are `/auth/login` and `/auth/callback`; the `state` cookie
prevents a bare login-CSRF, but that defence is weakened because the state cookie is not `Secure`
([H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting));
(b) if a browser ever treats the site as same-site via a sibling subdomain, `Lax` degrades to a
weaker guarantee, and there is no second layer.

**Recommendation.** Add antiforgery tokens (or require a custom header on state-changing endpoints
and enforce it) rather than relying solely on `SameSite`. Fix the cookie flags per
[H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting).
Optionally switch the session cookie to `SameSite=Strict`.

**Verification.** Automated test: a cross-site form POST to `/api/v1/jobs` without the token is
rejected with 400/403.

---

### M-6 — Skin deletion has no ownership check and never purges engine storage

**Severity:** Medium · **Category:** Broken access control / data integrity · **Roadmap §7.1:** ✔

**Evidence.** [`AssetsController.DeleteSkin`](LazerRender.Service/src/LazerRender.Api/Controllers/AssetsController.cs:95):

```csharp
var skin = await db.Skins.SingleOrDefaultAsync(s => s.Id == id, ct);
if (skin is null) return NotFound(...);
db.Skins.Remove(skin);
```

No `UploadedBy` comparison. Skins are a shared library by design (the briefing says so), and
`ListSkins` is likewise global. Note however that the row is removed from the service database while
the actual skin remains imported in the engine's Realm store — `DeleteSkin` never calls the importer,
unlike `AdminController.Purge`, which does
([`AdminController.cs:45`](LazerRender.Service/src/LazerRender.Api/Controllers/AdminController.cs:45)).
The engine's skin selection still resolves the deleted name.

**Impact.** Any allowed user (not just admins) can remove any skin from the shared list — a
denial-of-service on other users' renders and a policy question the briefing flags. The
inconsistent purge leaves orphaned assets in the Realm store.

**Recommendation.** Decide the intended model explicitly:
- If skins are shared, either restrict deletion to admins (`[Authorize(Roles = "admin")]`) or keep
  any-user deletion but document it, and
- make deletion consistent by removing the asset from engine storage too (or leaving the row and
  marking it hidden). At minimum, stop the silent divergence between the two stores.

**Verification.** Product decision recorded; if deletion becomes admin-only, a non-admin DELETE
returns 403.

---

### M-7 — Engine stdout/stderr is forwarded verbatim into the service log

**Severity:** Medium · **Category:** Logging / information exposure · **Roadmap §7.1:** ✔

**Evidence.** [`RendererProcessRunner.logEngineLine`](LazerRender.Service/src/LazerRender.Api/Services/RendererProcessRunner.cs:163)
classifies every engine line and writes it to the service log; lines matching
`error`/`exception`/`failed`/`failure`/`unhandled`/`fatal`/`crash`/`denied` are raised to **Warning**.
The engine, in turn, logs response bodies on failure
([`LazerRenderGame.cs:715`](LazerRender.Game/LazerRenderGame.cs:715),
`Logger.Log($@"Avatar: response body: {truncate(body)}")`). Separately,
[`AssetImportRunner.RunAsync`](LazerRender.Service/src/LazerRender.Api/Services/AssetImportRunner.cs:49)
logs the **entire** stdout and stderr of an import at Information:

```csharp
logger.LogInformation(
    "Asset operation exited with code {ExitCode}. stdout: {Stdout} stderr: {Stderr}",
    exitCode, stdout, stderr);
```

**Impact.** Engine output is derived from user-supplied inputs (replay usernames, beatmap metadata,
uploaded archives) and is unbounded in volume. Anyone who can read the service log sees
attacker-influenced text; the `osu! API login:` marker means token-lifecycle lines are surfaced by
default, and an adversarial or merely broken upload can emit enough output to fill the log. There is
no redaction, and the log is the intended Phase 8 data source for the admin panel — the briefing
explicitly warns that any pipeline feeding a browser must redact first.

**Recommendation.** Bound the number of bytes logged per line and per job; redact
`--osu-user-token`/`--avatar-api-key` and anything matching a bearer-token shape; do not log import
stdout/stderr verbatim (log an excerpt on failure only). Carry the redaction into the Phase 8.2
pipeline as a hard requirement.

**Verification.** A test feeds the runner synthetic engine output containing a token-shaped string
and asserts the logged record is redacted and length-capped.

---

### M-8 — Untrusted package parsing runs with GPU privileges and no sandbox

**Severity:** Medium · **Category:** Trust boundary / unsafe deserialisation (third-party parsers) · **Roadmap §7.1:** (new)

**Evidence.** Uploaded skins (`.osk`) and beatmaps (`.osz`/`.osu`) go straight to the engine importer
([`AssetsController.cs:61`](LazerRender.Service/src/LazerRender.Api/Controllers/AssetsController.cs:61),
[`AssetsController.cs:130`](LazerRender.Service/src/LazerRender.Api/Controllers/AssetsController.cs:130)),
and a missing beatmap is downloaded from public mirrors and imported
([`LazerRenderGame.cs:452`](LazerRender.Game/LazerRenderGame.cs:452)). The engine then renders the
beatmap **including its storyboard, video and skin**. All of this executes in the same process as the
GPU/EGL context, with the service user's full filesystem reach inside `data/`.

**Impact.** A malicious beatmap package is parsed by SharpCompress, HtmlAgilityPack, TagLibSharp and
the lazer beatmap/storyboard decoders in a process that is not sandboxed beyond the `systemd`
unit-level restrictions. A parser memory-safety or decompression-bomb bug is a plausible path to
crashing or compromising the render worker (which holds every render's token in the same process —
[H-4](#h-4--osu-user-access-token-and-avatar-api-key-cross-the-process-boundary-via-argv-and-disk)).
This is inherent to a renderer and cannot be fully eliminated, but the exposure is currently
unbounded: beatmap uploads have no per-file size cap other than the global 220 MB body limit, no
decompression-ratio guard, and no restriction on which mirrors may be contacted.

**Recommendation.**
1. Give beatmap/skin imports their own explicit size cap and (where the engine allows) a
   decompression-ratio/entry-count guard. ZIP-bomb resistance in `SharpCompress` should be verified.
2. Isolate the engine further: run the child under a dedicated low-privilege user, with a private
   Realm directory, and consider `systemd-run`/bubblewrap-style confinement for imports and renders.
3. Keep the credential exposure of the engine process as small as possible (fix
   [H-4](#h-4--osu-user-access-token-and-avatar-api-key-cross-the-process-boundary-via-argv-and-disk)),
   so a parser compromise does not immediately yield refresh tokens.
4. Add `Renderer:DownloadMissing` to the documented security posture: on a locked-down instance it
   can be set to `false`, removing the mirror egress entirely.

**Verification.** Upload a known zip-bomb/oversized `.osz`; confirm rejection with a bounded error
and no worker crash.

---

### M-9 — `AllowedHosts: "*"` with no host filtering

**Severity:** Medium · **Category:** Security misconfiguration · **Roadmap §7.1:** (new)

**Evidence.** [`appsettings.json:52`](LazerRender.Service/src/LazerRender.Api/appsettings.json:52):
`"AllowedHosts": "*"`.

**Impact.** The application accepts any `Host` header. The endpoints are relative-only, so there is
no immediate password-reset-poisoning or cache-poisoning primitive; combined with the service
listening on loopback this is mostly relevant for DNS-rebinding and for any future absolute-URL
generation. It is still a needless weakening of the host boundary.

**Recommendation.** Set `AllowedHosts` to the real deployment hostname (and document that it must be
overridden per deployment), and/or enforce the host at the proxy.

**Verification.** A request with `Host: evil.example` is rejected with 400.

---

### M-10 — Quota caps are check-then-act races

**Severity:** Medium · **Category:** Resource exhaustion · **Roadmap §7.1:** (new)

**Evidence.** [`QuotaService.ValidateAsync`](LazerRender.Service/src/LazerRender.Api/Services/QuotaService.cs:28)
counts active/daily jobs, and the caller then inserts the new job later
([`JobsController.Create`](LazerRender.Service/src/LazerRender.Api/Controllers/JobsController.cs:94)
validates at line 94, saves at line 133). `DisplayNumber` is likewise `MAX+1`
([`JobsController.cs:100`](LazerRender.Service/src/LazerRender.Api/Controllers/JobsController.cs:100)).

**Impact.** Concurrent requests from the same user can exceed `MaxActiveJobs` / `MaxJobsPerDay`;
duplicate `DisplayNumber` values are possible. Low-per-request but trivially parallelisable, and it
undercuts the primary per-user abuse control. The service is documented as single-instance, so a
transaction with a uniqueness constraint is sufficient.

**Recommendation.** Perform the count and the insert in one transaction with a unique constraint (or
rely on the worker's atomic claim as the real gate), and treat the quota check as advisory. Replace
`MAX+1` with a sequence/unique index.

**Verification.** Fire N concurrent `POST /api/v1/jobs` with `MaxActiveJobs = 1`; assert only one
queued job survives.

---

### M-11 — Session cookie has no absolute lifetime

**Severity:** Medium · **Category:** Session management · **Roadmap §7.1:** (new)

**Evidence.** [`Program.cs:81`](LazerRender.Service/src/LazerRender.Api/Program.cs:81):
`ExpireTimeSpan = TimeSpan.FromDays(14); SlidingExpiration = true;` — with no absolute cap.

**Impact.** An actively-used session (or a stolen cookie kept warm by the attacker) never expires.
Combined with [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting)
(cookie not `Secure`), the window in which a leaked cookie is useful is unbounded. Logout does
revoke the stored refresh token, but the auth cookie itself is only cleared client-side.

**Recommendation.** Add an absolute session lifetime (e.g. `ExpireTimeSpan = 7 days`, no sliding, or
an explicit "issued at" claim checked in the cookie events). Consider binding the session to a
server-side record so server-side revocation is possible.

**Verification.** A test advances the clock past the absolute limit and asserts `401`.

---

### Low-severity hardening items

<a id="l-1--engine-side-osr-header-parser-has-an-unbounded-allocation-defense-in-depth"></a>

- **L-1 — Engine-side `.osr` header parser has an unbounded allocation (defense-in-depth).**
  [`LazerRenderGame.readBeatmapHash`](LazerRender.Game/LazerRenderGame.cs:534) does
  `int length = readVarint(stream); byte[] hash = new byte[length];` with no upper bound. The
  service's own [`ReplayFileParser`](LazerRender.Service/src/LazerRender.Api/Services/ReplayFileParser.cs:47)
  rejects strings longer than 256 first and validates the hash as 32 hex, so the same bytes cannot
  reach the engine's unguarded path today — but the two parsers are independent duplicated logic and
  the engine is also a standalone CLI. Add the same 256 bound in the engine.

<a id="l-2--job-id-is-interpolated-into-css-selectors-without-cssescape"></a>

- **L-2 — `job.id` interpolated into CSS selectors without `CSS.escape`.**
  [`app.js:624`](LazerRender.Service/src/LazerRender.Api/wwwroot/app.js:624) and
  [`app.js:614`](LazerRender.Service/src/LazerRender.Api/wwwroot/app.js:614) build selectors with a
  raw `job.id`, while [`app.js:738`](LazerRender.Service/src/LazerRender.Api/wwwroot/app.js:738)
  correctly uses `CSS.escape`. IDs are server GUIDs, so this is not exploitable; make the usage
  consistent as defence-in-depth.

<a id="l-3--fetch-user-tokensh-prints-the-full-token-response-to-stdout"></a>

- **L-3 — `fetch-user-token.sh` prints the full token response to stdout.**
  [`fetch-user-token.sh:45`](LazerRender.Game/scripts/fetch-user-token.sh:45) emits the whole
  response (access **and** refresh token) plus the refresh token again. It is an operator script and
  writes nothing to disk, but the output lands in the terminal and shell history. Print only the
  refresh token, with a warning to treat it as a secret.

<a id="l-4--runner-and-setsid-resolution-depend-on-path-and-parent-directories"></a>

- **L-4 — Runner and `setsid` resolution depend on `PATH` and parent directories.**
  `ResolveSetsid()` falls back to the bare name `setsid` on `PATH`
  ([`RendererProcessRunner.cs:268`](LazerRender.Service/src/LazerRender.Api/Services/RendererProcessRunner.cs:268)),
  and `ResolveRunnerScript()` walks up to the filesystem root looking for `run-headless.sh`
  ([`RendererProcessRunner.cs:176`](LazerRender.Service/src/LazerRender.Api/Services/RendererProcessRunner.cs:176)).
  Failing to find an absolute path is a graceful error, but a writable ancestor directory or a
  poisoned `PATH` would let an attacker substitute both the script and the execution wrapper.
  Require absolute, configured paths in production.

<a id="l-5--swagger-package-is-present-in-the-release-build"></a>

- **L-5 — Swagger package is present in the release build.** It is gated to `IsDevelopment()`
  ([`Program.cs:155`](LazerRender.Service/src/LazerRender.Api/Program.cs:155)) and the briefing
  asks that its presence not be flagged; noted only so the Release bundle is understood to carry
  the dependency even though the UI is never mapped. Consider `PrivateAssets`/conditional reference.

<a id="l-6--shared-read-only-listings-expose-the-whole-library"></a>

- **L-6 — Shared read-only listings expose the whole library.** `ListSkins`, `ListPresets`
  (owner-scoped) and `GetBeatmapCache` return global/shared state to any authenticated user. This is
  the documented shared-library design, but be aware that skin **names** (client-controlled file
  names) are visible to every user.

<a id="l-7--auth-callback-has-no-dedicated-rate-limit"></a>

- **L-7 — `/auth/callback` has no dedicated rate limit.** Failed `code` exchanges produce unhandled
  500s ([M-4](#m-4--oauth-token-endpoint-error-body-lands-in-an-exception-message-and-the-log)) and
  each attempt contacts osu!. Add a stricter partition for `/auth/*`.

<a id="l-8--the-oauth-default-redirect-uri-is-a-localhost-http-url"></a>

- **L-8 — The OAuth default `RedirectUri` is a localhost HTTP URL.**
  `appsettings.json:40` ships `http://localhost:5080/auth/callback`. Harmless if always overridden
  in production, but it is a misconfiguration waiting to happen and the same origin is HTTP.
  Ship an empty default and fail loudly at startup when OAuth is enabled without a configured
  redirect URI.

<a id="l-9--admin-endpoints-echo-internal-exception-messages"></a>

- **L-9 — Admin endpoints echo internal exception messages.**
  [`AdminController.AllowUser`](LazerRender.Service/src/LazerRender.Api/Controllers/AdminController.cs:106)
  returns `e.Message` to the client on a failed username lookup. Admin-only and low impact; return a
  generic message and log the detail.

<a id="l-10--informational-project-documentation-nits"></a>

- **L-10 — Project/documentation nits.**
  The systemd unit points `Documentation=` at `https://github.com/ppy/osu`
  ([`lazerrender.service:3`](LazerRender.Service/deploy/lazerrender.service:3)), which is the
  submodule, not this project. The engine comment in `downloadBeatmapAsync` still mentions Nerinyan
  while the code uses `osu.direct` → `catboy.best`. Cosmetic.

<a id="l-11--no-central-package-management-and-a-silenced-transitive-advisory"></a>

- **L-11 — No central package management and a silenced transitive advisory.**
  There is no `Directory.Packages.props`, `nuget.config`, or `packages.lock.json`, so transitive
  versions can drift within the ranges the top-level packages allow
  ([`LazerRender.Api.csproj`](LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj:13)).
  The pinned `ppy/osu` submodule silences `NU1903` (high-severity advisory) for `AutoMapper 13.0.1`
  with the comment "does not affect us" — that claim belongs to upstream and was **not** verified
  here. Enable a lock file and, at minimum, record the transitive dependency inventory so the
  silenced advisory can be re-assessed on each submodule bump. `ManagedBass`, `ImageSharp`,
  `Silk.NET` and `Veldrid` arrive transitively through `ppy.osu.Framework` with no explicit pins in
  this repo and are therefore not auditable from our csproj files.

---

## 5. Dependency posture

| Item | Observed at HEAD | Assessment |
|---|---|---|
| Service packages | EF Core Sqlite/Design `8.0.11`, Swashbuckle `6.6.2` | Pinned, current for 8.x; Swashbuckle dev-only |
| Central pinning | none (`Directory.Packages.props`, `nuget.config`, `packages.lock.json` all absent) | **Gap** — no lock file, transitive drift possible ([L-11](#l-11--no-central-package-management-and-a-silenced-transitive-advisory)) |
| Engine dependencies | five `ProjectReference`s into `extern/osu`; no `PackageReference` of its own | Expected |
| osu! submodule | `https://github.com/ppy/osu.git` @ `e9451fe70b91292c482bab203ea87bd727eaa237` = `2026.821.0-tachyon` | Verified pinned by gitlink; out of audit scope |
| `AutoMapper 13.0.1` | `NU1903` silenced in the submodule csproj | **Unverified upstream claim** — re-assess on bump |
| FFmpeg | on `PATH`; args built as a list everywhere except `FrameSink` (single interpolated string, `UseShellExecute = false`, paths engine-generated) | Acceptable; `FrameSink` is the one place a future user-controlled value would be dangerous |
| `setsid` / `weston` / `mkfifo` | resolved at runtime | Hardening in [L-4](#l-4--runner-and-setsid-resolution-depend-on-path-and-parent-directories) |
| `Sentry 6.6.0` | arrives transitively; no Sentry initialisation found in `LazerRender.Game/*.cs` | Should be re-checked when the engine's logging is reworked in Phase 8.2 |

---

## 6. Positive observations (do not "fix" these)

1. **No shell anywhere in our code.** Every service-side spawn uses `ProcessStartInfo.ArgumentList`
   with `UseShellExecute = false`; the only concatenated command line is `FrameSink`'s FFmpeg
   invocation, whose interpolated values are engine-generated GUID/temp paths.
2. **Parameterised SQL.** All EF Core queries are parameterised; the only `ExecuteSqlRaw` calls are
   `DatabaseInitializer`'s `ALTER TABLE`/`CREATE TABLE` built from the compile-time `ColumnPatches`
   table under `#pragma warning disable EF1002`.
3. **GUID-scoped filesystem.** `StorageService` composes every path from a server-generated
   `Guid.NewGuid().ToString("N")`; uploads use a GUID prefix with only the extension taken from the
   client. No traversal surface in the helpers.
4. **Bounds-checked untrusted parsing.** The service's `ReplayFileParser` caps string length at 256
   and rejects oversized varints; `IsMd5Hash` enforces 32 hex characters before the value becomes a
   process argument or a URL path segment.
5. **Config validation at the boundary.** `RenderConfigValidator` rejects unknown enum values, the
   full HUD vocabulary, and every numeric range, on every job creation.
6. **SPA escaping is consistent.** `esc()` is applied at every `innerHTML`/`insertAdjacentHTML`
   interpolation of a user-, owner- or engine-derived string (usernames, map title/artist/creator/
   version, skin names, error messages, preset names, admin user fields, avatar URL, phase label).
   Numeric fields are formatted numerically. **No exploitable stored/reflected XSS was found**
   (see [L-2](#l-2--job-id-is-interpolated-into-css-selectors-without-cssescape) for the one
   consistency nit).
7. **No secrets in the tree.** `git ls-files` shows no key ring, `.env`, token file or credential;
   both `.gitignore` files exclude `keys/`, `data/`, `uploads/`, `jobs/`, `results/`, `storage/`,
   `publish/` and `*.env`. `appsettings.json` ships every credential field empty.
8. **Server-side authorization.** `[Authorize]` per controller, `[Authorize(Roles = "admin")]` on
   `AdminController`, and owner filtering on every job read/download. The admin **role is read from
   the database**, not the cookie, on the quota path.
9. **systemd sandboxing already present.** `NoNewPrivileges`, `PrivateTmp`, `ProtectSystem=strict`,
   `ProtectHome`, a dedicated unprivileged user and a narrow `ReadWritePaths` set.
10. **Rate limiting exists.** A global fixed-window limiter is in place; it needs
    [H-2](#h-2--no-forwarded-headers-handling-breaks-secure-cookies-https-redirect-and-rate-limiting)
    to become effective, but the mechanism and the 429 status are correct.

---

## 7. Phase 7.1 hardening backlog (audit output)

Ordered by risk. Each item is stated so it can be ticked off with a concrete verification.

### P0 — before any public exposure

- [ ] **H-1** Verify `OwnerUserId` in `JobsHub.Subscribe` (or move to per-user groups). *Verify:* cross-user subscribe receives no `progress` message.
- [ ] **H-2** Add `ForwardedHeaders` with explicit `KnownProxies`/`KnownNetworks`; set both cookies to always-`Secure`; re-key the rate limiter on the forwarded address. *Verify:* `Set-Cookie` includes `Secure` behind the proxy; per-client partitioning works.
- [ ] **H-3** Protect the key ring with a certificate/external store; `chmod 700 keys/`, `chmod 600` key files in the deployment runbook. *Verify:* a second local account cannot read the key files.
- [ ] **H-4** Move the osu! access token and avatar key off `argv` onto an inheritable pipe/`0600` file; ensure the engine clears its ini on every exit path; redact in logs. *Verify:* the token is not visible in `/proc/<pid>/cmdline` during a render, and no ini under the Realm directory contains it afterwards.
- [ ] **H-5** Ship `Admin:OsuUserIds` empty; replace first-user-admin with an explicit bootstrap; log a prominent warning while `AllowFirstUser` is true and no users exist. *Verify:* a fresh-DB login without the bootstrap secret is not promoted admin.

### P1 — hardening

- [ ] **M-1** Validate and size-cap preset `ConfigJson`.
- [ ] **M-2** Make the 25 s metadata wait actually cancellable / move it off the request path; add a `POST /jobs` limiter partition.
- [ ] **M-3** Add CSP / `nosniff` / `Referrer-Policy` / `frame-ancestors`, add HSTS at the proxy, remove the inline `onclick`.
- [ ] **M-4** Remove the osu! error body from the exception; catch generic failures in `Callback`.
- [ ] **M-5** Add antiforgery tokens or a required custom header on state-changing endpoints.
- [ ] **M-6** Decide the skin-deletion model; make database and engine storage consistent.
- [ ] **M-7** Bound and redact engine/import log lines; make redaction a hard requirement for the Phase 8.2 pipeline.
- [ ] **M-8** Add a per-file cap and decompression guard for beatmap/skin imports; document the option to disable mirror downloads; isolate the engine user.
- [ ] **M-9** Set `AllowedHosts` to the deployment hostname.
- [ ] **M-10** Make the quota check and job insert atomic; replace `MAX+1` display numbers.
- [ ] **M-11** Add an absolute session lifetime.

### P2 — low / hygiene

- [ ] **L-1 … L-11** as listed in §4.
- [ ] Enable central package management and a `packages.lock.json`; record the transitive inventory (BASS/ImageSharp/Silk.NET/Veldrid) and re-assess the silenced `AutoMapper` `NU1903`.
- [ ] Phase 8.5 check: no hard-coded personal identifiers (e.g. osu! user id `11566111`) in the published tree.
- [ ] Phase 8.1: confirm the container does not bake `keys/` into an image layer, passes GPU device nodes with least privilege, and preserves the process-group cancellation semantics when the service is PID 1.

Each item should be recorded with its verification evidence (a test, a reproduction, or a written
justification for accepting the risk), per Roadmap §7.1's "confirm each finding" requirement.

---

## 8. Accepted risks and non-findings

These were considered and are **not** recommended for change as part of this audit.

| Item | Rationale |
|---|---|
| Engine CLI has no authentication (`LazerRender.Game/Program.cs`) | Inherent to a local tool; reachable only through the local child-process boundary or by an operator with shell access. |
| `--download-missing` fetches from public mirrors | Documented feature; the only client-controlled input is a 32-hex MD5 validated before use. No SSRF. |
| No CORS policy | The SPA is same-origin; the ASP.NET default (no CORS headers) is the safe choice. |
| Swagger present in the source | Mapped only under `IsDevelopment()`. |
| `dev/` directory | Gitignored, historical, not shipped. |
| `extern/osu` submodule internals | Upstream third-party code; only our interaction with it is in scope. |
| `DatabaseInitializer` `EnsureCreated` + `ALTER TABLE` stopgap | No security impact; noted as a schema-divergence risk. |
| Renders serialised by one semaphore; single-instance assumption | Explicit product constraint; multi-host coordination is out of scope. |
| SQLite as a local single file | No network database surface. |
| `LAZERRENDER_FLATFILL` | Development-only capture diagnostic, gated by env var. |
| `QuotaService` reading the role from the database | Deliberate, and the right choice. |
| `--osu-user-token` uses `ArgumentList`, not a shell string | No command-injection risk (the confidentiality issue is separate: [H-4](#h-4--osu-user-access-token-and-avatar-api-key-cross-the-process-boundary-via-argv-and-disk)). |
| `ResolveRunnerScript` / `MapMetadataService` passing a validated MD5 | No injection vector. |

---

## Appendix A — Endpoint authorization map (verified at HEAD)

| Endpoint(s) | Controller | Attribute |
|---|---|---|
| `GET /auth/login`, `/auth/callback`, `/auth/denied`, `POST /auth/logout` | `AuthController` | *(none — intentional)* |
| `GET /api/v1/me` | `MeController` | `[Authorize]` |
| `POST\|GET /api/v1/jobs`, `GET\|DELETE /api/v1/jobs/{id}`, `GET /api/v1/jobs/{id}/result` | `JobsController` | `[Authorize]` + owner filter |
| `POST\|GET /api/v1/skins`, `DELETE /api/v1/skins/{id}`, `POST /api/v1/beatmaps`, `GET /api/v1/beatmaps/cache/{md5}` | `AssetsController` | `[Authorize]` (skin delete: **no ownership check**, [M-6](#m-6--skin-deletion-has-no-ownership-check-and-never-purges-engine-storage)) |
| `GET /api/v1/capabilities`, `/api/v1/render-config/defaults` | `MetaController` | `[Authorize]` |
| `GET\|POST /api/v1/presets`, `DELETE /api/v1/presets/{id}` | `PresetsController` | `[Authorize]` + owner filter |
| `POST /api/v1/admin/purge`, `GET /api/v1/admin/queue`, `/api/v1/admin/users`, `POST /api/v1/admin/users/allow`, `/api/v1/admin/users/revoke` | `AdminController` | `[Authorize(Roles = "admin")]` |
| `/hubs/jobs` (`Subscribe`, `Unsubscribe`) | `JobsHub` | `[Authorize]` — **no ownership check**, [H-1](#h-1--signalr-jobs-hub-group-subscription-has-no-ownership-check) |
| `/health` | `Program.cs` | *(none — health probe)* |

## Appendix B — Verification commands used

```bash
# Baseline equivalence of HEAD vs the briefing's commit
git diff --stat 28072c1 3f45d66

# No secrets / key material / runtime data tracked
git ls-files | grep -iE '\.env|secret|credential|password|keys/|\.key$|\.pfx$|\.pem$'
git check-ignore -v .env

# Hardening primitives absent from the service
grep -rInE 'AddAntiforgery|ValidateAntiForgeryToken|AddCors|UseCors|UseForwardedHeaders|UseHsts|X-Forwarded' LazerRender.Service

# Submodule pin
git ls-tree HEAD LazerRender.Game/extern/osu
git -C LazerRender.Game/extern/osu describe --tags --always
```

---

*End of report. This audit was a static source review of `3f45d66`; no live penetration testing,
fuzzing, dependency-scanner run, or runtime exploitation was performed. Findings marked
"config-dependent" are correct for the shipped defaults and deployment examples, and must be
re-verified against the actual production configuration and the Phase 8.1 container topology.*
