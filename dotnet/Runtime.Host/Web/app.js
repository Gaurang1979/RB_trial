// The browser dashboard (UI-UX-SPEC.md 4.17): view only. Every request is a GET except sign-in and
// sign-out; the runtime has no other web route. Text from the line is set with textContent only.
"use strict";

const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = String(text);
  return e;
};
const svgEl = (tag, attrs) => {
  const e = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const [k, v] of Object.entries(attrs || {})) e.setAttribute(k, v);
  return e;
};
const num = (v) => Number(v || 0);
const fmtTime = (iso) => (iso ? new Date(iso).toLocaleString() : "");

async function api(path) {
  const r = await fetch("web/api/" + path, { credentials: "same-origin" });
  if (r.status === 401) { showSignIn(); throw new Error("signed out"); }
  if (!r.ok) throw new Error(path + ": " + r.status);
  return r.json();
}

// ---- theme (per browser) -------------------------------------------------------------------
function setTheme(t) {
  if (t === "dark") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", t);
  try { localStorage.setItem("vs-theme", t); } catch (_) { /* private window */ }
}
(() => {
  let t = "dark";
  try { t = localStorage.getItem("vs-theme") || "dark"; } catch (_) { }
  $("theme").value = t;
  setTheme(t);
  $("theme").addEventListener("change", (e) => setTheme(e.target.value));
})();

// ---- sign-in -------------------------------------------------------------------------------
// Relative addresses throughout ("web/..."): the same page works at https://<line-pc>:<port>/ on the plant's network
// and at https://support.sundaramtech.com/<machine>/ through the remote support link (REQ-11).
function showSignIn() {
  stopLive();
  if (idleTimer) { clearInterval(idleTimer); idleTimer = null; }
  for (const p of document.querySelectorAll(".page")) p.hidden = true;
  $("tabs").hidden = true;
  $("sign-out").hidden = true;
  $("user").textContent = "";
  $("sign-in").hidden = false;
  // a user's password on the plant's network, the service console through remote service: the runtime says which.
  // Neither form shows before it answers, so a password field never appears on the relay.
  fetch("web/sign-in-options", { credentials: "same-origin" }).then((r) => {
    if (!r.ok) throw new Error(String(r.status));
    return r.json();
  }).then((o) => {
    $("sign-in-note").textContent = "";
    $("sign-in-form").hidden = !o.password;
    $("service-form").hidden = !o.service;
    if (o.console) $("console-link").setAttribute("href", o.console);
  }).catch(() => {
    $("sign-in-note").textContent = "The line PC did not answer. Reload the page.";
  });
}

$("sign-in-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  $("sign-in-error").textContent = "";
  const body = new URLSearchParams(new FormData(e.target));
  const r = await fetch("web/sign-in", { method: "POST", body, credentials: "same-origin" });
  const j = await r.json().catch(() => ({}));
  if (!r.ok || !j.ok) { $("sign-in-error").textContent = j.error || "Sign-in failed."; return; }
  e.target.reset();
  $("sign-in-error").textContent = "";
  start();
});

$("sign-out").addEventListener("click", async () => {
  await fetch("web/sign-out", { method: "POST", credentials: "same-origin" });
  showSignIn();
});

// ---- idle sign-out (WEB-04) -----------------------------------------------------------------
// Signs the user out after 15 minutes without input in THIS browser. Only a person's key, pointer, touch or
// scroll counts as use: the page's own polling, picture refresh and live stream never reset the clock, so they
// cannot keep a session open (the server sees them as use).
let idleMs = 15 * 60 * 1000, lastInput = Date.now(), idleTimer = null;
const touch = () => { lastInput = Date.now(); };
for (const ev of ["keydown", "pointerdown", "pointermove", "touchstart", "wheel", "scroll"])
  window.addEventListener(ev, touch, { passive: true, capture: true });

function armIdle(seconds) {
  if (seconds > 0) idleMs = seconds * 1000;
  touch();
  if (!idleTimer) idleTimer = setInterval(checkIdle, 5000);
}
function checkIdle() {
  if (Date.now() - lastInput < idleMs) return;
  clearInterval(idleTimer); idleTimer = null;
  fetch("web/sign-out", { method: "POST", credentials: "same-origin" }).catch(() => { }).finally(() => {
    showSignIn();
    $("sign-in-error").textContent = "You were signed out after " + Math.round(idleMs / 60000) + " minutes without use.";
  });
}
// a tab brought back to the front checks at once (background timers run slowly)
document.addEventListener("visibilitychange", () => { if (!document.hidden && idleTimer) checkIdle(); });

// ---- pages ---------------------------------------------------------------------------------
let page = "live";
for (const b of document.querySelectorAll("#tabs button")) b.addEventListener("click", () => show(b.dataset.page));

// the pages this user's role may see on this line (the runtime refuses the others' calls as well)
let pages = [];

function show(p) {
  if (!pages.includes(p)) p = pages[0];
  if (!p) return;
  page = p;
  for (const b of document.querySelectorAll("#tabs button")) b.classList.toggle("on", b.dataset.page === p);
  for (const s of document.querySelectorAll(".page")) s.hidden = s.id !== "page-" + p;
  if (p === "trends") { loadTrends(); loadQuality(); }
  if (p === "reports") loadReports();
  if (p === "audit") loadAudit();
}

async function start() {
  let me;
  try { me = await api("me"); } catch (_) { return; }
  $("sign-in").hidden = true;
  $("tabs").hidden = false;
  $("sign-out").hidden = false;
  $("user").textContent = me.fullName || me.name;
  armIdle(me.idleSignOutS);
  pages = me.pages || [];
  allReports = !!me.allReports;
  for (const b of document.querySelectorAll("#tabs button")) b.hidden = !pages.includes(b.dataset.page);
  if (!me.lineAllowed || !pages.length) {
    for (const s of document.querySelectorAll(".page")) s.hidden = true;
    $("tabs").hidden = true;
    setBanner(me.lineAllowed ? "Your role may not see this dashboard." : "You may not watch this line" + (me.line ? " (" + me.line + ")" : "") + ".", true);
    return;
  }
  setBanner(null);
  show(page);
  if (pages.includes("live")) startLive();
}

// ---- live ----------------------------------------------------------------------------------
let source = null, framesTimer = null, lastStatus = null, connected = false;

function showNotConnected() {
  const pill = $("pill");
  pill.className = "pill ng";
  pill.querySelector(".glyph").textContent = "✗";
  pill.querySelector(".text").textContent = "Not connected";
  setBanner("Please wait. Reconnecting to the inspection.");
  $("frames").classList.add("stale");
}
const recent = [];

function stopLive() {
  if (source) { source.close(); source = null; }
  if (framesTimer) { clearInterval(framesTimer); framesTimer = null; }
}

function startLive() {
  stopLive();
  api("status").then(onStatus).catch(() => { });
  api("counters").then(onCounters).catch(() => { });
  api("recent").then((list) => { recent.length = 0; recent.push(...list); drawNg(); }).catch(() => { });
  source = new EventSource("web/api/live");
  source.addEventListener("open", () => { connected = true; setBanner(null); if (lastStatus) onStatus(lastStatus); });
  source.addEventListener("status", (e) => onStatus(JSON.parse(e.data)));
  source.addEventListener("counters", (e) => onCounters(JSON.parse(e.data)));
  source.addEventListener("result", (e) => {
    recent.unshift(JSON.parse(e.data));
    if (recent.length > 32) recent.length = 32;
    drawNg();
  });
  source.addEventListener("error", () => {
    // UI-UX-SPEC.md 2.1 "Runtime not reachable": red "Not connected", the last picture greyed. EventSource
    // retries by itself after a dropped link, but gives up for good on an error answer (a proxy's 502):
    // then start again after a pause.
    connected = false;
    showNotConnected();
    api("me").catch(() => { });   // a 401 shows the sign-in
    if (source && source.readyState === EventSource.CLOSED) setTimeout(() => { if (source && source.readyState === EventSource.CLOSED) start(); }, 3000);   // asks again what this user may see
  });
  refreshFrames();
  framesTimer = setInterval(refreshFrames, 500);   // at most two frames a second per camera
}

function setBanner(text, warn) {
  const b = $("banner");
  b.hidden = !text;
  b.textContent = text || "";
  b.classList.toggle("warn", !!warn);
}

const STATES = {
  BUSY: ["ok", "▶", "Running"], READY: ["stopped", "‖", "Ready"], STOPPED: ["stopped", "‖", "Stopped"],
  ERROR: ["ng", "!", "Alarm"], TEACH: ["warn", "!", "Teach"],
};

function onStatus(s) {
  lastStatus = s;
  if (!connected && source) { showNotConnected(); return; }
  $("frames").classList.remove("stale");
  let [cls, glyph, text] = STATES[s.state] || ["stopped", "‖", "Stopped"];
  const alarms = s.alarms || [];
  // no job loaded and nothing wrong: the line is idle, not in alarm (UI-UX-SPEC.md 2.1); any fault below still wins
  const idle = s.state === "ERROR" && !s.activeJob && !alarms.length;
  if (idle) [cls, glyph, text] = ["stopped", "‖", "No job"];
  if (alarms.some((a) => a.level === "ALARM")) [cls, glyph, text] = ["ng", "!", "Alarm"];
  else if (s.state === "BUSY" && alarms.some((a) => a.level === "WARNING")) [cls, glyph, text] = ["warn", "!", "Check"];
  // a PLC set up whose link or heartbeat is not live (plcConnected false) is a lost link
  if (s.plc && (!s.plc.linkUp || !s.plcConnected)) [cls, glyph, text] = ["ng", "✗", "PLC not connected"];
  if ((s.cameras || []).some((c) => !c.connected)) [cls, glyph, text] = ["ng", "✗", "No camera"];
  if (s.licenceOk === false) [cls, glyph, text] = ["stopped", "🔒", "Locked"];
  const quiet = idle && cls === "stopped";
  if (s.errorText) setBanner(s.errorText, quiet);
  else setBanner(null);
  const pill = $("pill");
  pill.className = "pill " + cls;
  pill.querySelector(".glyph").textContent = glyph;
  pill.querySelector(".text").textContent = text;
  $("line-job").textContent = s.activeJob ? "Job: " + s.activeJob + (s.activeVariant ? " · " + s.activeVariant : "") : "No job";

  const ul = $("alarms");
  ul.replaceChildren();
  if (!alarms.length) ul.append(el("li", "muted", "None"));
  for (const a of alarms) {
    const li = el("li", (a.level || "").toLowerCase());
    li.append(el("div", null, a.text));
    if (a.help) {
      const d = el("details");
      d.append(el("summary", null, "What do I do?"), el("div", null, a.help));
      li.append(d);
    }
    ul.append(li);
  }

  const dl = $("batch");
  dl.replaceChildren();
  const rows = [["Started", fmtTime(s.batchStarted)]];
  for (const f of s.batch || []) rows.push([f.label || f.key, f.value || "–"]);
  for (const [k, v] of rows) dl.append(el("dt", null, k), el("dd", null, v || "–"));
}

function onCounters(c) {
  $("c-total").textContent = num(c.total).toLocaleString();
  $("c-ok").textContent = num(c.ok).toLocaleString();
  $("c-ng").textContent = num(c.ng).toLocaleString();
  $("c-cycle").textContent = c.medianCycleMs ? num(c.medianCycleMs).toFixed(1) + " ms" : "–";
  drawRing(num(c.total) ? num(c.yieldPercent) : null);
}

function drawRing(pct) {
  const svg = $("yield-ring");
  svg.replaceChildren();
  const r = 48, len = 2 * Math.PI * r;
  svg.append(svgEl("circle", { cx: 60, cy: 60, r, fill: "none", stroke: "var(--vs-color-status-ng)", "stroke-width": 12, opacity: pct === null ? 0.25 : 1 }));
  if (pct !== null) {
    svg.append(svgEl("circle", {
      cx: 60, cy: 60, r, fill: "none", stroke: "var(--vs-color-status-ok)", "stroke-width": 12,
      "stroke-dasharray": `${(len * pct) / 100} ${len}`, transform: "rotate(-90 60 60)",
    }));
  }
  const t = svgEl("text", { x: 60, y: 58, "text-anchor": "middle", "font-size": 22, "font-weight": 600, fill: "var(--vs-color-text)" });
  t.textContent = pct === null ? "–" : pct.toFixed(1) + "%";
  const l = svgEl("text", { x: 60, y: 78, "text-anchor": "middle", "font-size": 11, fill: "var(--vs-color-text-muted)" });
  l.textContent = "Yield";
  svg.append(t, l);
}

async function refreshFrames() {
  if (page !== "live" || document.hidden) return;
  let cams;
  try { cams = await api("cameras"); } catch (_) { return; }
  const box = $("frames");
  if (!cams.length) { box.replaceChildren(el("div", "muted", "No image yet.")); return; }
  for (const old of box.querySelectorAll(".frame")) if (!cams.includes(old.dataset.cam)) old.remove();
  for (const cam of cams) {
    let f = box.querySelector(`[data-cam="${CSS.escape(cam)}"]`);
    if (!f) {
      box.querySelector(".muted")?.remove();
      f = el("div", "frame");
      f.dataset.cam = cam;
      f.append(el("img"), el("span", "cap"));
      f.querySelector("img").alt = cam;
      box.append(f);
    }
    // the picture and its verdict come in one answer, so the caption always belongs to the picture
    let r;
    try { r = await fetch("web/api/frame/" + encodeURIComponent(cam), { credentials: "same-origin" }); } catch (_) { continue; }
    if (r.status === 401) { showSignIn(); return; }
    if (!r.ok) continue;
    const id = r.headers.get("X-Inspection-Id") || "0", pass = r.headers.get("X-Pass") === "1";
    if (f.dataset.id === id) { await r.body?.cancel(); continue; }
    const url = URL.createObjectURL(await r.blob());
    const img = f.querySelector("img");
    if (img.src.startsWith("blob:")) URL.revokeObjectURL(img.src);
    img.src = url;
    f.dataset.id = id;
    f.className = "frame " + (id === "0" ? "" : pass ? "ok" : "ng");
    f.querySelector(".cap").textContent = cam + (id !== "0" ? (pass ? "  ✓ OK" : "  ✗ NG") : "");
  }
}

// Only new bad parts are added and old ones dropped: a thumbnail already shown is never fetched again.
function drawNg() {
  const strip = $("ng-strip");
  const bad = recent.filter((r) => !r.pass && (r.imageIds || []).length).slice(0, 16);
  if (!bad.length) { strip.replaceChildren(el("span", "muted", "No bad parts yet.")); return; }
  strip.querySelector(".muted")?.remove();
  const keep = new Set(bad.map((r) => r.imageIds[0]));
  for (const b of [...strip.children]) if (!keep.has(b.dataset.img)) b.remove();
  let after = null;
  for (const r of bad) {
    const existing = strip.querySelector(`[data-img="${CSS.escape(r.imageIds[0])}"]`);
    if (existing) { after = existing; continue; }
    const b = el("button");
    b.dataset.img = r.imageIds[0];
    const img = el("img");
    img.src = "web/api/image/" + encodeURIComponent(r.imageIds[0]);
    img.alt = "NG " + (r.partId || r.inspectionId);
    img.loading = "lazy";
    b.title = fmtTime(r.time) + " " + (r.tools || []).map((t) => t.name + ": " + t.reasonText).join("; ");
    b.append(img);
    b.addEventListener("click", () => openViewer(r));
    if (after) after.after(b); else strip.prepend(b);
    after = b;
  }
}

function openViewer(r) {
  $("viewer-img").src = "web/api/image/" + encodeURIComponent(r.imageIds[0]);
  const why = (r.tools || []).map((t) => t.name + ": " + (t.reasonText || "NG")).join("\n");
  $("viewer-text").textContent = `${fmtTime(r.time)}  ·  part ${r.partId || r.inspectionId}  ·  ${r.job}\n${why}`;
  $("viewer").hidden = false;
}
$("viewer-close").addEventListener("click", () => { $("viewer").hidden = true; });
$("viewer").addEventListener("click", (e) => { if (e.target.id === "viewer") $("viewer").hidden = true; });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") $("viewer").hidden = true; });

// ---- trends --------------------------------------------------------------------------------
let range = "shift";
for (const b of document.querySelectorAll("#range button")) b.addEventListener("click", () => {
  range = b.dataset.range;
  for (const x of document.querySelectorAll("#range button")) x.classList.toggle("on", x === b);
  loadTrends();
  loadQuality();
});

const tip = el("div", "tip");
tip.hidden = true;
document.body.append(tip);
function hover(node, text) {
  node.addEventListener("mousemove", (e) => { tip.textContent = text; tip.hidden = false; tip.style.left = e.clientX + 12 + "px"; tip.style.top = e.clientY + 12 + "px"; });
  node.addEventListener("mouseleave", () => { tip.hidden = true; });
  node.addEventListener("click", (e) => { tip.textContent = text; tip.hidden = false; tip.style.left = e.clientX + 12 + "px"; tip.style.top = e.clientY + 12 + "px"; });
}

// One scale per chart, thin marks, the current value marked (UI-UX-SPEC.md principle 15).
function chartFrame(box, max, unit) {
  const W = Math.max(320, box.clientWidth || 800), H = 200, L = 44, B = 22, T = 18, R = 12;
  const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}`, role: "img" });
  const y = (v) => T + (H - T - B) * (1 - v / max);
  for (let i = 0; i <= 4; i++) {
    const v = (max * i) / 4;
    svg.append(svgEl("line", { x1: L, x2: W - R, y1: y(v), y2: y(v), class: "grid-line" }));
    const t = svgEl("text", { x: L - 6, y: y(v) + 4, "text-anchor": "end" });
    t.textContent = (max >= 10 ? Math.round(v) : v.toFixed(1)) + (unit || "");
    svg.append(t);
  }
  svg.append(svgEl("line", { x1: L, x2: W - R, y1: H - B, y2: H - B, class: "axis" }));
  box.replaceChildren(svg);
  return { svg, W, H, L, B, T, R, y };
}

function empty(box, text) {
  const W = Math.max(320, box.clientWidth || 800);
  const svg = svgEl("svg", { viewBox: `0 0 ${W} 60` });
  const t = svgEl("text", { x: W / 2, y: 34, "text-anchor": "middle", class: "empty" });
  t.textContent = text;
  svg.append(t);
  box.replaceChildren(svg);
}

function bins(data) {
  // hours in the browser's time; a week is shown per day
  const out = [];
  const perDay = range === "week";
  const map = new Map();
  for (const h of data.hours) {
    const d = new Date(h.hourUtc);
    const key = perDay ? d.toDateString() : d.getTime();
    const label = perDay ? d.toLocaleDateString(undefined, { weekday: "short", day: "numeric" }) : d.getHours().toString().padStart(2, "0") + ":00";
    const b = map.get(key) || { label, count: 0, ok: 0, msSum: 0, maxMs: 0 };
    b.count += h.count; b.ok += h.ok; b.msSum += h.avgMs * h.count; b.maxMs = Math.max(b.maxMs, h.maxMs);
    map.set(key, b);
  }
  for (const b of map.values()) out.push(b);
  return out;
}

let trendData = null;
async function loadTrends() {
  try { trendData = await api("trend?range=" + range); } catch (_) { return; }
  drawTrends();
}

function drawTrends() {
  const data = trendData;
  if (!data) return;
  $("parts-title").textContent = data.fromParts ? "Parts per hour" : "Inspections per hour (no PLC part log)";
  const b = bins(data);
  const parts = $("ch-parts"), yieldBox = $("ch-yield"), cycle = $("ch-cycle"), reasons = $("ch-reasons");
  if (!b.length) {
    for (const x of [parts, yieldBox, cycle]) empty(x, "No parts in this time.");
  } else {
    // parts per bin: OK and NG stacked (status colours: these are OK and NG counts)
    const maxCount = Math.max(...b.map((x) => x.count), 1);
    let f = chartFrame(parts, maxCount);
    const slot = (f.W - f.L - f.R) / b.length, bw = Math.min(24, slot - 2);
    b.forEach((x, i) => {
      const cx = f.L + slot * i + (slot - bw) / 2;
      const okR = svgEl("rect", { x: cx, width: bw, y: f.y(x.ok), height: f.y(0) - f.y(x.ok), class: "ok" });
      const ngR = svgEl("rect", { x: cx, width: bw, y: f.y(x.count), height: f.y(x.ok) - f.y(x.count), class: "ng" });
      hover(okR, `${x.label}: ${x.ok} OK of ${x.count}`);
      hover(ngR, `${x.label}: ${x.count - x.ok} NG of ${x.count}`);
      f.svg.append(okR, ngR);
      if (b.length <= 24 || i % Math.ceil(b.length / 12) === 0) {
        const t = svgEl("text", { x: cx + bw / 2, y: f.H - 6, "text-anchor": "middle" });
        t.textContent = x.label;
        f.svg.append(t);
      }
    });
    line(yieldBox, b, (x) => (100 * x.ok) / x.count, 100, "%");
    line(cycle, b, (x) => x.msSum / x.count, Math.max(...b.map((x) => x.msSum / x.count)) * 1.2 || 1, "");
  }
  if (!data.reasons.length) empty(reasons, "No bad parts in this time.");
  else {
    const W = Math.max(320, reasons.clientWidth || 800), row = 26, L = Math.min(260, W * 0.4), H = data.reasons.length * row + 8;
    const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}` });
    const max = Math.max(...data.reasons.map((r) => r.count));
    data.reasons.forEach((r, i) => {
      const y = 4 + i * row;
      const t = svgEl("text", { x: L - 8, y: y + 15, "text-anchor": "end" });
      t.textContent = r.reason.length > 40 ? r.reason.slice(0, 39) + "…" : r.reason;
      const w = Math.max(2, ((W - L - 60) * r.count) / max);
      const bar = svgEl("rect", { x: L, y: y + 2, width: w, height: 18, class: "bar" });
      hover(bar, `${r.reason}: ${r.count}`);
      const v = svgEl("text", { x: L + w + 6, y: y + 15 });
      v.textContent = r.count;
      svg.append(t, bar, v);
    });
    reasons.replaceChildren(svg);
  }
}

function line(box, b, value, max, unit) {
  const f = chartFrame(box, max, unit);
  const slot = (f.W - f.L - f.R) / b.length;
  const pts = b.map((x, i) => [f.L + slot * i + slot / 2, f.y(Math.min(max, value(x)))]);
  f.svg.append(svgEl("polyline", { points: pts.map((p) => p.join(",")).join(" "), class: "s1" }));
  pts.forEach((p, i) => {
    const last = i === pts.length - 1;
    const dot = svgEl("circle", { cx: p[0], cy: p[1], r: last ? 4 : 3, class: "s1-dot" });
    hover(dot, `${b[i].label}: ${value(b[i]).toFixed(1)}${unit}`);
    f.svg.append(dot);
  });
  const lastV = value(b[b.length - 1]);
  const t = svgEl("text", { x: pts[pts.length - 1][0] - 4, y: pts[pts.length - 1][1] - 8, "text-anchor": "end", fill: "var(--vs-color-text)" });
  t.textContent = lastV.toFixed(1) + unit;
  f.svg.append(t);
}

let resizeTimer = null;
window.addEventListener("resize", () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if (page === "trends") { drawTrends(); drawQuality(); } }, 250);   // redraw, never fetch again
});

// ---- print quality (WEB-05): grade of each graded Read code tool over time and its drift status ----------
const DRIFT_TEXT = { ok: "✓ Steady", drift: "! Drifting toward the lowest grade", too_few: "Too few graded parts yet" };
const LETTERS = "FDCBA";
let qualityData = null;
async function loadQuality() {
  try { qualityData = await api("quality?range=" + range); } catch (_) { return; }
  drawQuality();
}

function drawQuality() {
  const box = $("quality");
  if (!qualityData) return;
  box.replaceChildren();
  if (!qualityData.tools.length) { box.append(el("div", "muted", "No graded parts yet. Turn on grading in the Read code tool.")); return; }
  for (const t of qualityData.tools) {
    const row = el("div", "quality-tool");
    const head = el("div", "quality-head");
    head.append(el("strong", null, t.tool), el("span", "drift " + t.state, DRIFT_TEXT[t.state] || t.state));
    const facts = t.mean === null ? "" : `Latest ${t.samples}: mean ${LETTERS[Math.min(4, Math.max(0, Math.floor(t.mean + 1e-9)))]} (${t.mean.toFixed(2)})`
      + `, lowest allowed ${t.lowestLetter}${t.lowestFrom === "default" ? " (default)" : ""}`
      + (t.ungraded ? `, ${t.ungraded} could not be graded (counted as F)` : "");
    const chart = el("div", "chart");
    row.append(head, el("div", "muted", facts), chart);
    box.append(row);
    const b = t.buckets.map((x) => ({ ...x, label: new Date(x.hourUtc).getHours().toString().padStart(2, "0") + ":00" }));
    if (!b.length) { empty(chart, "No graded parts in this time."); continue; }
    const f = chartFrame(chart, 4, "");
    // the lowest allowed grade as a dashed line: the mean of the grade should stay above it
    f.svg.append(svgEl("line", { x1: f.L, x2: f.W - f.R, y1: f.y(t.lowest), y2: f.y(t.lowest), class: "limit" }));
    const slot = (f.W - f.L - f.R) / b.length;
    const pts = b.map((x, i) => [f.L + slot * i + slot / 2, f.y(x.mean)]);
    f.svg.append(svgEl("polyline", { points: pts.map((p) => p.join(",")).join(" "), class: "s1" }));
    pts.forEach((p, i) => {
      const dot = svgEl("circle", { cx: p[0], cy: p[1], r: i === pts.length - 1 ? 4 : 3, class: "s1-dot" });
      hover(dot, `${b[i].label}: mean ${b[i].mean.toFixed(2)}, worst ${LETTERS[Math.floor(b[i].worst)]}, ${b[i].samples} parts` + (b[i].ungraded ? `, ${b[i].ungraded} ungraded` : ""));
      f.svg.append(dot);
    });
  }
}

// ---- reports -------------------------------------------------------------------------------
// The tabs of UI-UX-SPEC.md 4.11, read only (WEB-07). Find a part and Operator flags only for the roles that see
// every report (the runtime refuses the others' calls as well).
let reportTab = "shift", allReports = false;
for (const b of document.querySelectorAll("#report-tabs button")) b.addEventListener("click", () => showReportTab(b.dataset.tab));

function showReportTab(t) {
  if (!allReports && (t === "find" || t === "flags")) t = "shift";
  reportTab = t;
  for (const b of document.querySelectorAll("#report-tabs button")) {
    b.classList.toggle("on", b.dataset.tab === t);
    if (b.dataset.tab === "find" || b.dataset.tab === "flags") b.hidden = !allReports;
  }
  for (const s of document.querySelectorAll(".report-tab")) s.hidden = s.id !== "tab-" + t;
  if (t === "shift") loadShifts();
  if (t === "flags") loadFlags();
  if (t === "files") loadFiles();
}
function loadReports() { showReportTab(reportTab); }

let shiftData = null;
async function loadShifts() {
  try { shiftData = await api("shifts"); } catch (_) { return; }
  drawShifts();
}

const shiftLabel = (s) => `${s.name} · ${new Date(s.fromUtc).toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}` + (s.current ? " (now)" : "");

function drawShifts() {
  const d = shiftData;
  if (!d) return;
  $("shift-title").textContent = `Shifts · ${d.line}` + (d.shifts.some((s) => s.total && !s.fromParts) ? " (inspections; no PLC part log)" : "");
  const body = $("shift-rows");
  body.replaceChildren();
  const pick = $("shift-pick"), was = pick.value;
  pick.replaceChildren();
  for (const s of d.shifts) {
    const tr = el("tr");
    tr.append(el("td", null, shiftLabel(s)), el("td", "ok-text", s.good), el("td", "ng-text", s.bad), el("td", null, s.total),
      el("td", null, s.goodPct === null ? "–" : s.goodPct + " %"));
    body.append(tr);
    const o = el("option", null, shiftLabel(s));
    o.value = s.index;
    pick.append(o);
  }
  if (was) pick.value = was;
  drawShiftReasons();
}

function drawShiftReasons() {
  const s = shiftData.shifts[Number($("shift-pick").value || 0)] || shiftData.shifts[0];
  $("shift-reasons-title").textContent = "Top 5 reasons for bad parts · " + shiftLabel(s);
  const box = $("shift-reasons");
  if (!s.reasons.length) { empty(box, "No bad parts in this shift."); return; }
  const W = Math.max(320, box.clientWidth || 800), row = 26, L = Math.min(260, W * 0.4), H = s.reasons.length * row + 8;
  const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}`, role: "img" });
  const max = Math.max(...s.reasons.map((r) => r.count));
  s.reasons.forEach((r, i) => {
    const y = 4 + i * row;
    const t = svgEl("text", { x: L - 8, y: y + 15, "text-anchor": "end" });
    t.textContent = r.reason.length > 40 ? r.reason.slice(0, 39) + "…" : r.reason;
    const w = Math.max(2, ((W - L - 60) * r.count) / max);
    const bar = svgEl("rect", { x: L, y: y + 2, width: w, height: 18, class: "bar" });
    hover(bar, `${r.reason}: ${r.count}`);
    const v = svgEl("text", { x: L + w + 6, y: y + 15 });
    v.textContent = r.count;
    svg.append(t, bar, v);
  });
  box.replaceChildren(svg);
}
$("shift-pick").addEventListener("change", () => { if (shiftData) drawShiftReasons(); });
$("shift-print").addEventListener("click", () => window.print());

// "Save as PDF" / "Save as Excel": fetched, then saved, so a busy line PC says so instead of opening an error page
async function saveShift(format) {
  const note = $("shift-note");
  note.textContent = "Making the report…";
  try {
    const r = await fetch(`web/api/shift-report?shift=${encodeURIComponent($("shift-pick").value || 0)}&format=${format}`, { credentials: "same-origin" });
    if (r.status === 401) { showSignIn(); return; }
    if (!r.ok) { const j = await r.json().catch(() => ({})); note.textContent = j.error || "The report could not be made."; return; }
    const name = (/filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(r.headers.get("Content-Disposition") || "") || [])[1];
    const a = el("a");
    a.href = URL.createObjectURL(await r.blob());
    a.download = name ? decodeURIComponent(name) : "shift." + format;
    document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 10000);
    note.textContent = "";
  } catch (_) { note.textContent = "The line PC did not answer."; }
}
$("shift-pdf").addEventListener("click", () => saveShift("pdf"));
$("shift-xlsx").addEventListener("click", () => saveShift("xlsx"));

// Find a part: by code or text, or by time
$("find-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const q = $("find-q").value.trim();
  if (q.length < 3) { $("find-note").textContent = "Type at least 3 characters of the code."; return; }
  findParts(`find?q=${encodeURIComponent(q)}&days=${$("find-days").value}`, `“${q}”`);
});
$("find-at-go").addEventListener("click", () => {
  const v = $("find-at").value;
  if (!v) { $("find-note").textContent = "Pick a date and time."; return; }
  const at = new Date(v);
  findParts("find?at=" + encodeURIComponent(at.toISOString()), "within a minute of " + at.toLocaleString());
});

async function findParts(path, what) {
  const note = $("find-note"), box = $("find-results");
  note.textContent = "Searching…";
  let rows;
  try { rows = await api(path); } catch (_) { note.textContent = "The search did not work."; return; }
  box.replaceChildren();
  note.textContent = rows.length ? `${rows.length === 50 ? "The newest 50" : rows.length} inspection${rows.length === 1 ? "" : "s"} ${what}` : "Nothing found " + what + ".";
  for (const r of rows) {
    const card = el("div", "card find-card " + (r.ok ? "ok" : "ng"));
    if (r.imageId) {
      const img = el("img");
      img.alt = "Part image";
      img.loading = "lazy";
      img.src = "web/api/image/" + encodeURIComponent(r.imageId);
      img.addEventListener("error", () => img.replaceWith(el("div", "muted no-image", "Image no longer kept")));
      card.append(img);
    }
    const kv = el("dl", "kv");
    const add = (k, v) => { if (v === null || v === undefined || v === "") return; kv.append(el("dt", null, k), el("dd", null, v)); };
    add("Result", r.ok ? "✓ OK" : "✗ NG");
    add("Time", fmtTime(r.timeUtc));
    add("Part", r.partId);
    add("Read", r.text1);
    add("Text", r.text2 !== r.text1 ? r.text2 : null);
    add("Failed", r.failTool ? r.failTool + (r.reason ? ": " + r.reason : "") : null);
    add("Job", r.job);
    add("Batch", r.batch);
    add("Camera", r.camera + 1);
    add("Cycle", r.ms + " ms");
    add("Image", r.imageId);
    card.append(kv);
    box.append(card);
  }
}

const DECISIONS = { accepted: "Was good", confirmed_ng: "Confirmed bad", accepted_record_only: "Accepted, record only" };
async function loadFlags() {
  let list;
  try { list = await api("flags"); } catch (_) { return; }
  const body = $("flags");
  body.replaceChildren();
  if (!list.length) { const tr = el("tr"); const td = el("td", "muted", "No reviewed parts yet."); td.colSpan = 7; tr.append(td); body.append(tr); }
  for (const f of list) {
    const tr = el("tr");
    const part = el("td");
    if (f.key && allReports) {
      const b = el("button", "link", f.key);
      b.addEventListener("click", () => { showReportTab("find"); $("find-q").value = f.key; findParts("find?days=31&q=" + encodeURIComponent(f.key), `“${f.key}”`); });
      part.append(b);
    }
    tr.append(el("td", null, fmtTime(f.atUtc)), el("td", null, f.job), el("td", null, f.cameraName || "Camera " + (f.camera + 1)),
      el("td", f.decision === "confirmed_ng" ? "ng-text" : "ok-text", DECISIONS[f.decision] || f.decision), el("td", null, f.user), el("td", null, f.note), part);
    body.append(tr);
  }
}

async function loadFiles() {
  let list;
  try { list = await api("reports"); } catch (_) { return; }
  const body = $("reports");
  body.replaceChildren();
  if (!list.length) { const tr = el("tr"); const td = el("td", "muted", "No reports yet."); td.colSpan = 3; tr.append(td); body.append(tr); }
  for (const r of list) {
    const tr = el("tr");
    const a = el("a", null, r.name);
    a.href = "web/api/reports/" + encodeURIComponent(r.name);
    const td = el("td"); td.append(a);
    tr.append(td, el("td", null, fmtTime(r.timeUtc)), el("td", null, Math.max(1, Math.round(r.size / 1024)) + " KB"));
    body.append(tr);
  }
}

// ---- audit trail ---------------------------------------------------------------------------
let auditDays = 1, audit = null;
for (const b of document.querySelectorAll("#audit-days button")) b.addEventListener("click", () => {
  auditDays = Number(b.dataset.days);
  for (const x of document.querySelectorAll("#audit-days button")) x.classList.toggle("on", x === b);
  loadAudit();
});
$("audit-filter").addEventListener("input", drawAudit);

async function loadAudit() {
  try { audit = await api("audit?days=" + auditDays); } catch (_) { return; }
  drawAudit();
}

function drawAudit() {
  if (!audit) return;
  const q = $("audit-filter").value.trim().toLowerCase();
  const has = (...v) => !q || v.some((x) => String(x || "").toLowerCase().includes(q));
  const fill = (body, rows, cols) => {
    body.replaceChildren();
    if (!rows.length) { const tr = el("tr"); const td = el("td", "muted", "Nothing in this time."); td.colSpan = cols; tr.append(td); body.append(tr); }
    for (const cells of rows) { const tr = el("tr"); for (const c of cells) tr.append(el("td", null, c)); body.append(tr); }
  };
  fill($("batches"), audit.batches.filter((b) => has(b.name, b.job, b.user))
    .map((b) => [b.name, b.job, fmtTime(b.startedUtc), b.endedUtc ? fmtTime(b.endedUtc) : "running", b.user]), 5);
  fill($("events"), audit.events.filter((e) => has(e.kind, e.detail)).slice().reverse()
    .map((e) => [fmtTime(e.timeUtc), e.kind, e.active ? "active" : "", e.detail]), 4);
}

start();
