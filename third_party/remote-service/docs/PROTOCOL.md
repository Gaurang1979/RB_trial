# Remote service protocol, version 1

What an agent and the relay say to each other. An agent in any language that follows this document works
with the relay and the service console. Design and reasons: [DESIGN.md](DESIGN.md).

## 1. The link

The agent opens a WebSocket to the relay: `wss://<relay>/link`.

| Header | Value |
| --- | --- |
| `X-Machine-Id` | The machine number, 2–41 characters: `A-Z a-z 0-9 _ -` |
| `Authorization` | `Bearer <link token>` (printed once by `relay add-machine`) |
| `X-RS-Protocol` | `1` |

- **Refused:** the relay closes with code 1008 for an unknown machine or a wrong token.
- **Keep-alive:** both sides send WebSocket pings every 20 s. A link with no answer for 40 s is dead.
- **Reconnect:** dial again with back-off from 1 s up to 30 s. Reset the back-off to 1 s only after the
  link has been up for 60 s.
- **Duplicate:** a newer link for the same machine replaces the older one.

Every message is one JSON text frame. A frame the receiver does not understand is ignored. A malformed
frame drops the link; it never stops the host.

## 2. Requests: relay → agent

The relay forwards every browser request for `/<machine>/<path>` as one `req` frame:

```json
{"t": "req", "id": "<1-64 chars>", "method": "GET|POST", "path": "/<path as sent, still percent-encoded>",
 "query": "?a=1" , "headers": {"accept": "…", "content-type": "…", "cookie": "…", "last-event-id": "…"},
 "user": "<relay account>", "role": "service|oem", "body": "<base64, at most 1 MiB>"}
```

- **Headers:** only `accept`, `accept-language`, `content-type`, `last-event-id`, `if-none-match` and
  `cookie` are forwarded. The `cookie` value never contains the relay's own `sr_session`.
- **Identity:** `user` and `role` come from the relay's sign-in. The agent never takes them from the
  browser.
- **Routing:** the agent serves paths under `/_rs/` itself (section 4). It forwards every other path to
  the host's local web pages, if the host has any; otherwise it answers 404.
- **Size:** a request body larger than 1 MiB is refused with 413 by the relay.

## 3. Answers: agent → relay

```json
{"t": "head", "id": "…", "status": 200, "headers": {"Content-Type": ["text/html"], "Set-Cookie": ["…"]}}
{"t": "data", "id": "…", "b64": "<at most 32 KiB, base64>"}
{"t": "end",  "id": "…"}                      // or {"t": "end", "id": "…", "error": "…"}
```

- **Order:** `head` comes first, once. Then zero or more `data` frames, then one `end` frame. An agent
  that cannot answer sends `end` with an `error` and no `head`; the relay answers the browser 502.
- **Cancel:** the relay sends `{"t": "cancel", "id": "…"}` when the browser leaves, when no `head` came
  within 20 s, or when the browser falls more than 64 `data` frames behind. The agent stops that answer.
- **Headers the relay passes back:** `Content-Type`, `Content-Disposition`, `Cache-Control`, `ETag` and
  `Set-Cookie`, plus any header starting with `X-` that the host names.
- **Headers the relay sets itself:** the page security headers (CSP, frame, referrer). A machine cannot
  loosen them.
- **Cookies:** the relay rewrites every `Set-Cookie` to the path `/<machine>/`, `SameSite=Strict` and
  `Secure`. It drops a `Domain` and drops any cookie named `sr_session`.
- **Ping:** `{"t": "ping"}` from the relay is answered with `{"t": "pong"}`.

## 4. The agent's own API (`/_rs/`)

All answers are JSON unless stated. Every call except `sign-in`, `ui/*` and `info` needs the session
cookie `rs_session`.

| Method and path | Capability | What it does |
| --- | --- | --- |
| `GET /_rs/ui/` (and `ui/<file>`) | — | The service console page |
| `GET /_rs/info` | — | `{"protocol":1, "product":"…", "machine":"…", "signedIn":bool}` |
| `POST /_rs/sign-in` (form `code`, `reason`) | — | Today's Service code. Sets `rs_session`. 401 for a wrong code, 429 when held |
| `POST /_rs/sign-out` | — | Ends the session |
| `GET /_rs/me` | — | Session, role, and what this role may use: roots, actions, views |
| `GET /_rs/files?root=&path=` | `files.read` | Lists one folder: `[{name, dir, size, modifiedUtc}]` |
| `GET /_rs/file?root=&path=` | `files.read` | Downloads one file (`application/octet-stream`, `X-RS-SHA256`) |
| `POST /_rs/uploads` (JSON `root, path, size, sha256`) | `files.write` | Starts an upload: `{"id"}` |
| `POST /_rs/uploads/<id>?offset=` (body: up to 1 MiB) | `files.write` | One chunk, in order |
| `POST /_rs/uploads/<id>/finish` | `files.write` | Checks size and SHA-256, then puts the file in place or waits for the operator: a job |
| `GET /_rs/diagnostics` | `diagnostics` | The diagnostics bundle (`application/zip`) |
| `POST /_rs/actions/<name>` (JSON arguments) | `actions` | Runs a declared action: a job |
| `GET /_rs/views/<name>?<params>&format=json\|csv` | `data.read` | One declared read-only view |
| `GET /_rs/packages` | `packages` | `{"product", "current", "previous", "maxBytes", "mayInstall"}` |
| `POST /_rs/packages` (JSON `name, size, sha256`) | `packages` | Starts a package upload: `{"id", "chunk"}`; chunks go to `/_rs/uploads/<id>?offset=` |
| `POST /_rs/packages/<id>/install` | `packages` | Checks the whole package (SHA-256, unpacking, signature, product, every file, the host's version rule), then installs as a job: machine stopped, operator confirms. 400 with the reason when refused |
| `POST /_rs/packages/rollback` | `packages` | Brings the previous version back, as a job with the same rules |
| `GET /_rs/corrections` | `corrections` | The kinds of record this role may correct, with their fields (`name, title, choices, maxLength`) |
| `GET /_rs/corrections/<kind>?record=` | `corrections` | The record's current values of those fields |
| `POST /_rs/corrections/<kind>` (JSON `record, changes {field: new}, reason`) | `corrections` | Proposes a correction: a job that waits for a supervisor at the machine. On approval the host adds a correcting entry (never an edit), unless the record changed meanwhile |
| `GET /_rs/jobs/<id>` | — | `{"id", "state", "message", "result"}` |

- **Job states:** `waiting` (for the operator), `running`, `done`, `failed`, `refused`.
- **Long work:** anything that can take long or needs the operator is a job. The browser polls it, so
  no request waits longer than the relay's 20 s.
- **Errors:** `{"error": "<text for the engineer>", "key": "<stable key>"}` with 400, 401, 403, 404,
  409 (not now: the machine is running), 413 or 429.

### Software packages

A package (`.rspkg`) is a zip:
- **Contents:** the files of the new version and `package.manifest.json`.
- **Manifest:** `{"payload": base64(JSON), "signature": base64(DER)}`. The signature is ECDSA P-256 /
  SHA-256 over the payload bytes.
- **Payload:** `{"product", "version", "created", "files": {"<relative path>": "<SHA-256 hex>"}}`, with
  optional `minVersion` and `expires`. This is Vision System's PackageSign format with `product` added.

The agent refuses a package that:
- has more than one entry with the same name, a link, or a path with `..`, a root, a drive or a colon;
- unpacks larger than allowed;
- has a manifest signed by none of the host's keys, or is for another product;
- has a file missing, extra or changed;
- has expired, is below its own `minVersion`, is below the machine's floor (the highest `minVersion`
  of any package installed there), or is older than the newest version ever installed there.

Rollback goes only to the kept previous version, and never below the floor. The agent keeps the
newest version and the floor in `rs-state.json` in its data folder. Without a data folder, or with a
damaged one, it takes no packages.

`rs-package` (dotnet/RemoteService.PackageTool) makes and checks packages.

## 5. Log: agent → relay

Every audit entry the agent writes to the host's audit sink is also sent to the relay:

```json
{"t": "log", "epoch": "<this run's mark>", "seq": 17, "entry": {"time": "2026-11-04T10:15:02Z", "session": "<id>", "user": "anita", "role": "service",
 "kind": "session_start|session_end|sign_in_refused|file_read|file_written|diagnostics|action|view|…",
 "detail": "<one line for people>", "data": {"…": "…"}}}
```

- **`session_start`** has `data.reason`.
- **`session_end`** has `data.endReason`: `sign-out`, `idle`, `max`, or `link`.
- **Correction kinds:** `record_read`, `correction_proposed`, `correction_approved` (`data.approvedBy`),
  `correction_applied` (`data.entry`, the host's ledger reference), `correction_rejected` and
  `correction_refused`. Each has `data.kind`, `data.record`, `data.reason`, and `data.old.<field>` /
  `data.new.<field>`.
- **Software kinds:** `package_uploaded`, `package_refused`, `package_install`, `package_installed`,
  `package_rollback`, `package_rolled_back`. Each has `data.version`, and `data.from` where it applies.
- **Delivery is confirmed.** Each entry carries `epoch`, a mark of the agent's run, and `seq`,
  numbered from 1 within that run.
  - The relay saves each entry once and answers `{"t": "ack", "epoch": "…", "seq": 17}`.
  - The agent drops an entry only after its `ack`, and sends unconfirmed entries again on each new link.
  - The relay recognises a repeat by `epoch` and `seq`, confirms it and does not save it again.
  - Unconfirmed entries (up to 1000) survive a restart of the agent in its data folder, under their
    old `epoch`.
  - Entries are never sent for a session that never signed in.
- **`session_end` chain head:** when the host's log is a hash chain, `session_end` has
  `data.chainHead`, the chain's head just before the `session_end` entry, so it covers every action
  of the session. The relay keeps it in
  `anchors/<machine>.jsonl` and sends it to ERPNext, where it is stored on the Remote Service Log. A
  later rewrite of the machine's log no longer matches it.
- **What the relay does:** keeps them as the session log, and at `session_end` sends the session to
  ERPNext (DESIGN.md section 5).

## 6. Versions

`X-RS-Protocol` and `GET /_rs/info` carry the version. A change that an older peer would read
wrongly needs a new version number. Adding a frame type, field or path does not.
