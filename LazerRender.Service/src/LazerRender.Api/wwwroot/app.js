"use strict";

const state = {
  me: null,
  defaults: null,
  skins: [],
  presets: [],
  jobs: [],
  expandedJobs: new Set(),
  techOpen: new Set(),
  listTimer: null,
  adminTimer: null,
  queueMessageJobId: null,
  logsTimer: null,
  logsSource: null,
  logsLastSequence: 0,
};

const $ = (id) => document.getElementById(id);

// State-changing requests must carry this header. A cross-site page cannot set it without a CORS
// preflight, and the service sends no CORS headers, so it doubles as a CSRF control alongside the
// cookie's SameSite policy. Must match RequestGuards.HeaderName.
const CSRF_HEADER = "X-LazerRender-Request";
const CSRF_SAFE_METHODS = ["GET", "HEAD", "OPTIONS"];

function esc(value) {
  return String(value ?? "")
    .replace(/&/g, "\u0026amp;")
    .replace(/</g, "\u0026lt;")
    .replace(/>/g, "\u0026gt;")
    .replace(/"/g, "\u0026quot;")
    .replace(/'/g, "\u0026#39;");
}

async function api(path, options = {}) {
  const method = (options.method || "GET").toUpperCase();
  const headers = { ...(options.headers || {}) };

  // FormData must keep the browser-generated multipart boundary, so no Content-Type is set for it.
  if (!(options.body instanceof FormData)) headers["Content-Type"] = "application/json";
  if (!CSRF_SAFE_METHODS.includes(method)) headers[CSRF_HEADER] = "1";

  const res = await fetch(path, { ...options, credentials: "same-origin", headers });

  if (res.status === 401) { showLogin(); throw new Error("unauthorized"); }
  if (res.status === 204) return null;

  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const validation = data?.errors
      ? Object.entries(data.errors).map(([k, v]) => `${k}: ${Array.isArray(v) ? v.join(", ") : v}`).join("; ")
      : null;
    throw new Error(data?.detail || validation || data?.error || data?.title || `HTTP ${res.status}`);
  }
  return data;
}

/* ---------- auth / shell ---------- */

function showLogin() {
  $("login-panel").hidden = false;
  $("app").hidden = true;
  $("nav").hidden = true;
  stopTimers();
}

function showApp() {
  $("login-panel").hidden = true;
  $("app").hidden = false;
  $("nav").hidden = false;
}

function renderUser() {
  const area = $("user-area");
  if (!state.me) { area.innerHTML = ""; return; }
  const avatar = state.me.avatarUrl
    ? `<img src="${esc(state.me.avatarUrl)}" alt="avatar" width="30" height="30" />`
    : "";
  area.innerHTML = `${avatar}<span>${esc(state.me.username)}</span>` +
    `<a class="btn ghost" id="logout-btn" href="/auth/logout">Log out</a>`;

  // Bound as a listener rather than an inline onclick, so the CSP can keep script-src at 'self'.
  $("logout-btn")?.addEventListener("click", (event) => {
    event.preventDefault();
    doLogout();
  });
}

async function doLogout() {
  await fetch("/auth/logout", {
    method: "POST",
    credentials: "same-origin",
    headers: { [CSRF_HEADER]: "1" },
  });
  window.location.reload();
}

async function loadMe() {
  let me;
  try {
    me = await api("/api/v1/me");
  } catch {
    showLogin();
    return;
  }

  state.me = me;
  showApp();
  renderUser();
  if (me.role === "admin") { $("admin-tab").hidden = false; initAdmin(); }

  // Non-critical data must never mask a successful authentication.
  await Promise.allSettled([loadSkins(), loadPresets(), loadDefaults(), loadJobs()]);
  startTimers();
}

function switchTab(name) {
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === name));
  document.querySelectorAll(".tab-panel").forEach((p) => (p.hidden = true));
  const panel = $("tab-" + name);
  if (panel) panel.hidden = false;
  if (name !== "render") clearQueueMessage();
  if (name !== "jobs") collapseAllJobs();
  // Log lines are retained only while the Console logs panel is open.
  if (name !== "admin") closeLogs(true);
}

/* ---------- render pc ---------- */

const BYTE_UNITS = ["B", "KB", "MB", "GB", "TB"];

function fmtBytes(bytes) {
  if (bytes == null || !Number.isFinite(bytes)) return "—";
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < BYTE_UNITS.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(unit === 0 || value >= 10 ? 0 : 1)} ${BYTE_UNITS[unit]}`;
}

async function loadRenderPc(refresh = false) {
  const target = $("render-pc");
  if (!target) return;
  target.innerHTML = "<dd>Collecting…</dd>";
  try {
    const pc = await api(`/api/v1/admin/render-pc${refresh ? "?refresh=true" : ""}`);
    const gpu = pc.gpu
      ? `${esc(pc.gpu)}${pc.gpuDriver ? ` · driver ${esc(pc.gpuDriver)}` : ""}`
      : "—";
    const rows = [
      ["CPU", `${esc(pc.cpuModel)} (${pc.cpuCores} cores)`],
      ["Memory", fmtBytes(pc.memoryTotalBytes)],
      ["GPU", gpu],
      ["FFmpeg", pc.ffmpegVersion ? esc(pc.ffmpegVersion) : "not found"],
      ["Encoder", `${esc(String(pc.encoder).toUpperCase())}${pc.encoderAutoDetected ? " (auto-detected)" : " (configured)"}`],
      ["Runtime", esc(pc.dotnetRuntime)],
      ["OS", esc(pc.operatingSystem)],
      ["Results volume", `${fmtBytes(pc.resultsFreeBytes)} free of ${fmtBytes(pc.resultsTotalBytes)} — ${esc(pc.resultsPath)}`],
      ["Collected", new Date(pc.collectedAt).toLocaleString()],
    ];
    target.innerHTML = rows.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${v}</dd>`).join("");
  } catch (err) {
    target.innerHTML = `<dd>Render PC summary unavailable: ${esc(err.message)}</dd>`;
  }
}

async function loadDefaults() {
  try {
    state.defaults = await api("/api/v1/render-config/defaults");
    syncResetButtons();
  } catch { state.defaults = null; }
}

/* ---------- skins ---------- */

async function loadSkins() {
  try {
    state.skins = await api("/api/v1/skins");
    const select = $("skin-select");
    const current = select.value;
    select.innerHTML = '<option value="">Default (argon pro)</option>' +
      state.skins.map((s) => `<option value="${esc(s.name)}">${esc(s.name)}</option>`).join("");
    select.value = current;

    $("skins-list").innerHTML = state.skins.length
      ? state.skins.map((s) => `
          <div class="skin-row">
            <span>${esc(s.name)}</span>
            <span class="muted">${new Date(s.importedAt).toLocaleDateString()}</span>
            <button class="btn" type="button" data-delete-skin="${esc(s.id)}">Remove</button>
          </div>`).join("")
      : '<p class="empty">No skins imported yet.</p>';
  } catch { /* non-critical */ }
}

async function submitSkin(e) {
  e.preventDefault();
  const input = $("skin-file");
  if (!input.files.length) { setStatus("skin-status", "Choose an .osk file.", false); return; }

  const form = new FormData();
  form.append("file", input.files[0]);
  setStatus("skin-status", "Importing…", true);

  try {
    await api("/api/v1/skins", { method: "POST", body: form });
    setStatus("skin-status", "Skin imported.", true);
    input.value = "";
    await loadSkins();
    syncResetButtons();
  } catch (err) {
    setStatus("skin-status", err.message, false);
  }
}

async function deleteSkin(id) {
  if (!confirm("Remove this skin from the library?")) return;
  try {
    await api(`/api/v1/skins/${id}`, { method: "DELETE" });
    await loadSkins();
    syncResetButtons();
  } catch (err) { alert(err.message); }
}

/* ---------- presets ---------- */

async function loadPresets() {
  try {
    const data = await api("/api/v1/presets");
    state.presets = data.items;
    renderPresetsList();
  } catch { state.presets = []; }
}

function renderPresetsList() {
  const el = $("presets-list");
  el.hidden = state.presets.length === 0;
  el.innerHTML = state.presets.map((p) => `
    <span class="preset-item">
      <button type="button" data-load-preset="${esc(p.id)}">${esc(p.name)}</button>
      <button type="button" data-delete-preset="${esc(p.id)}" title="Delete preset" aria-label="Delete preset ${esc(p.name)}">×</button>
    </span>`).join("");
}

async function savePreset() {
  const name = prompt("Preset name:", "My preset");
  if (name === null) return;

  const config = JSON.stringify(buildConfig());
  try {
    await api("/api/v1/presets", { method: "POST", body: JSON.stringify({ name: name.trim() || "Unnamed preset", configJson: config }) });
  } catch (err) {
    if (err.message.includes("already exists") && confirm("Overwrite this preset?")) {
      await api("/api/v1/presets", { method: "POST", body: JSON.stringify({ name: name.trim() || "Unnamed preset", configJson: config, overwrite: true }) });
    } else if (err.message.includes("already exists")) {
      alert(err.message);
      return;
    } else {
      alert(err.message);
      return;
    }
  }
  await loadPresets();
}

async function loadPreset(id) {
  const preset = state.presets.find((p) => p.id === id);
  if (!preset) return;
  try {
    applyConfig(JSON.parse(preset.configJson));
  } catch {
    alert("Could not read that preset.");
  }
}

async function deletePreset(id) {
  if (!confirm("Delete this preset?")) return;
  try {
    await api(`/api/v1/presets/${id}`, { method: "DELETE" });
    await loadPresets();
  } catch (err) { alert(err.message); }
}

/* ---------- render form ---------- */

function setStatus(id, message, ok) {
  const el = $(id);
  el.textContent = message;
  el.style.color = ok ? "var(--ok)" : "var(--err)";
}

function checked(id) { return $(id).checked; }
function num(id) { return Number($(id).value); }
function str(id) { return $(id).value; }

const BOOL_KEYS = [
  "storyboard", "video", "beatmap-skins", "beatmap-colours", "beatmap-hitsounds",
  "snaking-in", "snaking-out", "hit-animations", "hit-lighting", "cursor-trail", "cursor-ripples",
  "hide-gameplay-cursor", "show-click-markers", "show-frame-markers", "show-cursor-path",
  "disable-result-screen",
];

// HUD component keys accepted by the engine's --hud (must mirror HudVisibilityFilter.AllKeys).
// Every checkbox defaults to on; unchecking any of them switches the render into whitelist mode
// with only the still-checked components kept. The list is split into three visual groups
// (gameplay HUD, secondary HUD, cosmetic elements) that are rendered as separate blocks.
const HUD_ONLY_KEYS = [
  "hp", "combo", "score", "keyoverlay", "accuracy", "pp", "hiterror", "song-progress",
  "unstable-rate", "judgements", "mods", "aim-error",
  "rank", "longest-combo", "scoreboard", "bpm", "cps", "player-name", "avatar", "flags", "spectators",
  "cosmetic",
];

function buildConfig() {
  const [width, height] = str("resolution").split("x").map(Number);
  const config = {
    fps: num("fps"),
    width,
    height,
    motionBlur: num("motion-blur"),
    dimLevel: num("dim-level"),
    blurLevel: num("blur-level"),
    parallax: num("parallax"),
    comboColourNormalisation: num("combo-colour-normalisation"),
    hudVisibility: str("hud-visibility"),
    hudScale: num("hud-scale"),
    cursorSize: num("cursor-size"),
    replayAnalysisLength: num("replay-analysis-length"),
    playfieldBorder: str("playfield-border"),
  };

  BOOL_KEYS.forEach((id) => { config[toCamel(id)] = checked(id); });

  // All HUD elements checked means "show everything" (omit the key); otherwise only the checked
  // components are kept. An explicit empty list hides the whole HUD.
  const hud = HUD_ONLY_KEYS.filter((key) => checked(`hud-only-${key}`));
  if (hud.length !== HUD_ONLY_KEYS.length) config.hud = hud;

  const skin = str("skin-select");
  if (skin) config.skin = skin;

  return config;
}

function toCamel(kebab) {
  return kebab.replace(/-([a-z])/g, (_, c) => c.toUpperCase());
}

function applyConfig(config) {
  if (config.fps != null) $("fps").value = String(config.fps);
  if (config.width != null && config.height != null) {
    const match = [`${config.width}x${config.height}`];
    if ($(`#resolution option[value="${config.width}x${config.height}"]`)) $("resolution").value = `${config.width}x${config.height}`;
  }
  if (config.motionBlur != null) $("motion-blur").value = String(config.motionBlur);
  if (config.dimLevel != null) $("dim-level").value = String(config.dimLevel);
  if (config.blurLevel != null) $("blur-level").value = String(config.blurLevel);
  if (config.parallax != null) $("parallax").value = String(config.parallax);
  if (config.comboColourNormalisation != null) $("combo-colour-normalisation").value = String(config.comboColourNormalisation);
  if (config.hudVisibility != null) $("hud-visibility").value = config.hudVisibility;
  if (config.hudScale != null) $("hud-scale").value = String(config.hudScale);
  if (config.cursorSize != null) $("cursor-size").value = String(config.cursorSize);
  if (config.replayAnalysisLength != null) $("replay-analysis-length").value = String(config.replayAnalysisLength);
  if (config.playfieldBorder != null) $("playfield-border").value = config.playfieldBorder;
  if (config.skin != null && $("skin-select").querySelector(`option[value="${CSS.escape(config.skin)}"]`)) {
    $("skin-select").value = config.skin;
  }

  BOOL_KEYS.forEach((id) => { const key = toCamel(id); if (config[key] != null) $(id).checked = !!config[key]; });

  // A config without hud means "everything on".
  const hud = Array.isArray(config.hud) ? config.hud : HUD_ONLY_KEYS;
  HUD_ONLY_KEYS.forEach((key) => { $(`hud-only-${key}`).checked = hud.includes(key); });
  syncHudMaster();

  syncRangeOutputs();
  syncResetButtons();
}

// Keeps the master "All" checkbox in sync with the individual HUD element checkboxes. It shows an
// indeterminate state when only some elements are selected.
function syncHudMaster() {
  const boxes = HUD_ONLY_KEYS.map((key) => $(`hud-only-${key}`));
  const selected = boxes.filter((box) => box.checked).length;
  const master = $("hud-master");
  master.checked = selected === boxes.length;
  master.indeterminate = selected > 0 && selected < boxes.length;
}

function syncRangeOutputs() {
  [
    ["dim-level", "dim-out"], ["blur-level", "blur-out"], ["parallax", "parallax-out"],
    ["combo-colour-normalisation", "ccn-out"], ["cursor-size", "cursor-size-out"],
    ["replay-analysis-length", "ral-out"], ["hud-scale", "hud-scale-out"],
  ].forEach(([inId, outId]) => { $(outId).textContent = $(inId).value; });
}

/* ---------- reset to default ---------- */

function controlDefaultValue(id) {
  if (!state.defaults) return undefined;
  if (id === "resolution") return `${state.defaults.width}x${state.defaults.height}`;
  if (id === "skin-select") return state.defaults.skin ?? "";
  if (id.startsWith("hud-only-")) return true;
  const key = toCamel(id);
  if (key in state.defaults) return state.defaults[key];
  return undefined;
}

function syncResetButtons() {
  document.querySelectorAll("[data-reset]").forEach((input) => {
    const dflt = controlDefaultValue(input.id);
    if (dflt === undefined) return;

    const current = input.type === "checkbox" ? input.checked : input.value;
    const differs = input.type === "checkbox" ? current !== !!dflt : String(current) !== String(dflt);

    let btn = input.parentElement.querySelector(".reset-btn");
    if (!btn) {
      btn = document.createElement("button");
      btn.type = "button";
      btn.className = "reset-btn";
      btn.textContent = "↺";
      btn.title = "Reset to default";
      btn.setAttribute("aria-label", "Reset to default");
      input.parentElement.appendChild(btn);
      btn.addEventListener("click", () => {
        if (input.type === "checkbox") input.checked = !!dflt;
        else input.value = String(dflt);
        input.dispatchEvent(new Event("input", { bubbles: true }));
        syncRangeOutputs();
        syncResetButtons();
      });
    }
    btn.hidden = !differs;
  });
}

/* ---------- submit ---------- */

// Formats a timestamp with the viewer's time zone appended, e.g. "9/15/2026, 8:25:47 AM (Europe/Warsaw)".
function fmtTime(value) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const formatted = date.toLocaleString(undefined, {
    year: "numeric", month: "numeric", day: "numeric",
    hour: "numeric", minute: "2-digit", second: "2-digit",
  });
  return `${formatted} (${tz})`;
}

// Formats a duration in seconds as m:ss.
function fmtDuration(seconds) {
  if (seconds == null || !Number.isFinite(seconds) || seconds <= 0) return "—";
  const total = Math.round(seconds);
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, "0")}`;
}

function clearQueueMessage() {
  state.queueMessageJobId = null;
  $("form-status").textContent = "";
}

async function submitRender(e) {
  e.preventDefault();
  const fileInput = $("replay-file");
  if (!fileInput.files.length) { setStatus("form-status", "Choose an .osr file first.", false); return; }

  const form = new FormData();
  form.append("file", fileInput.files[0]);
  form.append("config", JSON.stringify(buildConfig()));
  const skin = str("skin-select").trim();
  if (skin) form.append("skin", skin);

  const btn = $("submit-btn");
  btn.disabled = true;
  setStatus("form-status", "Submitting…", true);

  try {
    const job = await api("/api/v1/jobs", { method: "POST", body: form });
    state.queueMessageJobId = job.id;
    setStatus("form-status", "Queued.", true);
    fileInput.value = "";
    $("replay-meta").textContent = "";
    await loadJobs();
  } catch (err) {
    setStatus("form-status", err.message, false);
  } finally {
    btn.disabled = false;
  }
}

/* ---------- jobs ---------- */

function statusBadge(status) {
  return `<span class="badge ${esc(status)}">${esc(status)}</span>`;
}

function isActive(status) {
  return ["claimed", "rendering", "finalizing"].includes(status);
}

function isCancellable(status) {
  return ["queued", "claimed", "rendering", "finalizing"].includes(status);
}

function jobTitle(job) {
  const map = job.mapTitle && job.mapArtist
    ? `${job.mapArtist} — ${job.mapTitle}${job.mapVersion ? ` [${job.mapVersion}]` : ""}`
    : null;
  if (map) return job.playerUsername ? `${job.playerUsername} | ${map}` : map;
  return job.playerUsername ? `Replay by ${job.playerUsername}` : `Render #${job.displayNumber}`;
}

function jobSubtitle(job) {
  const bits = [];
  if (job.mapCreator) bits.push(`by ${job.mapCreator}`);
  if (job.ownerUsername) bits.push(`queued by ${job.ownerUsername}`);
  bits.push(`${job.width} × ${job.height} @ ${job.fps}`);
  return bits.filter(Boolean).join(" · ");
}

// The Recent renders card shows a compact, unexpandable summary: who played what, plus uploader
// context. Resolution / frame rate are intentionally omitted (as they are on job cards); the encoder
// lives only in the admin "Render PC" card.
function recentSubtitle(job) {
  const bits = [];
  if (job.mapCreator) bits.push(`by ${job.mapCreator}`);
  if (job.ownerUsername) bits.push(`queued by ${job.ownerUsername}`);
  return bits.join(" · ");
}

function cancelButton(job) {
  return isCancellable(job.status)
    ? `<button class="btn" type="button" data-cancel="${esc(job.id)}">Cancel</button>`
    : "";
}

function downloadIcon() {
  return `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 3v12" /><path d="m7 10 5 5 5-5" /><path d="M5 21h14" /></svg>`;
}

function progressPercent(job) {
  if (!job.total) return null;
  return Math.min(100, (job.frame / job.total) * 100);
}

function phaseLabel(phase) { return phase ? phase.replaceAll("_", " ").toLowerCase() : ""; }

// Returns { kind, html, pct } where kind is "none" | "determinate" | "indeterminate" | "queued".
function progressState(job) {
  if (isActive(job.status)) {
    const pct = progressPercent(job);
    if (pct !== null) {
      return {
        kind: "determinate",
        pct,
        html: `<div class="job-progress" data-progress data-kind="determinate">
          <div class="bar"><div class="fill" data-fill-pct="${pct.toFixed(1)}"></div></div>
          <div class="text"><span data-frames>${job.frame.toLocaleString()} / ${job.total.toLocaleString()} frames</span><span data-pct>${pct.toFixed(1)}% · ${job.fpsNow.toFixed(1)} fps</span></div>
        </div>`,
      };
    }
    return {
      kind: "indeterminate",
      pct: null,
      html: `<div class="job-progress indeterminate" data-progress data-kind="indeterminate">
        <div class="bar"><div class="fill"></div></div>
        <div class="text"><span data-phase>${esc(phaseLabel(job.phase))}</span><span data-fps>${job.fpsNow.toFixed(1)} fps</span></div>
      </div>`,
    };
  }

  if (job.status === "queued") {
    const pos = job.queuePosition ? `Position #${job.queuePosition}${job.queueLength ? ` of ${job.queueLength}` : ""}` : "Queued";
    return {
      kind: "queued",
      pct: null,
      html: `<div class="job-progress" data-progress data-kind="queued"><div class="text"><span data-pos>${esc(pos)}</span></div></div>`,
    };
  }

  return { kind: "none", pct: null, html: "" };
}

function jobCard(job, compact = false) {
  const expanded = !compact && state.expandedJobs.has(job.id);

  const main = `<div class="job-main">
      <div class="job-title" data-title>${esc(jobTitle(job))}</div>
      <div class="job-sub" data-sub>${esc(compact ? recentSubtitle(job) : jobSubtitle(job))}</div>
    </div>`;

  const head = compact
    ? `<div class="job-card-top">${main}<div class="job-actions">${statusBadge(job.status)}${cancelButton(job)}</div></div>`
    : `<div class="job-card-top">
        <button class="expand-btn" type="button" data-expand="${esc(job.id)}" aria-expanded="${expanded}">
          <span class="chev ${expanded ? "open" : ""}">▶</span>
          ${main}
        </button>
        <div class="job-actions">${statusBadge(job.status)}${cancelButton(job)}</div>
      </div>
      <div class="job-expanded" data-expanded ${expanded ? "" : "hidden"}>${expanded ? expandedBody(job) : ""}</div>`;

  return `<div class="job-card${compact ? " compact" : ""}" data-job-id="${esc(job.id)}">
    ${progressState(job).html}
    ${head}
  </div>`;
}

function expandedBody(job) {
  const times = [
    ["Created", job.createdAt],
    job.startedAt ? ["Started", job.startedAt] : null,
    job.finishedAt ? ["Finished", job.finishedAt] : null,
  ].filter(Boolean);

  const stats = [
    ["Player", job.playerUsername || "—"],
    ["Queued by", job.ownerUsername || "—"],
    ["Map", job.mapTitle ? `${job.mapArtist} — ${job.mapTitle}${job.mapVersion ? ` [${job.mapVersion}]` : ""}` : "—"],
    ["Song length", fmtDuration(job.songLength)],
    ["Star rating", job.mapStars != null ? `${job.mapStars.toFixed(2)} ⭐` : "—"],
    ["Mods", job.mods ? `+${job.mods}` : "NM"],
    ["Accuracy", job.accuracy != null ? `${(job.accuracy * 100).toFixed(2)}%` : "—"],
    ["Resolution", `${job.width} × ${job.height}`],
    ["Frame rate", `${job.fps} fps`],
    ["Skin", job.skinName || "Default (argon pro)"],
    ["Status", job.status],
  ].map(([k, v]) => `<div class="stat"><div class="k">${esc(k)}</div><div class="v">${esc(v)}</div></div>`).join("");

  const timeIsland = times
    .map(([k, v]) => `<div class="time-row"><span class="time-k">${esc(k)}</span><span class="time-v">${esc(fmtTime(v))}</span></div>`)
    .join("");

  let message = "";
  if (job.status === "failed") message = `<div class="error-box">${esc(job.errorMessage || "The render failed.")}</div>`;
  else if (job.status === "rejected") message = `<div class="error-box">${esc(job.errorMessage || "This render was rejected because it would exceed the size limit.")}</div>`;
  else if (job.status === "cancelled") message = `<p class="muted">This render was cancelled.</p>`;

  const techOpen = state.techOpen.has(job.id) ? "open" : "";
  const download = job.resultAvailable
    ? `<div class="expanded-actions"><a class="btn icon" href="/api/v1/jobs/${esc(job.id)}/result" title="Download render" aria-label="Download render">${downloadIcon()}</a></div>`
    : "";

  return `${message}
    <div class="expanded-main">
      <div class="time-island">${timeIsland}</div>
      <div class="stat-grid">${stats}</div>
    </div>
    ${download}
    <div class="tech">
      <details ${techOpen} data-tech>
        <summary>Technical details</summary>
        <p>Job ID: <code>${esc(job.id)}</code><br/>Beatmap MD5: <code>${esc(job.beatmapMd5 || "—")}</code></p>
      </details>
    </div>`;
}

function renderJobList(container, jobs, compact = false) {
  const existing = [...container.querySelectorAll("[data-job-id]")].map((el) => el.dataset.jobId);
  const incoming = jobs.map((j) => j.id);

  const sameIds = existing.length === incoming.length && existing.every((id, i) => id === incoming[i]);

  if (!sameIds) {
    container.innerHTML = jobs.length
      ? jobs.map((j) => jobCard(j, compact)).join("")
      : '<p class="empty">No renders yet. Queue your first replay above.</p>';
    applyProgressWidths(container);
    return;
  }

  jobs.forEach((job) => {
    const card = container.querySelector(`[data-job-id="${CSS.escape(job.id)}"]`);
    if (!card) return;
    syncJobCard(card, job, compact);
  });
}

function syncJobCard(card, job, compact = false) {
  const title = card.querySelector("[data-title]");
  const sub = card.querySelector("[data-sub]");
  if (title) title.textContent = jobTitle(job);
  if (sub) sub.textContent = compact ? recentSubtitle(job) : jobSubtitle(job);

  const badge = card.querySelector(".badge");
  if (badge) { badge.className = `badge ${esc(job.status)}`; badge.textContent = job.status; }

  syncProgress(card, job);
  syncActions(card, job);
  if (!compact) syncExpanded(card, job);
}

function syncProgress(card, job) {
  const target = progressState(job);
  const el = card.querySelector("[data-progress]");

  if (target.kind === "none") {
    if (el) el.remove();
    return;
  }

  if (!el) {
    card.insertAdjacentHTML("afterbegin", target.html);
    applyProgressWidths(card);
    return;
  }

  // Structural change only (e.g. queued → determinate → indeterminate). Otherwise update in
  // place so the indeterminate animation is not restarted and DevTools state is preserved.
  if (el.dataset.kind !== target.kind) {
    el.outerHTML = target.html;
    applyProgressWidths(card);
    return;
  }

  if (target.kind === "determinate") {
    const fill = el.querySelector(".fill");
    if (fill) fill.style.width = `${target.pct.toFixed(1)}%`;
    setSpan(el, "[data-frames]", `${job.frame.toLocaleString()} / ${job.total.toLocaleString()} frames`);
    setSpan(el, "[data-pct]", `${target.pct.toFixed(1)}% · ${job.fpsNow.toFixed(1)} fps`);
  } else if (target.kind === "indeterminate") {
    setSpan(el, "[data-phase]", phaseLabel(job.phase));
    setSpan(el, "[data-fps]", `${job.fpsNow.toFixed(1)} fps`);
  } else if (target.kind === "queued") {
    const pos = job.queuePosition ? `Position #${job.queuePosition}${job.queueLength ? ` of ${job.queueLength}` : ""}` : "Queued";
    setSpan(el, "[data-pos]", pos);
  }
}

function setSpan(root, selector, text) {
  const span = root.querySelector(selector);
  if (span) span.textContent = text;
}

// The CSP keeps style-src at 'self', so the progress bar's width is applied through the CSSOM rather
// than an inline style attribute. Elements carrying data-fill-pct are synced after every (re-)render.
function applyProgressWidths(root) {
  root.querySelectorAll("[data-fill-pct]").forEach((fill) => {
    fill.style.width = `${fill.dataset.fillPct}%`;
  });
}

function syncActions(card, job) {
  const actions = card.querySelector(".job-actions");
  if (!actions) return;

  let cancel = actions.querySelector("[data-cancel]");
  if (isCancellable(job.status) && !cancel) {
    actions.insertAdjacentHTML("beforeend", `<button class="btn" type="button" data-cancel="${esc(job.id)}">Cancel</button>`);
  } else if (!isCancellable(job.status) && cancel) {
    cancel.remove();
  }
}

function expandedSignature(job) {
  return [
    job.status, job.phase, job.errorMessage, job.resultAvailable,
    job.mapTitle, job.mapArtist, job.mapCreator, job.mapVersion, job.mapStars,
    job.songLength, job.mods, job.accuracy,
    job.playerUsername, job.ownerUsername,
    job.width, job.height, job.fps, job.encoder, job.skinName,
    job.createdAt, job.startedAt, job.finishedAt, job.beatmapMd5,
  ].join("|");
}

function syncExpanded(card, job) {
  const expandedEl = card.querySelector("[data-expanded].job-expanded");
  if (!expandedEl || expandedEl.hidden) return;

  const sig = expandedSignature(job);
  if (expandedEl.dataset.sig === sig) return;

  expandedEl.dataset.sig = sig;
  expandedEl.innerHTML = expandedBody(job);
}

function toggleJob(id) {
  // Only one job can be expanded at a time; expanding another collapses the previous one.
  const wasExpanded = state.expandedJobs.has(id);
  state.expandedJobs.clear();
  if (!wasExpanded) state.expandedJobs.add(id);

  applyExpansionToAllCards();
}

// Collapses every expanded job (used when leaving the Jobs tab).
function collapseAllJobs() {
  if (state.expandedJobs.size === 0) return;
  state.expandedJobs.clear();
  applyExpansionToAllCards();
}

// Re-applies the current expansion state to every job card in the DOM. The same job appears in both
// the Recent renders list and the Jobs list, so each card is updated in place.
function applyExpansionToAllCards() {
  state.jobs.forEach((job) => {
    document.querySelectorAll(`[data-job-id="${CSS.escape(job.id)}"]`).forEach((card) => applyExpansion(card, job));
  });
}

function applyExpansion(card, job) {
  const expanded = state.expandedJobs.has(job.id);

  const chev = card.querySelector(".chev");
  if (chev) chev.classList.toggle("open", expanded);

  const btn = card.querySelector("[data-expand]");
  if (btn) btn.setAttribute("aria-expanded", String(expanded));

  const expandedEl = card.querySelector("[data-expanded].job-expanded");
  if (!expandedEl) return;

  if (expanded) {
    expandedEl.dataset.sig = expandedSignature(job);
    expandedEl.innerHTML = expandedBody(job);
    expandedEl.hidden = false;
  } else {
    expandedEl.hidden = true;
  }
}

async function loadJobs() {
  const data = await api("/api/v1/jobs?limit=100");
  state.jobs = data.items;
  renderJobList($("jobs-list"), data.items.slice(0, 6), true);
  renderJobList($("jobs-list-full"), data.items);
  updateQueueConfirmation();
}

function updateQueueConfirmation() {
  if (!state.queueMessageJobId) return;
  const job = state.jobs.find((j) => j.id === state.queueMessageJobId);
  if (!job) return;

  if (job.status === "queued" && job.queuePosition) {
    setStatus("form-status", `Queued — position ${job.queuePosition}${job.queueLength ? ` of ${job.queueLength}` : ""}.`, true);
  } else if (job.status === "queued") {
    setStatus("form-status", "Queued.", true);
  } else {
    // The job has started or finished; the confirmation is no longer useful.
    clearQueueMessage();
  }
}

async function cancelJob(id) {
  if (!confirm("Cancel this render?")) return;
  try {
    await api(`/api/v1/jobs/${id}`, { method: "DELETE" });
    await loadJobs();
  } catch (err) { alert(err.message); }
}

/* ---------- admin ---------- */

async function initAdmin() {
  document.querySelectorAll("[data-purge]").forEach((b) => b.addEventListener("click", () => purge(b.dataset.purge)));
  document.querySelectorAll("[data-log-source]").forEach((b) =>
    b.addEventListener("click", () => openLogs(b.dataset.logSource)));
  $("logs-close")?.addEventListener("click", () => closeLogs());
  $("render-pc-refresh")?.addEventListener("click", () => loadRenderPc(true));
  await refreshAdmin();
  await loadRenderPc();
}

async function refreshAdmin() {
  try {
    const stats = await api("/api/v1/admin/queue");
    $("queue-stats").textContent = `Active: ${stats.active} · Queued: ${stats.queued}`;
    await loadUsers();
  } catch { /* non-critical */ }
}

async function loadUsers() {
  const users = await api("/api/v1/admin/users");
  const selfId = state.me ? String(state.me.osuUserId) : "";
  const rows = users.map((u) => {
    const action = String(u.osuUserId) === selfId
      ? '<span class="muted" title="You cannot revoke your own access">you</span>'
      : u.isAllowed
        ? `<button class="btn" type="button" data-revoke="${esc(u.osuUserId)}">Revoke</button>`
        : `<button class="btn primary" type="button" data-allow-id="${esc(u.osuUserId)}">Allow</button>`;

    return `
    <tr>
      <td data-label="osu! ID">${esc(u.osuUserId)}</td>
      <td data-label="Username">${esc(u.username)}</td>
      <td data-label="Role">${esc(u.role)}</td>
      <td data-label="Access">${u.isAllowed ? '<span class="badge completed">allowed</span>' : '<span class="badge failed">blocked</span>'}</td>
      <td data-label="Action">${action}</td>
    </tr>`;
  }).join("");

  $("users-list").innerHTML = users.length
    ? `<table><thead><tr><th>osu! ID</th><th>Username</th><th>Role</th><th>Access</th><th></th></tr></thead><tbody>${rows}</tbody></table>`
    : '<p class="empty">No users yet.</p>';
}

async function submitAllow(e) {
  e.preventDefault();
  const id = $("allow-id").value.trim();
  const name = $("allow-name").value.trim();
  const body = {};
  if (id) body.osuUserId = Number(id);
  if (name) body.username = name;
  if (!id && !name) { setStatus("allow-status", "Enter an osu! user ID or username.", false); return; }

  setStatus("allow-status", "Allowing…", true);
  try {
    await api("/api/v1/admin/users/allow", { method: "POST", body: JSON.stringify(body) });
    setStatus("allow-status", "Allowed.", true);
    $("allow-id").value = ""; $("allow-name").value = "";
    await loadUsers();
  } catch (err) { setStatus("allow-status", err.message, false); }
}

async function revokeUser(osuUserId) {
  if (!confirm(`Revoke access for osu! user ${osuUserId}?`)) return;
  try {
    await api("/api/v1/admin/users/revoke", { method: "POST", body: JSON.stringify({ osuUserId }) });
    await loadUsers();
  } catch (err) { alert(err.message); }
}

async function purge(target) {
  if (!confirm(`Purge ${target} from the shared library?`)) return;
  try {
    await api(`/api/v1/admin/purge?target=${target}`, { method: "POST" });
    alert("Purge complete.");
  } catch (err) { alert(err.message); }
}

/* ---------- console logs ---------- */

// Keeps the accumulated DOM lines bounded; the server buffer is bounded too.
const LOG_MAX_LINES = 500;

function logLineClass(severity) {
  switch (severity) {
    case "warning": return "warn";
    case "error": return "error";
    case "debug": return "debug";
    default: return "info";
  }
}

function formatLogRecord(record) {
  const time = new Date(record.timestamp).toLocaleTimeString();
  return `${time}  ${String(record.severity).toUpperCase().padEnd(5)}  ${record.message}`;
}

function appendLogRecords(records) {
  const view = $("logs-view");
  if (!view || !records.length) return;

  const fragment = document.createDocumentFragment();
  records.forEach((record) => {
    const line = document.createElement("span");
    line.className = `log-line ${logLineClass(record.severity)}`;
    line.textContent = `${formatLogRecord(record)}\n`;
    fragment.appendChild(line);
  });
  view.appendChild(fragment);

  while (view.childNodes.length > LOG_MAX_LINES) view.removeChild(view.firstChild);
  if ($("logs-follow").checked) view.scrollTop = view.scrollHeight;
}

function stopLogPolling() {
  if (state.logsTimer) { clearInterval(state.logsTimer); state.logsTimer = null; }
}

function syncLogButtons() {
  document.querySelectorAll("[data-log-source]").forEach((b) =>
    b.classList.toggle("primary", b.dataset.logSource === state.logsSource));
}

async function pollLogs() {
  const source = state.logsSource;
  if (!source) return;

  const snapshot = await api(`/api/v1/admin/logs?source=${encodeURIComponent(source)}&after=${state.logsLastSequence}`);
  if (state.logsSource !== source) return; // the panel switched while this request was in flight

  if (snapshot.cleared) {
    // Our cursor is older than anything retained; drop what we have and resync.
    state.logsLastSequence = 0;
    $("logs-view").textContent = "";
  }

  appendLogRecords(snapshot.records);

  if (snapshot.records.length) {
    state.logsLastSequence = snapshot.records[snapshot.records.length - 1].sequence;
  }

  $("logs-meta").textContent =
    `${source} · buffer ${snapshot.capacity} lines · ${snapshot.dropped} dropped · level ${snapshot.minimumSeverity}`;
}

async function openLogs(source) {
  if (!source || state.logsSource === source) return;

  await closeLogs(true);

  state.logsSource = source;
  state.logsLastSequence = 0;
  $("logs-view").textContent = "";
  $("logs-follow").checked = true;
  $("logs-meta").textContent = `Watching ${source}…`;
  syncLogButtons();

  try { await pollLogs(); } catch { /* transient; the next tick retries */ }
  state.logsTimer = setInterval(() => { pollLogs().catch(() => {}); }, 1000);
}

// Releases the current stream server-side (which empties its buffer) and stops polling. Safe to call
// when nothing is open. `silent` skips the UI reset so openLogs can switch streams.
async function closeLogs(silent = false) {
  stopLogPolling();
  const source = state.logsSource;
  state.logsSource = null;
  state.logsLastSequence = 0;
  syncLogButtons();

  if (source) {
    try {
      await api(`/api/v1/admin/logs/close?source=${encodeURIComponent(source)}`, { method: "POST" });
    } catch { /* the idle sweeper clears it once the lease expires */ }
  }

  if (!silent) {
    if ($("logs-view")) $("logs-view").textContent = "";
    if ($("logs-meta")) $("logs-meta").textContent = "Closed.";
  }
}

/* ---------- timers ---------- */

function startTimers() {
  stopTimers();
  state.listTimer = setInterval(() => { loadJobs().catch(() => {}); }, 2000);
  if (state.me?.role === "admin") {
    state.adminTimer = setInterval(() => { refreshAdmin().catch(() => {}); }, 15000);
  }
}

function stopTimers() {
  [state.listTimer, state.adminTimer].forEach((t) => { if (t) clearInterval(t); });
  state.listTimer = state.adminTimer = null;
  stopLogPolling();
}

/* ---------- wiring ---------- */

function handleJobAction(e) {
  const expand = e.target.closest("[data-expand]");
  const cancel = e.target.closest("[data-cancel]");
  if (expand) { toggleJob(expand.dataset.expand); return; }
  if (cancel) { cancelJob(cancel.dataset.cancel); return; }
}

function handleGlobalClick(e) {
  const delSkin = e.target.closest("[data-delete-skin]");
  const loadPresetBtn = e.target.closest("[data-load-preset]");
  const delPreset = e.target.closest("[data-delete-preset]");
  if (delSkin) { deleteSkin(delSkin.dataset.deleteSkin); return; }
  if (loadPresetBtn) { loadPreset(loadPresetBtn.dataset.loadPreset); return; }
  if (delPreset) { deletePreset(delPreset.dataset.deletePreset); return; }

  const revoke = e.target.closest("[data-revoke]");
  const allow = e.target.closest("[data-allow-id]");
  if (revoke) { revokeUser(Number(revoke.dataset.revoke)); return; }
  if (allow) { allowById(Number(allow.dataset.allowId)); return; }
}

async function allowById(osuUserId) {
  try {
    await api("/api/v1/admin/users/allow", { method: "POST", body: JSON.stringify({ osuUserId }) });
    await loadUsers();
  } catch (err) { alert(err.message); }
}

document.addEventListener("DOMContentLoaded", () => {
  document.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => switchTab(t.dataset.tab)));

  $("render-form").addEventListener("submit", submitRender);
  $("skin-form").addEventListener("submit", submitSkin);
  $("allow-form")?.addEventListener("submit", submitAllow);
  $("preset-save").addEventListener("click", savePreset);
  $("preset-load").addEventListener("click", () => { $("presets-list").hidden = !$("presets-list").hidden; });

  // Master HUD toggle: selects or deselects every HUD element at once.
  $("hud-master").addEventListener("change", () => {
    const on = $("hud-master").checked;
    HUD_ONLY_KEYS.forEach((key) => { $(`hud-only-${key}`).checked = on; });
    syncHudMaster();
    syncResetButtons();
  });
  $("hud-only-list").addEventListener("change", syncHudMaster);
  syncHudMaster();

  $("jobs-list").addEventListener("click", handleJobAction);
  $("jobs-list-full").addEventListener("click", handleJobAction);
  document.addEventListener("click", handleGlobalClick);

  // The <details> "toggle" event does not bubble; listen in the capture phase instead.
  document.addEventListener("toggle", (e) => {
    const details = e.target.closest ? e.target.closest("[data-tech]") : null;
    if (!details) return;
    const card = details.closest("[data-job-id]");
    if (!card) return;
    if (details.open) state.techOpen.add(card.dataset.jobId);
    else state.techOpen.delete(card.dataset.jobId);
  }, true);

  document.querySelectorAll("[data-reset]").forEach((input) => {
    input.addEventListener("input", () => { syncRangeOutputs(); syncResetButtons(); syncHudMaster(); });
  });

  loadMe();
});
