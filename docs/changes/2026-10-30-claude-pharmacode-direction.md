# Pharmacode reading direction: read a Pharmacode with its bars lying flat (turned 90 degrees)

- Status: done
- Session: pharmacode-direction, 2026-10-30
- Pull request: #127
- Section 5 item: new (Pending in docs/changes/2026-10-30-claude-pharmacode-dotcode.md: "Pharmacode with its bars lying flat")
- Components/files: engine/include/codereader.h (cr_pharma_direction, cr_params.pharma_direction), engine/src/c_api.cpp, decode.cpp, pharmacode.cpp, reader.hpp, engine/tests/engine_tests.cpp, ml/engine.py, dotnet/CodeReader (Native.Params, PharmacodeDirection, ReaderSettings), dotnet/VisionCore/Tools/CodeTool.cs (CodeToolConfig.PharmacodeDirection), docs/vision-system/TOOL-SPECIFICATIONS.md 5.0
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #127: cr_params.pharma_direction across / down, Read code tool and job file PharmacodeDirection, default across) Pharmacode with its bars lying flat (a code turned 90 degrees): a reading-direction setting for it`

## Timeline (section 3)
| 2026-10-30 | Pharmacode reading direction: the Read code tool states across (bars upright, default, as before) or down (bars lying flat); engine reads flat codes turned either way in both standard and reverse, a code lying the other way gives no read; engine and .NET tests | 12 |

## Decisions (section 4)
| Pharmacode direction setting | The job states it (across / down), never the image: Pharmacode has no start or stop, so the other direction reads another valid value. Down: standard = the bottom bar weighs least (a code turned clockwise), reverse = the top bar. Default across, so older jobs and an older caller's shorter cr_params read as before; an unknown value reads no Pharmacode (NG) | 12 |

## Update log (section 8)
| 2026-10-30 | TOOL-SPECIFICATIONS.md 5.0: Pharmacode reading direction (across / down) | Claude (local Mac, branch claude/pharmacode-direction) |

## Pending
- Vision Studio Teach page: a reading-direction choice for a Pharmacode tool (today set in the job file only; English string, then the 15 languages).
