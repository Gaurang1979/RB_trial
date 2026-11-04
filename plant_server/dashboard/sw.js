// Minimal service worker: makes the dashboard installable and shows notifications. It does not cache data (live data must never be stale).
// Background Web Push (VAPID) is a later step; today notifications appear while the dashboard is open.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", e => e.waitUntil(self.clients.claim()));
self.addEventListener("push", e => {
  let d = {};
  try { d = e.data ? e.data.json() : {}; } catch (_) {}
  e.waitUntil(self.registration.showNotification(d.title || "Production alert", { body: d.body || "", tag: d.tag || "spi", data: d }));
});
self.addEventListener("notificationclick", e => {
  e.notification.close();
  e.waitUntil(self.clients.matchAll({ type: "window" }).then(cs => (cs.length ? cs[0].focus() : self.clients.openWindow("/"))));
});
