# PHASE_8_3_CONTEXT.md

**Purpose.** Hand-off after the Phase 8.3 **admin observability panel** shipped. It records what 8.3
built, the decisions a future prompt needs (especially for **8.4 `MAINTENANCE.md` + debug-workflow
docs** and the deferred release work in 8.5), and the gotchas found while building it. It does not
repeat material already in [`ROADMAP.md`](ROADMAP.md:284), [`ARCHITECTURE.md`](ARCHITECTURE.md:1219)
§3.6.5/§3.6.6, [`PHASE_8_LOGGING_CONTEXT.md`](PHASE_8_LOGGING_CONTEXT.md:1) or
[`MAINTENANCE_INFRA_CONTEXT.md`](MAINTENANCE_INFRA_CONTEXT.md:389).

**Status.** 8.3 implemented and verified: service builds clean, **114/114** tests pass (up from 99;
`AdminObservabilityTests` adds 15), and the host starts with the new hosted services and logs its
pipeline shape. Engine is untouched by 8.3.

---

## 1. What 8.3 built

| Concern | Location |
|---|---|
| Panel DTOs | [`AdminDtos.cs`](LazerRender.Service/src/LazerRender.Contracts/AdminDtos.cs:1) — `LogSnapshotDto`, `RenderPcDto` |
| Render PC collection | [`SystemInfoService.cs`](LazerRender.Service/src/LazerRender.Api/Services/SystemInfoService.cs:18) + [`SystemInfoWarmupService.cs`](LazerRender.Service/src/LazerRender.Api/Services/SystemInfoWarmupService.cs:11) |
| Log stream lifecycle | [`LogStreamService.cs`](LazerRender.Service/src/LazerRender.Api/Services/LogStreamService.cs:20) + [`LogRetentionService.cs`](LazerRender.Service/src/LazerRender.Api/Services/LogRetentionService.cs:14) |
| Endpoints | [`AdminController.cs`](LazerRender.Service/src/LazerRender.Api/Controllers/AdminController.cs:15) — `GET render-pc`, `GET logs`, `POST logs/close` |
| Registration | [`Program.cs`](LazerRender.Service/src/LazerRender.Api/Program.cs:220) |
| SPA | [`index.html`](LazerRender.Service/src/LazerRender.Api/wwwroot/index.html:230), [`app.js`](LazerRender.Service/src/LazerRender.Api/wwwroot/app.js:1), [`styles.css`](LazerRender.Service/src/LazerRender.Api/wwwroot/styles.css:348) |
| Tests | [`AdminObservabilityTests.cs`](LazerRender.Service/tests/LazerRender.Worker.Tests/AdminObservabilityTests.cs:23) |

The admin tab is now Queue/Library (row), Users, Render PC, Console logs. `#encoder-info` and
`loadCapabilities()` were removed; the admin card is fed by `loadRenderPc()` from the new endpoint.

---

## 2. The polling decision (read before touching the panel)

Roadmap §8.3 says "stream only while an admin has the panel open". The obvious transport is SignalR,
and `JobsHub` already exists — **but the SPA does not use SignalR at all** (jobs are polled; no
`signalr.js` is shipped, and the CSP keeps `script-src 'self'`, so a CDN is not an option). Shipping
the client would have meant vendoring a third-party script into `wwwroot`, which the project's
"zero-dependency, no build step" stance avoids.

So 8.3 uses a **server-side lease over REST**:

- `GET /api/v1/admin/logs?source=…&after=<seq>` returns only records newer than `after` and renews
  that stream's lease.
- `POST /api/v1/admin/logs/close?source=…` releases and empties a stream (omitting `source` does both).
- `LogRetentionService` sweeps every 5 s and empties a stream not polled for 20 s, covering a tab that
  is killed without calling close.

Consequences to keep in mind:

- The panel polls once a second while open; there is no push, so latency is ≤1 s by design.
- `LogSnapshotDto.Cleared` is the cursor-recovery signal: it is true when `after` is older than the
  oldest retained record (the ring wrapped or was cleared), and the UI resets its accumulated lines.
  Note `RingLogBuffer.Clear()` does **not** reset the sequence counter, so a clear alone is invisible
  to a correct cursor — `Cleared` is about genuine gaps, which is what the UI needs.
- `RingLogBuffer.SubscriberCount` / `EntryAppended` remain unused by this design; they are the hook if
  a push transport is ever added. `PHASE_8_LOGGING_CONTEXT.md` §2 documents them as such.

---

## 3. Render PC: what it collects and where it is weak

`RenderPcDto` fields: OS, .NET runtime, CPU model + core count, total memory, GPU (PCI id + driver),
FFmpeg version, resolved encoder + auto-detected flag, results path + free/total bytes, collection
time. Each probe is best-effort; a failure yields a null field.

- CPU/memory come from `/proc/cpuinfo` and `/proc/meminfo`; GPU from `/sys/class/drm/card*/device/uevent`
  (`PCI_ID=`, `DRIVER=`); FFmpeg from `ffmpeg -version`; disk from `DriveInfo` on the results volume's
  root. All are Linux-oriented; on another OS they degrade to "unknown"/null rather than failing.
- **Container caveat (8.1).** Inside the container these reflect the *host* kernel's `/proc`, not the
  cgroup limits, and `DriveInfo` reports the mounted volume — which is what the card wants, but worth
  stating when 8.4 documents the workflow. The roadmap's "cgroup-visible CPU/RAM" is not yet done.
- `EncoderResolver.Resolve()` runs its probe lazily; with `Renderer:Encoder=cpu` (or any explicit
  value) it does not probe at all, which is what the test relies on.
- Collection happens on the warmup hosted service (`Task.Run` inside `GetAsync` on refresh), so it
  never blocks or is blocked by the render worker.

---

## 4. Authorization contract

All three new endpoints live on `AdminController`, which is `[Authorize(Roles = "admin")]`. The
regression test reflects over every `HttpMethodAttribute` action on that controller and asserts it
carries the admin role (or is covered by the class attribute) and is not `[AllowAnonymous]`. That is
the project's established authz-test style (see `JobsHubAuthorizationTests`) because
`Microsoft.AspNetCore.Mvc.Testing` is not in the offline package cache or the central lock files.

**Deferred:** a true HTTP-level "non-admin gets 401/403" test. If the release work (8.5) or a later
phase allows adding packages, add `Microsoft.AspNetCore.Mvc.Testing` (+ a `Directory.Packages.props`
entry and lock-file update) and a `WebApplicationFactory` test that hits `/api/v1/admin/*` with a
non-admin cookie. Until then the reflection guard is the regression control.

---

## 5. Notes for 8.4 (`MAINTENANCE.md` + debug workflow)

8.4 should document, and can now rely on, the following being true:

- **Where to look without shell access:** the admin tab's Console logs (service/engine) and Render PC
  card are the in-app diagnostics; `/health` remains the liveness probe.
- **The engine stream only has content while a render is running**; the service stream always has
  content. Both are bounded (500 / 1000 lines) and emptied when the panel closes.
- **Debug vs release** is unchanged from `PHASE_8_LOGGING_CONTEXT.md` §4: `LAZERRENDER_DEBUG=1` lowers
  levels at runtime, `[Conditional]` engine lines exist only in Debug builds, and
  `LAZERRENDER_CONFIGURATION=Release` is the opt-in for a source-build Release engine.
- **8.4's required exercise** (one tachyon re-pin using the instrumentation) can now use the panel to
  watch the engine's classification live, which is exactly the workflow `MAINTENANCE_INFRA_CONTEXT.md`
  §5 wants written down.

---

## 6. Gotchas / limitations

- **One stream at a time.** The UI watches either service or engine; switching releases and clears the
  previous one. That matches the retention goal and keeps the DOM simple, but it means you cannot see
  service and engine interleaved.
- **Idle clear is time-based.** A stream is dropped 20 s after the last poll, so a panel left open in a
  background tab that the browser throttles can lose lines between polls. The UI's `Cleared` handling
  resyncs cleanly when it resumes.
- **`after` is a per-stream cursor**, not global; the client keeps one value and resets it on open.
- **DOM bound is 500 lines** (`LOG_MAX_LINES` in `app.js`), independent of the server buffer; the older
  lines are dropped from the view but remain in the server buffer until its own capacity/clear.
- **No new packages** were added (important under central package management): the whole feature uses
  the BCL and ASP.NET Core already referenced.
- **`GET /api/v1/capabilities` is now unused by the SPA** (the Render PC card replaced it). It is left
  in place because it is a documented, cheap endpoint; consider folding it into `render-pc` only if a
  future change makes it redundant.
- **`Observability` levels/`LAZERRENDER_DEBUG` are bound once at startup**; changing them needs a
  restart.

---

## 7. Verification commands

```bash
# Service: build + full suite (expects 114 passing)
dotnet build LazerRender.Service/LazerRender.Service.sln -c Debug
dotnet test  LazerRender.Service/LazerRender.Service.sln -c Debug

# Startup smoke test (hosted services + log-pipeline readout + listener)
ASPNETCORE_URLS=http://127.0.0.1:5199 timeout 12 \
  dotnet LazerRender.Service/src/LazerRender.Api/bin/Debug/net8.0/LazerRender.Api.dll
```
