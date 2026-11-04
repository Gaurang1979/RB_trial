# Audit of the merged Antigravity pull requests: findings and fix items registered (Req 17.11)

- Status: done
- Session: Claude (audit session), 2026-11-03
- Pull request: #267
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (new 17.11, AUD-01..11), this note
- Depends on: none. The fix pull requests for AUD-01..10 cite these items and merge after this one.

## Section 5
- [x] (done 2026-11-03: Req 17.11 AUD-01..11) Audit of the merged Antigravity pull requests #240-#261 on main 3c9c020: tools registered against the requirements, ten owner-approved fixes registered as AUD-01..10.

## Timeline (section 3)
| 2026-11-03 | Audit of main after the Antigravity merges: engine 23/23 tests, plant server 370, licence 4, mobile 48 + 106 pass; ten line-safety, security and test problems found and registered as Req 17.11 AUD-01..11 | Req 17.11 |

## Decisions (section 4)
| Antigravity audit fixes | Owner approved each, one at a time, 2026-11-03: taught references only, never change the camera image, only checking tools decide, new tools follow the fixture, AS-03 tests restored, signed recipe import, real numbers on the Run screen, Inspect model load fixed, AES-256-GCM assets, timed engine tests serial | Req 17.11 AUD-01..10 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.11: Antigravity tools registered; audit fix items AUD-01..11 | Claude (branch claude/audit-antigravity-reqs) |

## Pending
- Fold the 17 separately named engine libraries (vs_thermal, vs_anomaly, ...) into corert64 behind sys_dispatch (AGENTS.md IP rule 2; Req 17.11 AUD-09).
- Two commits reached main without a pull request on 2026-11-02 (123ed92, 042d254): turn on branch protection for main (owner's setting; Req 17.11 AUD-11).
