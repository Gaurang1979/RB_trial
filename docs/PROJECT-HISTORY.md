# Vision_System - Project History and Decisions

2026-10-29 (created 2026-10-29 from secure_me's project kit)

## 0. Currently being edited — check before touching these files

Work in progress is recorded in the **open pull requests** (drafts
included), not in this file: nothing is pushed straight to main, so a row
added here would only reach main after the work was finished.
`scripts/coord.sh prs` lists every open pull request with the files it
changes. `scripts/coord.sh overlap` shows which of them change the same
files as your branch. Section 7 has the process.

Agents do not add rows to this table. It is for the owner and for work
done outside pull requests.

| Started | Who | Files/services | Expected done |
|---|---|---|---|

## 1. How to use this file

Read this file first in a new chat instead of re-reading every project file; it is a summary of decisions and progress, not a transcript.

- The full detail lives in the requirements (`docs/REQUIREMENTS-VISION-SYSTEM.md`) and the design documents (`docs/vision-system/`); this file says what was decided, when, and which section holds it.
- Sections 2 to 5 give the current state; section 3 is the dated timeline; section 8 is the update log.
- Section 5 is not the whole picture: finished work that is not yet rolled up is in the change notes in `docs/changes/`, and work in progress is in the open pull requests. `scripts/coord.sh taken <words>` checks all three.
- Agents do not append to this file: each pull request writes its entries in its own `docs/changes/` note, and an automatic roll-up (a GitHub Action) copies them in after the merge (section 7, rule 12).

## 2. Project snapshot

Vision_System grows the aggregation code reader into a full 2D machine-vision platform positioned against Cognex VisionPro, with a plant-wide live dashboard and a mobile app for operators and admins across many lines.

| Item | Current state |
| --- | --- |
| Goal | A million-dollar product: an operator with no vision training builds and runs a job in under 15 minutes, AI tools match or beat Cognex on the same images, and the whole plant is visible live on the web and on phones |
| Built today | C++ engine (`codereader`: QR, Data Matrix, 1D, DPM rescue, multi-frame, grading; `vs_ocr`: OCR and OCV), .NET wrapper and tests, Hikrobot camera adapter, station runner, single-station web dashboard (live, trends, products, calibrate, tuning, AI models, audit), Vision Studio base (run, teach, test, batch, jobs, up to 3 cameras), ML training and ONNX models, Windows build in GitHub Actions |
| Designed, not built | Full tool library, job file format, licensing, SDK, PLC interface, 15 languages, test plan (`docs/vision-system/`); Plant Insight dashboard and mobile app (`docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md`) |
| Platform | Industrial PC, Windows 10/11, GigE Vision and USB3 Vision cameras only, every machine signal through the PLC over Ethernet; 2D only |
| Stack | C++17/20 + OpenCV + zxing-cpp; .NET 8 (Blazor today, WPF or Avalonia for the HMI); Python + PyTorch → ONNX; Flutter for the mobile app; PostgreSQL + TimescaleDB on the plant server |
| Users | Operator, supervisor / quality, maintenance, integrator / engineer, plant admin, group management |
| Languages | 15 (`docs/vision-system/LOCALISATION.md`) |
| Process | Parallel Claude sessions, claims by draft pull request, one testing session, owner merges only `tested-pass` pull requests (`CLAUDE.md`, section 7) |

## 3. Timeline

| Date | What | Sections |
|---|---|---|
| 2026-10-23 | Multi-frame reading: frames per case on the product, burst reads, `cr_decode_frames`, SDK `DecodeFrames` | Requirements 4, 10 |
| 2026-10-27 | Reader quality round R2–R10: grid fit, blur kernel (R9), glare step, learned state per product, under-resolved QR solver, miss diagnosis, 1D at any angle | Requirements 4; docs/QUALITY.md |
| 2026-10-29 | Requirements document and the Vision System design set (architecture, tools, AI, UI/UX, localisation, job file, licensing, SDK, PLC, test plan); ViDi / SuaKIT study (DL-01 to DL-26) | Requirements 1–15 |
| 2026-10-29 | Calibrated print grading, slot map, dot-printed codes, light-on-dark polarity; OCR / OCV tool (`vs_ocr`), OCR model (ocrnet) and Vision Studio base software | Requirements 4, 6 |
| 2026-10-29 | Project kit adopted from secure_me: CLAUDE.md process, helper agents, coord.sh, merge guard, testing-session picker, change notes and history roll-up; Plant Insight (live plant dashboard and mobile app) designed | CLAUDE.md; PLANT-DASHBOARD-AND-MOBILE.md |

## 4. Decisions log

| Topic | Decision and reason | Sections |
|---|---|---|
| Working process | The secure_me process applies here from 2026-10-29 (owner: "Copy all points and startup kit which are important from secure_me repo to Vision_System repo"): own worktree, claim by draft pull request, change notes, automatic roll-up, one testing session, merge only with `tested-pass` | CLAUDE.md; section 7 |
| Product ambition | Owner, 2026-10-29: "a million dollar project. Proper development, UI Design live dashboard and industry leading features required. I want mobile app also for multiple lines for plant operators and admin." Answered by the Plant Insight design (features F1–F16) | PLANT-DASHBOARD-AND-MOBILE.md |
| Line independence | The line never depends on the plant server or the phone: the station decides OK/NG and talks to the PLC; the plant layer only reads, and sends a few signed, role-checked commands | PLANT-DASHBOARD-AND-MOBILE.md 2 |
| Mobile technology | Flutter for Android and iOS, one app with screens by role (proposed; the owner's team already ships Flutter in secure_me) | PLANT-DASHBOARD-AND-MOBILE.md 8 |
| Plant Insight hosting | Owner, 2026-10-29: cloud relay from PI2. The plant server stays the master and works without internet; the relay carries push, remote access and the app over one outbound TLS link | PLANT-DASHBOARD-AND-MOBILE.md 2, 10 |
| Mobile platforms | Owner, 2026-10-29: Android and iOS together | PLANT-DASHBOARD-AND-MOBILE.md 6, 10 |
| Plant Insight licence | Owner, 2026-10-29: per connected line, web dashboard and mobile app included; cloud relay a yearly subscription | PLANT-DASHBOARD-AND-MOBILE.md 10 |
| Sign-in | Owner, 2026-10-29: local accounts (user name, password, PIN for shared phones) only for now; company SSO when a customer asks | PLANT-DASHBOARD-AND-MOBILE.md 6, 10 |
| Plant dashboard technology | Grows from `dotnet/Dashboard` (Blazor) on an ASP.NET Core plant server with PostgreSQL + TimescaleDB (proposed) | PLANT-DASHBOARD-AND-MOBILE.md 8 |
| Left out of the kit | secure_me's Mac heavy-test lanes, test pool and on-demand Colima / simulator scripts, release-test and error-fix workflows, and Go/Flutter CI: they fit secure_me's stack, not this one; added here when this repository has heavy tests that need them | CLAUDE.md step 10 |

## 5. Open items and next steps

Status marks: `- [ ]` open and free to take; `- [~] **Requested — Claude agent (session: <name>, <date>)**` already given to an agent, **do not give it to any other agent**; `- [x]` done. An item is also taken when an open pull request (draft or not) claims it, even though main still shows `- [ ]`, and it may already be done in a change note in `docs/changes/` that is not yet rolled up. New claims do not change the marks here; the draft pull request and its note are the claim. Section 7 has the claiming rule.

Owner decisions:

- [x] (done 2026-10-29, owner decision: cloud relay from PI2; the plant server stays the master and works offline) Plant Insight: on-premises only at first, or the cloud relay in PI2 as well (PLANT-DASHBOARD-AND-MOBILE.md section 10).
- [x] (done 2026-10-29, owner decision: Android and iOS together) Mobile app: Android only first, or Android and iOS together.
- [x] (done 2026-10-29, owner decision: per connected line, dashboard and app included, cloud relay a yearly subscription) Plant Insight licence model (per line, mobile app included?).
- [ ] Price of each parent tool and of the Plant Insight line licence and relay subscription.
- [x] (done 2026-10-29, owner decision: local accounts and PIN only for now; company SSO when a customer asks) Which plant SSO comes first.
- [ ] Is the golden-part check (F13) on by default for every line?
- [ ] Confirm the final 15 languages, including the two export languages.
- [ ] Build or buy the licensing system (own service or CodeMeter / Sentinel / Cryptlex).
- [ ] Which PLC brands come first (Siemens, Rockwell and Mitsubishi assumed).
- [ ] Is 21 CFR Part 11 required in the first release?
- [ ] Windows only for two years, or a Linux / Jetson runtime too?

Plant Insight (live plant dashboard and mobile app):

- [ ] PI1 Plant event schema v1 (inspection.result, line.state, counter.snapshot, alarms, job.changed, model.deployed, health.sample, heartbeat) as a shared contract with tests.
- [ ] PI1 Station agent in Vision Studio and LineRunner: events with store-and-forward on disk, gap-free sequence numbers.
- [ ] PI1 Plant server: ASP.NET Core ingest over gRPC/TLS, PostgreSQL + TimescaleDB, SignalR live hub, roles by plant, area and line.
- [ ] PI1 Web dashboard: floor map (all lines live) and line view, with TV mode.
- [ ] PI2 Mobile app (Flutter, Android and iOS): sign-in with local accounts and PIN, my lines, line view, alarms with push and guided fixes, NG review.
- [ ] PI2 Cloud relay: one outbound TLS link from the plant server; FCM and APNs push; remote access for the app.
- [ ] PI2 Mobile admin: users and roles, pair a line by QR code, approvals, notification rules.
- [ ] PI3 OEE with loss tree and downtime reasons; SPC charts with Cp/Cpk and rule alerts; defect Pareto and defect map.
- [ ] PI3 Shift handover report (PDF, Excel, scheduled e-mail) and traceability search.
- [ ] PI4 AI health (drift, score distribution, overrule rate) and NG review to the training set.
- [ ] PI4 Camera, light and PC health with predictive alerts; alarm escalation after ISA-18.2.
- [ ] PI5 Part 11 mode, OPC UA and MQTT Sparkplug B out, multi-plant roll-up over the relay, company SSO (OpenID Connect) when a customer asks.
- [ ] Plant Insight UI design: review the clickable prototype in `design/prototypes/plant-insight/` with two operators and a supervisor, then freeze it.

Vision platform (requirements section 15):

- [ ] P1 Foundation: tool framework and C API, GigE and USB3 acquisition (GenICam), Base tools, Locate, Measure, Calibrate, Blob, PLC base protocols, licence service, Run screen and job designer.
- [ ] P2 Identify: Code Reader and Code Verify as parent tools, OCR/OCV (classical and AI), Teach wizard, auto-find v1, Hindi plus 4 more languages.
- [ ] P3 AI: deep-learning tools, training workflow, auto-label, auto-tolerance, drift monitor.
- [ ] P4 Scale: multi-camera and Enterprise, Inspect, Colour, scripting, all 15 languages, .NET and Python SDK, web HMI.
- [ ] P5 Edge: Linux and Jetson runtime, natural-language job hint.
- [ ] Buy one VisionPro licence and build the 5,000-image benchmark library.
- [ ] Legal review of the open-source dependency list before P1 ends.

Project kit:

- [ ] CI for this repository's scripts: run `scripts/rollup_test.py` and `scripts/coord_test.sh` on pull requests that change `scripts/`.
- [ ] Heavy-test lanes and an on-demand script for this repository once benchmarks, soak and mobile integration tests run often enough to need them.

## 6. Links and assets

| Asset | Where | Status |
| --- | --- | --- |
| Requirements | docs/REQUIREMENTS-VISION-SYSTEM.md; shared copy https://claude.ai/artifact/9tswdE4aRB1rreSoKzgRcA | Source of truth |
| Design documents | docs/vision-system/ (start with its README) | Agreed starting point |
| Plant Insight design | docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md | Design, 2026-10-29 |
| Plant Insight UI prototype | design/prototypes/plant-insight/ (source); https://claude.ai/artifact/Py9Qexish3fDHqKZYDJvXr | Clickable prototype: 3 web pages, 5 mobile screens, 2026-10-29 |
| Code reader plan | https://claude.ai/artifact/17kZ3REP3ranSQVuUwTyjG | Plan and design of the aggregation reader |
| GitHub repository (private) | https://github.com/Gaurang1979/Vision_System | Every change through a pull request; only the owner merges |

## 7. Working rules for future chats

- Give to-the-point answers and keep them short, to save tokens.
- Make every value that could change configurable: tolerances, retention, quotas, alarm priorities, prices.
- Record each new decision in the requirements and in a change note (it reaches section 4 by the roll-up).
- When the owner says "you decide", choose the safer option for the line (a bad part must never pass) and record the reason.
- Never store or reuse access tokens; keep the repository private and free of keys, licence secrets and customer images.
- Label estimates as estimates and keep sourced facts separate.
- Parallel agents: the coordination process (from secure_me, adopted 2026-10-29). Several agents work on this repository at once. These rules stop two of them from taking the same item, and stop their pull requests from conflicting. `CLAUDE.md` at the top of the repository is the short version, and every Claude Code session loads it.
  1. **Own worktree.** Every session works in its own git worktree on its own branch, made from `origin/main` (`git worktree add <dir> -b claude/<topic> origin/main`). Never switch branches in the shared checkout.
  2. **Where the state is.** Work in progress lives in the open pull requests, drafts included, and nowhere else. Finished work is in section 5 plus the change notes in `docs/changes/` that are not yet rolled up. Nothing is pushed straight to main; only the owner merges.
  3. **Is it taken?** When given an item, or asked which one to do next, pull main and read section 5 and `docs/changes/`. Then run `scripts/coord.sh taken <words>`, which searches all three places, and also lists matching `[x]` items and pull requests and commits merged in the last 14 days, in case the work is already built under another name; separate other wordings with `|`. An item is taken if main marks it `[~]`, a note on main has it, or an open pull request claims it, in its title, its description or its note. Skip taken items when suggesting work, and never start one. If the owner asks for a taken item, say who holds it and ask before doing anything.
  4. **Claim before reading code.** Create the branch and run `scripts/coord.sh note`. Fill in the note's header with `Status: claimed`, the session, the section 5 item (its first words exactly as on main, or "new") and the components and files you expect to change. Commit only the note. Push, and open a draft pull request at once, titled `Claim: <item>` and using the template. Do not change section 5 marks: the draft pull request is the claim, and a new file cannot conflict.
  5. **Race check.** Run `scripts/coord.sh taken` again. If an older pull request claims the same item (the list is oldest first), close yours and choose another.
  6. **Overlap check.** Before editing code, and again before marking the pull request ready, run `scripts/coord.sh overlap`. If another open pull request changes the same code files: prefer an item that does not; otherwise keep your change there small, name that pull request in yours with the merge order (usually the older one first), and do not build on its unmerged code. Frequent conflict points: the root `README.md` layout table, `docs/REQUIREMENTS-VISION-SYSTEM.md` (change only the section your work needs), `engine/CMakeLists.txt`, the engine C API headers, and `.github/workflows/windows-build.yml`.
  7. **History entries go in the note, not in this file.** Timeline rows, decisions, the section 5 "done" text, new items and update-log rows all go in your own note, in its sections. Two exceptions may edit this file directly: the roll-up, and a pull request that changes this process.
  8. **Finish.** Set the note to `Status: done` and fill in its sections; fill in the pull request's Verified section; retitle the pull request (drop `Claim:`) and mark it ready. If you drop the work, close the draft, which releases the claim.
  9. **Keeping up with main.** When main moves, or a pull request shows conflicts, merge `origin/main` into your branch. Never rebase, never force-push, never push to main. Conflicts in documentation rows or lists: keep both sides, in date order. Conflicts in code: resolve them only when both sides' intent is clear, then build and test what the merge touches. Otherwise stop and ask the owner. After the merge, check that the diff against main holds only your own changes.
  10. **Handing work to another session.** Spawning a task chip or a subagent hands over the item: do not also do it yourself. The session that receives the item claims it like any other (rules 3 to 5).
  11. **Stale claims.** Only the owner or the claiming session releases a claim. If a draft pull request has had no new commit for a day, ask the owner rather than taking it over.
  12. **Roll-up (automatic).** The GitHub Action `.github/workflows/history-rollup.yml` runs `scripts/rollup.py` whenever a merge to main adds change notes. It copies every finished note into sections 3, 4, 5, 6 and 8 and deletes it, in one standing pull request titled "History roll-up", which it brings up to date (by merging main, never force-pushing) as more notes arrive. The owner merges it whenever convenient. Notes follow the rules in `docs/changes/README.md` so the script can place every line. Agents do not claim roll-ups. If the Action is off, an agent may claim `history roll-up` and run `scripts/rollup.py` on its branch.
  13. **Testing and merging.** Development sessions build and run only their new tests; the one testing session tests ready pull requests in batches and sets `tested-pass`; the owner merges only labelled pull requests (CLAUDE.md step 10).

## 8. Update log and how to update

| Date | Entry | Updated by |
| --- | --- | --- |
| 2026-10-29 | File created with the project kit from secure_me; snapshot, timeline and open items made from the requirements, the design documents, the git history and the Plant Insight design | Claude (cloud, branch claude/startup-kit) |
