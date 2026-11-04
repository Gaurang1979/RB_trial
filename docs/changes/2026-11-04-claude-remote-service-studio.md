# Remote service step 4b: Vision Studio badge, operator confirmation and supervisor approval for remote service

- Status: done
- Session: remote-service-studio, 2026-11-04
- Pull request: #347
- Section 5 item: Remote service 4b
- Components/files: proto/visionstudio/remote/v1/runtime.proto (RemoteService.requests, RemoteRequest, RemoteChange, AnswerRemoteRequest); dotnet/Runtime.Host (RemoteRequests.cs, BatchCorrections.cs new; RemoteService.cs, LineRuntime.cs, RuntimeHost.cs, Services.cs, README); dotnet/VisionReports (ServiceLog.BatchCorrections, ReportData.Corrections, ExcelReport, PdfReport); dotnet/VisionStudio/Services/Reports.cs (corrections source); dotnet/Studio (RemoteRequestViewModel and view, badge, card tap, client call, model, en.json); tests (Runtime.Host.Tests/RemoteServiceTests.cs, Studio.Tests/RemoteServiceTests.cs, Studio.Tests/Doubles.cs); docs/vision-system/REMOTE-SUPPORT.md
- Depends on: #345 (remote service 4a; merged 2026-11-04)

## Section 5
- [x] (done 2026-11-04, PR #347: Vision Studio's neutral "Remote service active" badge; remote requests in the status, answered at the line PC (operator Yes/No, supervisor PIN, password or card); job file uploads after the operator confirms with the line stopped; batch value corrections approved by a supervisor, chained in the service log, shown in batch reports next to the values entered) Remote service 4b: Vision Studio shows the neutral "Remote service active" badge (Status.remote_service; English string table, other languages when Studio gets them) and asks the operator to confirm and a supervisor to approve (PIN or card) through the runtime contract; then uploads to jobs and corrections of batch details are switched on.

## Timeline (section 3)
| 2026-11-04 | Remote service 4b: Vision Studio shows the neutral "Remote service active" badge and the remote requests at the line PC: the operator confirms a job file upload (line stopped), a supervisor approves a batch value correction with their own PIN, password or card (checked like a changeover's second person, local endpoint only). Corrections are chained entries in the service log; batch reports show them next to the values entered | REQ-11, REQ-08, REQ-12, PROT-30, REQ-16 |

## Decisions (section 4)
| Remote corrections | Owner, 2026-11-04: in Vision System only batch values may be corrected remotely; the correction is a chained entry next to the original, and batch reports show the corrected value with "was …, corrected by …, approved by …, reason". Inspection results and OK/NG are never correctable | REQ-11, REQ-16 |
| Remote requests at the line PC | A confirmation is answered by whoever is at the line PC (Vision Studio on the local endpoint); a correction needs a supervisor's own PIN, password or card there (Supervisor, QA or Admin), checked like a changeover's second person; neither is ever answered over the network. Requests name what changes, never who asks (REQ-08) | REQ-12, REQ-08 |
| Remote job uploads | A job file goes in place only while the line is stopped and after the operator confirms; a job loaded at the time is not run on the new file until opened again and checked (CV-12) | Req 12 |

## Update log (section 8)
| 2026-11-04 | REMOTE-SUPPORT.md: step 4b (badge, requests, job uploads, batch corrections), owner decision on corrections; Runtime.Host README | Claude (Mac, branch claude/remote-service-studio) |

## Pending
- Remote service on a Windows line PC: the confirmation and approval panels with a real card reader, and a remote job upload followed by the CV-12 re-check.
- remote-service agent: withdraw a session's open requests at the line PC when the session ends (today they stay until answered or expired: 10 minutes for a confirmation, 30 for an approval).
- Vision Studio string table: the new remote.request.* and top.remote_service texts in the other languages when Studio gets them (only en.json exists today).
