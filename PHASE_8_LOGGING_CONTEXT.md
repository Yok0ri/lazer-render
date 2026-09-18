# PHASE_8_LOGGING_CONTEXT.md

**Purpose.** Hand-off for the *next* Phase 8 work after the logging/instrumentation core shipped:
**§8.3 admin observability panel** and **§8.4 `MAINTENANCE.md` + debug-workflow docs**. It records the
design decisions and the exact interfaces 8.3/8.4 consume, plus the gotchas found while building 8.2.
It deliberately does **not** repeat material already in [`ROADMAP.md`](ROADMAP.md:264),
[`ARCHITECTURE.md`](ARCHITECTURE.md:1219) §3.6.5,
[`MAINTENANCE_INFRA_CONTEXT.md`](MAINTENANCE_INFRA_CONTEXT.md:1) or the security audit.

**Status.** 8.2 implemented and verified (engine builds Debug + Release, service builds Debug).
**Phase 8.3 has since been built on top of it** — see [`PHASE_8_3_CONTEXT.md`](PHASE_8_3_CONTEXT.md:1).
The consumption contract in §2 was used as specified, with one design change: the panel **polls**
rather than using SignalR, because the SPA ships no SignalR client and the CSP forbids a CDN.

---

## 1. What 8.2 built

| Concern | Location |
|---|---|
| Shared record model | [`LazerRender.Contracts/LogRecord.cs`](LazerRender.Service/src/LazerRender.Contracts/LogRecord.cs:1) — `LogRecord(Sequence, Timestamp, Source, Severity, Message)`, `LogSource { Service, Engine }`, `LogSeverity { Debug, Information, Warning, Error }` |
| Sink contract | [`Services/Logging/ILogSink.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/ILogSink.cs:7) |
| Bounded ring buffer | [`Services/Logging/RingLogBuffer.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/RingLogBuffer.cs:13) |
| The two streams | [`Services/Logging/LogBuffers.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/LogBuffers.cs:9) — `ServiceLogRingBuffer`, `EngineLogRingBuffer` (DI singletons) |
| Redaction | [`Services/Logging/LogRedactor.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/LogRedactor.cs:20) |
| Service capture | [`Services/Logging/RingBufferLoggerProvider.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/RingBufferLoggerProvider.cs:15) |
| Engine capture + classification | [`Services/Logging/EngineLogForwarder.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/EngineLogForwarder.cs:14); called from [`RendererProcessRunner.cs`](LazerRender.Service/src/LazerRender.Api/Services/RendererProcessRunner.cs:186) |
| Config | `Observability` section → [`ObservabilityOptions.cs`](LazerRender.Service/src/LazerRender.Api/Configuration/ObservabilityOptions.cs:5); registered in [`Program.cs`](LazerRender.Service/src/LazerRender.Api/Program.cs:212) |
| Debug/release gate | [`Directory.Build.props`](Directory.Build.props:1), [`DebugMode.cs`](LazerRender.Service/src/LazerRender.Api/Services/Logging/DebugMode.cs:11), [`DebugInstrumentation.cs`](LazerRender.Game/DebugInstrumentation.cs:1) |
| Tests | [`LoggingPipelineTests.cs`](LazerRender.Service/tests/LazerRender.Worker.Tests/LoggingPipelineTests.cs:18) |

`RendererProcessRunner.logEngineLine` no longer exists as a private method; its behaviour now lives in
`EngineLogForwarder` (same three levels, same message format `[engine/{stream}] …`). The runner still
exposes `RendererProcessRunner.Redact(...)` as a static bridge to `LogRedactor` for compatibility.

---

## 2. The consumption contract for 8.3

**Two singletons, both `RingLogBuffer : ILogSink`** — inject `ServiceLogRingBuffer` and
`EngineLogRingBuffer`:

- `Snapshot(long afterSequence = 0)` → `IReadOnlyList<LogRecord>`, oldest-first, a consistent copy.
  `0` means "everything retained"; pass the last sequence you saw for a delta.
- `Subscribe(Action<LogRecord>)` → `IDisposable`; increments `SubscriberCount`, raises on every accepted
  record **outside** the buffer lock, and isolates throwing handlers.
- `SubscriberCount`, `Capacity`, `MinimumSeverity`, `DroppedCount`, `Clear()`, and the
  `EntryAppended` event.

**Recommended wiring (poll or push, both supported):** on admin-panel connect, take a `Snapshot()`,
emit it, record the highest sequence, then `Subscribe` for live lines; on the last disconnect, dispose
the subscription and `Clear()` both buffers. Dispose/`Clear` is how 8.3 satisfies Roadmap §8.3's
"retain nothing once the panel disconnects".

**Capture-gate semantics (important).** The buffers capture *continuously* while the process runs (the
engine buffer only receives anything while a render is in flight), so a panel that opens late sees the
recent tail rather than nothing. `SubscriberCount` exists so a future optimisation can skip work when
nobody is watching, but do not assume "no subscribers" means "empty buffer" — call `Clear()` if strict
retention is required.

**Do not re-classify and do not re-redact.** `Severity` is already computed by `EngineLogForwarder`
(problem markers → Warning, notable markers → Information, everything else → Debug) and the message is
already redacted and length-capped. See §3.

**Sequence numbers are per-buffer.** A consumer must track `lastSequence` separately for the service
and engine streams.

**Serialization gotcha.** The REST controllers use the MVC JSON options (camelCase + string enums), but
**SignalR does not inherit them**. `JsonHubProtocol` defaults to camelCase property names but serializes
enums as **numbers**. If 8.3 pushes `LogRecord` over SignalR, either configure
`AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))`
or send `Severity`/`Source` as strings. The REST path needs no change.

**Authorization.** Reuse `[Authorize(Roles = "admin")]` as [`AdminController`](LazerRender.Service/src/LazerRender.Api/Controllers/AdminController.cs:12)
does, and remember Roadmap §8.3 requires server-side role checks plus regression tests asserting a
non-admin gets 401/403 on every admin route. The existing [`JobsHub`](LazerRender.Service/src/LazerRender.Api/Hubs/JobsHub.cs:17)
is a pattern for a hub, but its group-join authorization is ownership-based and not reusable as-is for
a global admin stream.

---

## 3. Redaction guarantees 8.3 relies on

`LogRedactor` masks, in order: every known secret value, then any `Bearer <token>` shape.

- Known values come from `LogRedactor.FromConfiguration`, which reads
  `Osu:OAuth:ClientSecret`, `Renderer:AvatarApiKey`, `Renderer:OsuBotToken`,
  `Renderer:OsuBotRefreshToken`, `Admin:BootstrapToken`, `DataProtection:CertificatePassword` and the
  `OSU_API_KEY` environment variable. **If the credential inventory grows, extend that list** (it is
  the single choke point; audit H-4).
- Per-render credentials (the osu! user token / avatar key minted for one job) are passed explicitly
  from `RenderInvocation.RedactedValues` into `EngineLogForwarder.Forward`.
- Service records are prefixed `[Category] …` and engine records `[stdout] …` / `[stderr] …`, so the
  origin is readable without extra fields.
- Consequence for 8.3: a record read from either buffer is safe to send to a browser **as-is**. Adding a
  producer that writes directly to a buffer (bypassing `LogRedactor`) would break that invariant — don't.

---

## 4. Debug/release contract (8.4 will document this)

- Root [`Directory.Build.props`](Directory.Build.props:1) defines `LAZERRENDER_DEBUG` when
  `Configuration == Debug`, and also when `-p:LazerRenderDebug=true`. It applies to `LazerRender.Game`,
  `LazerRender.Contracts`, `LazerRender.Api` and the tests, **not** to `extern/osu` (which has its own
  props file).
- `DebugInstrumentation.Log(...)` is `[Conditional("LAZERRENDER_DEBUG")]`: its call sites are removed
  from Release entirely. `DebugInstrumentation.LogRuntime(...)` is not compiled out and is inert unless
  `LAZERRENDER_DEBUG=1`.
- `DebugMode.Enabled` = runtime `LAZERRENDER_DEBUG=1|true` **or** a Debug build (a Debug build can opt
  out with `LAZERRENDER_DEBUG=0`). It lowers both ring buffers' minimum severity to Debug.
- `run-headless.sh` defaults to `dotnet run` **Debug**; `LAZERRENDER_CONFIGURATION=Release` builds a
  Release engine. `LAZERRENDER_DEBUG` also turns on `set -x` in the script. The container image runs a
  prebuilt publish via `LAZERRENDER_ENGINE` and does not use this path.
- Practical consequence for the 8.4 workflow: the render host ships Release, so `[Conditional]` debug
  lines will **not** appear there even with `LAZERRENDER_DEBUG=1`; only the runtime-switchable paths
  become verbose. Local `dotnet run` is Debug and shows everything.

---

## 5. Notes for 8.3's remaining scope (not in 8.2)

> **Done in 8.3.** This section was the 8.3 to-do list; it is retained for the rationale. See
> [`PHASE_8_3_CONTEXT.md`](PHASE_8_3_CONTEXT.md:1) for what was actually built.

- **Console logs endpoint**: a `GET /api/v1/admin/logs?source=service|engine&after=<seq>` returning
  `LogRecord[]` is the smallest REST shape that fits `Snapshot`; a SignalR stream is the push variant
  (see the serialization gotcha above). Whichever is chosen, the panel must not poll the engine stream
  when no render is running — the buffer will be a stale tail.
- **Render PC card**: unchanged by 8.2. `MetaController.Capabilities` still returns only
  `{ encoder, autoDetected }`; the CPU/GPU/RAM/FFmpeg/runtime summary and the collect-once-at-startup
  policy remain 8.3 work (see `MAINTENANCE_INFRA_CONTEXT.md` §4.2 and Roadmap §8.3).
- **Admin tab sections**: the existing markup is in [`index.html`](LazerRender.Service/src/LazerRender.Api/wwwroot/index.html:230)
  (Queue + Allow a user + Render PC + Users); [`initAdmin`/`refreshAdmin`](LazerRender.Service/src/LazerRender.Api/wwwroot/app.js:826)
  is the 15 s timer to extend. Splitting into Users / Library / Render PC / Console logs is still to do.

---

## 6. Gotchas and limitations discovered while building 8.2

- **In-process only.** The buffers are per-process memory; a multi-instance deployment shares nothing.
  Fine for the current single-node render host.
- **Provider ordering.** `RingBufferLoggerProvider` is registered as an `ILoggerProvider` in DI, which
  is additive with the console logger; provider order is not guaranteed and both receive the same call.
- **No recursion risk.** Ring buffers never log, so the provider cannot feed itself.
- **EF command noise.** `appsettings.json` pins `Microsoft.EntityFrameworkCore.Database.Command` to
  Warning, but the ring-buffer provider has its own minimum. If the global `Logging:LogLevel:Default` is
  ever lowered to Debug, the service buffer will also retain EF chatter (bounded by capacity, so
  harmless but noisy).
- **`EngineLogForwarder` budget.** 256 KB per render, shared across a render's output, independent of
  buffer capacity. Debug chatter counts against it, so a very chatty render can hit the budget and log
  one "output suppressed" warning.
- **`Clear()` is not atomic with a reader's `Snapshot()`** across a panel reconnect; it is atomic within
  the buffer. A reconnect can miss a handful of lines — acceptable for a diagnostic console.
- **`ObservabilityOptions` levels are bound at startup** and are not hot-reloaded; `LAZERRENDER_DEBUG`
  is read once into `DebugMode.Enabled`.
- **Renders remain non-byte-reproducible** (see `MAINTENANCE_INFRA_CONTEXT.md` §6.5), so the 8.4
  functional smoke tests must use tolerance, not hashes. 8.2 does not change this.

---

## 7. Quick verification commands

```bash
# Service: build + full suite (expects 99 passing)
dotnet build LazerRender.Service/LazerRender.Service.sln -c Debug
dotnet test  LazerRender.Service/LazerRender.Service.sln -c Debug

# Engine: Debug carries the instrumentation, Release compiles it out
dotnet build LazerRender.Game/LazerRender.Game.csproj -c Debug
dotnet build LazerRender.Game/LazerRender.Game.csproj -c Release

# Optional runtime verbosity on either half
LAZERRENDER_DEBUG=1 dotnet run --project LazerRender.Game/LazerRender.Game.csproj -- --help
```
