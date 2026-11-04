# Changeover verification: a supervisor checks the operator's changeover (product, batch, texts, other key inputs) on another PC or the mobile app before Run is enabled; plant server admin setting, on by default

- Status: done
- Session: changeover-verify, 2026-10-31
- Pull request: #213
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (CV-01 to CV-10), docs/vision-system/CHANGEOVER-VERIFY.md, proto runtime.proto (Status.changeover = 21), dotnet PlantEvents, StationAgent, VisionStudio (ChangeoverGate, Station, Batch page), Runtime.Host, Studio Run screen, plant_server (vision/changeover.py, api.py, events.py, store.py, dashboard/changeover.html), mobile/plant_app (changeover_api.dart, changeover_screen.dart, floor_screen.dart, vision_module.dart)
- Overlaps: #146, #191, #194, #199, #200 touch some of the same .NET files and runtime.proto; any merge order, the second resolves small text conflicts. #191 takes Status fields 18 to 20, so this one uses 21.
- Depends on: none

## What and why
Owner, 2026-10-31: "for changes operator can change batch, text, product etc and all important things to be verified by supervisor on any other pc or mobile to activate run button. This option can be given for double check while changeover and can be activated in server admin panel. by default it is active."

Related: PT11-30 and PT11-34 (batch start signature, four-eyes) in docs/vision-system/PART11-DESIGN.md section 6.

## Section 5
- New item: `[x] (done 2026-10-31, PR #213: the line PC refuses Start on the cameras until a supervisor accepts the job, its version and every batch value on the plant server's web page or in the mobile app; plant server setting per plant or line, on by default; requirements CV-01 to CV-10, CHANGEOVER-VERIFY.md)` Changeover verification by a supervisor before Run (owner, 2026-10-31)

## Timeline (section 3)
| 2026-10-31 | Changeover verification built: station gate in Station.Start (production runs only; batch and job locked while running), record and answer through the heartbeat, plant server request table, setting and decide API with four eyes, web page, mobile approval screen with a count badge, Studio Run band with Ask again. Owner 2026-11-01: a second person may accept at the line PC while the plant server is down (runtime AcceptChangeover, Studio dialog with PIN, password or card; the server records it). Tested once: dotnet PlantEvents 41, StationAgent 63, VisionStudio 47, Studio 197, RuntimeContract 18, Runtime.Host 41 (one card test that rewrites the users file with random times failed once in the full run and passes alone, 7/7); plant_server 89 (changeover and ingest, SQLite); flutter analyze clean, changeover tests 20 | 12 (CV-01 to CV-10) |

## Decisions (section 4)
| Changeover check: what and when | The job, its version, the variant and every batch value form the record; its SHA-256 (length-prefixed UTF-8, the same in C# and Python) is the code, 8 characters shown on both screens. Each change, and each Ask again, is a new request with an ask number that never repeats (at least the seconds since 2026), so an old acceptance never opens a new changeover. Stored-picture runs are not checked. | 12 (CV-01, CV-03, CV-06) |
| Changeover check in doubt | On by default. Before the first answer, with the server unreachable, with a state that cannot be written, and after the plant server was taken out of the settings once it had asked for the check, Start stays refused. A stored acceptance counts after a restart only while the server cannot be reached. A line PC that never had a plant server has no check. | 12 (CV-08), line safety |
| Changeover transport | In the heartbeat (every 5 s) and its answer, like NG image requests: no new queue. A record the server cannot take is left out, never the whole heartbeat, so events keep flowing. | 12 (CV-01) |
| Changeover accepted at the line PC | Owner, 2026-11-01: "Yes, allow at PC". While the plant server cannot be reached, a second person with Supervisor, QA or Admin on the line PC accepts at the PC itself (never over the network) with their own PIN, password or card, never the signed-in operator nor the one who made the change, for the code they were shown. The station sends it until the plant server records it as that person's decision; a refusal given on the server first wins. | 12 (CV-10) |
| Who accepts a changeover | A signed-in person with admin, plant manager, production manager or quality manager for the line, never the operator who made the change, never a service key; a refusal needs a note; decisions append-only. Only a signed-in Admin changes the setting. | 12 (CV-02, CV-05), PT11-34 |

## Update log (section 8)
| 2026-10-31 | Requirements section 12 CV-01 to CV-09 (owner's changeover verification); new docs/vision-system/CHANGEOVER-VERIFY.md and its row in docs/vision-system/README.md | Claude (Mac, branch claude/changeover-verify) |

## Pending
- Changeover verification: accepting becomes a Part 11 Approve e-signature (password asked again) once e-signatures are built (PT11-30, PT11-34, CV-09).
- Changeover verification: push notification to the line's approvers when a request arrives, and a count-only call for the mobile badge.
- Changeover verification: the job file's digest in the record (a job copied by hand with the same name and version counts as the same).
- Changeover verification: the VisionStudio Blazor screens (Jobs, Run product picker) pass the signed-in user on a product change and get an Ask again button.
- Changeover verification: try end to end on a line PC with a plant server on Postgres, the web page in a browser and the app on a phone.
