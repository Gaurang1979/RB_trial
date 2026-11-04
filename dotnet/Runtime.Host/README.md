# vs-runtime (Hybrid 1)

The vision runtime service: runs the line and serves the runtime contract
(`proto/visionstudio/remote/v1`) to Vision Studio (`dotnet/Studio`), the
browser dashboard and plant software. Design: `docs/vision-system/ARCHITECTURE.md`
sections 2.2-2.4 and 9.

- **One process.** Started by the Windows service manager it runs as the
  `vs-runtime` service; otherwise (or with `--console`) as a plain program.
- **Local endpoint.** Named pipe `\\.\pipe\vs-runtime` on Windows (as a service:
  interactive users may connect, the network may not), Unix socket
  `$TMPDIR/vs-runtime.sock` elsewhere (owner only). `--Runtime:Endpoint=` or
  `VS_RUNTIME_ENDPOINT` changes it. No TCP port unless `Runtime:NetworkPort` and
  `Runtime:CertificateFile` (TLS) are set.
- **Access (PR #88).** No token: reads, on the local endpoint only. Changes need a
  signed-in Vision Studio user on the local endpoint, or a plant token with the
  operate right. A sign-in over the network is a browser session: read-only.
  Five wrong PINs lock that user for a minute (per endpoint).
- **The line.** VisionStudio's `Station`, `PlcService`, licence, NG image store,
  results log and shift reports, used by project reference until draft #70 merges.
  Cameras open through `FrameIntake` (pooled buffers, a lost or dropped frame is an
  NG part). A part waits for its frame as long as the job needs (exposure, transfer
  and 100 ms, at most 2 s; RT-FW-01); a frame later than that is NG and is never given
  to the next part. `Runtime:SimulatorFolder` plays BMP or PNG frames (read by content) as a camera; a job camera with its own test folder plays that folder. Each picture is decoded once and played from memory (up to 256 MB a camera; RT-FP-01).
  Health: `Status.health` (cpu_pct, guard limits, frames missed) and the plant
  server's health.sample with cpu_pct and frame_drops.
- **Line speed and PLC on the Run screen.** `Runtime:LineSpeedPpm` (parts per minute)
  and `Runtime:CycleBudgetMs` (0 = 60000 / line speed) go out as `Status.line_speed_ppm`
  and `cycle_budget_ms`, the limit lines of Vision Studio's Overview charts; 0 = not set.
  `Status.plc` gives the PLC link, heartbeat, RunEnable, the last TriggerID and the last
  part's result (unset when no PLC is set up).
- **Browser dashboard (Hybrid 4).** `Runtime:WebPort` opens view-only pages (Live,
  Trends, Reports, Audit trail; UI-UX-SPEC.md 4.17) over HTTPS with the same
  `Runtime:CertificateFile`; 0 = off. Sign-in with a user's password (never a PIN or
  card over the network) gives a browser session that only reads. Every web route is a
  GET except sign-in and sign-out; the pages are inside the program, styled from
  `design/tokens` (`tools/tokens/gen_tokens.py` writes `Web/tokens.g.css`). Live
  updates come as server-sent events; frames are the runtime's own JPEGs, at most two
  a second per camera per browser. Plain HTTP on this PC only with
  `Runtime:AllowInsecureNetwork` (tests).
- **Dashboard scope** (requirements 6.4 WEB-01..03). The runtime checks each web call:
  Live and Trends for every role, Reports for Supervisor and above (Operator: shift
  reports only with `Runtime:WebOperatorShiftReports`), Audit trail for QA and Admin.
  `add-user ... --lines "L3,Line 1"` limits a user to those lines (plant id or report
  name); `--lines ""` = every line. Roles and lines are read from the users file on
  every call, so a change counts at once and a removed user is signed out. A web route
  must name its page (`WebAccess.Needs`), else it is refused.
- **Remote service (REQ-11, PROT-26).**
  - **Setup:** with `Runtime:SupportRelay` (wss://support.sundaramtech.com/link),
    `Runtime:SupportMachine`, the commissioning secret `VS_SUPPORT_LINK_TOKEN` and the ledger key,
    the runtime hosts the remote-service agent (`third_party/remote-service`, `RemoteServiceRunner`).
  - **Sign-in:** engineers sign in on the service console with today's Service code (role service)
    or Factory code (role oem). Five wrong codes hold remote sign-in for 15 minutes.
  - **What they get:** read-only access to reports, NG images and jobs; a diagnostics bundle; views
    of inspections, batches, events and NG reasons; a ledger check; and this dashboard with the same
    session.
  - **User log:** every remote event is chained in `service-log.db` (its own head file; the results
    ledger is unchanged).
  - **Off when:** there is no ledger key, or the service log fails its check at start. The line keeps
    running.
  - **Badge:** `Status.remote_service` tells the line screen that a session is open, never who.
  - **Requests (4b):** `Status.remote_service.requests` carries what remote service asks at the line
    PC. `AnswerRemoteRequest` answers it, on the local endpoint only:
    - the operator confirms a job file upload (only while the line is stopped);
    - a supervisor approves a batch correction with their own PIN, password or card.
  - **Corrections:** appended to the service log; batch reports show them next to the values
    entered.
  - Design: `docs/vision-system/REMOTE-SUPPORT.md`.
- **Inputs.** `SetInput("batch.<field>", value)` sets a batch value, only while stopped.
- **Images.** `GetImage` serves the last 64 parts: full resolution from the stored
  picture when replaying, else the live picture as drawn.

```bash
vs-runtime add-user operator "Line Operator" Operator --pin 1234
vs-runtime add-user anita "Anita" Operator --pin 1234 --card   # tap the badge on the reader; nothing is shown
vs-runtime add-token mes --operate
vs-runtime --console --Studio:JobsFolder=<jobs> --Studio:DataFolder=<data> --Studio:LicenceRequired=false
vs-runtime --console --Runtime:WebPort=8443 --Runtime:CertificateFile=<line-pc.pfx>   # dashboard at https://<line-pc>:8443/
```

Users and tokens live in the data folder (`runtime-users.json`, `runtime-tokens.json`)
as salted hashes; a token is printed once. Badge numbers (6 or more characters, compared in
capitals) are hashed with one salt that all cards share, so a card is found with one hash;
card numbers are short, so keep the data folder readable only by the service. A card signs in
only on the local endpoint; unknown cards are counted together (5 in a minute lock card
sign-in, never the PIN pad). `Runtime:StudioIdleSignOut` (default `00:10:00`) is the idle time
after which Vision Studio signs its user out; WhoAmI tells it to the app.
