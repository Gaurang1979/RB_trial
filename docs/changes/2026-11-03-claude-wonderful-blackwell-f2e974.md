# Run screen shows no invented data: filmstrip live, sample tabs removed (Req 6.1 RUN-LIVE-01)

- Status: done
- Session: claude (run-live-data), 2026-11-03
- Pull request: #301
- Section 5 item: new
- Components/files: dotnet/VisionStudio (Run.razor, Services/RunLive.cs, Shared components), VisionStudio.Tests, docs/REQUIREMENTS-VISION-SYSTEM.md (6.1)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #301: Run screen filmstrip live with an empty state; SplitWipe, Tooling Matrix and Process Stability Map tabs and their sample components removed because the runtime supplies no data for them) Req 6.1 RUN-LIVE-01: the Run screen shows no invented data (follow-up to AUD-07, PRs #266 and #270).

## Timeline (section 3)
| 2026-11-03 | Run screen: "Live NG Filmstrip" lists only NG inspections seen (time, first failing tool) with "No NG parts since this screen opened."; the suite tab bar, SplitWipe, Tooling Matrix and Process Stability Map (sample images, zone temperatures, cluster counts) removed; logic moved to `Services/RunLive.cs` with `RunLiveTests`. | 6.1 RUN-LIVE-01 |

## Decisions (section 4)
| Run tabs | A tab returns only together with a live feed (NG/golden image pair, sealing-zone temperatures, latent-space points); `RunLive.Tabs` encodes that rule. Today no feed exists, so none shows. | 6.1 RUN-LIVE-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 6.1: added RUN-LIVE-01. | Claude (Mac, branch claude/wonderful-blackwell-f2e974) |

## Pending
- Sealing-zone temperature feed from the PLC/sealer into the runtime, then bring back the Tooling Matrix tab (Req 6.1).
- Latent-space (Process Stability Map) points from the HaystackEngine into the Run screen, then bring back that tab (Req 6.1).
- Serve saved NG images and a golden reference so a real SplitWipe comparison can return (Req 6.1).
