# Requirements audit: every owner input written in the requirements document or its design document

- Status: done
- Session: req-audit, 2026-10-30
- Pull request: #144
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (sections 4, 8, 9, 12, 15, new 17); no product code
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-30, PR #144: owner inputs from every session, the history, the change notes, the commits and the memory checked against the requirements; missing ones written in sections 4, 8, 9, 12 and 17 with the owner's words; disagreements listed in section 15) Requirements audit: every owner input written in the requirements document or its design document`
- [ ] Owner: decide the six disagreements listed in requirements section 15 ("Owner inputs that do not agree"): read speed for 96 codes, learning while the line runs, use of patented methods, line PC tier, licensing model (dongle, trials, any GenICam camera), offline grace.

## Timeline (section 3)
| 2026-10-30 | Requirements audit: owner inputs from every session checked; supplied-parts licensing, reference line hardware, auto calibrate, lens effects, line instruments and wiring, length/gap/touching products, line safety, line PC performance, aggregation reading (product master, time per case, duplicates, 100% of visible codes), OCR print types and 100 ms, deferred plant/mobile/printer requirements and the build order written | Requirements 4, 8, 9, 12, 15, 17 |

## Decisions (section 4)
| Owner inputs in the requirements | Every requirement and decision the owner gives in a session is written in the requirements document (or the design document it belongs to) with the owner's words and date; inputs that disagree are listed for the owner in section 15, never settled by an agent | Requirements 15, 17 |

## Update log (section 8)
| 2026-10-30 | Requirements: owner inputs added in sections 4, 8, 9, 12 and 17; Delta first in the PLC open question; disagreements listed in section 15 | Claude (Mac, branch claude/req-audit) |

## Pending
- Design the supplied-parts rules not designed yet (requirements section 9): firmware check against the central database, company product names in the camera firmware, maker names hidden in the software.
- Requirements section 7 and LOCALISATION.md: confirm that "other languages" deferred (owner, 2026-10-30) means the strings wait, not that the 15 languages are dropped.
