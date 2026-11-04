# 21 CFR Part 11: users, rights, audit trail and e-signatures

One design for every part of the product: the runtime and Vision Studio on the line PC, the
aggregation station Dashboard, the Vision Package, the plant server, the browser dashboard and
the mobile app. It builds what [requirements section 12](../REQUIREMENTS-VISION-SYSTEM.md)
asks in PT11-01 to PT11-44 (owner, 2026-10-31: "Full software requires 21 CFR part 11
compliance in terms of multi level user login with rights assignment like view, edit and other
with log generation with old values if any to new values if any with all stamps audit trail").

Line safety first (requirements section 12): nothing here may stop inspection or let a bad part
pass. Sign-in, rights, signatures and the audit trail guard **changes**. A part is inspected and
judged the same whether anyone is signed in or not, and a full audit disk or a lost plant
server never turns an NG into OK.

## 1. What exists today (2026-10-31, main)

| Part | Users and sign-in | Roles | Audit | Gaps for Part 11 |
| --- | --- | --- | --- | --- |
| Runtime (`dotnet/Runtime.Host/Access.cs`) | `runtime-users.json`; PBKDF2 100k; PIN, password, card; sessions in memory, 12 h idle on the host (`RuntimeOptions.cs`; the 10 min is only in Studio); 5 wrong per endpoint and name = 1 min lock, unknown cards share one counter; plant tokens may start, stop and change job | Operator, Supervisor, Engineer, Admin, QA (proto `Role`) | none | no audit, PIN or card alone signs in, lock not logged, no user admin RPC, no rights per function |
| Vision Studio (`dotnet/Studio`, Avalonia) | through the runtime's `Session` service | same as runtime | none | no sign dialog, no audit view |
| Dashboard (`dotnet/Dashboard`, Razor) | own `users.json`, PBKDF2 210k, password only, no lockout | Operator, Engineer, QA | SQLite `caselog.db` table `audit(time_utc, user, action, detail)` free text; password re-entry for product sign and model promote | own user store, no old/new values, no record id, reason or meaning, editable table |
| VisionStudio (`dotnet/VisionStudio`, Razor, older) | copy of the Dashboard store | Operator, Engineer, QA | none | superseded by the Avalonia Studio |
| Plant server (`plant_server/backend`) | `spi_user` (integer ids), PBKDF2 200k, a `service` key acting as Admin and an `anon` mode, sessions 7 days, throttle in memory 5 per user+device for 15 min | 8 plant roles with plant/line scope | `alert_event`, `ng_review` (append-only verdicts) | no general audit, user and role changes not audited, `role_assignment` rows deleted, 7-day sessions, throttle lost on restart |
| Mobile (`mobile/plant_app`) | plant server `/auth/login`, token in secure storage | plant roles | none | no sign dialog with meaning, no audit view |
| Plant events (`dotnet/PlantEvents`) | — | — | `job.changed.by`, `signature_id`; `alarm.acknowledged.by` | sent by `StationAgent`/`StationPublisher` (used by `LineRuntime`), may drop under load |
| Time | runtime `TimeProvider`, Dashboard ISO UTC, plant epoch seconds | — | — | no clock check anywhere |
| Results and images | results in the station's SQLite; `FolderQuota` and `NgImageStore` delete kept images by space | — | — | results editable on disk; images removed with no entry |

## 2. Decisions

| # | Decision | Why |
| --- | --- | --- |
| D1 | **One identity per plant.** The plant server's `spi_user` is the master list of people. Each line PC runtime keeps a copy (users, hashes, roles, rights) pulled from the plant server, and signs people in from that copy, so a line keeps working when the server is away. Part 11 needs a plant server (owner, 2026-10-31, PT11-45); a line PC without one is not offered as Part 11 compliant. | PT11-01: one person, one account, across all parts. Today there are four stores. |
| D2 | **One sign-in code on the line PC.** A shared .NET library `VisionIdentity` (store, hashes, lockout, rights, snapshot sync) runs inside the runtime; Vision Studio signs in through the runtime's `Session` service. The aggregation station Dashboard has no runtime today (it references only CodeReader, HikCamera and StationGuard), so it hosts `VisionIdentity` and `VisionAudit` itself, with its own snapshot copy and its own chain. The Dashboard's `users.json` goes. The Razor `dotnet/VisionStudio` is retired and gets no Part 11 work (owner, 2026-10-31). | One lockout, one password policy, one place to check rights. |
| D3 | **Rights are data, roles are bundles.** A right is a key `<function>.<right>` from one catalogue (section 4). A role is a named set of rights, kept at the plant server and copied to the lines. The five line roles and the eight plant roles stay as the default role sets. | PT11-10, PT11-11: Admin adds roles and changes rights without a new release. |
| D4 | **The side that keeps a record checks the right and writes the audit entry.** The runtime for jobs, products, models, settings and users on the line; the plant server for its records. Screens only hide what is not allowed. | PT11-12. A screen check alone can be bypassed. |
| D5 | **One audit entry format** (section 5), in every language, written by one small library per platform (`VisionAudit` for .NET, `backend/audit.py` for the plant server). | PT11-21: the same fields and stamps everywhere; one exporter, one verifier. |
| D6 | **Append-only, hash-chained, anchored.** Each writer keeps its own chain; each entry holds the hash of the one before. The database refuses `UPDATE` and `DELETE` on the audit table (SQLite triggers, PostgreSQL rules and a role without those grants). The line PC sends each chain's head (`seq`, `hash`) in the 5 s StationAgent heartbeat, and the entries themselves at least every minute. The server keeps the newest head per chain; a later upload must contain that exact entry, a new first entry for a station the server already knows is refused, and either case raises an alarm. The server's own chain keeps its head in a file outside the database (`--audit-anchor`, by default next to a SQLite file), rewritten after each entry and checked at start, on demand and every minute; it never moves back and stays fixed at a break (PT11-09) until an Admin, with a reason and a signature, re-anchors it after the break was investigated: a `chain.reanchor` entry that records each break it covers, which later checks still list as investigated (PT11-09a). | PT11-20, PT11-23. A local Windows admin could rebuild the local chain; the heads the server already holds catch it within 5 s of being written. Part 11 needs a plant server (PT11-45), so every chain has this anchor. |
| D7 | **Old and new values are computed, not typed.** Every change goes through one differ that compares the stored record with the new one (JSON paths) and writes one `changes` row per changed field. Large values (images, model weights) are written as SHA-256 and size. | PT11-21, the owner's "old values if any to new values if any". |
| D8 | **Records are versioned.** A saved job, product, model or recipe is never overwritten: a save adds version n+1 with its content hash. Signatures point at a version and its hash. The line runs only an approved version. | PT11-33, PT11-42. |
| D9 | **Two-part sign-in everywhere.** User ID + password, card + PIN, or the organisation's directory (section 3, sign-in providers). PIN alone and card alone end. | PT11-03, 11.200(a). |
| D10 | **The plant server is the time source.** Line PCs sync Windows time (w32time) to the plant server or the plant's NTP server. The runtime compares its clock with the server's at each audit upload; above 2 s each entry is marked `clock: "unsynced"` and an alarm is raised; above 60 s signing is refused until fixed (inspection keeps running). When the last good comparison is older than 10 minutes (server away) entries are marked `clock: "unknown"`; an entry whose time is earlier than the one before it is marked `clock: "backwards"`; Windows time-change events (Security log 4616) are written to the audit trail. Times are stored as UTC with the station's time-zone offset. | PT11-21: a time a user cannot change. |
| D11 | **Audit sync has its own endpoint**, not the plant event stream: entries must arrive all, in order, once. The line keeps every entry until the server has acknowledged it and the line's retention (14 days) has passed. | PT11-24. Live plant events may be dropped under load; audit entries may not. |
| D12 | **Running needs no sign-in; changing does.** Inspection runs signed-out (UI-UX-SPEC principle 10). At the station, start batch, stop, job load, product change, alarm acknowledge and every edit need a signed-in user, written in the entry. | Line safety and PT11-06 together. |
| D13 | **System actors, for run actions only.** The PLC (job change by PLC-INTERFACE 5.2) and plant tokens may do `run` actions (start, stop, job load of an approved version) as named system actors: `user: { "kind": "plc" }` or `{ "kind": "token", "name": "<token name>" }`, audited like a person. They may never edit a GMP record or sign. The plant server's `service` key and `anon` mode are refused for GMP edits and all signatures. A refused PLC job change keeps the previous job and answers Error 200/201, so the PLC holds the line. | PT11-01 attributable; PLC and MES job change keep working. |
| D14 | **Results are records, append-only; images are kept for a short time.** OK/NG results are never updated: a review verdict is a new row (as `ng_review`). Each result holds its image's SHA-256, and each closed batch writes its results digest (SHA-256 over the rows) as an audit entry, so a changed result breaks the check. Images (owner, 2026-10-31, PT11-40a): the line sends a reduced-size copy of each NG image (JPEG, long side at most 1280 px) to the plant server at low priority; the server keeps them 7 days by default, at most 30, and deletes the oldest as new ones arrive, one audit entry per day's deletion. OK images only when switched on in the plant server's admin screens. Full-size images stay on the line under its own quota. | PT11-40; results are the main GMP record. Saving images gives way to performance (owner, 2026-10-29); inspection never waits for an image. |
| D15 | **Approve before load is fixed.** No setting removes "a job or model version must be approved before the line loads it". Admin may add signature rules, never remove this one. | PT11-42 and line safety: an unapproved looser tolerance must not run. |

## 3. Users, sign-in and sessions

**Accounts (PT11-01, PT11-02).** A user has: `id` (UUID, never reused; the plant server's integer `spi_user.id` gets a UUID column by migration), `username` (unique for
ever, also after retirement), `full_name`, `roles` with scope (plant, line, station), `status`
(`active`, `disabled`, `locked`), password hash, PIN hash, card hash, `password_set_at`,
`must_change`, last N password hashes, `esign_accepted_at` (PT11-35), and `identity_checked_by`
(the Admin who checked the person's identity when creating the account, 11.100(b)). There is no
delete: the API has `disable`, and a disabled username stays taken. Role assignments are ended
(`ended_at`), not deleted. A lost card is revoked at once (`card revoke`, 11.300(c)).

**Hashes (PT11-07).** One format everywhere: `pbkdf2-sha256$<iterations>$<salt b64>$<hash
b64>`, 600 000 iterations, 16-byte random salt. Old formats (runtime 100k, Dashboard 210k, plant
200k) are still read and are rehashed at the next good sign-in. Cards keep the shared salt so a
card is found with one hash (studio-signin-card note); the PIN then proves the person.

**Sign-in (PT11-03).**

| Method | First part | Second part | Where |
| --- | --- | --- | --- |
| Password | username | password | everywhere |
| Card | card | PIN (6–8 digits, under the PT11-04 rules: expiry, history, no 123456-style runs) | line PC (local reader) |
| Directory | company account (section 3, sign-in providers) | the directory's own password and second factor | everywhere the plant server allows it. Signing asks for the directory password again, never single sign-on (11.200(a)(1)) |

**Sign-in providers (PT11-03; owner, 2026-10-31: "keep options for various domain login like
windows and other which is being used by various organisation, also keep other login options
(needs to be controlled by plant server)").** The plant server has a provider list, set in its
admin screens only and audited:

| Provider | How | Line PC when the server is away |
| --- | --- | --- |
| Own accounts | user ID + password (this section) | signs in from the snapshot |
| Card + PIN | this section | signs in from the snapshot |
| Windows Active Directory | LDAPS bind or Kerberos, by the plant server | not possible; card + PIN |
| Entra ID, Google Workspace, Okta, other OIDC or SAML | the provider's sign-in page, with `prompt=login` / `ForceAuthn` when signing | not possible; card + PIN |

- Per provider: on or off, which roles may use it, and the mapping from directory groups to
  roles (a group never grants `user.*`; Admins are named one by one).
- A directory account is linked to one `spi_user`, so PT11-01 holds: one person, one account,
  however they sign in. A user removed from the directory is disabled at the next sync.
- Signing re-asks the directory: LDAP bind with the password typed again, or the OIDC or SAML
  provider with forced re-authentication. The line PC asks the plant server; with the server
  away the person signs with card + PIN.
- Directory passwords never reach a line PC; only the plant server talks to the directory.

**Password policy (PT11-04).** Kept at the plant server, copied to the lines: minimum length 8,
three of four classes, expiry 90 days, history 5, `must_change` after an Admin reset. Defaults
from the requirement; Admin changes them with a signature (they are audited settings).

**Lockout (PT11-05).** 5 wrong in a row locks that user **on that device** for 5 minutes, as the
owner decided on 2026-10-30 (NG-REVIEW.md: nobody can lock a colleague out from elsewhere); per
address 20 wrong in 15 minutes locks the address. Wrong tries over the network never lock the
user at the line's own screen, so a guess from a browser cannot block stop, alarm acknowledge or
job load. Wrong tries are also counted per user across devices, only to raise the alert below. The count is kept in the database
(not memory) so a restart does not clear it, and each wrong try and each lock is an audit entry.
Three locks of one user in an hour raise an alert to Admin (plant alert engine). A line PC
reports its locks to the plant server with the audit upload; it does not wait for the server.

**Sessions (PT11-06).** Idle sign-out: line PC 10 min, plant server and browser 15 min, mobile
15 min of no use. The plant server's 7-day session becomes 15 min idle and 12 h at most.
Built 2026-11-03 (PR #279, PT11-08): 15 min idle and 12 h at most on the plant server; an account
with only the Factory display role (a view-only wall screen) has no idle limit, only the 12 h (owner to
confirm); a live stream closes within 5 s of its session ending.
Built 2026-11-03 (PR #306, PT11-09): a sweep every minute ends abandoned sessions (idle or past 12 h) and
audits each end; three locks of one user in an hour (sign-in locks and mobile PIN lockouts, from the
`lock` entries) raise a high alert to Admin that a person resolves; disabling a user needs a reason.
Signing (section 6) asks for the secret again whatever the session's age. The runtime host enforces
the idle time itself (today it keeps sessions 12 h; Studio alone signs out at 10 min). A user with
`must_change` set may change the password and nothing else, and cannot sign (11.200(a)(3)).

**User sync (D1).** The runtime pulls `GET /identity/v1/snapshot?since=<rev>` every minute and
at start (device token, TLS): roles, rights, policy, and only the users whose roles cover that
line or station, each with a revision. Users who hold `user.*` rights (Admins) are never in a line
snapshot: their hashes never reach a line PC, and user, role and policy changes and their
second-Admin signature are made online at the plant server only (Part 11 always has one, PT11-45). This keeps an offline attack on a stolen snapshot to that line's people. It
keeps the last snapshot on disk. A disabled user is refused at the next pull. A password change
made at the line is sent to the plant server at once; when the server is away the change is
refused with "the plant server is not reachable; change your password at the next start", and an
expired password still signs in for one shift with an audit entry `password.expired-grace`.

## 4. Rights catalogue

A right key is `<function>.<right>`. Rights: `view`, `create`, `edit`, `disable`, `approve`,
`export`. Functions (first set; a new screen adds its function here):

| Function | Records | Held by |
| --- | --- | --- |
| `run` | start, stop, batch, job load, product change, alarm acknowledge (`run.edit`) | runtime |
| `job` | jobs and their versions | runtime |
| `product` | product master | runtime / Dashboard |
| `tolerance` | tolerances inside a job | runtime |
| `model` | AI models, promote, roll back | runtime |
| `recipe` | PLC recipes, line settings | runtime |
| `calibration` | calibrations | runtime |
| `result` | results, kept images, NG review verdicts | runtime, plant |
| `report` | reports | runtime, plant |
| `audit` | the audit trail (`view`, `export` only) | runtime, plant |
| `user` | users, roles, rights, policy | plant |
| `setting` | system settings, time, backup | runtime, plant |
| `licence` | licences | runtime |

Default role sets (Admin can change them):

One extra right: `job.edit_wizard` (change a job only through the setup wizard's steps).

| Role | Rights |
| --- | --- |
| Operator | `run.view`, `run.edit`, `job.view`, `product.view`, `result.view`, `report.view` |
| Supervisor | Operator + `tolerance.view`, `tolerance.edit`, `job.edit_wizard`, `result.approve` (NG review) |
| QA | Operator + `tolerance.view`, `model.view`, `recipe.view`, `calibration.view`, `job.approve`, `model.approve`, `tolerance.approve`, `product.approve`, `result.approve`, `report.export`, `audit.view`, `audit.export`, `audit.approve` (periodic review) |
| Engineer | Supervisor + `job.view/create/edit/disable/export`, `model.view/create/edit/disable`, `recipe.*` except `approve`, `calibration.*` except `approve`, `product.view/create/edit/disable` |
| Admin | `user.*`, `setting.*`, `licence.*`, `audit.view`, `audit.export`, all `view`; `user.*` changes need a second Admin's signature (PT11-13). No `approve` on jobs, models or tolerances by default. |
| Plant roles | as `auth.py` today, mapped onto `result`, `report`, `audit`, `user`, `setting` with their scope |

**Nobody approves a version they authored** (or any version they edited in between). This holds
for every role, Admin included, and cannot be turned off (PT11-34 adds a second person on top).

## 5. Audit entry

One JSON object, stored as one row, the same everywhere (snake_case, as the plant events):

```json
{
  "schema": 1,
  "id": "0d6c…uuid",
  "source": { "site": "PLANT-01", "line": "L3", "station": "AGG-1", "component": "runtime", "device_id": "…" },
  "seq": 18234,
  "ts": "2026-10-31T09:14:22.381Z",
  "tz_offset": "+05:30",
  "clock": "synced",
  "user": { "id": "…uuid", "username": "rkumar", "full_name": "R. Kumar", "roles": ["Engineer"] },
  "client": "studio",
  "action": "edit",
  "object": { "type": "job", "id": "…", "name": "Case 48 DM", "version": 7 },
  "changes": [
    { "path": "/tools/2/params/min_grade", "old": "C", "new": "B" },
    { "path": "/tools/2/params/timeout_ms", "old": 450, "new": 500 }
  ],
  "reason": "Customer asked for grade B",
  "signature_id": null,
  "outcome": "ok",
  "detail": null,
  "prev_hash": "9f2c…",
  "hash": "41aa…"
}
```

- `action`: `sign-in`, `sign-in.failed`, `lock`, `sign-out`, `create`, `edit`, `disable`,
  `approve`, `sign`, `sign.failed`, `export`, `print`, `start`, `stop`, `job.load`,
  `mode.change`, `alarm.ack`, `setting.change`, `refused` (a call without the right),
  `backup`, `restore`, `chain.check`, `chain.reanchor` (an Admin's signed restart of the anchor after an investigated break, PT11-09a), `unlock` and `unlock.failed` (a PIN reopening a sign-in on a
  shared phone; its lockout is `lock`). New actions are added, never renamed.
- `changes`: empty for actions that change nothing; `old` is absent on create, `new` absent on
  disable. Secrets never appear: a password change is `{ "path": "/password", "old": "***",
  "new": "***" }`.
- `outcome`: `ok`, `refused`, `failed`. Refused and failed calls are written too.
- `reason`: required for `create`, `edit`, `disable`, `approve` on records in section 4 (except
  `run`), for `setting.change` and for every user, role, rights and policy change, chosen from a
  list per function or typed (PT11-22).
- `user.kind`: `person` (default), `plc`, `token` (D13) or `host` (Vision Package, section 7).
- `hash` = SHA-256 of the entry without `hash`, canonical JSON (RFC 8785). The writer stores the
  exact bytes it hashed; the server checks the hash over the bytes it received and stores those
  bytes, never a re-serialised copy, so .NET and Python number formatting cannot cause a false
  break. Shared test vectors (`truth/audit-vectors.json`) are checked by both libraries. `prev_hash` of the
  first entry of a chain is the SHA-256 of `source` and the chain's start time.
- One chain per `source` (`station` + `component`). `seq` has no gaps; a gap is a broken chain.

**Storage.** Line PC: SQLite `audit.db` in the runtime data folder, owned by the service account,
with `BEFORE UPDATE` and `BEFORE DELETE` triggers that abort; the Dashboard writes its own chain
in its own file with the same library. Plant server: table `audit_entry` (columns for the
indexed fields, the whole entry as JSON) in SQLite or PostgreSQL; on PostgreSQL the app role
has no `UPDATE`/`DELETE` on it.

**Full disk.** The audit write happens before the change is stored; if it fails the change is
refused. If the change then fails, a second entry with `outcome: "failed"` follows. Results are
records of their own (D14), so a full audit store stops changes, never inspection. A refused
stop, alarm acknowledge or job load is safe for parts: the previous job stays, the PLC gets an
error and holds the line. 50 MB of the audit volume is reserved for `run` entries and sign-ins so
those still work when the rest is full. The resource guard warns at 80 % of the audit volume
(a new check in the guard).

**Upload (D11).** `POST /audit/v1/entries` with the device token: a batch of entries of one
chain in `seq` order. The server checks that the first `prev_hash` equals the last stored
`hash` of that chain and every hash; it stores them and answers with the last stored `seq` and
its own time (used by D10). A mismatch is refused, stored as a `chain.broken` alarm on the
server, and shown at the station. Entries wait on the line until acknowledged and are kept for
the retention time either way.

**Check (PT11-23).** At start, nightly and on demand (`audit.view`), each writer walks its
chain; the plant server walks all chains it holds. A break is an alarm with the first bad
`seq`; it never deletes anything.

**Periodic review.** QA reviews the audit trail at a set interval (default monthly) and signs the
review (`audit.approve`, meaning `review`) for the period; an overdue review raises an alert.

**View and export (PT11-26).** Filter by time, user, station, object, action and outcome. Export
PDF (with the chain check result and who exported) and CSV; an export is itself an entry.
Studio and the browser dashboard show it; mobile shows a read-only list for `audit.view`.

**Retention (PT11-25; owner, 2026-10-31: "minimum 1 year storage. Local line level PC storage
can be for 1-2 weeks. Full 1 year storage can be kept in server").** The plant server keeps
every chain, signature and record version for at least 1 year (Admin may set longer, never
shorter). A line PC keeps 14 days (Admin: 7–14). A line entry is pruned only when it is older
than that **and** the server has acknowledged its `seq`; the newest acknowledged entry is never
pruned, so the chain still continues from it. While the server is away nothing is pruned, and
the guard warns at 80 % of the audit volume. Pruning is itself an entry
(`audit.prune`, first and last `seq`). Removal on the server after retention is a later item
(archive to a signed file first).

**Part 11 compliance report (owner, 2026-10-31: "when report is requested from line it pulls
pdf report from server directly, without affecting production line").** The plant server builds
it: for a chosen period and station, every audit item (users and rights in force, sign-ins,
failed sign-ins and locks, changes with old and new values and reasons, signatures with
meaning, chain check result, clock status, exports, backups and restores), signed by the
server's key and stamped with who asked. A line asks with `GET /audit/v1/report.pdf?…`; the
runtime streams the finished file from the server on a low-priority background thread and
does no report work itself; inspection is never slowed. With the server away the line says so
and gives no partial report.

## 6. Electronic signatures

**Signature record**, stored by the side that keeps the record, with an audit entry `sign`:

```json
{
  "id": "…uuid",
  "user": { "id": "…", "username": "aqa", "full_name": "A. Sharma" },
  "meaning": "approve",
  "ts": "2026-10-31T09:20:02.004Z", "tz_offset": "+05:30",
  "object": { "type": "job", "id": "…", "version": 7 },
  "content_hash": "sha256 of the canonical record version",
  "method": "password",
  "reason": "Released for batch 2611"
}
```

- `meaning`: `author`, `review`, `approve`, `responsibility` (PT11-32), shown in words in the
  user's language with the name and time wherever the record is shown, printed or exported.
- Signing asks for both parts again (PT11-31): password, or card + PIN. A wrong secret is
  `sign.failed` and counts towards the lockout.
- A signature is valid while the record's current version hash equals `content_hash`. Any edit
  makes version n+1, unsigned (PT11-33).
- Rules per record type (data, signed): which meanings are needed before use, and whether a
  second person must sign. Admin may add rules, never remove the fixed ones (D15). Fixed: job
  and model versions need `approve` before the line loads them; tolerance changes need
  `approve`; user, role and policy changes need a second Admin's `approve`; batch release needs
  `approve` (PT11-30); an NG review verdict is signed `responsibility`. Also by default (owner,
  2026-10-31): batch start (`responsibility`), line clearance before a new batch (a checklist,
  `responsibility`), alarm acknowledge (`responsibility`) and report export or release
  (`approve`). A batch start or alarm acknowledge whose signature is refused (wrong secret,
  clock in doubt) leaves the line stopped; it never lets parts through. PLC and plant tokens
  (D13) cannot sign, so a batch started from the PLC or MES needs a person's signature at the
  station or through the host's signed-in session (section 7).
- First signature (PT11-35): a one-time screen with the e-signature statement; acceptance is
  stored as `esign_accepted_at` and an audit entry.
- The `sign` audit entry carries the whole signature record, so a signature is inside the hash
  chain and an edited signature row is found (11.70).
- `PlantEvent` `job.changed.signature_id` carries the signature's `id`.

## 7. Contract changes

**Runtime (`proto/visionstudio/remote/v1/runtime.proto`).** Additive, no field renumbered:

- `SignInRequest`: `pin` and `card` share `oneof secret`, so the card's PIN is a new field
  outside it, `string card_pin = 6`; a card without it answers `runtime.signIn.needPin`.
- `WhoAmIResponse`: `repeated string rights`, `bool must_change_password`, `bool esign_accepted`.
- `service Identity`: `ListUsers`, `CreateUser`, `UpdateUser`, `DisableUser`, `ResetSecret`,
  `ListRoles`, `SetRole`, `GetPolicy`, `SetPolicy`, `ChangeOwnPassword`. Each changing call
  carries a `reason` and, where the rules ask, a `SignatureToken`.
- `service Signing`: `Sign(object, version, meaning, reason, secret) -> SignatureToken`,
  `ListSignatures(object)`.
- `service Audit`: `Query(filter, page)`, `Export(filter, format) -> stream bytes`,
  `CheckChain() -> result`.
- Every changing RPC (job save, product save, start, stop, …) gains `string reason` and checks a
  right key instead of `MayChangeSetup`; `RuntimeAccess` becomes a rights lookup.

**Plant server.** New: `GET /identity/v1/snapshot`, `POST /audit/v1/entries`,
`GET /audit/v1/entries` (filter), `GET /audit/v1/export.{csv,pdf}`, `GET /audit/v1/report.pdf`
(the Part 11 compliance report), `POST /audit/v1/check`,
`POST /sign/v1` and the user, role and policy endpoints with reason and signature. Existing
`/admin/users…` endpoints write audit entries. Mobile uses `POST /sign/v1` for verdicts.

**Vision Package.** The host signs its user in through the package's `Session.SignIn` and passes
that session token with each changing call, so the user is proven, not just named; the entry has
`client: "plant_software"`. A host that only asserts a name (`user.kind: "host"`) may run but
never edit a GMP record, sign or approve. The host's "remove products" (requirements section 10)
retires a product; nothing is deleted (PT11-02).

## 8. Records, backup and system checks

- **Records (PT11-40):** results with images, reports, job, product, model and recipe versions,
  signatures and the audit trail, kept for the retention time and exportable as PDF and CSV.
- **Backup (PT11-41):** the backup set includes `audit.db`, the signature tables and every
  record version. A restore is an audit entry and is followed by a chain check; a restored
  chain must continue from the head the plant server holds, or the restore is flagged.
- **Sequence (PT11-42):** the line loads only an approved job version; a batch must be started
  before parts count; enforced in the runtime.
- **Devices (PT11-43):** only stations with a device token write to the plant server (exists
  today); the token's station name is written in `source`.
- **Validation (PT11-44):** a separate item: URS, FS, traceability (PT11 id → design section →
  test), IQ/OQ scripts and results per release.

## 9. Build order

Each line is one pull request (AGENTS.md), in this order; 2 and 3 can run side by side.

1. `VisionAudit` .NET library: entry, canonical JSON, hash chain, SQLite store with triggers,
   differ, chain check, CSV export, shared test vectors; with tests. (Opus: security.)
2. Plant server (started 2026-11-03, PR #279: `backend/audit.py` and `audit_entry` with the sign-in
   entries, chain check at start and on demand, session idle and 12 h; PR #306: alarm on a broken chain,
   the server's own chain head anchored in a file outside the database, session sweep, repeated-lock
   alert, reason for disable; PR #334: reasons for activate and role add and remove, audited, and the Admin's
   signed re-anchor of the chain after an investigated break (PT11-09a); the rest below is still open): `backend/audit.py`, `audit_entry`, upload, query, export, check; identity
   snapshot; hash format and rehash; persistent lockout; session idle and 12 h; password
   policy; user, role and policy changes audited and second-Admin signed; `/sign/v1`; the
   Part 11 compliance report PDF; head check from the heartbeat; `service`/`anon` refused for GMP
   edits. (Opus: security.)
3. Runtime contract and host: rights catalogue, `Identity`, `Signing`, `Audit` services; card +
   PIN; audit entry on every changing RPC; user sync; clock check; job versions and approval
   before load; 14-day pruning after acknowledgement; report fetched from the server.
   (Opus: engine contract and OK/NG path.)
4. Vision Studio: Users and Roles screens, sign dialog with meaning, reason prompts, audit
   viewer and export, first-signature statement. 15 languages (`translator`).
5. Dashboard: `VisionIdentity` in-process (D2), `CaseLog.audit` replaced by `VisionAudit`, product
   sign and model promote as signatures with meaning.
6. Mobile and browser dashboard: sign dialog for verdicts, read-only audit list.
6a. Results append-only with batch digests; record images copied to the server before the
   quota removes them (D14).
7. Validation pack.

## 10. Tests that prove it

- Audit: an edit writes one entry with each changed field's old and new value; a refused call
  writes `refused`; an `UPDATE` or `DELETE` on the table fails; a changed or removed row is
  found by the chain check with its `seq`; an upload with a gap is refused.
- Sign-in: PIN alone and card alone refused; 5 wrong = 5 min lock that survives a restart; idle
  sign-out at 10 and 15 min; a disabled user is refused after the next sync.
- Rights: each default role against each right key, on the runtime and the plant server, not
  only in the screens.
- Signatures: a signed version edited becomes unsigned; author cannot approve their own version
  where the rule says so; the line will not load an unapproved job.
- Clock: a 3 s offset marks entries `unsynced`; a 61 s offset refuses signing and inspection
  still gives results.
- Anchor: a chain truncated or rewritten after its head went out in a heartbeat is refused; a
  new first entry for a known station is refused.
- System actors: a PLC job change of an approved version is audited as `plc`; of an unapproved
  one is refused, the old job stays and the PLC gets Error 200/201.
- Line safety: inspection runs signed-out, with the plant server away and with the audit store
  full, and every NG stays NG.

## 11. Owner answers (2026-10-31)

- Razor `dotnet/VisionStudio` is retired; it gets no Part 11 work (D2).
- Sign-in: the organisation's directories (Windows AD and the common company sign-in services)
  next to own accounts and card + PIN, all controlled from the plant server (section 3).
- Default signatures also on batch start, line clearance, alarm acknowledge and report export
  (section 6).
- Part 11 needs a plant server (D1, PT11-45).
- Lockout stays per user and device (section 3, PT11-05).
- Images: reduced-size NG images 7 days on the server by default, at most 30, rolling; OK images
  optional (D14, PT11-40a).
