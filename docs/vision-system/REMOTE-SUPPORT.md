# Remote service through support.sundaramtech.com

Requirements: REQ-11, REQ-15, PROT-26, PROT-30, PROT-32 (Service and OEM tiers), REQ-08 (screen
shielding), REQ-16 and PROT-31 (tamper-evident records).

The remote service module is its own repository, `Gaurang1979/remote-service`. Vision_System takes it
as the git submodule `third_party/remote-service`. Its design and protocol are in that repository
(`docs/DESIGN.md`, `docs/PROTOCOL.md`). This note says how Vision System hosts it.

## Owner decisions

| Date | Decision |
| --- | --- |
| 2026-11-03 | No AnyDesk, TeamViewer or VNC (REQ-11, PROT-26). |
| 2026-11-04 | Each line PC connects directly and outbound. Today's code starts a session with no approval on the line screen, which shows only a neutral "Remote service active" badge. |
| 2026-11-04 | The engineer may use only what the module's table lists. Direct database edits are never allowed. |
| 2026-11-04 | Every remote action goes into the line's tamper-evident log. Each session goes to ERPNext as a Remote Service Log and a draft Timesheet. |
| 2026-11-04 | The module has its own repository, comes in as a git submodule, and arrives in three steps: 4a, 4b and 4c. |
| 2026-11-04 | Software packages are signed with the existing PackageSign key. |
| 2026-11-04 | Remote corrections in Vision System: batch values only. The correction is a chained entry next to the original, and batch reports show the corrected value with "was …, corrected by …, approved by …, reason". Inspection results and OK/NG are never correctable. |

## Step 4a: the runtime hosts the agent

```
 Line PC: vs-runtime                                     support.sundaramtech.com (remote-service relay)
┌──────────────────────────────────────────────┐ outbound ┌────────────────────────────────┐      ERPNext
│ RemoteServiceRunner ── RemoteAgent ──────────┼─────────►│ engineer accounts, machines,   │────► Remote Service Log,
│   sign-in: OtpAttempts (signin-remote.dat)   │ wss /link│ session log, chain heads,      │      draft Timesheet
│   user log: ServiceLog (service-log.db,      │          │ outbox to ERPNext              │
│             own HMAC chain, ledger key)      │          └────────────────────────────────┘
│   folders, views, diagnostics, ledger check  │
│   browser dashboard (forwarded, read-only)   │
└──────────────────────────────────────────────┘
```

### Sign-in

- **Role service:** today's **Service** code.
- **Role oem:** today's **Factory** code. Following PROT-32, a service engineer never opens what
  needs the factory tier.
- **Wrong codes:** counted for all engineers together in `signin-remote.dat`. Five wrong codes hold
  remote sign-in for 15 minutes.
- **Where:** the engineer signs in once, on the service console (`/<machine>/_rs/ui/`). The browser
  dashboard then opens with the same session, through the agent.
  - Through the relay, the dashboard never shows a password field.
  - On the plant network, nothing about the dashboard changes.

### User log

Every remote event goes first into the **service log**:
- its own SQLite file, `service-log.db`;
- its own HMAC-SHA256 chain, using the results ledger's key and row hash;
- its own head file, `service.ledger-head`, which only the key can write.

The results ledger and its check at start are not changed. A copy of each entry also goes to the
audit trail's events, for the Audit trail page.

The agent sends each session to the relay, which passes it on to ERPNext. The service log's chain
head at the end of the session goes too, so a later rewrite on the line PC no longer matches the
copy kept outside.

### Safety

- **No ledger key:** no remote service, because nothing remote may happen unrecorded.
- **A service log that fails its check at start** (an edited row, missing rows, a changed head):
  remote service stays off and the audit trail says why. **The line keeps running.**

### What the engineer can do in 4a

| Capability | Vision System |
| --- | --- |
| Folders (read) | `reports`, `ng-images` and `jobs`, read-only |
| Diagnostics bundle | Versions, the line's status, runtime settings (no secrets live there) and the last day's events |
| Data views (read-only, CSV) | Inspections (hours, batch), batches, events and alarms, NG reasons |
| Actions | "Check the results ledger and the service log", read-only |
| Browser dashboard | Live, Trends, Reports, Audit trail, read-only |
| Uploads, software installs, corrections | **Off in 4a.** Uploads and corrections come with 4b (below); software installs with 4c |

### Setting it up

1. **On the relay:**
   - `add-machine M-1001 "Barcode Line 1" --licence <licence key>`; it prints the link token once.
   - `add-user <name> "<full name>" --machines M-1001 --role service --employee <ERPNext Employee>`.
2. **On the line PC,** store the link token in the protected secret store:

   ```bash
   VisionStudio secrets set VS_SUPPORT_LINK_TOKEN
   ```

3. **In `vs-runtime.json`:**

   ```json
   "Runtime": {
     "WebPort": 8443,
     "CertificateFile": "…",
     "SupportRelay": "wss://support.sundaramtech.com/link",
     "SupportMachine": "M-1001"
   }
   ```

   The Service and Factory code secrets and the ledger key are the ones Vision Studio already uses.
4. **Agent state:** the agent keeps its state in `<data folder>/remote-service/`: the package
   versions installed (for 4c) and log entries the relay has not confirmed yet.

## Step 4b: Vision Studio asks at the line PC

- **Badge:** Vision Studio's top bar shows a neutral **"Remote service active"** badge while a session
  is open, from `Status.remote_service.active`. It never says who is connected or what they do.
- **Requests:**
  - **How they travel:** what remote service asks at the line PC goes out in
    `Status.remote_service.requests`. Vision Studio opens one panel at a time, oldest first, and only
    when no other panel is open. It answers with `AnswerRemoteRequest`, a call that works only on
    the line PC's own endpoint, never over the network.
  - **Wording:** a panel says "Asked by Sundaram remote service", never the engineer's name (REQ-08).
  - **Confirmation:** the operator at the line PC answers Yes or No. Who answered goes into the audit
    trail: the signed-in user, or "the operator at the line PC".
  - **Approval of a correction:** a supervisor approves with their own name and PIN or password, or
    taps their card. It is checked exactly like a changeover's second person: Supervisor, QA or
    Admin, at the line PC only, with wrong tries counted.
  - **Unanswered:** a confirmation not answered in 10 minutes is refused; an approval, in 30.
- **Line safety:** the line must be stopped before any change goes in (`LineStopped`). This is
  checked again after the answer, because the line may have started meanwhile.

### Job file uploads (role oem)

- **What and where:** `.json` files into the jobs folder, up to the job file size limit.
- **Checks before anyone is asked:** each file is read as a job file (`JobFile.Parse`).
- **Then:** it goes in place only while the line is stopped and after the operator confirms. The
  file before is kept as `.previous`.
- **A job loaded at the time** is not run on the new file until it is opened again and checked
  (requirement CV-12).

### Batch corrections (role oem, owner 2026-11-04: batch values only)

- **What:** the values entered at a batch's start (lot, expiry, MRP and the like). The fields offered
  are the batch fields of the line's recent batches.
- **Stored:** in `results.db` the batch row stays as it was entered. The approved correction is a
  `batch_correction` entry in the chained service log: batch, field, old and new value, engineer,
  approver, reason. A later correction starts from the corrected value.
- **Shown:** batch reports list each correction next to the recorded values. Excel has a "Corrected
  batch values" table; the PDF has a line under the batches. The values entered are never
  overwritten.
- **Never reaches the line:** a correction never changes the batch values the running station uses,
  so no inspection, OK or NG decision depends on it.

## Next steps

- **4c: software installs.** An installer for the `vs-runtime` Windows service: stop it, swap
  versions, start it again, with rollback. Packages are signed with the PackageSign key; whether the
  installed version also passes the start-up integrity check is confirmed then.
- **Before the relay goes live:**
  - one origin per machine;
  - two-factor sign-in for engineers;
  - signed session grants (remote-service `docs/COMPARABLE-PROJECTS.md` items 4, 5 and 9).
