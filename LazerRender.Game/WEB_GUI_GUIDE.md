# WEB_GUI_GUIDE.md — LazerRender integration contract for the web GUI

This document is the complete, minimal contract for building a web GUI / daemon on top of LazerRender.
It describes only what you need to drive the recorder, not its internals.

---

## 1. The model

LazerRender performs **exactly one operation per invocation**. You drive it by spawning the binary
with one command plus options. There is no long-running daemon inside LazerRender itself — the
daemon/GUI is the component you are building.

Three kinds of operations:

1. **Render a replay** — `--replay <path.osr>` → produces `output.mp4` in the output directory.
2. **Import assets** — `--import-map <path.osz>` / `--import-skin <path.osk>` into the persistent
   storage.
3. **Purge assets** — `--purge <beatmaps|skins|all>` to reclaim disk space.

---

## 2. Invocation

Run the built DLL (or via `scripts/run-headless.sh`, which wraps it in a headless Weston
compositor — required when there is no desktop session):

```bash
# direct (requires a GPU + Wayland/X11 session)
dotnet LazerRender.Game/bin/Debug/net10.0/LazerRender.dll <args>

# headless server
scripts/run-headless.sh <args>
```

All arguments after `run-headless.sh` pass through unchanged.

### Command-line verbs

| Verb | Purpose |
|---|---|
| `--replay <path.osr>` | render a replay |
| `--replay-info <path.osr>` | print replay + beatmap metadata as JSON (size estimation, job details) |
| `--import-map <path.osu|osz>` | import a beatmap |
| `--import-skin <path.osk>` | import a skin |
| `--purge <beatmaps|skins|all>` | purge assets |
| `--map-info <md5>` | print beatmap metadata for an MD5 hash as JSON |

Exactly one verb is required.

### Instance-level options (set by the daemon, not per render)

| Flag | Meaning |
|---|---|
| `--storage <dir>` | persistent storage dir (Realm DB + files) — keep one per instance |
| `--output <dir>` | where `output.mp4` is written |
| `--encoder <cpu|amd|nvidia|intel>` | FFmpeg encoder backend (default `cpu`) |
| `--download-missing` | auto-download the beatmap when its hash is not in the DB |

### Per-render options (can be exposed to end users)

| Flag | Meaning |
|---|---|
| `--width <px>` / `--height <px>` | output resolution (default 1280×720) |
| `--fps <n>` | output frame rate (default 60) |
| `--duration <sec>` | clip length (omit = full replay + 5 s results tail) |
| `--skin <name>` | skin by name (omit = built-in osu! "argon" pro skin) |
| `--motion-blur <n>` | FFmpeg `tmix` frame count (0 = off, 3 = light, 5 = heavy) |
| `--hud-scale <n>` | extra UI scale (default 1.0) |
| `--disable-result-screen` | fade to black at the end of the replay instead of showing the results screen |
| `--skip-intro` / `--no-skip-intro` | skip the beatmap's intro and start at the first object (default: `no-skip-intro`, so the intro before the first object is played like the audio track's opening) |
| `--avatar-api-key <key>` | osu! API v2 token for the player avatar (public endpoint; a client-credentials token is fine) |
| `--osu-user-token <token>` | osu! API v2 **user** access token that signs the engine in, enabling online beatmap leaderboards / the `scoreboard` element (needs the `public` scope; see §6) |
| `--osu-user-token-expires-in <sec>` | validity of `--osu-user-token` (default 3600) |

---

## 3. Per-render settings: use `--render-config`

Instead of constructing a long flag string, pass one JSON document:

```bash
scripts/run-headless.sh --replay <path.osr> --output out --storage storage \
    --render-config /path/to/config.json
```

Use `--render-config -` to read the JSON from stdin.

### JSON schema

A single JSON object. All keys are optional; omitted keys use the defaults below.

```json
{
  "fps": 60, "width": 1920, "height": 1080,
  "dimLevel": 0.7, "blurLevel": 0.0, "parallax": 1.0,
  "storyboard": true, "video": true,
  "beatmapSkins": true, "beatmapColours": true, "beatmapHitsounds": true,
  "comboColourNormalisation": 0.2,
  "hudVisibility": "always",
  "snakingIn": true, "snakingOut": true, "hitAnimations": true, "hitLighting": false,
  "starFountains": false,
  "cursorTrail": true, "cursorRipples": false, "cursorSize": 1.0,
  "playfieldBorder": "none",
  "showClickMarkers": false, "showFrameMarkers": false,
  "showCursorPath": false, "hideGameplayCursor": false,
  "replayAnalysisLength": 800,
  "motionBlur": 0,
  "disableResultScreen": false,
  "skipIntro": false,
  "leaderboardScope": "global",
  "hud": ["hp", "combo", "score", "hiterror"]
}
```

### Key reference

| Key | Type | Range / values | Default |
|---|---|---|---|
| `fps` | int | `30` / `60` / `90` / `120` | 60 |
| `width` / `height` | int | `1280x720` / `1920x1080` / `2560x1440` / `3840x2160` | 1280 / 720 |
| `dimLevel` | float | 0..1 | 0.7 |
| `blurLevel` | float | 0..1 | 0 |
| `parallax` | float | 0..2 | 1 |
| `storyboard` | bool | — | true |
| `video` | bool | — | true |
| `beatmapSkins` | bool | — | true |
| `beatmapColours` | bool | — | true |
| `beatmapHitsounds` | bool | — | true |
| `comboColourNormalisation` | float | 0..1 | 0.2 |
| `hudVisibility` | enum | `never` / `hiddengameplay` / `always` | `always` |
| `snakingIn` / `snakingOut` | bool | — | true |
| `hitAnimations` | bool | — | true |
| `hitLighting` | bool | — | false |
| `starFountains` | bool | — | false |
| `cursorTrail` | bool | — | true |
| `cursorRipples` | bool | — | false |
| `cursorSize` | float | 0.1..2 | 1 |
| `playfieldBorder` | enum | `none` / `corners` / `full` | `none` |
| `showClickMarkers` | bool | — | false |
| `showFrameMarkers` | bool | — | false |
| `showCursorPath` | bool | — | false |
| `hideGameplayCursor` | bool | — | false |
| `replayAnalysisLength` | int | 200..2000 | 800 |
| `motionBlur` | int | 0..32 (0 = off) | 0 |
| `disableResultScreen` | bool | — | false |
| `skipIntro` | bool | — | false |
| `leaderboardScope` | enum | `global` / `country` / `friend` / `team` (`country`/`friend` need supporter, `team` needs a team; `global` always works) | `global` |
| `hud` | string[] | any of the HUD component keys (below); omit to show everything, `[]` to hide every HUD element | omitted |

Individual flags also exist for direct CLI use (booleans `--<flag>` / `--no-<flag>`, numeric/enum
`--<flag> <value>`); the JSON keys above map 1:1 to those flags. See `--help` for the exact flag
names.

### HUD whitelist (`hud`)

Omitting `hud` renders every HUD component the active skin exposes. Supplying it switches to
**whitelist mode**: only the listed components are shown and every other HUD component is hidden.
This is the robust way to strip a custom skin down to a known set of elements, because it also
removes skin-specific components (extra counters, star difficulty readouts, decorative boxes, ...).

The bundled SPA presents this as an all-on checklist with a master toggle, so it omits `hud` while
every box is ticked and otherwise sends the still-ticked keys — meaning unticking any element also
removes skin-specific decorations. Sending an explicit empty array hides the whole HUD.

Valid keys, in display order:

```
hp, combo, score, keyoverlay, accuracy, pp, hiterror, song-progress,
unstable-rate, judgements, mods, aim-error, rank, longest-combo, scoreboard,
bpm, cps, player-name, avatar, flags, spectators, cosmetic
```

| Key | Lazer component(s) | o!rdr equivalent |
|---|---|---|
| `hp` | health display | Show HP Bar |
| `combo` | combo counter (incl. legacy) | Show Combo Counter |
| `score` | gameplay score counter | Show Score |
| `keyoverlay` | key counter display | Show Key Overlay |
| `accuracy` | accuracy counter | — |
| `pp` | performance points counter | Show PP Counter |
| `hiterror` | hit error meter | Show Hit Error Meter |
| `song-progress` | song progress bar | — |
| `unstable-rate` | unstable rate counter | Show Unstable Rate |
| `judgements` | hit counter (100/50/miss) | Show Hit Counter (100, 50, misses) |
| `mods` | mod display | Show Mods |
| `aim-error` | aim error meter (hit position relative to the aim direction) | Show Aim Error Meter |
| `rank` | rank (SS/S/A/...) display | — |
| `longest-combo` | longest combo counter | — |
| `scoreboard` | gameplay leaderboard | Show Scoreboard |
| `bpm` | BPM counter | — |
| `cps` | clicks-per-second counter | — |
| `player-name` | player name | — |
| `avatar` | player avatar | Avatars on Scoreboard |
| `flags` | country/team flags | — |
| `spectators` | spectator list | — |
| `cosmetic` | decorative elements (Argon wedge pieces, boxes, text elements, beatmap attribute text, generic sprites) | — |

o!rdr settings with no lazer HUD counterpart (Strain Graph, Slider Breaks) are not listed. o!rdr's
"Borders" maps to the existing `playfieldBorder` setting.

When no skin is requested at all, the recorder uses the built-in osu! "argon" pro skin as its default
(argon without the x300 hit popups) rather than plain argon. `--skin` matches a skin's canonical name
(the `skin.ini` `Name`, which may differ from the archive/file name); the web service stores that
canonical name on import, and the engine also accepts the archive form `Name (Creator)` for skins
exported by lazer.

---

## 4. Progress and status

During a render, LazerRender writes one JSON object per line to **stdout**:

```json
{"type":"progress","phase":"PARSING","frame":0,"total":null,"fps":0}
{"type":"progress","phase":"RENDERING_FRAMES","frame":60,"total":450,"fps":123.4}
{"type":"progress","phase":"RESULTS_TAIL","frame":450,"total":450,"fps":122.0}
{"type":"progress","phase":"FINALIZING","frame":450,"total":450,"fps":122.0}
{"type":"progress","phase":"DONE","frame":450,"total":450,"fps":122.5}
```

- `phase` order: `PARSING` → `RENDERING_FRAMES` → `RESULTS_TAIL` → `FINALIZING` → `DONE`.
- `frame` = frames rendered so far. `total` = expected total, or `null` when unbounded
  (no `--duration`).
- `fps` = current render throughput (wall-clock, so >1× realtime is normal).

Parse stdout line by line; ignore non-JSON lines (they are human logs).

### Cancellation

Send `SIGINT` or `SIGTERM` to abort a job. LazerRender cancels its internal token and kills the
FFmpeg child process cleanly.

### Exit codes

- `0` — success.
- `2` — invalid arguments (missing file, unknown flag, bad purge target, etc.).

---

## 5. Recommended architecture for the daemon

- **One persistent `--storage` directory per instance.** It accumulates imported beatmaps, skins and
  the Realm database across renders.
- **Render flow per request:**
  1. Accept the `.osr` upload, save it to a temp path.
  2. Build the `--render-config` JSON from the GUI's per-render settings.
  3. Spawn `scripts/run-headless.sh --replay <tmp.osr> --output <jobdir> --storage <storage>
     --render-config <json> [--download-missing] [--encoder auto-detected]`.
  4. Stream/parse stdout progress lines to report progress to the user.
  5. On `DONE`, serve `<jobdir>/output.mp4`.
- **Asset management endpoints** map to `--import-map`, `--import-skin`, `--purge`.
- **Encoder**: auto-detect at startup (VAAPI → NVENC → QSV → `cpu`) rather than asking the user; keep
  `--encoder` as a server-side override.
- **Download missing beatmaps**: enable `--download-missing` for unattended operation. When a
  replay's beatmap hash is not in the DB, LazerRender downloads and imports it automatically
  (osu.direct, falling back to catboy.best).
- **Size guard**: before starting a render, call `--replay-info <path.osr>` to learn the replay's
  natural duration and speed, estimate the output size, and reject requests that exceed the internal
  budget (default: one hour of 1080p60). The JSON contract is:

  ```json
  {"found":true,"beatmapMd5":"...","durationSeconds":38.99,"rate":1.0,
   "title":"...","artist":"...","creator":"...","version":"...","stars":6.22,
   "songLengthSeconds":184.0,"mods":"HDDT","accuracy":0.9935}
  ```

  `found:false` means the beatmap is not in the DB (the daemon should download it first). The
  service rejects oversized jobs with the terminal `Rejected` status and a clear hint. The extra
  fields (`title`/`artist`/`creator`/`version`/`stars`/`songLengthSeconds`/`mods`/`accuracy`) feed
  the job's extended view; `mods` is the concatenated mod acronyms (empty for NM) and `accuracy` is
  a 0..1 fraction.

---

## 6. Avatars and online leaderboards (optional)

Both features use the osu! API v2 and both are optional — without a token, renders still succeed with
a placeholder avatar and an offline (local-only) scoreboard.

### Avatars

The replay's own avatar is fetched from the public `GET /api/v2/users/{user}` endpoint, so **any**
token works, including a client-credentials one (`--avatar-api-key`, or `OSU_API_KEY`):

```bash
OSU_OAUTH_CLIENT_ID=<id> OSU_OAUTH_CLIENT_SECRET=<secret> scripts/fetch-bearer-token.sh
```

Note that a token is not strictly required: lazer `.osr` replays embed the player's `user_id`, and
`https://a.ppy.sh/{id}` resolves the avatar without an API call. The API lookup is only needed to
resolve a player whose id the replay does not carry.

### Online leaderboards (scoreboard)

The results-screen scoreboard and the `scoreboard` HUD element are populated by lazer's
`LeaderboardManager`, which refuses to fetch unless lazer's API provider is signed in. lazer signs in
by validating the token against `GET /api/v2/me`, which is `requires user` — a client-credentials
token is guest-scoped and **cannot** satisfy that check. A **user** token is therefore required.

**When a daemon is involved, you need to configure nothing.** Each render is signed in with the
identity of the player who queued it, reusing the refresh token the service already stores when that
player signs in through the web UI (`UserOsuTokenService`). The scope request must include `public`
(see below), or lazer will sign in but the leaderboard fetch will fail.

Without a daemon (engine only), pass a user access token directly:

```bash
scripts/run-headless.sh --replay replay.osr --osu-user-token <access-token>
```

To mint one, authorize in a browser as whoever owns the account and exchange the code — no second
account is required if you use your own:

```bash
# https://osu.ppy.sh/oauth/authorize?client_id=<id>&redirect_uri=<callback>&response_type=code&scope=identify+public&state=lazerrender
OSU_OAUTH_CLIENT_ID=<id> OSU_OAUTH_CLIENT_SECRET=<secret> \
OSU_OAUTH_REDIRECT_URI=<callback> OSU_OAUTH_CODE=<code> \
scripts/fetch-user-token.sh
```

As a fallback for jobs whose owner has no usable stored credential, the daemon also accepts
`Renderer:OsuBotRefreshToken` (a refresh token, refreshed on demand) or `Renderer:OsuBotToken`
(a ready-made access token).

The beatmap also needs a populated online id; the service sets the in-memory ranked status when a
map was imported from a bare `.osu`/`.osz` (which carries no online status).

---

## 7. Environment variables

| Variable | Purpose |
|---|---|
| `OSU_API_KEY` | fallback avatar token (same as `--avatar-api-key`) |
| `LAZERRENDER_MESA_DRIVER` | force a Mesa driver (e.g. `radeonsi`, `iris`) in headless mode |
| `LAZERRENDER_FLATFILL` | `1` = diagnostic flat-fill render (cull the scene) |
| `OSU_OAUTH_CLIENT_ID` / `OSU_OAUTH_CLIENT_SECRET` | credentials for the token helper scripts |
| `OSU_OAUTH_REDIRECT_URI` / `OSU_OAUTH_CODE` | authorization-code inputs for `fetch-user-token.sh` |

---

## 8. Minimal example

```bash
cat > /tmp/render.json <<'EOF'
{
  "fps": 60, "width": 1920, "height": 1080,
  "dimLevel": 0.8, "hudVisibility": "always",
  "cursorRipples": true, "motionBlur": 3,
  "hud": ["hp", "combo", "score", "accuracy", "pp", "hiterror"]
}
EOF

scripts/run-headless.sh \
  --replay /tmp/uploaded_replay.osr \
  --output /tmp/jobs/job-123 \
  --storage storage \
  --render-config /tmp/render.json \
  --download-missing
```

On success, the video is at `/tmp/jobs/job-123/output.mp4`.
