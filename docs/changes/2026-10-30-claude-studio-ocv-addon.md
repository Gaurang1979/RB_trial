# Vision Studio (Avalonia): the Read code add-on and Check text's PrintQuality on the result

- Status: done
- Session: studio-ocv-addon, 2026-10-30
- Pull request: #134
- Section 5 item: new
- Components/files: proto/visionstudio/remote/v1/runtime.proto (fields added only: ToolResult.print_quality, CodeRead.add_on, LiveFrame.tools and LiveFrame.codes), dotnet/Runtime.Host/LineRuntime.cs, dotnet/Studio (RuntimeModels, GrpcRuntimeClient, GraphicHit, RunViewModel, Viewers, i18n/en.json), dotnet/Studio.Tests/ResultDetailTests.cs (new), dotnet/Runtime.Host.Tests/LineTests.cs, docs/vision-system/SDK-API.md 11.1
- Depends on: none. Overlaps #137 (sign-in; other proto messages, other parts of RuntimeModels, GrpcRuntimeClient and en.json): either merges first.

## Section 5
- [x] (done 2026-10-30, PR #134: runtime contract fields ToolResult.print_quality, CodeRead.add_on, LiveFrame.tools and LiveFrame.codes, filled by vs-runtime from VisionCore; in Vision Studio a tap on a code shows its add-on and the NG viewer lists each Check text tool's print quality and each add-on; English only) Vision Studio (Avalonia): the Read code add-on and Check text's PrintQuality on the result.

## Timeline (section 3)
| 2026-10-30 | Avalonia Vision Studio shows the EAN/UPC add-on and Check text's PrintQuality on the result: four fields added to the runtime contract, filled by vs-runtime, shown in tooltips and the NG viewer | 5.2, 7.3 |

## Decisions (section 4)
| Add-on and PrintQuality in the Avalonia app | The Studio has only the Run screen, and the contract has no calls for editing tool settings, so this pull request does the result side only. The live frame carries its camera's tool results and codes (fields added, `v1` kept), because the Studio reads frames, not the result stream. The settings panels wait for the Hybrid 3 tool-editing calls. | 5.2, 7.3, 10 |

## Update log (section 8)
| 2026-10-30 | SDK-API.md 11.1: ToolResult.print_quality | Claude (Mac, branch claude/studio-ocv-addon) |

## Pending
- Vision Studio (Avalonia) Read code tool panel: "Add-on required" and "Expected add-on" (CodeToolConfig.AddOnRequired, ExpectedAddOn). Needs tool-editing calls in the runtime contract (Hybrid 3).
- Vision Studio (Avalonia) Check text tool panel: expected-text source per line (Job, PLC, Tool), Allow shift, Learn dots, print type Inkjet and join strength (TextToolConfig). Needs tool-editing calls and a Learn dots call in the runtime contract (Hybrid 3).
- Vision Studio on a line PC: look at the add-on tooltip and the NG viewer's print quality lines with a real EAN-13 with an add-on and a Check text job.
