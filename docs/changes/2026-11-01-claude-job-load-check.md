# Setup checks at job load (AS-13, AS-14): capture 1-2 pictures per camera after a job load and before Run, picture check and pixel check, advice only

- Status: done
- Session: suspicious-mclaren-e13b58, 2026-11-01
- Pull request: #219
- Section 5 item: new
- Components/files: proto runtime.proto (Status.setup_check = 22, SetupCheck, CameraSetupCheck), dotnet Runtime.Host (SetupCheck.cs, LineRuntime, RuntimeHost), Studio (Run screen "Setup check" card, SetupAdvice shared with the designer, en.json), requirements section 16 (one "Built" line)
- Depends on: none (#215 and #217 are merged)

## Section 5
- [x] (done 2026-11-01, PR #219: runtime SetupCheck on every job load while stopped, Status.setup_check, Run screen "Setup check" card; closes the "Setup checks at job load" follow-up of PR #217) Setup checks at job load: one or two pictures captured per camera when a job is loaded on the line PC, before Run, with the picture check (AS-13) and pixel check (AS-14) advice shown; advice only, never in a running batch (requirements 16, "When the picture and pixel checks run")

## Timeline (section 3)
| 2026-11-01 | Setup checks at job load built: on each job load (product change, PLC job select, runtime start with a job, job saved again) while the line is stopped, each camera with tools gives one picture (two when a Read code or text tool found nothing to measure), grabbed as Grab in job setup does (Station.GrabOne, 1 s timeout) or from its stored pictures on a replay line; picture check per corner and centre and the tools with the size measure on, on a copy of the job; advice in Status.setup_check and on the Run screen's "Setup check" card while stopped. Start stops a check still going and drops its pictures; a refused Start lets it run again; a failed capture is said plainly; Run is never refused for it. Tests: Runtime.Host.Tests 73/73 (9 new), Studio.Tests 249/249 (3 new), RuntimeContract.Tests 19/19 | 16 (AS-13, AS-14), 12 |

## Decisions (section 4)
| Setup check picture at job load | A software grab outside the run, the same as Grab in job setup (the camera opened, fired once, closed, under the station's lock), not a PLC-triggered picture: the PLC refuses triggers until Run (code 111), so a PLC picture before Run would need a new PLC signal, which the task ruled out. Outside a run the PLC may keep the light off (LightOn 0 = on while running): a dark grabbed picture says so on the Run screen. Whether the light is on in set-up is to be tried on the line (Pending) | 16, PLC-INTERFACE 3, 5 |
| No CPU work in the batch | The check runs on its own below-normal thread; Start tells it to stop before the station starts, and it looks at the line between every picture and measure, so nothing is grabbed or measured once Run is pressed. A picture already being taken holds Start at most 1 s (the grab's timeout). Its tools are built on a copy of the job and disposed at once; the line's job and tools are never touched | 12, 16 |
| Where the advice shows | On the Run screen's own "Setup check" card while the line is stopped, not on the changeover screen of draft #213: the advice is for the engineer or operator before Run and needs no supervisor; #213 can show the same Status.setup_check on its screen later if wanted | 16, UI-UX-SPEC 4.1 |

## Update log (section 8)
| 2026-11-01 | Requirements section 16: one "Built at job load" line under "When the picture and pixel checks run" | Claude (Mac, branch claude/job-load-check) |

## Pending
- Setup check at job load: try it on the line PC with the real camera and PLC; if the grabbed picture is dark because the PLC keeps the light off outside a run, the owner chooses between the PLC keeping the light on in set-up and a PLC-triggered picture before Run (a new PLC signal)
- Setup check advice at job load to the plant server and mobile app (needs a plant event; only the Run screen shows it now)
- UI-UX-SPEC 4.1: the Run screen's "Setup check" card
