# Check text: expected text from PLC user registers or a string block longer than PartId

- Status: done
- Session: ocv-plc-text, 2026-10-30
- Pull request: #136
- Section 5 item: new (Pending in docs/changes/2026-10-30-claude-ocv-shift-plc-inkjet.md: "Expected text from PLC user registers")
- Components/files: dotnet/VisionCore/Plc (PlcBlocks.cs, ModbusServer.cs, PlcRuntime.cs), dotnet/VisionCore/Tools (ToolModel.cs, TextTool.cs), dotnet/VisionStudio/Services/Station.cs (PLC values only), dotnet/VisionCore.Tests/PlcTextTests.cs; docs/vision-system/PLC-INTERFACE.md 3.1, 3.7; TOOL-SPECIFICATIONS.md 7.3
- Depends on: none

## What was built

- **UserIn1..4** are PLC fields of a Check text line (decimal; 0 = no value, like ProductID 0). They are taken from the input block in the scan that sees the trigger.
- **Text block** (PLC-INTERFACE.md section 3.7, new and optional): Modbus holding registers 500–627. Register 500 is `TextTriggerId`; registers 501–627 hold strings wherever the job places them. A line with `SourceName = "Text"` keeps its place in the job (`TextLineSpec.PlcText`: register, length up to 254, `Ascii` or `S7String`, byte order `HighFirst` or `LowFirst`).
- **Stale check.** The runtime takes the block with each trigger (main trigger and each camera channel on its own). The strings belong to the part only when `TextTriggerId` equals the trigger's `TriggerID`, the ID is not 0, and that source has not taken the same ID before. So each part needs a new TriggerID when the text block is used.
- **Line safety.** A stale, empty or unreadable string (an S7 length beyond its header, a byte that is not printable ASCII) fails the line with `explain.ocv.no_expected` and the reason. The part is NG and the PLC gets `Ng`, as for a missing PartID. Setup refuses a `Text` line with no place or one outside 501–627. Remote API triggers carry no text block, so such lines fail there.
- Additive: `IPlcLink.ReadText()` has a default (no block), `PlcTrigger` and `PlcValues` gain init properties, and old jobs load unchanged (`PlcText` is left out when null).

## Section 5
- New item: `[x] (done 2026-10-30, PR #136: UserIn1..4 and a PLC text block at Modbus 500-627 stamped with the TriggerID; stale, empty or unreadable = NG) Check text expected text from PLC user registers or a string block longer than PartId`

## Timeline (section 3)
| 2026-10-30 | Check text: expected text from the PLC's UserIn1..4 and from a new text block (Modbus 500–627, strings up to 254 characters placed by the job). The TriggerID stamp makes a stale string NG | Tool spec 7.3; PLC interface 3.7; requirements 12 |

## Decisions (section 4)
| PLC text block: stale strings | The block carries the TriggerID it was written for, and each TriggerID is taken once per trigger source. A string from another part, an ID of 0 or a repeated ID fails the part. So the PLC must give each part a new TriggerID when it uses the block; checking the stamp needs no write back to the PLC, so it works for any protocol | PLC interface 3.7, 4.1; requirements 12 |
| PLC text: characters | Printable ASCII only (0x20–0x7E); any other byte makes the string unreadable and the part NG, never a guess | PLC interface 3.7; requirements 12 |

## Update log (section 8)
| 2026-10-30 | PLC-INTERFACE.md: new section 3.7 text block, UserIn as expected text, protocol table; tool spec 7.3 "Built" note for PLC UserIn and Text | Claude (Mac, branch claude/ocv-plc-text) |

## Pending
- A try with a real PLC writing the text block (Delta or Siemens MB_CLIENT): byte order and the stamp-then-trigger order on the wire.
- Studio (`dotnet/Studio`) settings for a Check text line's PLC text place (register, length, encoding, byte order) and the UserIn fields; the PLC screen showing the text block.
- PLC function blocks (section 8) and the Delta program: write the text block and `TextTriggerId` before the trigger.
