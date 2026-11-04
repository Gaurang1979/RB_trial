# Changeover verification: a second person checks before Run

Status: designed and built in PR #213 (2026-10-31). Requirements CV-01 to CV-10 (section 12).

Owner, 2026-10-31: "for changes operator can change batch, text, product etc and all important
things to be verified by supervisor on any other pc or mobile to activate run button. This option
can be given for double check while changeover and can be activated in server admin panel. by
default it is active."

At a changeover the operator picks the product and enters the batch values (lot, dates, MRP, the
texts the print check reads). Before the line can run, a supervisor looks at the same values on
another PC (the plant server's web page) or in the mobile app and accepts them. Until then Start
is refused on the line PC, from every caller (Vision Studio, the PLC, plant software). The check is
on by default and an Admin can turn it off per plant or per line in the plant server.

## 1. Decisions

| # | Decision | Why |
| --- | --- | --- |
| C1 | **What is checked: the changeover record.** The job (product) name, its version, the variant, and every batch field (key, label, value) in tool order. Its **code** is the SHA-256 of the record's canonical form (section 2); both screens show its first 8 hex characters so the supervisor can see they are accepting what the line shows. | Everything the operator can change that decides what the line accepts. The job version is in it, so a job saved again needs a new check. |
| C2 | **When a check is needed:** whenever the record's code differs from the last one accepted on this station, including the first start. Stop and Start with nothing changed needs no new check. | A changeover is a change of what is checked, not a restart. |
| C3 | **The gate is in the station** (`Station.Start`), so every start path goes through it. Start is allowed only when the check is off, or the station holds an acceptance for exactly the current code. A running batch is never stopped by the check (turning it on, or a decision, acts at the next start). Batch values and the job cannot change while the line runs (refused under the station's lock), so parts are never judged against values nobody checked. | Line safety (section 12): nothing runs against values nobody checked; the check never makes a running line stop parts. |
| C4 | **In doubt, the check is on.** The station keeps the last answer (on or off, the acceptance) in its data folder. Before it has ever heard from the plant server, the check counts as on. With the plant server unreachable and the check on, Start stays refused with "Supervisor check needed: the plant server cannot be reached". After a restart a stored answer counts only while the server cannot be reached; with the server reachable the station waits for a fresh answer (one heartbeat), so an edited file opens nothing. A state that cannot be written to disk keeps the line waiting. A station that never had a plant server has no check (nobody could accept); once a plant server has asked for the check, taking the server out of the settings does not end it (the line waits until the server is back, or an engineer deletes `changeover.json`). | Default on (owner). A check that cannot be done must not turn into a pass. |
| C5 | **Transport: the heartbeat.** Every heartbeat (5 s) carries the station's current record and its ask number; the server's answer carries the check's state for it. No new queue: a lost answer is repaired 5 s later. The station only makes outgoing connections, as for NG images. | Same pattern as NG-REVIEW.md section 1. |
| C6 | **Who accepts:** a signed-in person holding admin, plant manager, production manager or quality manager for the line (the NG reviewer roles). Never the operator who made the change (same user name, ignoring case; the operator is the person signed in at the line who changed the product, entered the values or asked again), never a service key or device token. A refusal needs a note. Decisions are only appended, never changed. On a plant server with no accounts (no sign-in) nobody can accept, so the line waits: create accounts, or turn the check off. | Four eyes (PT11-34); a decision names a person. Line PC users and plant server users are separate lists: give a person the same user name in both, or the self-check cannot see them. |
| C7 | **Refused, then asked again.** A refused record stays refused for its code. The operator fixes the values (a new code, a new request) or, when the values are right, presses "Ask again" (the ask number goes up, a new request with the same code). | The operator is never stuck, and every ask is its own record. |
| C8 | **The setting:** `changeover_verify`, true by default, set by a signed-in Admin (never the shared service key, which every phone holds) for a plant or a line; a line's own setting wins over its plant's. Every change is kept with who and when. | Owner: "activated in server admin panel, by default active". |
| C10 | **Accepting at the line PC while the plant server is down** (owner, 2026-11-01: "Yes, allow at PC"). Only while the check waits because the plant server cannot be reached (or was taken out of the settings after it asked for the check); only at the line PC itself, never over the network; by a second person with the Supervisor, QA or Admin role on the line PC, who signs with their own PIN, password or card (counted with the sign-in's wrong tries) without signing the operator out; never the signed-in operator nor the one who made the change; only for the 8-character code their dialog showed. The station sends the acceptance (`accepted_at_line`) with its record until the server has it; the server keeps it as that person's decision (`accept_at_line`, decided by `line:<user>`). A refusal given on the server first (only the station was cut off) wins for the next start. | The line is not stopped by a server outage, and four eyes still hold. Line PC users and plant server users are separate lists. |
| C9 | **What the supervisor sees:** station, line, operator, time, code, each field with its value, and what changed since the last accepted record of this station (old → new, highlighted). | A checker who sees only the new values cannot see a wrong change. |

## 2. Heartbeat (plant event schema, additive)

The station adds `changeover` to every heartbeat once it has a job open:

```json
"changeover": {
  "code": "<64 hex, SHA-256 of the record>",
  "accepted_at_line": {"by": "asha", "name": "Asha Rao", "at": 1793500050000},
  "ask": 1,
  "job": "Carton 24", "job_version": 7, "job_digest": "<64 hex, optional>", "variant": "",
  "batch": [{"key": "lot", "label": "Lot", "value": "L2611"}],
  "operator": "ravi",
  "changed_at": 1793500000000
}
```

`code` is 64 lower-case hex characters; `ask` a whole number 1 or more; `job` 1 to 128
characters; `job_version` a whole number 0 or more; `batch` at most 32 fields, key 1 to 64,
label at most 128, value at most 256 characters; `operator` at most 64 characters (empty when
nobody is signed in at the station); `changed_at` ms; `accepted_at_line` only while a second person's acceptance at
the line PC (C10) is not yet recorded: `by` 1 to 64, `name` at most 128 characters, `at` ms. It is not part of the code. A heartbeat whose `changeover` breaks these
is left out by the server: the heartbeat is still taken (so the station's events keep flowing), the answer has
no `changeover`, and the line stays stopped. The station checks the same rules first (lengths in characters, no
NUL, no half surrogate pair) and, when its values break them, sends no record and shows why on the Run screen.

**Canonical form** (the same bytes in C# and Python, no JSON escaping involved): the texts
`job`, `job_version` as decimal digits, `variant`, then for each batch field in the order sent
its `key`, `label` and `value`; each text written as its UTF-8 byte length in decimal, a colon,
and its UTF-8 bytes, all joined with nothing between. `code` is the lower-case hex SHA-256 of
those bytes. Example: job `A`, version 7, variant empty, one field lot/Lot/L1 gives
`1:A1:70:3:lot3:Lot2:L1`. The server computes it again from what it received; a mismatch leaves the record out as above.

**Job file digest (CV-11).** `job_digest` is the lower-case hex SHA-256 of the job file's bytes as the station read them
(`Job.Digest`, set when the file is loaded), empty or absent from a station that sends none. When present it is written after
the last batch field in the same form (`64:` and the digest) and is part of the code, so a job file copied by hand with the same
name and version, but other bytes, is a new changeover that needs a new check. Without it the canonical form and code are exactly
those above, so older stations and old stored records read as before. The supervisor sees the digest as the row "Job file digest"
and, when it differs from the last accepted record's, as a change. Any difference in the bytes counts (a re-indented copy asks
again): the safe side.

The answer adds:

```json
"changeover": {"required": true, "code": "<the code asked about>", "ask": 1,
               "state": "pending" | "accepted" | "refused" | "not_needed",
               "by": "Priya Shah", "at": 1793500100000, "note": ""}
```

`not_needed` when the setting is off for the line (no request is kept). An answer without
`changeover` (an older server) leaves the station's last known state unchanged.

## 3. Server

- `vision_setting(scope_level, scope_id, key, value, updated_by, updated_at)`: append-only, the
  newest row per (scope, key) is in force.
- `vision_changeover(id, station, plant, line, code, ask, record JSON, operator, changed_at,
  asked_at, state, decided_by, decided_name, decided_at, note)`: one row per (station, code,
  ask), made by the first heartbeat that carries it while the check is on. A newer request from
  the same station marks its older pending ones `superseded`.
- `vision_changeover_decision(id, changeover_id, user_id, name, decision, note, at)`: every
  decision, never changed; the first one decides (a second answer to a decided request is 409).

| Call | Who | What |
| --- | --- | --- |
| `GET /vision/changeover` | view | `{"items": [...]}`, requests newest first; `state` (`pending` default, `accepted`, `refused`, `superseded`, `all`), `plant`, `line`, `limit` (≤ 200); each with its record, code, operator, times, decision, and `previous` (the last accepted record of that station) with `changes` [{key, label, old, new}] |
| `GET /vision/changeover/{id}` | view | one request with its decisions |
| `POST /vision/changeover/{id}/decide` | approver (C6) | `{"decision": "accept" \| "refuse", "note": "…"}`; 403 for the operator themself or a service key, 409 when already decided or superseded, 400 for a refusal without a note |
| `GET /vision/changeover/page` | — | the web page (pending list, details, accept and refuse after a confirmation naming the station and code; cards are kept by request id, so a refresh never moves another request under the click; the setting for Admins) |
| `GET /admin/vision/settings` | admin | every setting row in force |
| `PUT /admin/vision/settings` | signed-in admin | `{"key": "changeover_verify", "plant": "p1", "line": "l1" or null, "value": true \| false}` |

## 4. Station (dotnet)

- **ChangeoverGate** (VisionStudio services) keeps `changeover.json` in the data folder: the last
  known `required` (default true), the current ask number, and the last answer per code. It
  makes the record from the open job and batch, and answers `Allows(code)`.
- **Station.Start** asks the gate first; a refusal is an `InvalidOperationException` with the
  reason, so the runtime returns `runtime.refused.changeover` and the Run screen shows it.
- **Agent** puts the gate's record in each heartbeat and gives the answer back to the gate.
- **Runtime status** adds `Changeover changeover = 21` (required, state, by, at, note, code; 18 to 20 are draft PR #191's);
  `SetInput("changeover.ask")` asks again after a refusal. `AcceptChangeover(user, pin | password | card, code)`
  is the second person's acceptance at the line PC (C10); `Changeover.at_line` says the acceptance was given there.
- **Run screen:** while stopped, a band above the image: "Waiting for the supervisor to check this changeover.
  Code 3fa1c2d0.", "Priya Shah did not accept this changeover: lot is wrong" with an Ask again button, or
  "… the plant server cannot be reached"; Start is faded with the reason until the check allows it. An accepted
  changeover shows no band (nothing needs the operator).

## 5. Mobile app (Vision module)

A "Changeover" list (pending first, with a count on the module's tab), the request with its
changes highlighted and the code, and Accept / Refuse (a note) buttons for a signed-in approver.
A decision needs a live connection and is never queued offline, as for NG review.

## 6. Left for later

- Part 11 e-signature: once e-signatures are built (PART11-DESIGN.md section 6) accepting a
  changeover becomes an `approve` signature that asks for the password again.
- A push notification to the approvers of the line when a request arrives; a count-only call for the mobile badge
  (today it reads up to 200 requests every 5 s).
- The job file's digest in the record, so a job copied by hand with the same name and version is a new changeover.
- Other line screens that start the station directly (not through the runtime) get the same refusal from
  `Station.Start` and show its text, but have no "Ask again" button.
