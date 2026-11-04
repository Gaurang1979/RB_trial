# Learning session: supervisor sorts good and bad pictures full screen, then learns (AS-04 addition)

- Status: done
- Session: claude/learn-sorting, 2026-11-04
- Pull request: #327
- Section 5 item: new
- Components/files: proto/visionstudio/remote/v1/runtime.proto (Status.learning = 24, ReviewPart flags, LearnResponse percentages, ListToSort, Get/SetLearningSettings; additive); dotnet/VisionCore (AutoTune.NearLimit, TuneCount percentages); dotnet/StationGuard (NgImageStore.Pictures); dotnet/VisionStudio/Services (OkSampler new, Station: faults in NG .json, good pictures offered); dotnet/Runtime.Host (Sorting.cs new, Reviews.cs, LineRuntime.cs, RuntimeHost.cs, Services.cs); dotnet/Studio (Sorting view and view model new, NG viewer second validation and percentages, Run screen Learning button and idle notice, client calls, en.json); docs/REQUIREMENTS-VISION-SYSTEM.md AS-04a..g
- Depends on: none. Overlaps #325 (remote support) in Access.cs (not changed here), LineRuntime.cs, RuntimeHost.cs, RuntimeOptions.cs (not changed here) and runtime.proto: #325 first; this one uses Status field 24 because #325 takes 23.

## Section 5
- [x] (done 2026-11-04, PR #327: learning session built and tested on the runtime and Vision Studio; owner-approved process) Learning session: good pictures kept (1 in 50 plus near-limit), a Supervisor setting (on/off, idle minutes), the Learning button and the near full-screen sorting of saved good and bad pictures (zoom, pan, Accept, Reject, Back, Next, Skip), second validation of unreadable parts with "record only", possible-escape alarm, acceptable % and not acceptable % in Learn, and a small closable idle notice on the Run screen (requirements AS-04a..g)

## Timeline (section 3)
| 2026-11-04 | Learning session (owner, same day, after a proposal and the owner's answers): the line keeps 1 good part in 50 per camera and the near-limit ones in data/learning-ok; the supervisor sorts the saved good and bad pictures nearly full screen; an unreadable part is validated twice and may be kept as a record only; a passed part rejected raises possible_escape; Learn reports acceptable % and not acceptable %, and may run on an idle line with RunEnable off; the Run screen shows a small closable notice when the line is idle and pictures wait. Tested on Runtime.Host, Studio, VisionCore, StationGuard, VisionStudio | AS-04a..g, DL-06, DL-08, section 12 |

## Decisions (section 4)
| Learning session started by a person | Owner, 2026-11-04, after the first proposal (learning started by itself on idle): learning is started with a button by Supervisor, QA, Engineer, Admin (OEM/Support pending), and the idle notice only invites sorting. Keeps DL-06 ("training is started only by an explicit user action"). | AS-04, DL-06 |
| The supervisor sorts, not the operator | Owner's choice, 2026-11-04. The runtime's review rights (Supervisor, QA, Engineer, Admin) are reused for listing, sorting, learning and the setting. | AS-04c, PT11-11 |
| Unreadable bad pictures validated twice, also in the NG review | Owner, 2026-11-04: "NG image which can not be read will ask for second validation". Decided on the safe side (review finding): every reason that is not a listed defect the tool saw, and no reason at all, needs the second validation, so a new or unknown reason never teaches "good" on one tap. A bad picture whose cause was not kept (saved before this change, or a part the PLC rejected) is treated as clearly bad: record only when accepted. The earlier one-tap Accept of "code not found" (PR #206) now asks first. | AS-04d, AS-12 |
| Good pictures in their own store | data/learning-ok (Mode All, 2000 files, 2 GB, the resource guard's clean-up), never the NG images or their counts; under High memory or disk pressure no good picture is kept (the NG store's rule for OK images). Near limit = within 0.10 of Find the part's lowest match or 10 of Check text's strictness, at most 30 an hour per camera. | AS-04a, section 12 |
| NG pictures keep their faults | The NG image .json now holds "faults" ("tool|reason key"), so a picture can be sorted, with its double-check, hours later and after a restart; the sorting reads the stores' files, not the 500 recent parts in memory. | AS-04c |
| Learning on an idle running line | Allowed only when the line has had no part for the idle minutes, a set-up PLC reports RunEnable off with its link up and heartbeat good (anything else counts as on), and the resource guard is at Normal; it stops at the next picture when a part comes, RunEnable comes on or the line starts or stops. Apply still needs the line stopped. | AS-04f, DL-06, section 12 |
| Possible escape | A passed picture rejected raises plant alarm possible_escape (High), cleared at the next Start, and writes an audit-trail event; the picture joins the confirmed bad set, so learning can only make the job stricter for it. | AS-04e, section 12 |
| Setting off | The sorting lists no picture and no good picture is kept; the Learning screen still opens for a supervisor so the setting can be turned on there; the NG review is unchanged. |  AS-04b |
| Setting default on | The owner asked for the feature with a switch; good pictures are capped and dropped first under pressure, so on is the default. Changes are kept with who and when and are audit-trail events. | AS-04b |

## Update log (section 8)
| 2026-11-04 | Requirements AS-04a..g: the learning session (good pictures, setting, sorting, second validation, possible escape, Learn percentages and idle learning, idle notice) | Claude (Mac, branch claude/learn-sorting) |

## Pending
- OEM and Support sign-in in Vision Studio on the line PC (today's OTP, as OemSignIn in VisionStudio) so they can sort and start learning; the runtime has no OEM/Service role on gRPC today and PR #325 keeps Service sessions view-only (owner, 2026-11-04 named OEM/Support as starters)
- Learning session strings in the 14 other languages: Vision Studio ships only en.json today (run.learning.*, sort.*, review.unreadable, review.accept_checked*, review.percent)
- Learning session on a line PC: the sorting screen at 1366 x 768 and on touch, the idle notice, and good-picture disk use over a shift with real cameras
- AI model fine-tuning on the plant server from the sorted pictures (DL-25), with the same gate and approval as Learn from reviews (owner's phase 2)
