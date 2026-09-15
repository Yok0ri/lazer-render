# LazerRender

A native-lazer, headless, faster-than-realtime replay recorder. It treats
[`ppy/osu`](https://github.com/ppy/osu) as a pinned submodule dependency and wraps the real game
engine rather than reimplementing gameplay mechanics.

LazerRender is designed to run on a home server as the backend for a web application (similar to
o!rdr). The engine maintains its own persistent Realm database of beatmaps and skins and exposes a
command-line interface, and the bundled ASP.NET Core web service
([`LazerRender.Service`](LazerRender.Service)) wraps that CLI in a multi-user web API and UI.

## Repository layout

- [`LazerRender.Game`](LazerRender.Game) – the recorder host/loop/player, FBO capture, FFmpeg pipe and
  headless asset management. It also contains the pinned client checkout
  ([`LazerRender.Game/extern/osu`](LazerRender.Game/extern/osu)), the engine scripts, the test
  replays and the engine's runtime storage.
- [`LazerRender.Service`](LazerRender.Service) – the ASP.NET Core web API + single-page frontend.
- [`LazerRender.Service/src/LazerRender.Contracts`](LazerRender.Service/src/LazerRender.Contracts) –
  shared DTOs and enums used by the API and any future client.

## Pinning

The official client is pinned to a specific release commit so its APIs cannot churn underneath the
recorder. The pin currently tracks the **Tachyon** pre-release stream (rather than the stable
`lazer` stream) to pick up the reworked extended results-screen layout from
[ppy/osu#38643](https://github.com/ppy/osu/pull/38643). This is a deliberate exception to the
stable-stream pinning strategy: Tachyon tags are opt-in pre-releases, so treat every re-pin onto
this stream as a regression event, not a routine version bump.

```bash
git -C LazerRender.Game/extern/osu fetch --depth 1 origin tag 2026.821.0-tachyon
git -C LazerRender.Game/extern/osu checkout 2026.821.0-tachyon
git add LazerRender.Game/extern/osu   # record the new gitlink in the super-project
```

`osu.Game` consumes `ppy.osu.Framework` and `ppy.osu.Game.Resources` from NuGet, so only the
`osu.Game` and ruleset projects need to be referenced from
[`LazerRender.Game.csproj`](LazerRender.Game/LazerRender.Game.csproj:1).

## Build & run

```bash
dotnet build LazerRender.sln
```

LazerRender performs exactly one operation per invocation. The `--storage` directory is persistent:
it retains imported beatmaps, skins and the `client.realm` database between runs, so a headless
server can accumulate a library over time.

### Import a beatmap

```bash
dotnet LazerRender.Game/bin/Debug/net8.0/LazerRender.dll \
    --import-map path/to/beatmap.osz \
    --storage LazerRender.Game/storage
```

Imports the package into the persistent Realm database. Both `.osu` and `.osz` are accepted. The
source archive is consumed (moved into managed file storage) after a successful import.

### Import a skin

```bash
dotnet LazerRender.Game/bin/Debug/net8.0/LazerRender.dll \
    --import-skin path/to/skin.osk \
    --storage LazerRender.Game/storage
```

Imports a legacy skin package into the persistent Realm database.

### Purge beatmaps / skins

```bash
dotnet LazerRender.Game/bin/Debug/net8.0/LazerRender.dll \
    --purge all \
    --storage LazerRender.Game/storage
```

`--purge <beatmaps|skins|all>` deletes imported beatmap sets and/or legacy skins from the persistent
Realm database, reclaiming the disk space their managed file storage occupies. This is the Phase 4
server cleanup mechanism.

### Render a replay

```bash
dotnet LazerRender.Game/bin/Debug/net8.0/LazerRender.dll \
    --replay path/to/replay.osr \
    --skin "Skin Name" \
    --output out \
    --storage LazerRender.Game/storage \
    --fps 60 --width 1280 --height 720
```

### Headless operation (no desktop session)

LazerRender needs a real GPU-backed EGL context, but it does not need a visible window. For a truly
headless server (no logged-in desktop / no compositor), run it against a throwaway Weston headless
compositor via [`run-headless.sh`](LazerRender.Game/scripts/run-headless.sh):

```bash
LazerRender.Game/scripts/run-headless.sh --replay LazerRender.Game/tests/replay_nm_short.osr --skin "Aristia v2" \
    --output out --storage LazerRender.Game/storage --fps 60 --width 1920 --height 1080 --encoder amd
```

The script creates an isolated Wayland socket, starts `weston --backend=headless --renderer=gl`, runs
LazerRender against it (all arguments pass through unchanged), and tears Weston down afterwards. No
window appears on the desktop. The `--renderer=gl` flag is what keeps Mesa from falling back to the
software `llvmpipe` driver; set `LAZERRENDER_MESA_DRIVER` (e.g. `radeonsi` for AMD, `iris` for Intel)
to force a specific hardware driver.

`--duration` is optional: when omitted, the recorder renders until the replay's final input frame
(plus a 5-second results tail). Pass `--duration <sec>` to record a fixed-length clip instead.

The beatmap is resolved automatically: the recorder reads the MD5 hash embedded in the `.osr`
header and looks up the matching `BeatmapInfo` in its own Realm database. No `--beatmap` argument
is required. `--output` receives `output.mp4`.

Pass `--download-missing` to make a headless server self-sufficient: when the replay's beatmap hash
is not in the local database, LazerRender downloads the `.osz` from a public mirror (osu.direct,
falling back to catboy.best), imports it, and proceeds with playback automatically.

`--skin` is optional. When provided, the named skin is looked up in the Realm database and applied
to the `SkinManager` before playback. When omitted — or when the name is unknown — the recorder
falls back to the built-in **osu! "argon" pro** skin (argon without the x300 hit popups) rather than
plain argon; beatmap-bundled skins still apply as a fallback layer.

Visual and gameplay settings are exposed through a data-driven table
([`SettingDescriptor.cs`](LazerRender.Game/SettingDescriptor.cs:1)); every value is set explicitly
each run so persisted state cannot leak between renders. Booleans accept `--<flag>` /
`--no-<flag>`; numeric and enum settings accept `--<flag> <value>`. The same keys are accepted by
`--render-config` (below).

- `--render-config <path>` — a JSON document bundling per-render settings (`-` reads stdin). Keys
  mirror the settings below plus `hud`, `fps`, `width`, `height`, `motionBlur`, `hudScale`,
  `disableResultScreen`, `skin` and `duration`. Intended for the Phase 5 web daemon; individual
  flags remain for direct CLI use.

Visual settings (global):
- `--dim-level <0..1>` (default 0.7) — background dim.
- `--blur-level <0..1>` (default 0) — background blur.
- `--parallax <0..2>` (default 1) — background parallax scale.
- `--storyboard` / `--no-storyboard` (default on) — beatmap storyboard.
- `--video` / `--no-video` (default on) — beatmap background video.
- `--beatmap-skins` / `--no-beatmap-skins` (default on) — beatmap skin.
- `--beatmap-colours` / `--no-beatmap-colours` (default on) — beatmap colours.
- `--beatmap-hitsounds` / `--no-beatmap-hitsounds` (default on) — beatmap hitsounds.
- `--combo-colour-normalisation <0..1>` (default 0.2).
- `--hud-visibility <never|hiddengameplay|always>` (default always) — the three Shift+Tab states.
- `--hit-lighting` / `--no-hit-lighting` (default off) — hit lighting (click aftereffects).

osu! ruleset settings:
- `--snaking-in` / `--no-snaking-in` (default on) — snake sliders in.
- `--snaking-out` / `--no-snaking-out` (default on) — snake sliders out.
- `--hit-animations` / `--no-hit-animations` (default on) — hit animations.
- `--cursor-trail` / `--no-cursor-trail` (default on) — cursor trail.
- `--cursor-ripples` / `--no-cursor-ripples` (default off) — cursor ripples.
- `--cursor-size <0.1..2>` (default 1) — gameplay cursor size.
- `--playfield-border <none|corners|full>` (default none) — playfield border style.

Replay-analysis overlays (osu! ruleset):
- `--show-click-markers` / `--no-show-click-markers` (default off).
- `--show-frame-markers` / `--no-show-frame-markers` (default off).
- `--show-cursor-path` / `--no-show-cursor-path` (default off).
- `--hide-gameplay-cursor` / `--no-hide-gameplay-cursor` (default off).
- `--replay-analysis-length <200..2000>` (default 800) — cursor-path display length.

HUD whitelist (hide everything *except* the listed components):
- `--hud <key> [<key> ...]` — space- and/or comma-separated and repeatable. When given, the recorder
  switches to whitelist mode and hides every HUD component that is not listed, including
  skin-specific elements. Valid keys, in display order: `hp`, `combo`, `score`, `keyoverlay`,
  `accuracy`, `pp`, `hiterror`, `song-progress`, `unstable-rate`, `judgements`, `mods`, `aim-error`,
  `rank`, `longest-combo`, `scoreboard`, `bpm`, `cps`, `player-name`, `avatar`, `flags`, `spectators`,
  `cosmetic`. Omit the flag entirely to show everything; pass it with no keys to hide the whole HUD.
  The web UI always sends the full list except any elements the user unchecked, and an explicit empty
  `hud: []` hides every HUD element.

Legacy aliases (still accepted): `--disable-storyboard` (→ `--no-storyboard`), `--disable-video`
(→ `--no-video`), `--hide-overlay` (→ `--hud-visibility never`). The replay "Watching <player> play
<map>" banner and settings cog are always hidden.

- `--hud-scale <multiplier>` — additional UI-scale multiplier on top of the resolution-independent
  HUD scaling (default 1.0 = lazer-native size; pass e.g. 1.2 to grow the HUD).
- `--disable-result-screen` — skip the results screen and fade the render (video and audio) to black
  over ~1 second, then stop.
- `--leaderboard-scope <scope>` — which beatmap leaderboard to warm for the scoreboard:
  `global` (default), `country`, `friend` or `team`. This mirrors the scope a player can pick in song
  select. Only has an effect when a user token signs the engine in (`--osu-user-token`), and note
  that `country`/`friend` additionally require osu!supporter on that account, and `team` requires the
  account to be on a team — so `global` is the only choice that always works. The web UI always sends
  `global`.
- `--avatar-api-key <key>` — osu! API v2 token used to fetch the replay player's avatar for the
  results screen (falls back to the `OSU_API_KEY` environment variable). The endpoint is public, so
  a client-credentials token is enough. When unset, the avatar is resolved from the user id embedded
  in the replay (`https://a.ppy.sh/{id}`).
- `--osu-user-token <token>` — osu! API v2 **user** access token that signs the engine in, which is
  what enables online beatmap leaderboards (the results-screen scoreboard and the `scoreboard` HUD
  element). Client-credentials tokens cannot be used here: lazer validates the token against `/me`.
  `--osu-user-token-expires-in <sec>` sets its validity (default 3600).
- `--motion-blur <n>` — blends `n` consecutive frames with FFmpeg's `tmix` filter (exponential-decay
  weights, most recent frame dominant) to simulate a high shutter angle. `0` (default) disables it;
  `3` is a light blur, `5` is heavy.
- `--encoder <backend>` — selects the FFmpeg video encoder backend:
  - `cpu` (default) — software `libx264` (`-crf 18 -preset fast`), the original fallback;
  - `amd` — VAAPI `h264_vaapi` (`-vaapi_device /dev/dri/renderD128` + `format=nv12,hwupload`,
    `-qp 18`);
  - `nvidia` — NVENC `h264_nvenc` (`-preset p4 -cq 18`);
  - `intel` — Intel QSV `h264_qsv` (`-init_hw_device qsv=hw -filter_hw_device hw` +
    `format=nv12,hwupload=extra_hw_frames=64,format=qsv`, `-global_quality 18`).
  Hardware backends offload encoding to the GPU's dedicated media engine, preventing the RAM/CPU
  saturation that software encoding causes at high resolutions (e.g. 4K @ 240fps).

> Note: hash resolution requires the database to contain a beatmap whose `.osu` file is
> byte-identical to the one the replay was recorded on. If the replay hash is not found, import the
> matching map version with `--import-map` first.

## Architecture

- [`LazerRenderGameHost`](LazerRender.Game/LazerRenderGameHost.cs:19) wraps the platform desktop
  host and owns the [`ManualClock`](LazerRender.Game/LazerRenderGameHost.cs:31) that drives both
  gameplay and scene-graph time. Wall-clock and vsync are ignored (`FrameSync.Unlimited`).
- [`LazerRenderGame`](LazerRender.Game/LazerRenderGame.cs:49) subclasses `OsuGameBase`, redirects
  storage to the persistent server directory, and dispatches on the requested
  [`RunMode`](LazerRender.Game/RecordOptions.cs:13):
  - import modes call `BeatmapManager`/`SkinManager` directly and exit;
  - record mode decodes the `.osr` with a [`DatabaseLegacyScoreDecoder`](LazerRender.Game/LazerRenderGame.cs:1131)
    that resolves the replay's MD5 hash against the Realm database, optionally applies a skin, and
    pushes a recorder player onto an `OsuScreenStack`.
  The game runs in `ExecutionMode.SingleThread` and re-sources the scene-graph clock to the
  `ManualClock` in [`SetHost`](LazerRender.Game/LazerRenderGame.cs:77).
- [`CaptureContainer`](LazerRender.Game/CaptureContainer.cs:36) wraps the screen stack in a
  `BufferedDrawNode`-backed FBO and exposes `CaptureAsync()`, which reads the rendered FBO instead
  of the undefined post-swap backbuffer.
- [`ReplayRecorderPlayer`](LazerRender.Game/ReplayRecorderPlayer.cs:36) subclasses `ReplayPlayer`,
  freezes the gameplay clock, seeks it to the `ManualClock` for each recorded frame, and pipes the
  FBO pixels into [`FfmpegFrameSink`](LazerRender.Game/FrameSink.cs:29). When the final replay input
  frame is reached it pushes an [`ExtendedResultsScreen`](LazerRender.Game/ExtendedResultsScreen.cs:28)
  (via an overridden `CreateResults`) and records a 5-second results tail before stopping. Lazer's
  `FrameStabilityContainer` keeps simulation stepping deterministically at ~60Hz while the draw rate
  scales independently.

## Web service (Phase 5)

The repository also ships a complete web service that turns the recorder into a multi-user,
o!rdr-like app: an ASP.NET Core (.NET 8) API plus a zero-dependency vanilla-JS single-page frontend.

### Layout

- [`LazerRender.Service/src/LazerRender.Contracts`](LazerRender.Service/src/LazerRender.Contracts) –
  the DTOs and enums shared between the API and any future client.
- [`LazerRender.Service/src/LazerRender.Api`](LazerRender.Service/src/LazerRender.Api) –
  the web API, background worker, SQLite database and the
  [`wwwroot`](LazerRender.Service/src/LazerRender.Api/wwwroot) SPA.
- [`LazerRender.Service/deploy`](LazerRender.Service/deploy) – systemd unit and Caddyfile for production.

### Run (development)

```bash
dotnet run --project LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj --launch-profile http
```

The app listens on `http://localhost:5080` (Swagger at `/swagger`, SPA at `/`). Runtime data is
written to `LazerRender.Service/src/LazerRender.Api/data/` (gitignored).

### What it does

- **Login** via osu! OAuth v2 (`identify public`), gated by an allowlist; the first account to sign
  in is granted `admin` (configurable via `Admin:OsuUserIds`). `public` is requested alongside
  `identify` because the beatmap-leaderboard endpoint needs it; the service warns at startup if the
  configured scopes omit it.
- **Render queue** — upload an `.osr`, choose render options, and the job is validated, queued and
  rendered by a single serialized worker. Live progress (`frame/total/fps`) is polled by the UI. On
  the Render tab the Recent renders card is a compact summary (`<player> | <map>`); the full
  Jobs tab expands to details and offers the result as an icon download button in the expanded body.
- **Avatars & online leaderboards** — each render is signed into the osu! API with the identity of
  the player who queued it, so the results screen and the `avatar` / `scoreboard` HUD elements show
  real data without a dedicated bot account (the configured bot credential is only a fallback).
- **HUD checklist** — the HUD group is a list of HUD elements (all enabled by default, with a master
  toggle); unticking any of them switches the render into whitelist mode via `hud`, which also
  removes skin-specific decorations. See `--hud` below.
- **Map metadata** — the replay's beatmap MD5 is resolved against the engine (`--map-info`) so job
  cards show `Artist — Title [Version]` instead of "Render #N".
- **Skins & presets** — import `.osk` skins and save/reuse render presets.
- **Admin** — allow/revoke users and purge the shared beatmap/skin library.

### Configuration & deployment

Secrets (OAuth client id/secret) are supplied via environment variables, never committed. See
[`LazerRender.Service/DEPLOYMENT.md`](LazerRender.Service/DEPLOYMENT.md) for the systemd +
self-contained publish recipe, and [`ARCHITECTURE.md`](ARCHITECTURE.md) for the
in-depth walkthrough of every file (engine + service).

## Project phases

The authoritative plan is [`ROADMAP.md`](ROADMAP.md). Status at a glance:

| Phase | Theme | Status |
|---|---|---|
| 0 | Architecture spike | ✅ completed |
| 1 | Video & audio pipeline | ✅ completed |
| 2 | Headless asset management | ✅ completed |
| 3 | Rendering polish & visual toggles | ✅ completed |
| 4 | Hardware acceleration & optimization | ✅ completed |
| 5 | The web API daemon | ✅ completed |
| 6 | Render & web UX refinements | ✅ completed |
| 7 | Security audit & hardening | ⬜ planned |
| 8 | Docker, maintenance infrastructure & release | ⬜ planned |
| 9 | New features (replay viewer, strain graph) | ⬜ planned |

The `Phase N status` sections below are a chronological record of engine work — a few later-phase
refinements are filed under whichever heading they were originally added beneath. Phase 5 is covered
by **Web service (Phase 5)** above, and Phase 6 by **Phase 6 status** at the end of this file.

## Phase 1 status

- FBO capture replaces `TakeScreenshotAsync()`: working (no SwapBuffers race, no black frames).
- Raw RGBA pixels piped straight into an external FFmpeg `System.Diagnostics.Process`: working.
- FFmpeg encodes `libx264`, `crf 18`, `preset fast`, `yuv420p`, 1280×720 @ 60fps.
- End-to-end run produced a valid 3.000s `output.mp4` (h264, 1280×720, 60fps).

## Phase 2 status

- Persistent Realm storage: working — `--storage` is no longer wiped between runs.
- `--import-map <path.osz>` native beatmap import: working.
- `--import-skin <path.osk>` native skin import: working.
- `.osr` MD5-hash → `BeatmapInfo` lookup for recording: working.
- `--skin "Skin Name"` selection with default/beatmap-skin fallback: working.

## Phase 3 status — Rendering polish

### Working

- **Native 1080p output on Wayland.** The `OsuScreenStack` is wrapped in a
  [`DrawSizePreservingFillContainer`](LazerRender.Game/LazerRenderGame.cs:188) with
  `TargetDrawSize` set to the requested output size, and [`CaptureContainer`](LazerRender.Game/CaptureContainer.cs:36)
  forces the FBO to that exact size via `FrameBufferScale`. A 1366×768 physical window now produces a
  genuine 1920×1080 render without breaking osu!lazer's UI anchors.
- **Dynamic FFmpeg resolution sizing.** [`FfmpegFrameSink`](LazerRender.Game/FrameSink.cs:29) starts
  FFmpeg lazily from the actual captured frame dimensions, eliminating rawvideo stride corruption.
- **Fractional-sample audio accumulator.** [`ReplayRecorderPlayer`](LazerRender.Game/ReplayRecorderPlayer.cs:36)
  keeps 44100Hz aligned at 60/75/90/120fps, removing the 120fps electrical buzz.
- **Safe frame extraction (superseded — see Phase 4).** The FBO was assumed to be RGBA32F and was read
  back as `GL_FLOAT`, but Phase 4 verified at runtime that the legacy `GLRenderer` actually allocates
  the capture FBO as `GL_RGBA8` (8/8/8/8 bits). The float read was therefore doing a driver-side
  8→32-bit expansion each frame; the capture path now reads `GL_UNSIGNED_BYTE` directly.
- **Headless-friendly window.** The desktop window is hidden/minimised; Wayland may still show it,
  but rendering is unaffected. Rendering runs natively on Wayland rather than under Xvfb, because
  X11/Xvfb triggers an uncatchable GPU driver segfault on this AMD machine.
- **Native results-screen transition.** [`ReplayRecorderPlayer`](LazerRender.Game/ReplayRecorderPlayer.cs:36)
  detects the final replay input frame and hands off to lazer's own `CreateResults` factory in
  [`pushResultsScreen`](LazerRender.Game/ReplayRecorderPlayer.cs:592), then records the score panel +
  statistics transition for a 5-second tail. The video and the remaining map music then fade to black
  over the last 1.5 seconds of that tail (3.5 s → 5.0 s), while results-screen sounds (applause /
  pop-in) are suppressed.
- **Extended statistics panel.** [`ExtendedResultsScreen`](LazerRender.Game/ExtendedResultsScreen.cs:28)
  expands the main score panel in [`OnEntering`](LazerRender.Game/ExtendedResultsScreen.cs:66), so the
  recorded results screen shows the main card on the left and the full Performance Breakdown /
  Timing Distribution / Accuracy Heatmap on the right immediately, without a click. The score panel's
  expand slide and "new score" flair animations are skipped, and the bottom toolbar (collection /
  loved buttons) is hidden before the screen is first drawn so it never flashes.
- **Settings surface.** [`applyVisualToggles`](LazerRender.Game/LazerRenderGame.cs:984) iterates the
  data-driven [`SettingsCatalog`](LazerRender.Game/SettingDescriptor.cs:79) and applies every value
  explicitly each run — global `OsuSetting` values via `LocalConfig`, osu! ruleset values via
  `IRulesetConfigCache` — so persisted state cannot leak between renders. This covers the visual,
  gameplay and replay-analysis settings listed under "Render a replay", plus the HUD whitelist
  from [`HudVisibilityFilter`](LazerRender.Game/HudVisibilityFilter.cs:31). The replay `ReplayOverlay`
  ("Watching …" banner + settings cog) is always hidden in
  [`LoadComplete`](LazerRender.Game/ReplayRecorderPlayer.cs:160).
- **Auto duration.** With no `--duration`, [`totalFrames`](LazerRender.Game/ReplayRecorderPlayer.cs:93)
  is unbounded and the recorder stops at the replay's final input frame (plus the results tail);
  a fixed `--duration` still acts as a clip-length cap with a grace window.
- **Avatar lookup.** [`applyAvatarAsync`](LazerRender.Game/LazerRenderGame.cs:652) gives the score's
  `APIUser` the replay player's real osu! user id (taken from the replay's own `RealmUser.OnlineID`,
  which is what lazer's `DrawableAvatar` actually gates on), optionally enriches it via
  `GET /api/v2/users/{id}` using `--avatar-api-key` (or `OSU_API_KEY`, a client-credentials token is
  enough), then pre-warms the texture through the same `OnlineAssetCachingStore` the avatar drawable
  reads. When no key is set the public `https://a.ppy.sh/{id}` URL is used, so avatars work without
  any authentication. It logs the request URL, HTTP status and response body for diagnostics — the
  pre-warm is also what prevents the 1080p segfault a late remote texture upload caused.
- **Online scoreboards.** Each render is signed into lazer's API provider with the identity of the
  player who queued it, reusing the refresh token the service already holds for them, which lets the
  results-screen scoreboard and the `scoreboard` HUD element fetch real beatmap leaderboards without
  a dedicated bot account. The engine forces lazer onto the **production** endpoints, because lazer
  otherwise uses `dev.ppy.sh` for debug builds — where a production token is rejected. A *user*
  credential is required either way, because the `/me` identity check rejects client-credentials
  tokens. Without one the render still succeeds: the scoreboard
  shows only the replaying player and the results screen skips its online rank fetch instead of
  logging a `NotLoggedIn` error.

## Phase 4 status — Hardware acceleration (completed)

### Working

- **Hardware encoder selection.** [`--encoder`](LazerRender.Game/RecordOptions.cs:69) chooses the
  FFmpeg video encoder backend (`cpu`, `amd`, `nvidia` or `intel`; defaults to `cpu`). The
  [`FfmpegFrameSink`](LazerRender.Game/FrameSink.cs:29) builds the matching FFmpeg arguments at
  [`startProcess`](LazerRender.Game/FrameSink.cs:152):
  - [`buildHardwareInitArgs`](LazerRender.Game/FrameSink.cs:254) injects the VAAPI device
    (`-vaapi_device /dev/dri/renderD128`) or QSV device (`-init_hw_device qsv=hw -filter_hw_device hw`)
    before the inputs;
  - [`buildVideoFilterArgs`](LazerRender.Game/FrameSink.cs:265) merges the `tmix` motion-blur filter
    with the backend's pixel-format/upload filters into a single `-vf` chain, so `--motion-blur`
    works with every encoder;
  - [`buildEncoderArgs`](LazerRender.Game/FrameSink.cs:295) selects `h264_vaapi -qp 18`,
    `h264_nvenc -preset p4 -cq 18`, `h264_qsv -global_quality 18` or the `libx264` fallback.
- **AMD RDNA4 verified.** A 2560×1440 @ 60fps, 32-second render with `--encoder amd` completed
  without OOM. Output: 2560×1440 h264, 60fps, ~36 MB — the raw RGBA pipe is now encoded by the GPU
  media engine instead of the software encoder.
- **The capture FBO is 8-bit RGBA, not float.** [`logFboDiagnostics`](LazerRender.Game/CaptureContainer.cs:207)
  queries `glGetFramebufferAttachmentParameter` at runtime: the renderer is
  `osu.Framework.Graphics.OpenGL.GLRenderer` and the colour attachment is `GL_UNSIGNED_NORMALIZED`
  (0x8C17) with 8/8/8/8 bits. Despite the misleading `TexturePixelFormat.R8G8B8A8Float` name, the
  legacy GL renderer maps it to `TextureComponentCount.Rgba8` (GL_RGBA8).
- **Direct 8-bit readback.** [`onFrameBufferRendered`](LazerRender.Game/CaptureContainer.cs:148) reads
  the FBO with `glReadPixels(GL_UNSIGNED_BYTE)` into a 5-buffer `GL_PIXEL_PACK_BUFFER` ring (8.3 MB
  per 1080p frame) with a zero-copy pointer handoff to a background copy thread. This removed the 4×
  float expansion and the redundant CPU float→byte pass, roughly doubling throughput: 1080p60 went
  from ~29 fps (0.49×) to 60–118 fps (1.0–2.0× realtime).
- **The 60 fps cap was vsync/compositor pacing, not the scene render.** A flat-fill control (scene
  culled via `LAZERRENDER_FLATFILL=1`) ran at 360 fps / 5.99× while the real scene was locked at
  exactly 60 fps. osu.Framework already requests an immediate swap (`FrameSync.Unlimited` →
  `renderer.VerticalSync = false`, verified at runtime), but the Wayland compositor ignores EGL
  swap-interval 0 and paces `SwapBuffers` to its frame callback. Forcing `vblank_mode=0` (and
  NVIDIA's `__GL_SYNC_TO_VBLANK=0`) raised the 1080p baseline from a locked 60 fps to
  175–363 fps (2.9–6.0× realtime). The fix is applied in code in
  [`LazerRenderGameHost`](LazerRender.Game/LazerRenderGameHost.cs:35).
- **1440p soak (the original failure case).** The full ~4-minute 1440p60 replay that previously took
  over 20 minutes now renders in ~3:19–3:49 (61–70 fps, 1.0–1.17× realtime) with `--encoder amd`,
  across two clean full runs (13 918 frames each, no OOM, no SIGSEGV). At 1440p the scene render is
  now the remaining wall (the capture path alone still has several× headroom).
- **90 fps pipeline deadlock fixed.** At 90 fps content the old lazy FFmpeg start produced a startup
  burst that filled the bounded queues and deadlocked the pipeline (FFmpeg stalled waiting for audio,
  video backpressure blocked the draw thread, which stopped the recorder loop). FFmpeg now starts
  eagerly in [`FfmpegFrameSink.Start`](LazerRender.Game/FrameSink.cs:87), so it is consuming both
  inputs before the first frame arrives. A 90 fps 1080p render now completes cleanly (3 420 frames,
  60–67 fps). The remaining ~60 fps at >60 fps content is the ruleset's 60 Hz simulation stepping,
  not the capture path.
- **90 fps + motion-blur deadlock fixed.** With `--motion-blur` at 90 fps, FFmpeg's `tmix` filter
  delayed its output, and `-shortest` made FFmpeg stop reading the video pipe whenever audio
  momentarily stalled — deadlocking the bounded capture queues. `-shortest` was removed from
  [`FfmpegFrameSink`](LazerRender.Game/FrameSink.cs:29) (the recorder closes both inputs at
  `Finish`), and [`CaptureContainer.CaptureAsync`](LazerRender.Game/CaptureContainer.cs:120) now
  waits on a semaphore so the recorder loop throttles to FFmpeg instead of blocking the draw thread.
  A 90 fps 1080p render with `--motion-blur 3` completes cleanly (8 762 frames, ~136 fps, 1.51×
  realtime).
- **Supported render matrix (4K120 ceiling).** Rendering is restricted to the resolutions
  1280x720 / 1920x1080 / 2560x1440 / 3840x2160 at 30 / 60 / 90 / 120 fps. Anything else is rejected
  up front by the engine CLI and the service validator. 240 fps was removed: it proved unstable at
  720p, and the ruleset simulation steps at about 60 Hz anyway.
- **Headless deployment (verified).** The windowless [`HeadlessGameHost`](LazerRender.Game/extern/osu) uses a
  `DummyRenderer` (stub) and cannot drive FBO capture. Instead, run under a throwaway Weston headless
  compositor via [`run-headless.sh`](LazerRender.Game/scripts/run-headless.sh), which starts
  `weston --backend=headless --renderer=gl`, runs LazerRender against it, and tears it down. The
  `--renderer=gl` flag keeps Mesa on a hardware driver (verified: `radeonsi` on AMD) rather than the
  software `llvmpipe` path; no window appears on the desktop.
- **Missing-beatmap auto-download.** With `--download-missing`, an unknown replay hash triggers a
  download from osu.direct (fallback: catboy.best), an import into Realm, and seamless playback —
  Phase 5 groundwork for unattended server operation. The downloaded `.osz` is deleted after import
  so repeated runs don't accumulate orphan temp archives.
- **Resolution-independent HUD scaling.** The renderer now replicates osu!lazer's own top-level UI
  scaling: the screen stack is laid out at lazer's reference size
  (`OsuGame.ScalingContainerTargetDrawSize` = 1024×768) via a
  [`DrawSizePreservingFillContainer`](LazerRender.Game/LazerRenderGame.cs:188) and scaled uniformly to
  fill the output resolution, exactly like the in-game `ScalingContainer(ScalingMode.Everything)`. This
  scales the whole HUD uniformly (gaps and relative positions preserved, the bottom progress bar stays
  within bounds) while the 4:3 playfield keeps its size. An optional `--hud-scale <multiplier>` shrinks
  the virtual layout to act as the in-game UI scale (default 1.0). Verified by measuring the health bar:
  489 px at 1080p and 653 px at 1440p, matching the in-game reference; `--hud-scale 1.2` yields 588 px.
- **Tachyon results-screen layout + per-render settings.** The pin was moved to `2026.821.0-tachyon`
  (see "Pinning"), adopting the reworked extended results screen (Average Hit Error / Unstable Rate
  merged into the Timing Distribution card; Timing Distribution + Accuracy Heatmap on one row;
  updated card border styling). Per-render settings, the HUD whitelist and `--render-config` JSON
  input were added in [`SettingDescriptor.cs`](LazerRender.Game/SettingDescriptor.cs:1) and
  [`HudVisibilityFilter.cs`](LazerRender.Game/HudVisibilityFilter.cs:1).
- **Supervisor progress + cancellation hooks.** The recorder emits machine-readable JSON progress
  lines to stdout (`PARSING` → `RENDERING_FRAMES` → `RESULTS_TAIL` → `FINALIZING` → `DONE`, with
  `frame`/`total`/`fps`) via [`reportProgress`](LazerRender.Game/ReplayRecorderPlayer.cs:248), and a
  process-wide [`RenderState.CancellationToken`](LazerRender.Game/RenderState.cs:14) is cancelled on
  Ctrl+C / SIGINT / SIGTERM so an external supervisor (the future web GUI) can abort a job cleanly.
- **Mod speed support (DT / HT / Daycore / Nightcore).** The recorder advances the gameplay clock at
 the mod's `SpeedChange` rate so the replay visual runs at the same speed as the rate-adjusted music.
 DT/HT "adjust pitch" is honoured (frequency-scaled through a BASS FX tempo stream for the
 chipmunk/deep-pitch sound); Daycore/Nightcore and DT/HT with pitch off use pitch-preserving tempo.
 Previously the music was sped up but the replay played at 1.0x, so the song ended before the map.
- **Output-size guard (engine + service).** A new `--replay-info` verb reports the replay's natural
 render duration and speed, and the service's [`RenderSizeEstimator`](LazerRender.Service/src/LazerRender.Api/Services/RenderSizeEstimator.cs:16)
 estimates the final `.mp4` size before the worker starts rendering. Jobs whose estimate exceeds the
 internal budget (default: one hour of 1080p60) are marked `Rejected` with a clear hint instead of
 filling the result disk. See [`WEB_GUI_GUIDE.md`](LazerRender.Game/WEB_GUI_GUIDE.md) and
 `Quota:MaxResultBytes` in [`appsettings.json`](LazerRender.Service/src/LazerRender.Api/appsettings.json:1).
- **HUD whitelist + argon pro default.** `--hud <keys>` (JSON `hud`, exposed in the SPA as an
 all-on component checklist with a master toggle) hides every HUD component except the listed ones,
 which also removes skin-specific elements. When no skin is requested the recorder now defaults to
 the built-in **argon pro** skin rather than argon. See
 [`HudVisibilityFilter`](LazerRender.Game/HudVisibilityFilter.cs:31).
- **HUD visibility parity + late metadata fix.** The `hiddengameplay` HUD-visibility mode now hides
 the HUD during a rendered replay exactly like a human player sees it (lazer deliberately force-shows
 the HUD for replays; see [`ReplayRecorderPlayer`](LazerRender.Game/ReplayRecorderPlayer.cs:36)), and
 the service re-resolves a job's beatmap metadata after its render finishes when the queue-time
 lookup failed (the map was only imported/downloaded during the render), so job cards show
 `Artist — Title [Version]` instead of the "Replay by …" fallback.

## Phase 6 status — Render & web UX refinements (completed)

### Web UI

- **Render tab** — the "View all" link is gone and the Recent renders card is a pure summary. Job
  titles are unified to `<player> | <map>` on both tabs, and a job's beatmap metadata is resolved
  (downloading the map if needed) *before* it becomes claimable, so the real title appears the moment
  a render is queued instead of after it finishes.
- **Jobs tab** — the extended view gained song length, star rating, mods and accuracy (parsed from the
  engine's extended `--replay-info` output), and the created/started/finished timestamps were
  collapsed into one stacked time island labelled with the viewer's time zone. Only one job can be
  expanded at a time, and the expanded view collapses when leaving the tab. Downloads are named
  `lazerrender-video.mp4` rather than exposing the internal render number.
- **Admin** — the encoder auto-detection readout moved here, into a "Render PC" card.

### HUD options

- New `Aim error meter` (`aim-error`) element, split out of the hit error meter, plus a
  `Cosmetic elements` (`cosmetic`) group covering Argon wedge pieces, boxes, text elements, beatmap
  attribute text and generic sprites.
- The checklist is now three gap-separated groups (gameplay HUD / secondary HUD / cosmetic) with a
  master select/deselect-all toggle, and additional gameplay toggles were added — notably
  `Hit lighting` (`OsuSetting.HitLighting`, default off).

### Engine & service

- **Avatars render.** The replay's real osu! user id is propagated onto the score's `APIUser` —
  lazer's `DrawableAvatar` gates on `OnlineID > 1` before it ever looks at an avatar URL, which is
  why every earlier attempt showed the guest icon. The country code is filled in so `flags` works, and
  the texture is pre-warmed through the same online asset store the avatar drawable reads.
- **Online leaderboards work**, signed in with the identity of the player who queued the render rather
  than a bot account. Two blockers had to be cleared: the recorder forced
  `PlayerConfiguration.ShowLeaderboard = false` (lazer hides the scoreboard's scores independently of
  the HUD filter), and lazer was talking to `dev.ppy.sh` because debug builds default to the
  development endpoints, where a production token is rejected.
- **Tail fades.** `--disable-result-screen` now fades video *and* audio to black over ~1 second, and
  the results-tail fade — which had stopped happening entirely — holds the results screen readable and
  then fades video *and* audio over its final 1.5 s (3.5 s → 5.0 s). Both share a single smoothstep
  ramp instead of a framework `Easing.Out` transform.
- **CLI/config cleanup.** The granular `--hide-*` flags and the separate `--hud-only` whitelist were
  merged into one `--hud <keys...>` flag (JSON `hud`), with `--help`, the docs and the SPA updated to
  match.

