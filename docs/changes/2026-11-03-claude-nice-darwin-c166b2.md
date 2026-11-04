# Read code tool sends its lowest print grade as <tool>.grade_min to the plant server (Req 17.3 PQ-04)

- Status: done
- Session: Claude desktop (grade-min-values), 2026-11-03
- Pull request: #297
- Section 5 item: <first words of the existing item exactly as on main, or new>
- Components/files: dotnet/VisionCore/Tools/CodeTool.cs, dotnet/VisionCore.Tests, plant_server/tests/test_vision_grades.py
- Depends on: none (PR #272 merged)

## Section 5
- [x] (done 2026-11-03, PR #297: the Read code tool sends `<tool>.grade_min` in the result's `values` when its lowest print grade is set, via `CodeTool.GradeValues`; the job result prefixes it per tool and the plant server stores it) Vision Studio Read code tool: send the tool's lowest grade as `<tool>.grade_min` (Req 17.3 PQ-04).
<!-- (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
| 2026-11-03 | Read code tool sends its lowest print grade as `<tool>.grade_min`; plant server test keeps it with the grade | 17.3 PQ-04 |
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->

## Decisions (section 4)
<!-- | Topic | Decision and reason | requirement sections | -->

## Update log (section 8)
| 2026-11-03 | Change note only; PQ-04 text already describes `grade_min` | Claude (Mac, branch claude/nice-darwin-c166b2) |
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->

## Pending
<!-- Follow-ups this session leaves, one per line: "- Unit tests for ...". Each becomes an open section 5 item. Leave the section out if none. -->
