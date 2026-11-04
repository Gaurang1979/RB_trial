// DOM smoke test for dashboard/index.html: runs the page's real script in jsdom with stubbed fetch/WebSocket.
const fs = require("fs"), path = require("path");
const { JSDOM } = require("jsdom");
const [fixturePath, htmlPath] = process.argv.slice(2);
const status = JSON.parse(fs.readFileSync(fixturePath, "utf8"));
const html = fs.readFileSync(htmlPath, "utf8");
const errors = [], calls = [];
const dom = new JSDOM(html, {
  url: "http://localhost:8000/", runScripts: "dangerously", pretendToBeVisual: true,
  beforeParse(w) {
    w.prompt = (msg) => {
      if (msg && /username/i.test(msg)) return "asha";
      if (msg && /password/i.test(msg)) return "s3cret";
      return "tester";
    };
    w.fetch = async (u, o) => {
      calls.push([u, o]);
      if (u === "/auth/login") return { ok: true, status: 200, json: async () => ({ token: "tok123", expires_at: "2026-01-01T00:00:00Z",
        user: { user_id: 1, username: "asha", name: "Asha Rao", roles: [], is_admin: false } }) };
      if (u === "/auth/logout") return { ok: true, status: 200, json: async () => ({ ok: true }) };
      return { ok: true, status: 200, json: async () => u.startsWith("/health") ? { demo: true } :
        u.startsWith("/plants") ? [{ id: "P", name: "Plant", lines: [{ id: status.line_id, name: status.name }] }] : status };
    };
    w.WebSocket = class { constructor(u) { this.url = u; setTimeout(() => this.onmessage && this.onmessage({ data: JSON.stringify(status) }), 10); } close() {} };
    w.addEventListener("error", e => errors.push(String(e.message)));
  },
});
const d = dom.window.document;
const check = (name, ok) => { if (!ok) { console.error("FAIL:", name); process.exitCode = 1; } else console.log("ok:", name); };
setTimeout(async () => {
  check("no script errors", errors.length === 0);
  check("demo badge visible", d.getElementById("demo").hidden === false);
  check("line tab rendered", d.querySelectorAll(".tab[data-id]").length === 1);
  const cards = d.querySelectorAll(".mc");
  check("one card per machine", cards.length === status.equipment.length);
  check("machine codes shown", status.equipment.every(e => d.getElementById("flow").textContent.includes(e.equipment_id)));
  check("queues drawn between machines", d.querySelectorAll(".q").length === status.queues.length);
  check("state text shown", status.equipment.every(e => d.getElementById("flow").textContent.includes(dom.window.SPI.STATES[e.state][1])));
  check("kpi tiles", d.querySelectorAll("#kpis .card").length === 4);
  check("equipment table rows", d.querySelectorAll("#table tr").length === status.equipment.length + 1);
  check("clock shows data time", d.getElementById("clock").textContent.includes("data time"));
  const al = status.alerts || [];
  check("alerts rendered", d.querySelectorAll("#alerts .al").length === al.length && al.length > 0);
  check("acknowledge button per open alert", d.querySelectorAll("#alerts [data-ack]").length === al.filter(a => a.state === "OPEN").length);
  const btn = d.querySelector("#alerts [data-ack]");
  btn.click();
  await new Promise(r => setTimeout(r, 50));
  const post = calls.find(c => c[1] && c[1].method === "POST");
  check("acknowledge posts user to the alert", post && post[0] === `/alerts/${btn.dataset.ack}/acknowledge` && JSON.parse(post[1].body).user === "tester");
  const before = d.getElementById("attention").innerHTML;
  const xss = JSON.parse(JSON.stringify(status)); xss.attention = [{ severity: "high", equipment_id: "X", title: "<img src=x onerror=alert(1)>", why: "w", effects: [], action: "a", estimated_loss_units: 1, estimate_basis: "b" }];
  dom.window.SPI.render(xss);
  check("untrusted text is escaped", !d.getElementById("attention").querySelector("img") && d.getElementById("attention").textContent.includes("<img"));

  // --- login flow: sign in, then acknowledge using the authenticated identity (no name prompt) ---
  check("sign-in button shown before login", d.getElementById("loginbtn").textContent === "Sign in");
  d.getElementById("loginbtn").click();
  await new Promise(r => setTimeout(r, 80));
  const loginPost = calls.find(c => c[0] === "/auth/login");
  check("login posts the entered username and password", loginPost && JSON.parse(loginPost[1].body).username === "asha" && JSON.parse(loginPost[1].body).password === "s3cret");
  check("session token stored after login", dom.window.localStorage.getItem("spi_token") === "tok123");
  check("login button shows the signed-in name", /Asha Rao/.test(d.getElementById("loginbtn").textContent) && /sign out/.test(d.getElementById("loginbtn").textContent));

  const preAckCalls = calls.length;
  const btn2 = d.querySelector("#alerts [data-ack]");
  if (btn2) {
    btn2.click();
    await new Promise(r => setTimeout(r, 80));
    const ackPost = calls.slice(preAckCalls).find(c => c[1] && c[1].method === "POST" && c[0].includes("/acknowledge"));
    check("signed-in acknowledge uses the session identity, not a name prompt", ackPost && JSON.parse(ackPost[1].body).user === "asha");
    check("acknowledge is sent with the bearer token", ackPost && ackPost[1].headers && ackPost[1].headers["Authorization"] === "Bearer tok123");
  } else {
    check("signed-in acknowledge uses the session identity, not a name prompt", false);
  }

  d.getElementById("loginbtn").click();   // now signed in: this click signs out
  await new Promise(r => setTimeout(r, 80));
  check("logout clears the stored session", dom.window.localStorage.getItem("spi_token") === null);
  check("login button reverts to Sign in after logout", d.getElementById("loginbtn").textContent === "Sign in");

  process.exit(process.exitCode || 0);
}, 300);
