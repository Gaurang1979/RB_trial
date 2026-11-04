# Vision System documentation

This folder holds the design documents for growing this repository from the aggregation code
reader into a full 2D vision system positioned against Cognex VisionPro. Development has not
started; these documents are the agreed starting point. Where a document and the
requirements disagree, the requirements win until the document is updated.

## Read in this order

| # | Document | What it decides |
| --- | --- | --- |
| 0 | [Requirements](../REQUIREMENTS-VISION-SYSTEM.md) | Scope, users, tool list, targets, roadmap. The source of truth. |
| 1 | [Architecture](ARCHITECTURE.md) | Processes, layers, the common tool contract, execution and trigger model, migration from today's engine |
| 2 | [Tool specifications](TOOL-SPECIFICATIONS.md) | Every parent tool and sub-tool: inputs, outputs, parameters, auto-learn, pass/fail |
| 3 | [AI and deep learning](AI-DEEP-LEARNING.md) | Auto-find, auto-learn, auto-tolerance, deep-learning tools, training and inference |
| 4 | [UI/UX specification](UI-UX-SPEC.md) | Every screen, wireframes, wording rules for unskilled operators |
| 5 | [Localisation](LOCALISATION.md) | The 15 languages, string keys, fonts, RTL, speech, glossary |
| 6 | [Job file format](JOB-FILE-FORMAT.md) | The `.vsjob` container and its JSON schema |
| 7 | [Licensing](LICENSING.md) | Serial numbers per parent tool, activation flows, licence server |
| 8 | [SDK and API](SDK-API.md) | Tool DLLs, C ABI, .NET and Python wrappers, Remote API |
| 9 | [PLC interface](PLC-INTERFACE.md) | Protocols, handshake data blocks, timing, error codes |
| 9a | [Delta DVP line PLC](PLC-DELTA-DVP.md) | Wiring, ISPSoft set-up, VFD, line settings, instrument conditions, commissioning |
| 9b | [Licence server on ERPNext](LICENCE-SERVER-ERPNEXT.md) | Periodic licence and part-serial verification with the ERPNext licence server |
| 10 | [Test and acceptance plan](TEST-ACCEPTANCE-PLAN.md) | How every target is verified, per-phase checklists |
| 12 | [Plant platform](PLANT-PLATFORM.md) | One Python plant server and one database for Vision, Production Measurement and EuroCod TIJ printers; one dashboard, one app |
| 11 | [Plant Insight](PLANT-DASHBOARD-AND-MOBILE.md) | Live plant dashboard for every line, OEE and SPC, mobile app for operators, supervisors and admins |
| 13 | [Dependencies](DEPENDENCIES.md) | What the product uses, the monthly upgrade check with its e-mail reminder, and how upgrades are made and tested |
| 14 | [NG review](NG-REVIEW.md) | Per-part results, NG images sent when the plant server asks, verdicts on the web page and in the mobile app |
| 15 | [Roadmap](ROADMAP.md) | What is built and what is not, for each thing the owner asked for, and the order of the remaining work with what "done" means |
| 16 | [21 CFR Part 11](PART11-DESIGN.md) | One identity per plant, rights per function, the audit entry with old and new values and its hash chain, e-signatures, contract changes and build order |
| 17 | [Package protection](PACKAGE-PROTECTION.md) | Anti–reverse-engineering and anti-copy: native strip/LTO, .NET IL hardening decision, signed-manifest integrity check with a line-safe failure |
| 18 | [Software protection threat model](THREAT-MODEL.md) | Who the attacker is, what we protect, what model encryption, build/package hardening and the anti-tamper runtime each defend, the realistic bar, and the fail-safe NG on any protection failure |
| 19 | [Antigravity suggestions](ANTIGRAVITY-SUGGESTIONS.md) | Advanced tools, algorithms and engineering methods proposed by Antigravity for Claude sessions to try and test |
| 20 | [Global best methods & patents study](GLOBAL-BEST-METHODS-PATENTS-STUDY.md) | Global benchmark of Cognex, Keyence, HALCON, DALSA, Omron, open AI tools, active patents with twists, and accuracy blueprints |
| 21 | [Changeover verification](CHANGEOVER-VERIFY.md) | A supervisor checks the product and batch values on another PC or the mobile app before the line may start; plant server setting, on by default |
| 22 | [Remote support](REMOTE-SUPPORT.md) | support.sundaramtech.com: engineers open a line PC's own read-only dashboard through its outbound link with today's Service code; the operator keeps their screen, with a neutral "Remote service active" badge |

## Fixed decisions

- 2D only. No 3D tools.
- GigE Vision and USB3 Vision cameras only. No smart cameras, fixed barcode readers or frame
  grabbers.
- The industrial PC always works with a PLC. Every trigger and machine signal goes through the
  PLC over Ethernet. Nothing is wired to the camera or PC by M8/M12 cable.
- One serial number per parent tool. Licences can be added later without reinstalling.
- The vision application and other applications are separate processes that load the same tool
  DLLs.
- The Code Reader parent tool is built from the existing `codereader` engine in `engine/`.

## Related research

- [RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md](RESEARCH-DEEP-LEARNING-VIDI-SUALAB.md): ViDi and SuaKIT deep learning, patents to design around, requirements DL-01 to DL-26
- [RESEARCH-COGNEX-HALCON.md](../RESEARCH-COGNEX-HALCON.md)
- [RESEARCH-COGNEX-PATENTS.md](../RESEARCH-COGNEX-PATENTS.md)
- [RESEARCH-NEXT-GENERATION.md](../RESEARCH-NEXT-GENERATION.md)
- [PATENTS.md](../PATENTS.md)
- [QUALITY.md](../QUALITY.md)

The editable shared copy of the requirements is at
https://claude.ai/artifact/9tswdE4aRB1rreSoKzgRcA.
