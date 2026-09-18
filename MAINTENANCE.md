# MAINTENANCE.md

**What this is.** The routine-workflow runbook for LazerRender: how to move the pinned osu!lazer
("tachyon") submodule, how to repair what that breaks, how to diagnose a render without an IDE, and what
to do when osu!'s public API changes underneath us.

**Who it is for.** Whoever is holding the pager (or the terminal) when a render fails. It assumes you
have read enough of [`ARCHITECTURE.md`](ARCHITECTURE.md) to know the engine (`LazerRender.Game`) from
the service (`LazerRender.Service`).

**Where things live.** The fragile coupling points are inventoried inline in §5.4–§5.5 so this runbook is
self-contained. The security model is [`SECURITY.md`](SECURITY.md); the file-by-file architecture is
[`ARCHITECTURE.md`](ARCHITECTURE.md).

---

## 1. The two halves, and what "maintenance" means here

```
LazerRender.Game/      the recorder (engine)   net10.0, pins extern/osu (tachyon)
LazerRender.Service/   the web API + SPA       ASP.NET Core 10
```

Two independent failure classes:

1. **The pin moves** (a new `*-tachyon` tag). This is a *code* maintenance event: the engine compiles
   against real osu!lazer internals, so a bump can break the build *or* silently disable a feature.
   §4–§5 are about this.
2. **osu!'s servers change** (OAuth rules, API shapes, leaderboard gating, beatmap mirrors). This needs
   no new submodule but breaks a feature at runtime. §6 is about this.

Everything else (deploys, key ring, backups) is [`LazerRender.Service/DEPLOYMENT.md`](LazerRender.Service/DEPLOYMENT.md).

---

## 2. debug vs release, and when to use which

This is the first thing to get right when reproducing a bug: **the render host usually runs Release,
but a development checkout runs Debug, and the two differ in what they log.**

| Path | How it is built | Compile-time `LAZERRENDER_DEBUG` | Effect |
|---|---|---|---|
| `dotnet run` via `run-headless.sh` (default) | **Debug** | defined | `DebugInstrumentation.Log(...)` lines are present |
| `LAZERRENDER_CONFIGURATION=Release run-headless.sh` | **Release** | not defined | those lines are compiled out |
| Prebuilt `LAZERRENDER_ENGINE` (the container) | **Release** publish | not defined | as above |
| Service (`dotnet run` / publish) | Debug / Release | follows config | service side has no `[Conditional]` calls of note |

Definitions and the runtime switch:

- `LAZERRENDER_DEBUG` is set for Debug builds by the repository-root
  [`Directory.Build.props`](Directory.Build.props:1). Force it into a Release build with
  `-p:LazerRenderDebug=true`.
- **Runtime** verbosity is `LAZERRENDER_DEBUG=1`. It is read once into
  [`DebugMode.Enabled`](LazerRender.Service/src/LazerRender.Api/Services/Logging/DebugMode.cs:11)
  (service) and `DebugInstrumentation.RuntimeEnabled` (engine), and lowers the in-memory log buffers to
  Debug. It also turns on `set -x` in [`run-headless.sh`](LazerRender.Game/scripts/run-headless.sh:15).
- A Debug build defaults to verbose; `LAZERRENDER_DEBUG=0` turns that back off without a rebuild.
- **A Release binary cannot show `[Conditional]` engine lines even with `LAZERRENDER_DEBUG=1`** — they
  were removed by the compiler. If you need them, build Debug.

Practical commands:

```bash
# Service, development
dotnet run --project LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj --launch-profile http

# Engine, Debug (verbose instrumentation present)
LazerRender.Game/scripts/run-headless.sh --replay LazerRender.Game/tests/replay_nm_short.osr \
    --output out --storage LazerRender.Game/storage --fps 60 --width 1280 --height 720

# Engine, Release (what production runs); instrumentation compiled out
LAZERRENDER_CONFIGURATION=Release LazerRender.Game/scripts/run-headless.sh ...same args...

# Verbose runtime logging on either half
LAZERRENDER_DEBUG=1 LazerRender.Game/scripts/run-headless.sh ...same args...
LAZERRENDER_DEBUG=1 dotnet run --project LazerRender.Service/src/LazerRender.Api/LazerRender.Api.csproj
```

`dotnet build LazerRender.Game/LazerRender.Game.csproj -c Debug` and `-c Release` are both expected to
be clean; run both before trusting a change (see §4).

---

## 3. Reading the logs (diagnose without an AI model)

### 3.1 Where the logs are

- **In the app:** the admin tab → **Console logs** (service and engine, one stream at a time) and
  **Render PC**. The panel polls only while it is open and empties a stream when closed, so open it
  *before* reproducing, or reproduce and then open it during the render.
- **From a shell:** the service's own stdout/stderr (systemd journal or `docker logs`), and the engine's
  framework log files under the engine storage directory (`storage/`).
- **The engine's stdout is a contract, not a log:** line-delimited JSON progress
  (`PARSING → RENDERING_FRAMES → RESULTS_TAIL → FINALIZING → DONE`). Non-JSON stdout and all stderr are
  what the service forwards as the *engine* stream.

### 3.2 How lines are classified

[`EngineLogForwarder`](LazerRender.Service/src/LazerRender.Api/Services/Logging/EngineLogForwarder.cs:14)
tags each engine line and the tag is what the panel colours:

| Marker hit | Severity | Meaning |
|---|---|---|
| `error`, `exception`, `failed`, `failure`, `unhandled`, `fatal`, `crash`, `denied` | **Warning** | something went wrong |
| `Leaderboard:`, `osu! API login:`, `Avatar:`, `Replay complete;`, `Recorded `, `treating it as ranked`, `HUD visibility`, `HudVisibilityFilter`, `failure` | **Information** | notable, expected events |
| anything else | **Debug** | framework chatter |

Every line is **redacted** (known credentials + `Bearer …`) and length-capped before it is stored. If
you are adding a log line that could contain a secret, run it through
[`LogRedactor`](LazerRender.Service/src/LazerRender.Api/Services/Logging/LogRedactor.cs:20) rather than
logging it directly.

### 3.3 Symptom → where to look

| Symptom | Most likely cause | What to look for |
|---|---|---|
| Black or garbled frames; "FBO size shifted mid-render" | capture/readback (`CaptureContainer`, `FrameSink`) broke against a framework bump | engine stream, Warning; `CaptureContainer.logFboDiagnostics` one-shot line (renderer backend + FBO format) |
| Render hangs on `frame:0`, no frames | FFmpeg build mismatch (Debian's FFmpeg 5.1 deadlocks without `-analyzeduration 0 -probesize 32`) | engine stream; FFmpeg `fps=`/`speed=` telemetry from `FrameSink.startStderrTelemetry` never appears |
| Hitsounds missing but music plays | `HitsoundMixer` reflection target moved → `IsAvailable` false | engine stream, Warning about the sample mixer |
| Rendering is non-deterministic run to run | the manual-clock attach failed | engine stream, "could not locate `FramedBeatmapClock`" |
| Avatars show the guest placeholder | `DrawableAvatar` online-id rule or the asset store moved | engine stream, `Avatar:` lines |
| Scoreboard empty / "NotLoggedIn" | token invalid, missing `public` scope, or the dev-endpoint regression | engine stream, `Leaderboard:` / `osu! API login:` lines; also check the service startup warning about scopes |
| A HUD element will not hide (or hides the wrong one) | `HudVisibilityFilter.matches` type matching drifted | engine stream, `HudVisibilityFilter` lines |
| Fails to start under Weston | compositor flags / `XDG_RUNTIME_DIR` | run with `LAZERRENDER_DEBUG=1` to get `set -x`; Weston output is in its log |

The engine deliberately **degrades quietly**: most class-(B) breakages log one warning and continue with
the feature off. Treat any new `Warning` in the engine stream after a bump as a possible regression even
if the render "succeeded".

### 3.4 Ad-hoc diagnostics already built in

| Diagnostic | Where | Use it for |
|---|---|---|
| One-shot FBO/renderer line | `CaptureContainer.logFboDiagnostics` (once per render) | Renderer backend, FBO colour format, reflected vsync/tearing state |
| Flat-fill capture mode | `LAZERRENDER_FLATFILL=1` | Isolate readback/encode cost from scene-render cost |
| FFmpeg throughput telemetry | `FrameSink.startStderrTelemetry` | Encoder `fps=`/`speed=`, hardware-vs-software fallback |
| Mesa driver override | `LAZERRENDER_MESA_DRIVER` (via `run-headless.sh`) | Force `radeonsi`/`iris`/`zink` choice |
| Vsync suppression | `LazerRenderGameHost` (`vblank_mode=0`, `__GL_SYNC_TO_VBLANK=0`) | Wayland compositor pacing an offscreen loop |
| Leaderboard/API diagnosis helpers | `LazerRenderGame.describeApiEndpoint`, `describeSignedInUser`, `describeLeaderboardFailure`, `waitForApiLoginAsync` | Explaining a scoreboard failure instead of masking it |
| Render PC + Console logs | admin tab (Phase 8.3) | Host summary and the two live log streams |

---

## 4. Tachyon re-pin runbook

The pin is a git submodule, not a NuGet package, so a bump is a source change. `README.md` explains the
policy (tachyon is an opt-in pre-release stream; every re-pin is a regression event).

### 4.1 Preconditions

```bash
dotnet --version                       # must satisfy the tag's global.json
git -C LazerRender.Game/extern/osu describe --tags   # current pin
```

Check the candidate tag's toolchain **before** checking it out:

```bash
git -C LazerRender.Game/extern/osu fetch --depth 1 origin tag <TAG>
git -C LazerRender.Game/extern/osu show <TAG>:global.json | grep version
git -C LazerRender.Game/extern/osu show <TAG>:osu.Game/osu.Game.csproj | grep TargetFramework
```

### 4.2 Worked example — the .NET 8 → .NET 10 migration (2026-09-18)

The pin was moved from `2026.821.0-tachyon` to `2026.918.0-tachyon`. This was a **toolchain migration**,
not a routine bump: the stream crossed .NET versions between those tags.

```
pin   2026.821.0-tachyon : TargetFramework net8.0  : global.json 8.0.100  : ppy.osu.Framework 2026.807.0
      2026.909.0-tachyon : TargetFramework net10.0 : global.json 10.0.100 : ppy.osu.Framework 2026.901.0
      2026.911.0-tachyon : TargetFramework net10.0 : global.json 10.0.100 : ppy.osu.Framework 2026.901.0
      2026.918.0-tachyon : TargetFramework net10.0 : global.json 10.0.100 : ppy.osu.Framework 2026.917.0
```

A first attempt against `2026.918.0-tachyon` with only SDK 8 installed failed before any LazerRender code
compiled, for all five submodule projects:

```
error NETSDK1045: The current .NET SDK does not support targeting .NET 10.0.
```

What the successful migration actually took:

1. Install a .NET 10 SDK **and the ASP.NET Core 10 shared runtime**. The SDK alone is not enough to *run*
   the web half: `dotnet test` aborts with `app-launch-failed` until `Microsoft.AspNetCore.App 10.x` is
   installed alongside `Microsoft.NETCore.App 10.x`.
2. Retarget `LazerRender.Game`, `LazerRender.Contracts`, `LazerRender.Api` and the test project to
   `net10.0`.
3. Bump the service packages in `LazerRender.Service/Directory.Packages.props`: EF Core `10.0.12`,
   test SDK `18.10.1`, xunit `2.9.3`, xunit.runner.visualstudio `3.1.5` (Swashbuckle stays Debug-only).
   Restore rewrites the `packages.lock.json` files.
4. Point the container at the .NET 10 images — which are **Ubuntu 24.04 (noble)**, not Debian bookworm.
   The apt block moved to `libasound2t64` and the `bookworm-backports` workaround was dropped (noble's
   Weston 13 already serves the headless path); see `DEPLOYMENT.md` §11 for the GPU/Mesa caveat.
5. **Local SDK gotcha:** a standalone .NET 10 SDK whose package-pruning data is incomplete fails the web
   project with `NETSDK1226`. The root `Directory.Build.props` sets `AllowMissingPrunePackageData` for
   that case.

**Outcome:** the engine builds clean in Debug and Release against `2026.918.0-tachyon`; a 38 s test
replay rendered to a valid MP4 with no degraded-path warnings; the service suite is 114/114. **No tachyon
API breakage (class A/B/C) was hit** — the couplings in §5.4 did not drift.

### 4.3 The mechanics of a bump that *is* compatible

```bash
# 1. Fetch and check out the tag in the submodule
git -C LazerRender.Game/extern/osu fetch --depth 1 origin tag <TAG>
git -C LazerRender.Game/extern/osu checkout <TAG>

# 2. Build both configurations and the test suite
dotnet build LazerRender.Game/LazerRender.Game.csproj -c Debug
dotnet build LazerRender.Game/LazerRender.Game.csproj -c Release
dotnet test  LazerRender.Service/LazerRender.Service.sln -c Debug

# 3. Smoke a render (see §4.4)

# 4. Record the new gitlink in the super-project
git add LazerRender.Game/extern/osu
git commit -m "Phase 4.x: re-pin osu! to <TAG>"
```

### 4.4 Feature smoke matrix (the silent-failure checklist)

A green build proves nothing about class-(B)/(C) coupling. Run one short render and confirm each of
these, reading the **engine** stream:

- [ ] Render completes and produces a playable `.mp4` (`DONE` phase).
- [ ] Hitsounds are audible (otherwise `HitsoundMixer` dropped out).
- [ ] Rendering is deterministic across two runs within tolerance (otherwise the manual clock broke).
- [ ] A skin applies (`--skin`), and an unknown skin falls back to argon pro.
- [ ] Avatars resolve for a replay with a real user id (not the guest placeholder).
- [ ] With `--osu-user-token` (or the service's user token), the scoreboard populates.
- [ ] Every requested HUD element hides/shows correctly, including `aim-error` vs `hiterror`.
- [ ] The results-screen tail records, and `--disable-result-screen` fades to black.
- [ ] No unexpected `Warning` in the engine stream.

Renders are **not** byte-reproducible (~1% playfield drift), so use a control run and a tolerance rather
than an exact hash.

### 4.5 Harness checklist (non-tachyon files that must move with a bump)

- [`LazerRender.Game/LazerRender.Game.csproj`](LazerRender.Game/LazerRender.Game.csproj:1) — the five
  `ProjectReference`s must match the submodule layout.
- [`.gitmodules`](.gitmodules) — path/URL unchanged unless upstream moves.
- `README.md` "Pinning" section — update the tag in the prose.
- Re-derive the dependency closure (`dotnet list LazerRender.Game/LazerRender.Game.csproj package
  --include-transitive`) and update [`SECURITY.md`](SECURITY.md) §6 with the new commit/tag.

---

## 5. Repairing breakage caused by a tachyon bump

Three classes, in increasing order of nastiness. The inventory of known fragile points is §5.4 below;
this is the repair procedure.

### Class A — compile-time references (build breaks; easy)

The build tells you where. Fix the call site to the new API. The named coupling points are listed in
§5.4. Watch especially:

- The settings catalog ([`SettingDescriptor.cs`](LazerRender.Game/SettingDescriptor.cs:1)): `OsuSetting` /
  `OsuRulesetSetting` members. A rename breaks the build, but **inversion semantics** (e.g.
  `PreferNoVideo` with `inverted: true`) can change silently — re-verify the setting's direction.
- `UseDevelopmentServer` must stay pinned `false` ([`LazerRenderGame.cs`](LazerRender.Game/LazerRenderGame.cs:73));
  if the property is renamed away, debug builds point at `dev.ppy.sh` and tokens 401 silently.

### Class B — reflection into private/internal members (breaks silently)

Each site is coded defensively and logs a warning, so the build stays green and the *feature* disappears.
Re-discover the target with the runtime, not with guesswork:

1. Reproduce with `LAZERRENDER_DEBUG=1` and read the engine stream for the feature's warning.
2. In a scratch build, enumerate candidates at the failure point, e.g.
   `type.GetMembers(BindingFlags.NonPublic | BindingFlags.Instance)` — the project's pattern is
   `<Name>k__BackingField` for auto-properties.
3. Update the reflected name/shape. Known sites (from §5): `HitsoundMixer` (mixer handle, active mixers),
   `ReplayRecorderPlayer.attachGameplayClockToManualClock` (`GameplayClock`, `interpolatedTrack`),
   `ReplayRecorderPlayer.getPendingTextureUploadCount` (`textureUploadQueue`), `ExtendedResultsScreen`
   (`displayWithFlair`), and `CompositeDrawable.InternalChildren` used by both `HudVisibilityFilter` and
   `ExtendedResultsScreen`.

### Class C — type-name / heuristic matching (breaks silently)

[`HudVisibilityFilter.matches`](LazerRender.Game/HudVisibilityFilter.cs:168) matches lazer component
types by name, with deliberate base-type exclusions (e.g. `ComboCounter and not LongestComboCounter`,
`HitErrorMeter and not AimErrorMeter`). If lazer re-parents a component, the exclusion inverts and the
wrong thing is hidden. Re-check the 22 HUD keys against the current type tree after any bump.

### Cross-boundary vocabulary duplication (no compiler will catch it)

`RenderConfigValidator.HudComponentKeys` and `HudVisibilityFilter.AllKeys`, and
`RenderConfigValidator.LeaderboardScopeValues` and `LeaderboardScope.cs`, are **hand-maintained and
duplicated**. A new HUD key or scope must be added in **both** halves or the service rejects a valid
config. Grep both files when adding a key.

### External mirrors (not tachyon, same class of fragility)

`LazerRenderGame.downloadBeatmapAsync` parses `beatmapset_id` from osu.direct and `ParentSetID` from
catboy.best. A mirror can change its payload shape independently of any release; the engine logs each
mirror failure and only throws when both fail.

### 5.4 Fragility inventory (the rebase checklist)

Ordered roughly by risk (silent failure × importance). Line numbers drift; match on the symbol.

| # | File / symbol | What is fragile | Why it matters |
|---|---|---|---|
| 1 | `HitsoundMixer` | Reflection: `SampleMixer.Handle` (+ `<Handle>k__BackingField`), `AudioManager.ActiveMixers` (+ `activeMixers`) | Any rename ⇒ `IsAvailable` false ⇒ hitsounds silently stop mixing. |
| 2 | `HudVisibilityFilter.matches` | Type matching for ~22 HUD keys (`ArgonWedgePiece`, `BigBlackBox`, `BoxElement`, `TextElement`, `BeatmapAttributeText`, `SkinnableSprite`, `DrawableGameplayLeaderboard`, `BarHitErrorMeter`, `AimErrorMeter`, `Argon*` counters, …) | Rename/re-parent ⇒ the wrong element is hidden or leaks into the capture; base-type exclusions can invert. |
| 3 | `ReplayRecorderPlayer.attachGameplayClockToManualClock` | `GameplayClockContainer.GameplayClock`, `FramedBeatmapClock.interpolatedTrack` (private) | If either moves, the manual clock stops driving gameplay ⇒ non-deterministic renders, no crash. |
| 4 | `LazerRenderGame.warmLeaderboardAsync` | `LeaderboardManager.FetchWithCriteria`, `.Scores.Value`, `LeaderboardScores.AllScores/.TotalScores/.FailState`, `LeaderboardCriteria(...)` | Newest, most API-shaped coupling; login-gating rules have changed before. |
| 5 | `LazerRenderGame.UseDevelopmentServer` | Override pins production endpoints | Must stay `false`; if renamed away, debug builds point at `dev.ppy.sh` and score fetches 401. |
| 6 | `ExtendedResultsScreen` | `ScorePanel.displayWithFlair` (private) | Rename ⇒ results-screen animations silently return (affects video output). |
| 7 | `ExtendedResultsScreen` + `HudVisibilityFilter` | `CompositeDrawable.InternalChildren` (reflected) | Core tree-walking primitive; if it stops resolving, HUD filtering and results-screen surgery degrade silently. |
| 8 | `ReplayRecorderPlayer.getPendingTextureUploadCount` | `renderer.textureUploadQueue` + `Count` | Used to pace the record loop; losing it reduces high-resolution safety. |
| 9 | `CaptureContainer` | `VerticalSync`/`AllowTearing` (diagnostic) | Benign if absent; the swap-state log line is omitted. |
| 10 | `CaptureContainer` + `FrameSink` | FBO readback + FFmpeg rawvideo expectations (`glReadPixels`, PBO, RGBA, exact WxH) | Framework-level; symptom is black/garbled frames or "FBO size shifted mid-render". |
| 11 | `ReplayRecorderPlayer` ctor | `Configuration.ShowLeaderboard`, `ShowFailingOverlay` | If renamed, the scoreboard element stops appearing. |
| 12 | `LazerRenderGame.applyOsuUserToken` | `OsuSetting.SavePassword`/`Token`, `OAuthToken.ToString()` | Compile-time names, but the **ordering** is load-bearing: `SavePassword` before `Token`, or the stored token is reset. |
| 13 | `LazerRenderGame` avatar path | `DrawableAvatar` requires `OnlineID > 1`; pre-warm via `OnlineAssetCachingStore.Get(url)`; `ScoreInfo.RealmUser.OnlineID` | If the threshold/store changes, avatars revert to the placeholder (previously a crash under texture load). |
| 14 | `SettingDescriptor` (`SettingsCatalog.Build`, `SettingsEngine`) | Every `OsuSetting`/`OsuRulesetSetting` member + `inverted` handling + reflective `SetValue<TValue>` | Build breaks loudly on renames, but **inversion/default semantics** (e.g. `PreferNoVideo`) can change silently. |
| 15 | `LazerRender.Service/.../Data/DatabaseInitializer.cs` `ColumnPatches` | Hand-maintained `ALTER TABLE ADD COLUMN` list + raw `CREATE TABLE presets` | Not tachyon-related: a new entity property must be added to EF **and** this list, or existing DBs silently lack the column. |
| 16 | `LazerRender.Game.csproj` + `.gitmodules` | The five submodule `ProjectReference` paths and the gitlink tag | A bump starts here; the reference list must match the submodule layout. |
| 17 | `LazerRenderGame` import paths | `BeatmapManager.Import`, `SkinManager.Import`, `ImportTask`, `Live<T>.PerformRead` | Used by `--import-map`, `--import-skin`, `--download-missing`; churn breaks asset management. |
| 18 | `LazerRenderGameHost` | `Host.GetSuitableDesktopHost(name, HostOptions{…})` | Framework host factory; the concrete SDL hosts are internal, so this is the only supported route. |
| 19 | `LazerRenderGame.downloadBeatmapAsync` | Mirror JSON keys (`beatmapset_id`, `ParentSetID`) | External mirrors change independently of tachyon. |
| 20 | `RenderConfigValidator` vs `HudVisibilityFilter`/`LeaderboardScope` | Duplicated HUD-key / scope / fps-resolution vocabularies | Cross-boundary: add to **both** halves or the service rejects a valid config. |

### 5.5 Historical breakages and the patterns behind them

These are the things that have actually bitten the project; use them as the seed for "what to watch":

1. **`UseDevelopmentServer`/debug endpoint selection** — a debug build defaulted to `dev.ppy.sh`, so a
   production user token 401s and leaderboards fail. *Pattern:* an override that defeats a debug-only
   heuristic; after a bump, verify the property still exists and is honoured.
2. **Reflection target drift in the audio path** — auto-properties compiled to `<Name>k__BackingField`;
   verify the compiler still emits the same name shape.
3. **Reflection target drift in the clock path** — the recorder logs "could not locate
   `FramedBeatmapClock`" and gameplay stops being manual-clock driven.
4. **Private texture-upload-queue accounting** — losing `textureUploadQueue.Count` drops the loop to a
   less safe pacing rule.
5. **`ScorePanel.displayWithFlair`** — rename ⇒ results-screen flair returns.
6. **`CompositeDrawable.InternalChildren`** — the tree-walking primitive behind HUD filtering and
   results-screen surgery.
7. **Online-leaderboard fetch shape** — signatures and login-gating semantics; the engine logs a
   diagnosis per fail state.
8. **HUD component type matching** — the base-type exclusions in §5.4 row 2 can invert.
9. **`OsuSetting` enum semantics** — compile-time names break the build, but inversion/defaults can
   change silently (e.g. `PreferNoVideo` is handled with `inverted: true`).

**Cross-cutting pattern:** everything in classes B/C fails *quietly by design*. A bump workflow must
therefore include a functional smoke test per feature (§4.4), not just a build. The engine's own log
emits a distinct warning for each degraded path — those warnings are the maintenance signal.

---

## 6. Repairing breakage from osu! API changes

No submodule bump needed; these break at runtime.

| Area | Where | What to check |
|---|---|---|
| OAuth scopes | `Osu:OAuth:Scopes`, `AuthService`, `Program.cs` startup warning | `public` must be present or leaderboards fail; a scope change requires users to sign in again |
| Token shapes | `UserOsuTokenService`, `OsuBotAuthService`, `LazerRenderGame.applyOsuUserToken` | user token ≠ client-credentials token; `/me` validation; the `SavePassword`-before-`Token` ordering |
| Leaderboard fetch | `LazerRenderGame.warmLeaderboardAsync` | `FetchWithCriteria` signature, `LeaderboardScores.FailState` handling; failures are diagnosed explicitly |
| Avatar loading | `LazerRenderGame` avatar path + `OnlineAssetCachingStore` pre-warm | `OnlineID > 1` gate; a moved store downgrades to the placeholder (historically a crash under texture load) |
| Beatmap mirrors | `downloadBeatmapAsync` | JSON keys per mirror |
| Dev vs prod endpoints | `LazerRenderGame.UseDevelopmentServer` | must remain `false` |

When an osu! API shape changes, prefer fixing the **engine** (it owns the API coupling) and leave the
`LazerRender.Contracts` DTOs alone unless the service itself talks to osu! (`OsuOAuthService`,
`AdminController` username lookup). After any fix, add or extend a service test if the logic lives in the
service, or a feature smoke item (§4.4) if it lives in the engine.

---

## 7. Routine operations

- **Deploy / configure / TLS / backups:** [`LazerRender.Service/DEPLOYMENT.md`](LazerRender.Service/DEPLOYMENT.md).
- **Secrets:** environment only. The Data Protection key ring (`keys/`, `0700`) is the master key for
  every stored refresh token — back it up encrypted, and never bake it into an image layer. See
  `DEPLOYMENT.md` §8/§12 and the Phase 7 audit.
- **Shared library hygiene:** the admin Library card purges beatmaps/skins through the engine's
  `--purge`; the engine's Realm is the source of truth.
- **Diagnostics inventory:** the admin Render PC card (encoder, FFmpeg build, GPU/driver, free space) is
  the first thing to read when a render behaves unlike any other host.

---

## 8. Verification commands (copy-paste)

```bash
# Builds
dotnet build LazerRender.sln -c Debug
dotnet build LazerRender.Game/LazerRender.Game.csproj -c Release
dotnet build LazerRender.Service/LazerRender.Service.sln -c Debug

# Tests (service suite; engine has no unit tests, only .osr fixtures)
dotnet test LazerRender.Service/LazerRender.Service.sln -c Debug

# One short render, verbose engine log
LAZERRENDER_DEBUG=1 LazerRender.Game/scripts/run-headless.sh \
    --replay LazerRender.Game/tests/replay_nm_short.osr --output out \
    --storage LazerRender.Game/storage --fps 60 --width 1280 --height 720 --encoder cpu

# Service startup smoke test (confirms the log pipeline + hosted services come up)
ASPNETCORE_URLS=http://127.0.0.1:5199 timeout 12 \
  dotnet LazerRender.Service/src/LazerRender.Api/bin/Debug/net10.0/LazerRender.Api.dll
```
