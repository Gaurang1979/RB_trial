# Plant server: repeated-lock alert, audit chain alarm and anchored head, session sweep, disable reason

- Status: done
- Session: claude/plant-audit-alarms, 2026-11-03
- Pull request: #306
- Section 5 item: new (from the ## Pending list of docs/changes/2026-11-03-claude-plant-session-idle-pin-audit.md); Req 12 PT11-05, PT11-22, PT11-23, PT11-08
- Components/files: plant_server/backend (audit.py, auth.py, api.py, alerts.py, store.py, serve.py), plant_server/tests; docs/REQUIREMENTS-VISION-SYSTEM.md (section 12 PT11 items), docs/vision-system/PART11-DESIGN.md
- Depends on: none. Overlap: only docs/REQUIREMENTS-VISION-SYSTEM.md with #273, #294, #296, #299, #300, #301, #302, #304, all in other sections or items; any merge order

## Section 5
- [x] (done 2026-11-03, PR #306: three locks of one user in an hour alert Admin; a broken audit chain or anchor alerts Admin and IT/OT; the server's chain head anchored in a file outside the database; sessions swept every minute and each end audited; disable needs a reason) Plant server audit alarms, chain anchor, session sweep and disable reason (PT11-09)

## Timeline (section 3)
| 2026-11-03 | Plant server, follow-ups of PR #279: three locks of one user within an hour (sign-in locks and mobile PIN lockouts, counted from the `lock` audit entries) raise a high alert to Admin; a broken audit chain at start or on demand, or an anchor mismatch found then or by the check every minute, raises a high alert to Admin and IT/OT and a log line; both stay open until a person resolves them. The newest seq and hash of the server's own chain are kept in an anchor file outside the database (`--audit-anchor`, by default `<db>.audit-head` for SQLite; `SPI_AUDIT_ANCHOR`), so removing or rewriting the newest entries is found; the anchor never moves back and stays fixed at a break. A sweep every minute ends idle and expired sessions and audits each end (an expired session used again is audited too). Disabling a user needs a reason (PT11-22). 14 new tests; whole plant_server suite passes on SQLite, the audit and auth files on PostgreSQL | PT11-05, PT11-09, PT11-22, PT11-23 |

## Decisions (section 4)
| Security alerts on the plant alert engine | Repeated locks and a broken audit chain are alerts of the existing engine (rule types `security_repeated_locks` and `audit_chain_broken`, added by default like the vision rules, turned off only with `"enabled": false`), on the wall clock, and never resolve by themselves: a person resolves them with a reason. One alert per user (locks) and one for the chain while open; a lock or a break after it is resolved opens a new one | PT11-05, PT11-23, PT11-09 |
| Anchor of the server's own chain | A file, not a database row: whoever removes entries in the database must also find and change the file. It is rewritten (fsync, atomic replace, mode 600) after each entry, never moves back, and once a break is found it stays fixed, so a restart finds the same break. A missing file starts at the table's head (first run); an unreadable one is a break. It is as strong as the file's separation from the database: best on another disk or account; the line PC heads in heartbeats (D6) remain the stronger anchor for station chains | PT11-09, PT11-23, D6 |
| Session sweep | Every minute, ends sessions past their 12 h or idle past the limit (the view-only wall screen keeps only the 12 h) and audits each end with "ended by the session sweep". A session used between the sweep's read and its delete is left alone | PT11-06, PT11-08, PT11-09 |

## Update log (section 8)
| 2026-11-03 | REQUIREMENTS PT11-09 (new); PART11-DESIGN D6 (the server's own anchor), 3 (what is built), 9 (step 2 progress) | Claude (Mac, branch claude/plant-audit-alarms) |

## Pending
- Plant server: copy the audit chain head off the server too (to the ERP or a second server), so someone who can change both the database and the anchor file is still found.
- Plant server: an Admin step, with a reason and signed, to re-anchor after a break has been investigated (today the anchor stays fixed at the break until the file is replaced by hand).
- Plant server: reasons for the other user changes (activate, role add and remove), and the rest of PART11-DESIGN build order step 2.
