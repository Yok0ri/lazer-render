# LazerRender

> ⚠️ **This project is 100% vibe-coded.** It was built end-to-end by AI assistants with a human in the
> loop, mostly for the author's personal use. It works, but expect rough edges, sparse comments in
> places, and a few cargo-culted decisions. Read the code before relying on it.

A native-lazer, headless, faster-than-realtime replay recorder. LazerRender wraps the real
[`ppy/osu`](https://github.com/ppy/osu) engine (pinned as a submodule) instead of reimplementing gameplay,
records a `.osr` to MP4 via FFmpeg, and ships with a multi-user web service — so a home server can run an
o!rdr-like render farm.

- **Engine** (`LazerRender.Game`) — one-shot CLI: import beatmaps/skins, render a replay to `output.mp4`,
  or purge assets. Needs a GPU (EGL) but no visible window.
- **Service** (`LazerRender.Service`) — ASP.NET Core API + vanilla-JS SPA: osu! OAuth login, a serialized
  render queue with live progress, skins/presets, an admin panel and in-memory console logs.
- **Container** — one image holding both halves, with a compose stack for Docker/Portainer.

## Features

- Faster-than-realtime rendering (typically 1–6× depending on resolution and encoder).
- 1280×720 / 1920×1080 / 2560×1440 / 3840×2160 at 30/60/90/120 fps.
- Hardware encoders: VAAPI (AMD), NVENC (NVIDIA), QSV (Intel), or software `libx264`.
- Full osu! ruleset visuals: skins, storyboards, videos, hitsounds, mods (DT/HT/Daycore/Nightcore),
  cursor trail/ripples, playfield border, motion blur, and per-render settings.
- HUD whitelist: show every HUD element, or only a chosen set (which also strips skin-specific extras).
- Automatic beatmap resolution by replay MD5, with optional auto-download from public mirrors.
- Optional avatars and online beatmap leaderboards (scoreboard) via the osu! API v2.
- Web service: queue, live progress, downloads, presets, skins, admin user management.

## Requirements

- **.NET 10 SDK** and the **ASP.NET Core 10 runtime** (the runtime is needed to *run* the service; the SDK
  bundles it on most installs). Linux, macOS or Windows.
- **A GPU with a working EGL/GL driver** for the engine.
- **FFmpeg** on `PATH`.
- **Linux headless renders:** `weston` (a throwaway headless compositor is used to get a real GPU context).
  This is the deployment the project is designed for; on Windows/macOS the engine expects a desktop session.
- **Linux build tools** are not required beyond the SDK; the pinned osu! submodule builds from source.

## Getting started

```bash
git clone --recurse-submodules <your-fork-url> lazer-render
cd lazer-render
# if you cloned without --recurse-submodules:
git submodule update --init --recursive

dotnet build LazerRender.sln                          # engine
dotnet build LazerRender.Service/LazerRender.Service.sln   # service
```

The osu! submodule (`LazerRender.Game/extern/osu`) is **required** — a clone that skipped it will not
build the engine.

## Engine usage

LazerRender performs exactly one operation per invocation.

```bash
# Import a beatmap into the persistent library
dotnet LazerRender.Game/bin/Debug/net10.0/LazerRender.dll \
    --import-map path/to/beatmap.osz --storage LazerRender.Game/storage

# Import a skin
dotnet LazerRender.Game/bin/Debug/net10.0/LazerRender.dll \
    --import-skin path/to/skin.osk --storage LazerRender.Game/storage

# Purge beatmaps / skins / everything
dotnet LazerRender.Game/bin/Debug/net10.0/LazerRender.dll \
    --purge all --storage LazerRender.Game/storage

# Render a replay (beatmap resolved from the .osr's MD5 hash)
dotnet LazerRender.Game/bin/Debug/net10.0/LazerRender.dll \
    --replay path/to/replay.osr --skin "Skin Name" --output out \
    --storage LazerRender.Game/storage --fps 60 --width 1280 --height 720 --encoder cpu
```

`--output` receives `output.mp4`. The source is looked up by the MD5 in the `.osr` header, so no
`--beatmap` is needed; add `--download-missing` to fetch it from a public mirror when absent. `--skin`
is optional (defaults to the built-in osu! "argon" pro skin). `--duration` is optional (defaults to the
whole replay plus a 5 s results tail).

> The repository does not ship replay files — point `--replay` at your own `.osr`.

### Headless (no desktop session)

```bash
LazerRender.Game/scripts/run-headless.sh --replay path/to/replay.osr --output out \
    --storage LazerRender.Game/storage --fps 60 --width 1920 --height 1080 --encoder amd
```

The script starts a throwaway Weston headless compositor, runs the engine against it, and tears it down
(all arguments pass through). Set `LAZERRENDER_MESA_DRIVER` (e.g. `radeonsi`, `iris`, `zink`) to force a
driver. `LAZERRENDER_ENGINE=/path/to/published/LazerRender.dll` makes it run a prebuilt engine instead of
`dotnet run` (this is how the container runs it).

### Options

Run the engine with `--help` for the exact flag names. In short:

- **Output:** `--fps`, `--width`, `--height`, `--encoder <cpu|amd|nvidia|intel>`, `--motion-blur <n>`.
- **Content:** `--skin`, `--duration`, `--storyboard`/`--no-storyboard`, `--video`/`--no-video`,
  `--beatmap-skins`, `--beatmap-colours`, `--beatmap-hitsounds`, `--dim-level`, `--blur-level`,
  `--parallax`, `--combo-colour-normalisation`.
- **Gameplay:** `--snaking-in`, `--snaking-out`, `--hit-animations`, `--hit-lighting`, `--cursor-trail`,
  `--cursor-ripples`, `--cursor-size`, `--playfield-border`.
- **Replay analysis:** `--show-click-markers`, `--show-frame-markers`, `--show-cursor-path`,
  `--hide-gameplay-cursor`, `--replay-analysis-length`.
- **HUD:** `--hud-visibility <never|hiddengameplay|always>`, `--hud-scale`, `--hud <keys...>`
  (`hp, combo, score, keyoverlay, accuracy, pp, hiterror, song-progress, unstable-rate, judgements, mods,
  aim-error, rank, longest-combo, scoreboard, bpm, cps, player-name, avatar, flags, spectators, cosmetic`).
- **Results:** `--disable-result-screen`, `--leaderboard-scope <global|country|friend|team>`.
- **Credentials (optional):** `--secrets-file`, `--avatar-api-key`, `--osu-user-token`.

`--render-config <path|->` takes the same settings as one JSON document (used by the service). The full
schema and the supervisor contract (JSON progress lines, cancellation, `--replay-info`) are in
[`WEB_GUI_GUIDE.md`](LazerRender.Game/WEB_GUI_GUIDE.md).

## Web service

```bash
dotnet run --project LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj --launch-profile http
```

Listens on `http://localhost:5080` (Swagger at `/swagger` when built in Debug; SPA at `/`). Runtime data
goes to `LazerRender.Service/src/LazerRender.Api/data/` (gitignored).

- **Login** via osu! OAuth v2, gated by an allowlist (`Admin:OsuUserIds`), or a one-shot
  `Admin:BootstrapToken` to claim a fresh instance.
- **Render queue** — upload an `.osr`, pick options, and a single serialized worker renders it with live
  progress. Job cards show the player and resolved map metadata; results download as
  `lazerrender-video.mp4`.
- **Skins & presets** — import `.osk` skins and save/reuse render presets.
- **Admin** — allow/revoke users, purge the shared library, a "Render PC" hardware summary, and live
  service/engine console logs. The logs are kept **in memory only**, redacted, and dropped when the panel
  closes; set `LAZERRENDER_DEBUG=1` for verbose capture.

## Deployment (quick guide)

A short version:

### Docker (Linux, macOS, Windows)

Identical commands everywhere (Docker Desktop on macOS/Windows; the container is `linux/amd64`):

```bash
# create a .env next to docker-compose.yml (its keys are listed in that file), then:
docker compose up -d --build               # host port 5180
```

GPU passthrough uses render nodes only (`/dev/dri/renderD*`), so pass the node for the card you want
(Linux/Windows with WSL2 GPU support; macOS has no GPU passthrough and can only run the service half).
Data and the Data Protection key ring live in named volumes (`/app/data`, `/app/keys`). See
`docker-compose.yml` for `.env` keys (`Osu__OAuth__ClientId`, `AllowedHosts`, `Proxy__KnownProxies`, …).

### Bare metal

**Linux (recommended for rendering):**

```bash
# runtimes + engine system deps
sudo apt install ffmpeg weston        # Debian/Ubuntu; adapt for your distro
# service
LazerRender.Service/scripts/publish-service.sh linux-x64
# then run the published binary under systemd (unit provided in LazerRender.Service/deploy/)
```

The render host needs the .NET 10 runtime, FFmpeg, Weston and GPU access; the service needs the ASP.NET
Core 10 runtime.

**Windows / macOS:**

```bash
dotnet run --project LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj
```

The service builds and runs anywhere with the .NET 10 SDK/runtime. Rendering with the engine on
Windows/macOS works in a desktop session but has not been tuned for those platforms — the supported
headless path is Linux. FFmpeg must be on `PATH`.

### Secrets

Never commit credentials. Supply the osu! OAuth client id/secret and any fallback tokens via environment
variables (or the `.env` used by compose). The Data Protection key ring encrypts every stored osu!
refresh token — keep it out of image layers and back it up if you care about existing sessions.

## Roadmap

Status at a glance:

| Phase | Theme | Status |
|---|---|---|
| 0–1 | Architecture spike; video & audio pipeline | ✅ done |
| 2 | Headless asset management (import/resolve/purge) | ✅ done |
| 3 | Rendering polish & visual toggles | ✅ done |
| 4 | Hardware acceleration & optimization (4K120) | ✅ done |
| 5 | The web API daemon | ✅ done |
| 6 | Render & web UX refinements | ✅ done |
| 7 | Security audit & hardening | ✅ done |
| 8 | Docker, observability & release | ✅ done |
| 9 | New features (browser replay viewer, strain graph, Discord bot) | ⬜ planned |

Recent Phase 8 work: a single container image + compose stack, an in-memory logging/instrumentation core,
an admin observability panel, a maintenance runbook, and a **.NET 10 rebase** of the pinned osu! engine
(tachyon `2026.918.0`).

## Documentation

- [`WEB_GUI_GUIDE.md`](LazerRender.Game/WEB_GUI_GUIDE.md) — the CLI/supervisor contract for building on top.

## License

MIT — see [`LICENSE`](LICENSE).
