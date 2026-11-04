# API (implemented in M3: `backend/api.py`; interactive docs at `/docs` when running)

Auth: shared service key via env `SPI_API_KEY` (header `X-API-Key`, WebSocket `?api_key=`), and/or user accounts with
role-scoped access (`Authorization: Bearer <token>`, WebSocket `?token=`) - see `docs/architecture/AUTH.md` for the
full model (roles, scope, Web Push). No key and no accounts = fully open (local/dev). `/health` and the dashboard
page are always open.

| Endpoint | Returns |
|---|---|
| `POST /auth/login`, `/auth/logout`, `GET /auth/me`, `POST /auth/change-password` | session tokens |
| `POST /admin/users`, `GET /admin/users`, `POST /admin/users/{id}/roles`, `DELETE .../roles/{id}`, `POST .../activate`\|`deactivate` | admin only (or bootstrap: first user ever); `deactivate`, `activate`, `POST .../roles` (with the role) and `DELETE .../roles/{id}` (a JSON body) need `{"reason": "..."}` (3 to 500 characters, kept in the audit entry, Part 11 PT11-22, PT11-09a) |
| `GET /admin/audit`, `GET /admin/audit/check`, `POST /admin/audit/reanchor` | Admin only. `reanchor` (`{"reason": "...", "password": "..."}`, a signed-in Admin; the password is the e-signature) re-anchors the audit chain after a break was investigated: a `sign` and a `chain.reanchor` entry, then the check result; 409 when there is no break, 403 for a wrong password or anyone else (PT11-09a) |
| `GET /webpush/public-key`, `POST /webpush/subscribe`, `/unsubscribe`, `GET /webpush/subscriptions` | Web Push; subscribe/unsubscribe require sign-in |

| Endpoint | Returns |
|---|---|
| `GET /health` | status, demo flag, data time, event counts |
| `GET /plants`, `/plants/{id}/lines` | hierarchy |
| `GET /lines/{id}/status` | live read model: equipment states (since, confidence, evidence), queues, KPI window, bottleneck, attention items |
| `GET /lines/{id}/equipment`, `/lines/{id}/bottlenecks` | equipment status list; bottleneck candidate + utilization |
| `GET /equipment/{id}/status`, `/states`, `/downtime`, `/production?bucket=minute\|hour\|day` | current state; state intervals; downtime events; production buckets (+ counter-reconciled total) |
| `GET /sensors/{id}/readings` | measured readings |
| `GET /reports/oee?line_id=` | per-equipment availability/performance/quality/OEE + line output vs target |
| `GET /alerts?state=active\|open\|acknowledged\|resolved&line_id=` | alerts (newest first) |
| `GET /alerts/{id}` | alert with audit trail and notifications |
| `POST /alerts/{id}/acknowledge` `{user, note}` | acknowledge (409 if not OPEN) |
| `POST /alerts/{id}/resolve` `{user, reason}` | manual resolve (reason required) |
| `GET /alert-rules` | configured rules |
| `GET /sensors/{id}/health`, `/lines/{id}/sensors` | sensor health (status, since, last valid, current, reason) |
| `GET /ingest/stats` | event counts, rejected, sequence gaps per gateway |
| `WS /stream/lines/{id}?interval=1` | pushes the line status |

Query window: `from` / `to` as `2026-01-01T00:00:00Z`, default = last 60 min of data time. Every payload carries `basis`
(measured | calculated | detected). Planned, not implemented: dashboard configuration, shift buckets, password reset.
