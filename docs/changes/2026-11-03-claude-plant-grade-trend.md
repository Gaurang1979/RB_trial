# Plant server: print grade trend per line and tool, drift alarm before the lowest grade

- Status: done
- Session: Claude desktop (plant-grade-trend), 2026-11-03
- Pull request: #272
- Section 5 item: new
- Components/files: plant_server/backend/vision/grades.py (new), vision/ingest.py, vision/alerts.py, vision/api.py, backend/alerts.py (rule type and default rule), backend/store.py (vision_grade table), plant_server/tests (test_vision_grades.py new; rule lists in test_vision_alerts.py, test_api_alerts.py), plant_server/docs/architecture/ALERTS.md, docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md 3, docs/REQUIREMENTS-VISION-SYSTEM.md 17.3
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #272: vision_grade table filled from each stored result (and by the results backfill); GET /vision/grades/trend per station and tool with drift status; vision_grade_drift alert on by default on the vision alert lifecycle; 12 tests, SQLite and PostgreSQL; dashboard and mobile screens pending) Plant server: store the print grade values per line and tool, serve the print-quality trend for the dashboard and the mobile app, and raise a drift alarm before the lowest grade is reached (Req 17.3 PQ-01..05).

## Timeline (section 3)
| 2026-11-03 | Plant server keeps print grades per station and tool, serves their trend (`/vision/grades/trend`) and raises a drift alert before the lowest grade; tested on SQLite and PostgreSQL | 17.3 PQ-01..05 |

## Decisions (section 4)
| Grades stay in `values` | The plant server reads `<tool>.grade` and its siblings from the inspection result's existing `values` map: no plant event schema change. An optional `<tool>.grade_min` lets a station send its tool's lowest grade; without it the rule's `lowest_grade` (default C) is used | 17.3 PQ-01, PQ-04 |
| Drift per station and tool | The alert is per station and tool, not per line, so two stations with a tool of the same name never mix their printers. It opens when the mean of the latest 20 samples is within 0.5 grade of the lowest grade, or a least-squares fall reaches it within 20 more samples; an ungraded sample counts as F (doubt is never read as good). It only warns: the line still judges each graded part | 17.3 PQ-03, PQ-05 |
| No separate grade backfill | Grades are kept from the stored result, so the existing results backfill also keeps them; a server whose backfill already finished keeps grades from this version on (grades exist only since PR #70, 2026-10-30) | 17.3 PQ-01 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.3 PQ-01..05 registered; PLANT-DASHBOARD-AND-MOBILE.md 3 and plant_server ALERTS.md describe the grade values, the trend endpoint and the drift rule | Claude (Mac, branch claude/plant-grade-trend) |

## Pending
- Dashboard and mobile app: a print-quality trend chart per line, station and tool from `GET /vision/grades/trend`, with the drift status and the lowest grade line (Req 17.3 PQ-02).
- Vision Studio Read code tool: send the tool's lowest grade as `<tool>.grade_min` in the result's `values`, so the drift alarm uses the line's own limit (Req 17.3 PQ-04).
