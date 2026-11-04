// The service console (DESIGN.md section 3): files, diagnostics, actions and data views of one machine. It only calls the
// agent's API next to it ("../" = /<machine>/_rs/); the agent checks every call. Text from the machine is set with
// textContent only.
"use strict";

const $ = (id) => document.getElementById(id);
const api = (path) => "../" + path;
let me = null, root = "", path = "";

function status(text, kind) {
  $("status").textContent = text || "";
  $("status").className = "status " + (kind || "");
}

async function call(path, opts) {
  const r = await fetch(api(path), Object.assign({ credentials: "same-origin" }, opts || {}));
  if (r.status === 401) { showSignIn(); throw new Error("Please sign in."); }
  const type = r.headers.get("content-type") || "";
  const body = type.startsWith("application/json") ? await r.json() : null;
  if (!r.ok) throw new Error((body && body.error) || ("Error " + r.status));
  return body;
}

function el(tag, text, cls) {
  const e = document.createElement(tag);
  if (text !== undefined && text !== null) e.textContent = String(text);
  if (cls) e.className = cls;
  return e;
}

const size = (n) => n < 1024 ? n + " B" : n < 1 << 20 ? (n / 1024).toFixed(1) + " KB" : (n / (1 << 20)).toFixed(1) + " MB";

// ---- sign-in -------------------------------------------------------------------------------------------------------------
function showSignIn() {
  for (const t of document.querySelectorAll(".tab")) t.hidden = true;
  $("tabs").hidden = true;
  $("sign-out").hidden = true;
  $("who").textContent = "";
  $("sign-in").hidden = false;
}

$("sign-in-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  $("sign-in-error").textContent = "";
  const r = await fetch(api("sign-in"), { method: "POST", credentials: "same-origin", body: new URLSearchParams(new FormData(e.target)) });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) { $("sign-in-error").textContent = j.error || "Sign-in failed."; return; }
  e.target.reset();
  start();
});

$("sign-out").addEventListener("click", async () => {
  await fetch(api("sign-out"), { method: "POST", credentials: "same-origin" }).catch(() => { });
  showSignIn();
});

// ---- tabs ----------------------------------------------------------------------------------------------------------------
for (const b of document.querySelectorAll("#tabs button")) b.addEventListener("click", () => show(b.dataset.tab));
function show(tab) {
  for (const b of document.querySelectorAll("#tabs button")) b.classList.toggle("on", b.dataset.tab === tab);
  for (const t of document.querySelectorAll(".tab")) t.hidden = t.id !== "tab-" + tab;
  status("");
}

async function start() {
  try { me = await call("me"); } catch (_) { return; }
  $("sign-in").hidden = true;
  $("tabs").hidden = false;
  $("sign-out").hidden = false;
  $("title").textContent = (me.product ? me.product + " · " : "") + me.machine;
  $("who").textContent = me.user + " (" + me.role + ")";
  $("pages-link").hidden = !me.pages;
  $("diagnostics").hidden = !me.diagnostics;
  $("software-tab").hidden = !me.packages;
  fillRoots();
  fillActions();
  fillViews();
  if (me.packages) software();
  $("corrections-tab").hidden = !me.corrections;
  if (me.corrections) corrections();
  show("files");
}

// ---- files ---------------------------------------------------------------------------------------------------------------
function fillRoots() {
  const sel = $("root");
  sel.textContent = "";
  for (const r of me.roots) sel.append(new Option(r.name + (r.writable ? " (upload)" : ""), r.name));
  root = me.roots.length ? me.roots[0].name : "";
  path = "";
  listFiles();
}
$("root").addEventListener("change", (e) => { root = e.target.value; path = ""; listFiles(); });

async function listFiles() {
  const body = $("files");
  body.textContent = "";
  const r = me.roots.find((x) => x.name === root);
  $("upload-form").hidden = !(r && r.writable);
  $("upload-note").textContent = r && r.extensions.length ? "Types: " + r.extensions.join(", ") : "";
  crumbs();
  if (!root) { body.append(el("tr")).append(el("td", "No folders on this machine.", "muted")); return; }
  let rows;
  try { rows = await call("files?root=" + encodeURIComponent(root) + "&path=" + encodeURIComponent(path)); }
  catch (e) { status(e.message, "bad"); return; }
  for (const f of rows) {
    const tr = el("tr");
    const name = el("td");
    const b = el("button", (f.dir ? "📁 " : "") + f.name);
    b.addEventListener("click", () => {
      const p = (path ? path + "/" : "") + f.name;
      if (f.dir) { path = p; listFiles(); }
      else location.href = api("file?root=" + encodeURIComponent(root) + "&path=" + encodeURIComponent(p));
    });
    name.append(b);
    tr.append(name, el("td", f.dir ? "" : size(f.size), "num"), el("td", new Date(f.modifiedUtc).toLocaleString()));
    body.append(tr);
  }
  if (!rows.length) body.append(el("tr")).append(el("td", "Empty folder.", "muted"));
}

function crumbs() {
  const c = $("crumbs");
  c.textContent = "";
  const parts = path ? path.split("/") : [];
  const top = el("button", "/");
  top.addEventListener("click", () => { path = ""; listFiles(); });
  c.append(top);
  parts.forEach((p, i) => {
    const b = el("button", p + "/");
    b.addEventListener("click", () => { path = parts.slice(0, i + 1).join("/"); listFiles(); });
    c.append(b);
  });
}

$("diagnostics").addEventListener("click", () => { location.href = api("diagnostics"); });

async function sha256Hex(buf) {
  const h = await crypto.subtle.digest("SHA-256", buf);
  return [...new Uint8Array(h)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

$("upload-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const file = $("upload-file").files[0];
  if (!file) return;
  const target = (path ? path + "/" : "") + file.name;
  try {
    status("Checking " + file.name + "…");
    const buf = await file.arrayBuffer();
    const sha256 = await sha256Hex(buf);
    const up = await call("uploads", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ root, path: target, size: file.size, sha256 }) });
    for (let off = 0; off < file.size; off += up.chunk) {
      status("Uploading " + file.name + ": " + Math.round(100 * off / file.size) + " %");
      await call("uploads/" + up.id + "?offset=" + off, { method: "POST", body: buf.slice(off, off + up.chunk) });
    }
    const fin = await call("uploads/" + up.id + "/finish", { method: "POST" });
    await follow(fin.job, file.name);
    listFiles();
  } catch (err) { status(err.message, "bad"); }
});

// ---- jobs ----------------------------------------------------------------------------------------------------------------
async function follow(id, what) {
  for (;;) {
    const j = await call("jobs/" + id);
    if (j.state === "waiting") status(what + ": " + (j.message || "waiting for the machine") + "…");
    else if (j.state === "running") status(what + ": running…");
    else if (j.state === "done") { status(what + ": " + (j.result || "done"), "ok"); return j; }
    else { status(what + ": " + (j.message || j.state), "bad"); return j; }
    await new Promise((r) => setTimeout(r, 1500));
  }
}

// ---- actions -------------------------------------------------------------------------------------------------------------
function fillActions() {
  const list = $("actions");
  list.textContent = "";
  for (const a of me.actions) {
    const li = el("li");
    li.append(el("strong", a.title), el("span", a.description, "muted"));
    const notes = [a.needsStopped ? "only when the machine is stopped" : "", a.needsConfirmation ? "the operator confirms" : ""].filter(Boolean);
    if (notes.length) li.append(el("span", notes.join("; "), "muted"));
    const b = el("button", "Run", "primary");
    b.addEventListener("click", async () => {
      if (!confirm("Run \"" + a.title + "\" on this machine?")) return;
      b.disabled = true;
      try {
        const r = await call("actions/" + encodeURIComponent(a.name), { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
        await follow(r.job, a.title);
      } catch (err) { status(err.message, "bad"); }
      b.disabled = false;
    });
    li.append(b);
    list.append(li);
  }
  if (!me.actions.length) list.append(el("li", "No actions on this machine.", "muted"));
}

// ---- data views ----------------------------------------------------------------------------------------------------------
function fillViews() {
  const sel = $("view");
  sel.textContent = "";
  for (const v of me.views) sel.append(new Option(v.title, v.name));
  params();
}
$("view").addEventListener("change", params);

function params() {
  const box = $("params");
  box.textContent = "";
  const v = me.views.find((x) => x.name === $("view").value);
  for (const p of v ? v.parameters : []) {
    const i = el("input");
    i.placeholder = p;
    i.dataset.param = p;
    box.append(i);
  }
  $("view-table").textContent = "";
  $("view-note").textContent = me.views.length ? "" : "No data views on this machine.";
}

function viewQuery(extra) {
  const q = new URLSearchParams();
  for (const i of document.querySelectorAll("#params input")) if (i.value) q.set(i.dataset.param, i.value);
  for (const [k, v] of Object.entries(extra || {})) q.set(k, v);
  return "views/" + encodeURIComponent($("view").value) + "?" + q.toString();
}

$("view-show").addEventListener("click", async () => {
  const t = $("view-table");
  t.textContent = "";
  try {
    const d = await call(viewQuery());
    const head = el("tr");
    for (const c of d.columns) head.append(el("th", c));
    t.append(el("thead")).append(head);
    const body = el("tbody");
    for (const r of d.rows) {
      const tr = el("tr");
      for (const c of r) tr.append(el("td", c));
      body.append(tr);
    }
    t.append(body);
    $("view-note").textContent = d.rows.length + " rows" + (d.truncated ? " (more exist; narrow the parameters)" : "") + ". Read-only.";
  } catch (err) { status(err.message, "bad"); }
});
$("view-csv").addEventListener("click", () => { location.href = api(viewQuery({ format: "csv" })); });

// ---- software (OEM) ------------------------------------------------------------------------------------------------------
async function software() {
  let p;
  try { p = await call("packages"); } catch (e) { status(e.message, "bad"); return; }
  $("sw-product").textContent = p.product;
  $("sw-current").textContent = p.current || "none";
  $("sw-previous").textContent = p.previous || "";
  $("sw-previous-box").hidden = !p.previous;
  $("rollback").disabled = !p.previous;
}

$("package-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const file = $("package-file").files[0];
  if (!file) return;
  try {
    status("Checking " + file.name + "…");
    const buf = await file.arrayBuffer();
    const up = await call("packages", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: file.name, size: file.size, sha256: await sha256Hex(buf) }) });
    for (let off = 0; off < file.size; off += up.chunk) {
      status("Uploading " + file.name + ": " + Math.round(100 * off / file.size) + " %");
      await call("uploads/" + up.id + "?offset=" + off, { method: "POST", body: buf.slice(off, off + up.chunk) });
    }
    status("Checking the signature and every file…");
    const inst = await call("packages/" + up.id + "/install", { method: "POST" });
    await follow(inst.job, "Install " + inst.version);
  } catch (err) { status(err.message, "bad"); }
  software();
});

$("rollback").addEventListener("click", async () => {
  if (!confirm("Go back to the previous version on this machine?")) return;
  try {
    const r = await call("packages/rollback", { method: "POST" });
    await follow(r.job, "Go back to " + r.version);
  } catch (err) { status(err.message, "bad"); }
  software();
});

// ---- corrections ---------------------------------------------------------------------------------------------------------
let kinds = [], opened = null;
async function corrections() {
  try { kinds = await call("corrections"); } catch (e) { status(e.message, "bad"); return; }
  const sel = $("kind");
  sel.textContent = "";
  for (const k of kinds) sel.append(new Option(k.title, k.name));
}

$("find-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  $("correct-form").hidden = true;
  const kind = kinds.find((k) => k.name === $("kind").value);
  if (!kind) return;
  try {
    const r = await call("corrections/" + encodeURIComponent(kind.name) + "?record=" + encodeURIComponent($("record").value.trim()));
    opened = { kind, record: r.record };
    const body = $("fields");
    body.textContent = "";
    for (const f of kind.fields) {
      const tr = el("tr");
      let input;
      if (f.choices.length) {
        input = el("select");
        for (const c of f.choices) input.append(new Option(c, c));
      } else {
        input = el("input");
        input.maxLength = f.maxLength;
      }
      input.value = r.values[f.name] || "";
      input.dataset.field = f.name;
      const cell = el("td");
      cell.append(input);
      tr.append(el("td", f.title), el("td", r.values[f.name] || ""), cell);
      body.append(tr);
    }
    $("correct-form").hidden = false;
    status("");
  } catch (err) { status(err.message, "bad"); }
});

$("correct-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!opened) return;
  const changes = {};
  for (const i of document.querySelectorAll("#fields [data-field]")) changes[i.dataset.field] = i.value;
  try {
    const r = await call("corrections/" + encodeURIComponent(opened.kind.name), { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ record: opened.record, changes, reason: $("correct-reason").value }) });
    const j = await follow(r.job, "Correction of " + opened.record);
    if (j.state === "done") { $("correct-form").hidden = true; $("correct-reason").value = ""; }
  } catch (err) { status(err.message, "bad"); }
});

// ---- start ---------------------------------------------------------------------------------------------------------------
start();
