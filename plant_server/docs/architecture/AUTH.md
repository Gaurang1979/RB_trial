# User accounts, RBAC and Web Push (M4b; `backend/auth.py`, `backend/webpush.py`)

## Accounts and roles
Sessions use opaque server-side tokens (not JWT, so they can be revoked), wall-clock expiry (7 days), and
PBKDF2-HMAC-SHA256 password hashes (200k iterations, random salt, stdlib only). A user holds one or more role
assignments, each scoped to `org` (everywhere), a `plant`, or a `line`.

| Role | View | Act (acknowledge/resolve) |
|---|---|---|
| `system_administrator` | everywhere | everywhere, plus manages users/roles |
| `corporate_management` | its scope (typically org) | no |
| `plant_manager`, `production_manager`, `maintenance_manager`, `quality_manager`, `it_ot` | its scope | its scope |
| `factory_display` | its scope (kiosk) | no |

**Bootstrap:** `POST /admin/users` is open when the store has zero users (creates the first account, always
also granted `system_administrator` at org scope so the system is never left without an admin); after that it
requires an admin. See `docs/api/API_CONTRACT.md` for the full endpoint list.

**Backward compatible by construction:** a server with no accounts and no `SPI_API_KEY` behaves exactly as in
M1-M3 (fully open). The shared API key still grants full ("service") access, unchanged, for gateways/scripts.
RBAC applies only once accounts exist; a scoped user simply sees a filtered `/plants` and `/alerts`, and gets
403 outside their scope (line/plant/equipment endpoints; alert acknowledge/resolve).

**Audit:** acknowledging/resolving an alert while signed in records the session's username, not whatever the
client claims in the request body (the body's `user` field is only used as a fallback for the no-accounts
service-key path, kept for backward compatibility with M4's audit trail).

## Web Push
Real background notifications (RFC 8291/8292: VAPID + aes128gcm), via the browser vendor's own push service -
no third-party server of ours in the path. `backend.serve --vapid-key <path>` generates (once) and persists a
key pair; the dashboard's "Enable notifications" button subscribes a signed-in user's browser and posts the
subscription to `/webpush/subscribe`. An alert rule opts in with `"channels": [..., "webpush"]`; at send time
the engine resolves each role recipient to the actual users holding it *within the alert's plant/line scope*
(`auth.users_with_role`) and pushes to each of their devices. A subscription the browser has revoked (push
service returns 404/410) is removed automatically.

**Verified for real, in this sandbox:** the full pipeline end to end - real VAPID keys, a real running server,
real password login and RBAC-scoped subscription, a real alert from the demo simulator, a real encrypted HTTP
POST captured by a throwaway local server standing in for a push service, independently decrypted with a
separately generated "browser" key pair and confirmed byte-for-byte against the original alert (see
`tests/test_webpush.py` and the module docstring in `backend/webpush.py`).
**Not verified, for lack of one:** delivery through an actual push service (FCM/Mozilla autopush/...) to an
actual phone. That needs an HTTPS deployment and a real subscription - confirm it there before relying on it.

## Known limits
- No password reset flow (an admin must know the old password, or recreate the account).
- No rate limiting on `/auth/login` (add one, e.g. at a reverse proxy, before exposing this beyond a trial).
- `area`-level scope is not modelled (only org/plant/line); a role can be assigned per line to approximate it.
- Dashboard sign-in is a minimal prompt()-based flow, not a form; there is no admin UI yet for managing users
  (use the API directly, e.g. via `/docs`).

## Plant master data and devices (PL1, 2026-10-29)
Plants, areas, lines and devices are tables shared by every module (Production, Vision, printing;
`docs/vision-system/PLANT-PLATFORM.md`). At start `backend.serve` copies the site JSON's plants, areas,
lines and gateways into them (`backend/master.py: sync_site`); the JSON stays the description of
Production's equipment. Lines used only by vision stations or printers are added by an administrator.

| Endpoint | Who | What |
|---|---|---|
| `GET /admin/master` | admin | plants, areas, lines |
| `POST /admin/plants`, `/admin/areas`, `/admin/lines` | admin | add or change one (rows from the site JSON are changed there: 409) |
| `GET /admin/devices` | admin | every device, with `has_token`, never the token |
| `POST /admin/devices` | admin | add a device (`vision_station`, `production_gateway`, `tij_printer`); answers its token **once** |
| `POST /admin/devices/{id}/token` | admin | a new token; the old one stops at once |
| `POST /admin/devices/{id}/disable`, `/enable` | admin | a disabled device's token is refused |

The server keeps only a device token's SHA-256 (`master.device_for_token`), so a database copy gives no
working token, and the token names the device, so a device never writes for another.
