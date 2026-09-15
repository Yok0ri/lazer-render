# LazerRender Project Roadmap (Server Architecture)

This document outlines the phased development plan for building a headless, faster-than-realtime replay recorder using the native `osu!lazer` engine, designed to run on a home server as the backend for a web service (similar to o!rdr).

## ✅ Phase 0 — Architecture Spike (Completed)
- [x] Custom desktop host (`ExecutionMode.SingleThread`) with a fixed window size.
- [x] `ReplayPlayer` subclass running a local `.osr` score.
- [x] `ManualClock` loop uncoupled from wall-clock vsync.
- [x] Prove faster-than-realtime simulation and deterministic scoring.

## ✅ Phase 1 — Video & Audio Pipeline (Completed)
- [x] GPU readback via persistent FBO (Frame Buffer Object).
- [x] FFmpeg dual-pipe architecture (`pipe:0` for video, `mkfifo` for audio).
- [x] Offline beatmap track decoding (BASS) with tempo support (DT/HT rate mods).
- [x] Hitsound interception via BASS mixer reflection into the audio pipe.

## ✅ Phase 2 — Headless Asset Management (Completed)
- [x] Maintain a persistent, isolated Realm database on the server (no storage wipe between runs).
- [x] CLI command to natively import beatmaps (`--import-map <path.osz>`).
- [x] CLI command to natively import skins (`--import-skin <path.osk>`).
- [x] Resolve maps automatically by reading the beatmap MD5 hash from the provided `.osr`.
- [x] Apply specific skins via CLI (`--skin "Skin Name"`), with a fallback to the default/beatmap skin.

## ✅ Phase 3 — Rendering Polish & Visual Toggles (Completed)
- [x] Native output on Wayland — `DrawSizePreservingFillContainer` + FBO scale decouples the render from the physical window size.
- [x] Dynamic FFmpeg resolution sizing — FFmpeg is started from the actual captured frame dimensions (no raw stride corruption).
- [x] Fractional-sample audio accumulator — 44100Hz stays aligned at 60/75/90/120fps (no buzz sounds).
- [x] Safe frame extraction — `ImageSharp.CopyPixelDataTo` flattening + strict FBO size guard (no segfault/corruption).
- [x] Headless-friendly desktop window — hidden/minimised (Wayland may keep it visible; rendering unaffected).
- [x] Clean transitions into the native `osu!lazer` Results Screen at the end of the replay.
- [x] CLI toggles for visual elements (Disable Storyboard, Disable Video, Hide Chat/Overlay).
- [x] Frame Blending / Motion Blur (oversampling the clock to simulate high-shutter-angle motion blur).

## ✅ Phase 4 — Hardware Acceleration & Optimization (Completed)
- [x] Rebased the pinned `ppy/osu` checkout onto the Tachyon pre-release stream
      (`2026.821.0-tachyon`) to adopt the reworked extended results-screen layout (#38643: AHE /
      Unstable Rate merged into the Timing Distribution card, Timing Distribution + Accuracy
      Heatmap on one row, updated card border styling). Regression battery re-verified
      (1080p60 / 1080p90 / 1440p soak) with no performance regression.
- [x] Integrate hardware encoder arguments into the FFmpeg pipe (NVENC for NVIDIA, QSV for Intel, VAAPI/AMF for AMD).
- [x] Target resolution & framerate scaling up to 4K @ 240fps (tested and profiled).
- [x] Implement a cleanup mechanism (purging unneeded beatmaps/skins from the database to save server disk space).
- [x] Resolution-independent HUD scaling — auto `render_height / 768` with a `--hud-scale` override.
- [x] Pre-GUI backend hooks — JSON progress lines, SIGINT/SIGTERM cancellation, temp `.osz` cleanup.

## ✅ Phase 5 — The Web API Daemon (External)
- [x] ASP.NET Core web server wrapping `LazerRender` (self-contained publish, systemd unit, TLS proxy).
- [x] Endpoints to accept `.osr` uploads, validate render config, and create render jobs.
- [x] Automatic beatmap downloading if the hash isn't in the local database (`--download-missing`,
      osu.direct → catboy.best fallback; implemented during Phase 4).
- [x] Job queue — serialized single-worker queue with atomic SQLite claim (no Redis/RabbitMQ needed
      for MVP), stdout JSON progress + SignalR, cancellation, retries, and crash recovery.
- [x] osu! OAuth v2 login (allowlist-gated) and web UI (render form, skins, jobs, admin).

## ✅ Phase 6 — Render & Web UX Refinements
- [x] Render tab: removed the "View all" link; the Recent renders card is a pure summary.
- [x] Job titles unified to `<player> | <map>` on both the Render and Jobs tabs, and the beatmap
      metadata is now resolved (downloading the map if needed) *before* the job becomes claimable, so
      the real title appears the moment a render is queued instead of after it finishes.
- [x] `Hit lighting` gameplay toggle (`OsuSetting.HitLighting`, default off).
- [x] Encoder auto-detection readout moved from the Render tab to a new admin "Render PC" card.
- [x] Jobs extended view: added song length, star rating, mods and accuracy (parsed from the engine's
      extended `--replay-info` output).
- [x] Jobs extended view: created/started/finished collapsed into a single stacked time island with
      the viewer's time zone, leaving the stat tiles to fill the remaining width.
- [x] Added the `Aim error meter` HUD element (`aim-error`), split out from the hit error meter.
- [x] Added a `Cosmetic elements` HUD group (`cosmetic`) covering Argon wedge pieces, boxes, text
      elements, beatmap attribute text and generic sprites.
- [x] HUD element checklist split into three gap-separated groups (gameplay HUD / secondary HUD /
      cosmetic) with a master select/deselect-all toggle.
- [x] Jobs tab: only one job can be expanded at a time, and the expanded view collapses when leaving
      the tab.
- [x] `Disable result screen` option: fades the render (video and audio) to black over ~1 second and
      stops instead of transitioning to the results screen.
- [x] Fixed the results-screen fade-out, which had stopped happening entirely: the recorded tail now
      holds the results screen readable and then fades video *and* audio to black over its final
      1.5 s (3.5 s → 5.0 s of the 5 s tail). Both fades share one 0→1 smoothstep ramp instead of a
      framework `Easing.Out` transform, which is what made the disabled-results fade feel abrupt.
- [x] HUD element checklist: widened the gap between the three groups, which was too small to read
      as a deliberate break, and removed the encoder from the Jobs extended view (it belongs only to
      the admin "Render PC" card).
- [x] Downloads are named `lazerrender-video.mp4` instead of exposing the internal render number.
- [x] CLI/config cleanup: the granular `--hide-*` flags and the separate `--hud-only` whitelist were
      merged into a single `--hud <keys...>` flag (JSON `hud`); the docs, `--help` and the SPA were
      updated to match.
- [x] Avatars now actually render. The results-screen avatar and the `avatar` HUD element were
      always falling back to the guest icon because the replay's real osu! user id was never
      propagated to the score's `APIUser` (lazer's `DrawableAvatar` requires `OnlineID > 1` before it
      looks at any avatar URL). The id is now taken from the replay (falling back to the public
      `GET /api/v2/users/{user}` lookup), the country code is filled in so `flags` works, and the
      texture is pre-warmed through the same `OnlineAssetCachingStore` the avatar drawable reads.
- [x] Online beatmap leaderboards (the results-screen scoreboard and the `scoreboard` HUD element)
      are usable. The engine can now be signed into lazer's API provider via a new
      `--osu-user-token` flag, wired to `Renderer:OsuBotRefreshToken` / `Renderer:OsuBotToken` in
      the service (a **user** credential is required — the `/me` identity check rejects
      client-credentials tokens), and the beatmap leaderboard is fetched before the player is pushed
      so the scoreboard has data when it loads. Maps imported without an online status are treated
      as ranked in memory to satisfy lazer's client-side guard.
- [x] The results screen no longer issues a doomed online score fetch when no osu! user token is
      configured: `SoloResultsScreen` always asked for the beatmap leaderboard to compute the
      player's rank, which logged `Failed to fetch scores …: NotLoggedIn` into every render's tail.
      The fetch is now skipped unless the API is genuinely logged in. A bad/expired token degrades
      the same way (lazer logs out) without failing the render.
- [x] Online leaderboards no longer need a bot account. Each render is signed into the osu! API with
      the identity of the player who queued it, reusing the refresh token the service already stores
      at web sign-in (`UserOsuTokenService`); the configured bot credential is only a fallback. This
      required requesting the `public` scope alongside `identify`, so previously signed-in users must
      sign in once more.
- [x] The in-game `scoreboard` HUD element was impossible to show: the recorder forced
      `PlayerConfiguration.ShowLeaderboard = false`, and lazer's `DrawableGameplayLeaderboard` hides
      its scores whenever that is false — independently of the HUD filter. It is now enabled exactly
      when the `scoreboard` element is requested, and a new `--leaderboard-scope` / `leaderboardScope`
      option (global / country / friend / team) selects which leaderboard is fetched, mirroring the
      scope a player can pick in song select. No UI control was added for it: `country`/`friend`
      require osu!supporter and `team` requires a team, so the service always sends `global`.
- [x] Leaderboard failures are now diagnosed instead of masked. The fetch loop treated
      `LeaderboardScores.FailState` as "still loading" and reported a timeout after 8 s, hiding the
      real cause (`NotLoggedIn`, `BeatmapUnavailable`, `NotSupporter`, `NoTeam`, `NetworkFailure`);
      it now reports the state with an explanation and logs the signed-in identity and the API
      endpoint. The service also warns at startup when the configured osu! OAuth scopes omit
      `public`, which is what silently disables online leaderboards.
- [x] Fixed online leaderboards never populating under `dotnet run`: lazer selects the **development**
      endpoints (`dev.ppy.sh`) for debug builds, so it validated the production user token against the
      dev server, got 401 from `/me`, and logged itself out before the score fetch. `LazerRenderGame`
      now pins `UseDevelopmentServer` to `false`. The service's engine log forwarding is what made this
      visible (the `Request to https://dev.ppy.sh/api/v2/me/ failed with Unauthorized` line), and the
      authorize URL / OAuth scopes / engine output are all logged now.

## ⬜ Phase 7 — Security Audit & Hardening (Planned)

The full security audit — threat model, authn/authz review, rate limiting, upload handling, secret
handling and the exposure of a Cloudflare + NGinx-fronted public deployment — will be performed by
another model. `SECURITY_AUDIT_CONTEXT.md` is that model's hand-off briefing: it maps every auth,
credential, network, IPC, filesystem and user-input surface with code excerpts. This phase is assurance
only; the observability work that used to live here has moved to Phase 8, behind the infrastructure it
depends on.

### 7.1 Hardening backlog (produced by the audit)

The audit is complete: see [`SECURITY_AUDIT_REPORT.md`](SECURITY_AUDIT_REPORT.md) — 5 High (P0), 11
Medium (P1) and 11 Low (P2) findings, each with evidence, a recommendation and a verification step.

**P0 — before any public exposure (implemented):**

- [x] **H-1** `JobsHub.Subscribe` verifies that the job belongs to the caller, and validates the job
      id shape. Regression tests in `JobsHubAuthorizationTests`.
- [x] **H-2** Forwarded headers with an explicit `Proxy:KnownProxies`/`KnownNetworks` trust list;
      cookies always `Secure` outside Development; the rate limiter therefore keys on the forwarded
      client address. Tests in `ProxyConfigurationTests`; the end-to-end cookie/partition check is a
      documented manual step (`DEPLOYMENT.md` §5).
- [x] **H-3** The key ring is created `0700` with existing key files tightened, and can be encrypted
      at rest with `DataProtection:CertificatePath`. Tests in `FilePermissionsTests`.
- [x] **H-4** Credentials moved off `argv` into an owner-only secrets file that the engine deletes
      after reading; the injected token is cleared from engine config on exit; the log bridge redacts
      the values. Tests in `SecretsChannelTests`.
- [x] **H-5** Shipped `OsuUserIds` is empty (the personal id is gone) and first-login-wins is
      replaced by a constant-time, one-shot, token-gated bootstrap. Tests in `BootstrapAdminTests`.

**P1 — hardening (implemented):**

- [x] **M-1** Preset `ConfigJson` is validated like a job config, capped at 16 KB, and limited to 50
      presets per user (`PresetGuard`).
- [x] **M-2** The metadata lookup takes a real 25 s deadline (it is cancelled rather than orphaned while
      holding the render lock), and `POST /api/v1/jobs` has its own rate-limit partition.
- [x] **M-3** CSP / `nosniff` / `Referrer-Policy` / `X-Frame-Options` are set on every response, HSTS is
      added outside Development, and the SPA's inline `onclick` (and its one inline `style`) are gone, so
      `script-src` and `style-src` stay at `'self'`.
- [x] **M-4** The osu! token error body never reaches an exception or the log (only the short `error`
      code), and a failed exchange redirects to `/auth/error` instead of returning a 500.
- [x] **M-5** State-changing requests require a `X-LazerRender-Request` header — a second CSRF layer
      behind `SameSite` (see `DEPLOYMENT.md` §12 for curl usage).
- [x] **M-6** Skins stay a shared library, but deletion is restricted to the uploader or an admin
      (decided by the project owner), and the Realm divergence is documented.
- [x] **M-7** Engine/import log lines are redacted (known secrets plus bearer-token shapes),
      length-capped, and bounded by a per-render byte budget.
- [x] **M-8** Beatmap packages get their own size cap and archives are checked for bomb shapes before
      import; `Renderer:DownloadMissing=false` and engine-user isolation are documented.
- [x] **M-9** *Partial* — the service warns at startup while `AllowedHosts` is unrestricted. Setting it
      to the real hostname is a deployment step (see `SECURITY_AUDIT_REPORT.md` and `DEPLOYMENT.md` §12).
- [x] **M-10** Quota check and insert are serialised by `JobCreationGate`, and `jobs.DisplayNumber` now
      has a unique index.
- [x] **M-11** The session has an absolute lifetime (7 days, no sliding, plus an `auth_time` check in the
      cookie validator).

**P2 — low / hygiene (implemented):**

- [x] **L-1** The engine's own `.osr` header parser bounds the hash length at 256, matching the service.
- [x] **L-2** `job.id` is passed through `CSS.escape` in the one selector that lacked it.
- [x] **L-3** `fetch-user-token.sh` prints only the refresh token, with a warning to treat it as a secret.
- [x] **L-4** The runner-script search is confined to the content root outside Development (so a
      writable ancestor cannot substitute the executed script), and the bare-name `setsid` PATH fallback
      is refused there.
- [x] **L-5** Swagger is `#if DEBUG` and its package reference is Debug-only, so Release bundles carry
      neither.
- [x] **L-6** Shared read-only listings remain by design; skin names are client-controlled and visible
      to every authenticated user — documented in `DEPLOYMENT.md` §12.
- [x] **L-7** `/auth/*` has its own, tighter rate-limit partition.
- [x] **L-8** The OAuth `RedirectUri` default is empty and the service fails loudly at startup when
      `ClientId` is set without one.
- [x] **L-9** `AllowUser` no longer echoes an internal exception message; the detail is logged.
- [x] **L-10** The systemd unit no longer points `Documentation=` at the osu! submodule, and the mirror
      comment matches the code.
- [x] **L-11** Central package management plus `packages.lock.json` for the service half, and the
      transitive inventory recorded in `MAINTENANCE_INFRA_CONTEXT.md` §8 for the next submodule bump.

Phase 7 is done: the audit's remaining items are the deliberate acceptances recorded in
`SECURITY_AUDIT_REPORT.md` §8, and the deployment-side steps called out in the rows above.

Each item is closed with its verification evidence: a test, a reproduction, or a written justification
for accepting the risk.

## ⬜ Phase 8 — Docker, Observability & Release (Planned)

The detailed design (compose stack, image layering, logging/instrumentation design, debugging workflow)
is to be produced by a more capable model first; this phase records the scope, constraints and the parts
that are already clear. `MAINTENANCE_INFRA_CONTEXT.md` is the hand-off briefing for that design work.

### 8.1 Docker deployment

- [ ] Package the service as a container image plus a `docker-compose` service that drops into the
      existing Portainer stacks (currently Jellyfin and Nextcloud).
- [ ] Assign a non-default host port so the stack cannot clash with those services.
- [ ] Public access follows the existing pattern: Cloudflare domain → NGinx reverse proxy → this
      service. Cover TLS/forwarded-headers handling, and reuse the existing `/health` endpoint for
      container and proxy health checks.
- [ ] Work out which codebase changes are actually required for containerisation: GPU access for
      VAAPI/NVENC (`/dev/dri` passthrough), the headless rendering path inside the image, volumes for
      the Realm/beatmap storage and results, and keeping every credential in the environment.
- [ ] Confirm the render worker's process-group cancellation still behaves correctly when it is PID 1
      inside a container.
- [ ] Settle the deployment shape that Phase 8.3's **Render PC** card will report on — container-visible
      CPU/RAM versus host, GPU passthrough, and the FFmpeg build and .NET runtime inside the image — so
      the card is designed against the environment that actually ships rather than a bare-metal host.

### 8.2 Logging & instrumentation core

- [ ] Lock the shared design **once**, before either consumer is built: a single log record model
      (level, source, timestamp, message); a bounded in-memory ring buffer for the service stream; an
      `ILoggerProvider` feeding it; `RendererProcessRunner.logEngineLine` feeding it with
      `source = engine`; and pluggable sinks.
- [ ] Make Phase 8.3's admin panel and the local debug workflow two consumers of that one pipeline —
      neither may reimplement it.
- [ ] Reuse the classification already implemented in `RendererProcessRunner` (Warning for problems,
      Information for notable lines, Debug for framework chatter) and expose level, source and timestamp.
- [ ] Honour the security audit's redaction findings: engine stdout/stderr can carry credentials (the
      osu! user token arrives via argv, and avatar-lookup failures log response bodies), so redaction
      must happen in the pipeline, before anything reaches a browser.
- [ ] Establish the debug/release distinction: debug-only instrumentation gated by a compile-time flag
      and compiled out of release builds, plus a runtime toggle for paths that must not need a rebuild.
      Note that `run-headless.sh` currently runs the engine via `dotnet run` (Debug), so "release" has to
      be arranged deliberately rather than assumed.

### 8.3 Admin observability panel

Pure consumer of 8.2, designed against 8.1's deployment shape and the audit's redaction findings.

- [ ] Split the admin tab into explicit sections: **Users** (existing allow/revoke), **Library**
      (existing purge), **Render PC** and **Console logs**.
- [ ] **Render PC**: grow the existing encoder readout (today the card's only content) into a
      hardware/software summary — CPU, GPU plus driver/API, RAM, free space on the results volume,
      the FFmpeg build, the engine's .NET runtime and the resolved `--encoder` backend.
- [ ] **Render PC**: collect the summary once at startup and refresh on demand rather than on every
      request; nothing in the panel should block the render worker.
- [ ] **Console logs**: two independent streams — **engine** (the recorder child process) and
      **service** (this API/worker) — so a failed render can be diagnosed without shell access.
- [ ] **Console logs**: do not write logs to disk continuously. Stream only while an admin has the
      panel open — a bounded in-memory ring buffer for the service stream, and a tee of the engine's
      stdout/stderr while a render is running — and retain nothing once the panel disconnects.
- [ ] Keep the panel admin-only as it grows. Every admin route must enforce the role **server-side**
      (never by hiding UI), and every destructive or state-changing control must be authorized
      against the database `Role`, consistent with how `QuotaService` reads it.
- [ ] Add regression tests asserting that a non-admin session receives 401/403 on every admin route.

### 8.4 MAINTENANCE.md & debug workflow docs

- [ ] Write `MAINTENANCE.md`: what to watch for when rebasing onto a newer Tachyon release, how to
      repair breakage caused by osu! API changes, and a comprehensive how-to for both. Use the
      fragility inventory in `MAINTENANCE_INFRA_CONTEXT.md` as the starting checklist.
- [ ] Validate it against a real exercise rather than theory: perform one tachyon re-pin (or a dry-run
      bump) using the 8.2 instrumentation, and write the workflow from what actually broke.
- [ ] Document the debug workflow end to end: how to build and run debug versus release, and how to read
      the logs to identify issues without an AI model.

### 8.5 GitHub release

- [ ] Add the missing `LICENSE` file.
- [ ] Do not ship `ROADMAP.md`, the `dev/` prompts and reports, test artifacts (`out/`, `tests/`),
      the local `storage/` tree or the editor's `.zed/` directory; encode all of it in `.gitignore`.
- [ ] Verify the published tree contains no secrets or personal leftovers (tokens, keys, local paths,
      dev-server credentials) before the first push.
- [ ] Document project setup — in particular that the pinned `extern/osu` submodule must be
      initialised, which is what a fresh clone currently lacks.
- [ ] Publish, and record the publish steps so the release can be repeated.
- [ ] Add a prominent warning at the top of `README.md` that the project is 100% vibe-coded.

## ⬜ Phase 9 — New Features (Planned)

Scope only — each feature is to be planned (architect mode) and reviewed before implementation.

- [ ] **View replays in the browser**, like o!rdr: a "watch replay" action (play icon, placed to the
      left of the download button) that opens a new browser tab and plays the render there. Open
      questions to settle when planning: stream the already-produced MP4 (simplest) versus re-render
      on demand, the player implementation, seeking, and keeping the view inside the allowlist.
- [ ] **Strain graph** HUD element, inspired by o!rdr's rather than copied: a difficulty/strain
      over-time plot drawn alongside the gameplay. Needs a decision on the data source (lazer's
      difficulty calculation), a renderable/skinnable component, and a home in the render options —
      added as its **own entry separate from the "HUD elements" group**, since it is a data
      visualisation rather than a standard HUD component.
