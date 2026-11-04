# heavy-pick.sh parses under macOS bash 3.2

- Status: done
- Session: heavy-pick bash 3.2 fix, 2026-10-30
- Pull request: #106
- Section 5 item: new
- Components/files: scripts/heavy-pick.sh
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #106) scripts/heavy-pick.sh parses under macOS bash 3.2 again; every subcommand had failed with a syntax error
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
| 2026-10-30 | scripts/heavy-pick.sh fixed for bash 3.2: the `case` in the plant_server pytest block inside `$( )` now uses leading `(` patterns; `bash -n` clean, docs-only/queue/units/tests run | - |

## Decisions (section 4)
| Shell scripts on the Mac | `case` patterns inside `$( )` take a leading `(`: macOS /bin/bash is 3.2, which misparses the bare `)` there | - |

## Update log (section 8)
| 2026-10-30 | heavy-pick.sh bash 3.2 fix noted | Claude (Mac, branch claude/heavy-pick-bash32) |

## Pending
<!-- Follow-ups this session leaves, one per line: "- Unit tests for ...". Each becomes an open section 5 item. Leave the section out if none. -->
