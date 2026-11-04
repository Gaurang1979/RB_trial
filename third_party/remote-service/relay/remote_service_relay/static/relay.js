// The relay's own pages: sign-in and the list of machines this engineer may open. Text from the server is set with
// textContent only.
"use strict";
const $ = (id) => document.getElementById(id);

async function post(path, body) {
  const r = await fetch(path, { method: "POST", body, credentials: "same-origin" });
  return { ok: r.ok, json: await r.json().catch(() => ({})) };
}

if (document.body.dataset.page === "login") {
  $("login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    $("error").textContent = "";
    const r = await post("/login", new URLSearchParams(new FormData(e.target)));
    if (!r.ok) { $("error").textContent = r.json.error || "Sign-in failed."; return; }
    const next = new URLSearchParams(location.search).get("next") || "/";
    location.href = /^\/[A-Za-z0-9][A-Za-z0-9_-]{1,40}\/$/.test(next) ? next : "/";
  });
}

if (document.body.dataset.page === "index") {
  $("logout").addEventListener("click", async () => { await post("/logout"); location.href = "/login"; });
  $("open-form").addEventListener("submit", (e) => {
    e.preventDefault();
    location.href = "/" + encodeURIComponent($("open-id").value.trim()) + "/";
  });
  fetch("/api/machines", { credentials: "same-origin" }).then((r) => {
    if (r.status === 401) { location.href = "/login"; throw new Error("signed out"); }
    return r.json();
  }).then((d) => {
    $("user").textContent = d.user;
    const list = $("machines");
    for (const m of d.machines) {
      const a = document.createElement("a");
      a.href = "/" + encodeURIComponent(m.id) + "/";
      const dot = document.createElement("span");
      dot.className = "dot" + (m.online ? " on" : "");
      const text = document.createElement("span");
      text.className = "grow";
      const id = document.createElement("strong");
      id.textContent = m.id;
      text.append(id, " " + m.name);
      const site = document.createElement("span");
      site.className = "muted";
      site.textContent = m.online ? "connected" : "not connected";
      a.append(dot, text, site);
      const li = document.createElement("li");
      li.append(a);
      list.append(li);
    }
    if (!d.machines.length) list.textContent = "No machines are assigned to you.";
  }).catch(() => { });
}
