# Remote service module: design

A reusable module that lets Sundaram's service and OEM engineers work on a machine's software from
any browser, with no remote-desktop software. It is a package that any of our applications hosts:
Vision System is the first, and other products follow.

Owner decisions:
- **2026-11-03:** REQ-11 and PROT-26 in Vision_System: no AnyDesk, TeamViewer or VNC.
- **2026-11-04, how the link works:** each machine connects directly and outbound. Today's Service code
  starts a session with no approval on the line screen. The line screen shows only a neutral "Remote
  service active" badge.
- **2026-11-04, what the engineer may do:** what the table in section 3 lists. Direct database edits are
  not allowed. Every remote action goes into the host's tamper-evident log.
- **2026-11-04, ERPNext:** each session sends a Remote Service Log and a draft Timesheet.
- **2026-11-04, packaging:** the module has its own repository. The .NET agent comes first, over a
  documented protocol so agents in other languages can follow.
- **2026-11-04, signing key:** software packages are signed with the existing PackageSign key (ECDSA
  P-256). The host trusts the `package-public.pem` it already ships. One key covers the shipped
  package and remote installs. Because the manifest has the same format, an installed version can
  also pass the host's own start-up integrity check; step 4 confirms this for Vision System.

## 1. Parts

```
 Machine PC (host application)                    support.sundaramtech.com                 ERPNext
┌────────────────────────────────────┐ outbound  ┌──────────────────────────────┐ REST  ┌────────────────────┐
│ host app (e.g. vs-runtime)         │ wss /link │ relay/                       │──────►│ erpnext/           │
│  └ RemoteService.Agent (.NET)      ├──────────►│  engineer accounts, machines │       │  remote_service app│
│     link, sessions, capabilities   │           │  forwarding, service console │       │  Remote Service Log│
│     ▲ host supplies: audit sink,   │           │  session log → ERPNext       │       │  draft Timesheet   │
│       Service-code check, roots,   │           └──────────────┬───────────────┘       └────────────────────┘
│       actions, data views, …       │                          │ HTTPS
└────────────────────────────────────┘                    engineer's browser
```

| Part | Folder | Language |
| --- | --- | --- |
| Protocol | `docs/PROTOCOL.md` | Language-neutral: what any agent must do |
| Relay | `relay/` | Python 3.11+, FastAPI (same stack as the plant server) |
| Agent | `dotnet/RemoteService.Agent/` | .NET 10 library, no dependency on any host |
| ERPNext app | `erpnext/remote_service/` | Frappe app: doctypes and one API method |

The agent knows nothing about Vision System. The host plugs in what only it knows:
- how a Service code is checked, and its audit store;
- which folders may be read or written;
- which named actions exist;
- which data views exist, and how a correction is proposed and approved;
- how a package is installed, and whether the machine is safe to change now.

## 2. Sessions and identity

1. The engineer signs in on the relay with their own account.
2. They open `/<machine>/`. The relay shows only machines on their account.
3. The relay asks the agent to open a session. The engineer enters **today's Service code** for that
   machine; the host checks it. Five wrong codes in 15 minutes hold remote sign-in for every engineer;
   the host counts them in its own file.
4. The session carries the relay account name, the engineer's role (`service` or `oem`, from the
   relay account) and a reason. It ends at sign-out, after 15 minutes idle, after 8 hours at most, or
   when the link drops.
5. While a session is open the host shows "Remote service active". It never shows who is connected or
   what they are doing.

A role on the relay never widens what the host allows. The agent checks every request against the
session's role.

## 3. Capabilities

Each capability is switched on by the host, and each has its own rights. The `service` role gets the
first five; `oem` gets all of them.

| # | Capability | What the engineer does | Rules the agent enforces | Audit entry |
| --- | --- | --- | --- | --- |
| 1 | `dashboard` | Uses the host's own local web pages, forwarded through the link (as Vision System does today) | Only the host's web port, only its own paths | session start / end |
| 2 | `files.read` | Lists and downloads files in named roots (logs, NG images, reports, job files) | Only declared roots. No `..`, links or absolute paths. Size limit per file | each download: root, path, size, SHA-256 |
| 3 | `diagnostics` | Downloads a diagnostics bundle (zip) | Contents chosen by the host (logs, versions, settings with secrets removed) | bundle made, SHA-256 |
| 4 | `actions` | Runs named actions the host declares (restart a service, clear temp files, check drivers…) | Only declared names. Each action says whether it needs the machine stopped and whether the operator must confirm | each run: name, arguments, result |
| 5 | `data.read` | Opens read-only data views the host declares, and exports them as CSV | Only declared views; parameters checked by the host; row limit | each view: name, parameters, rows |
| 6 | `files.write` | Uploads into writable roots (job files, models) | Only writable roots. Extension allow-list per root. A host check may verify signatures. The operator confirms if the root says so | each upload: root, path, size, SHA-256 |
| 7 | `packages` | Uploads a signed software package to install, or goes back to the previous version | Signature checked against the public key the host ships (same ECDSA P-256 key and manifest format as Vision System's PackageSign, with the product added). Every file and the product checked before the operator is asked. Installed only when the host says the machine is safe (stopped), after the operator confirms; checked again after the confirmation. Old version kept for rollback | uploaded, refused (with reason), confirmed, install, installed, rollback, rolled back |
| 8 | `corrections` | Opens a record the host declares as correctable and proposes new values for its declared fields, with a reason (at least 10 characters) | **Never an edit.** Only declared fields, checked against their choices or length and the host's own rule. A supervisor at the machine approves with their own sign-in (4-eyes; up to 30 minutes). If the record changed while waiting, nothing is applied. Then the host adds a new correcting entry next to the original, chained in its ledger. Fields that decide OK or NG are never declared | record opened, proposed (old and new values), approved (by whom) / rejected, applied (ledger reference) / refused |

**Line safety.** An action, upload, install or correction that could change how the machine decides
OK or NG is refused while the machine runs. The host decides this through `IMachineState`. If the
host cannot tell, the answer is "not safe".

## 4. User log

Every remote event goes to the host's audit sink (`IAuditSink`): sign-in, refused sign-in, session
end, and every row of the table in section 3. In Vision System the sink is the results ledger
(HMAC-chained, alarm ALM-801 on tampering), so the remote user log is as tamper-evident as the results.

Every entry also goes to the relay, which keeps the **session log**. At session end the relay sends the
session to ERPNext. The relay keeps its own append-only audit file as well.

## 5. ERPNext

The `remote_service` Frappe app adds:
- **Remote Service Log**: machine, licence key and customer (from Vision Licence when the app is there),
  engineer (relay account), employee, role, reason, start, end, duration, end reason, and a table of
  every action (time, kind, detail, result).
- **Remote Service Settings**: the default Activity Type for remote service time.
- One API method, `remote_service.api.record_session`, called by the relay with an ERPNext API key of
  an integration user. It is idempotent by session id: a retried call never makes a second log.

At session end:
1. The relay sends the session.
2. ERPNext creates the Remote Service Log and a **draft Timesheet** for the engineer's Employee: one
   time log from start to end, the Activity Type from the settings, and the machine and reason in the
   description.
3. The engineer adds a work note in ERPNext and submits it.

If ERPNext cannot be reached, the relay keeps the session in an outbox and retries with back-off. It
never drops a session.

## 6. Security

These rules carry over from Vision_System PR #325. They are the rules of the module now.
- **Machine connection:** outbound link only, each machine with its own token, stored as a hash on the
  relay.
- **Service code:**
  - accepted only through the link, never on the plant network;
  - a customer's password never goes through the relay;
  - wrong codes are counted on disk in the host's own file.
- **Robustness:** nothing the relay sends can stop the host. A malformed frame drops the link, which is
  dialled again.
- **Before the relay goes live:** each machine gets its own origin, so pages of one machine cannot
  read another's. The owner chooses the address format.
- **Data limits:** every forwarded answer is bounded, and a slow browser is cut off. Paths are checked
  on the agent, never trusted from the relay.
- **Secrets:** never logged. Settings exported in a diagnostics bundle pass the host's redaction.
- **Signing key:** the PackageSign private key stays on the vendor's signing PC only. Whoever holds it
  can install software on every machine that has a Service code for the day, so it is kept like the
  licence signing key.

## 7. Steps

| Step | Delivers |
| --- | --- |
| 1 | Protocol; relay moved from Vision_System with session log and ERPNext outbox; .NET agent with link, sessions, dashboard forwarding, `files.read`, `files.write`, `diagnostics`, `actions`, `data.read`; ERPNext app with Remote Service Log and draft Timesheet |
| 2 | `packages`: signed install with operator confirmation and rollback; `FolderPackageInstaller` for programs kept in versioned folders; `rs-package` to make and check packages |
| 3 | `corrections`: append-only correction with supervisor approval (`CorrectionKind`, `ISupervisorApproval`) |
| 4 | Vision_System uses the package: its ledger as the audit sink, its folders, actions and views; `support_relay/` and `SupportLink.cs` removed there |
