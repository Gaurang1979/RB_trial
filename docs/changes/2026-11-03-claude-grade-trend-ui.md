# Print-quality trend screens in the plant dashboard and the mobile app (Req 17.3 PQ-02, PQ-06)

- Status: done
- Session: Claude desktop (grade-trend-ui), 2026-11-03
- Pull request: #300
- Section 5 item: new
- Components/files: plant_server/dashboard/grades.html (new), plant_server/backend/vision/api.py (page route), plant_server/tests, mobile/plant_app/lib/modules/vision (grade_api.dart, grade_screen.dart new; floor_screen.dart, vision_module.dart), mobile/plant_app/test, docs/REQUIREMENTS-VISION-SYSTEM.md 17.3
- Depends on: none (PR #272 is merged)

## Section 5
- [x] (done 2026-11-03, PR #300: web page `/vision/grades/page` and the mobile Print quality screen draw the trend per line, station and tool with the lowest grade, the drift status and the ungraded parts) Dashboard and mobile app: print-quality trend chart (Req 17.3 PQ-02, PQ-06).
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
| 2026-11-03 | Plant dashboard page `grades.html` (served at `/vision/grades/page`) and the mobile app's Print quality screen (floor screen app bar) show the grade trend from `/vision/grades/trend`; English only; server and Flutter tests pass | 17.3 PQ-02, PQ-06 |
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->

## Decisions (section 4)
| Test helper fixed | `test_vision_grades.py` created its test sessions at time 0, which the Part 11 idle timeout now ends (the trend test got 401); they start now | 17.3 PQ-06 |
| Trend screens are read only | Same data and drift rule as the alarm; the charts mark a bucket with ungraded parts instead of hiding it, and an error or no data draws no chart | 17.3 PQ-06 |
<!-- | Topic | Decision and reason | requirement sections | -->

## Update log (section 8)
| 2026-11-03 | Requirement 17.3 PQ-06 registered | Claude (Mac, branch claude/grade-trend-ui) |
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->
