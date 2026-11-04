# Part 11 design: one audit-trail and e-signature design shared by runtime, Studio, Dashboard, plant server and mobile

- Status: done
- Session: part11-design, 2026-10-31
- Pull request: #185
- Section 5 item: Part 11 design: one audit-trail
- Components/files: docs/vision-system/PART11-DESIGN.md (new), docs/vision-system/README.md
- Depends on: none (#184 merged)

## Section 5
- [x] (done 2026-10-31, PR #185: docs/vision-system/PART11-DESIGN.md: one identity per plant with line snapshots that never carry Admin hashes, rights catalogue per function with default roles, one audit entry with old and new values and all stamps, per-writer hash chains anchored by heads in the 5 s heartbeat, system actors for PLC and token run actions, append-only results with batch digests, fixed approve-before-load, e-signatures bound to the record version and carried in the chain, 1 year on the server and 14 days on the line, Part 11 report PDF built by the server, contract changes and build order; Opus review findings fixed) Part 11 design: one audit-trail and e-signature design shared by runtime, Studio, Dashboard, plant server and mobile
- "Part 11 build in the line PC runtime": follow PART11-DESIGN.md section 9 steps 1 (VisionAudit library) and 3 (contract and host); both security, Opus
- "Part 11 build in the aggregation station Dashboard": PART11-DESIGN.md D2 and section 9 step 5 (VisionIdentity and VisionAudit in-process)
- "Part 11 build in the plant server": PART11-DESIGN.md section 9 steps 2 and 6, including the Part 11 report PDF

## Timeline (section 3)
| 2026-10-31 | Part 11 design written and reviewed: identity, rights, audit entry and chain, e-signatures, retention 1 year server / 14 days line, report from the server, build order | 12 |

## Decisions (section 4)
| Part 11 identity | The plant server holds the people; each line keeps a snapshot of its own users only, never Admins; user changes are made online at the plant server | 12 |
| Part 11 audit chain | One entry format everywhere; one hash chain per writer, append-only in the database; heads sent in the 5 s heartbeat so a rewritten local chain is caught; the server checks and stores the exact bytes received | 12 |
| Part 11 system actors | PLC and plant tokens may run (start, stop, load an approved job) as named actors, never edit or sign; the service key and anon mode are refused for GMP edits; a refused PLC job change keeps the previous job and sends Error 200/201 | 12 |
| Approve before load | A job or model version must be approved before the line loads it; no setting removes this; nobody approves a version they authored | 12 |
| Part 11 lockout | 5 wrong locks the user on that device for 5 minutes (owner 2026-10-30 rule kept); network tries never lock the line's own screen; tries across devices only raise an alert | 12 |

## Update log (section 8)
| 2026-10-31 | docs/vision-system/PART11-DESIGN.md (new) and its row in docs/vision-system/README.md | Claude (Mac, branch claude/part11-design) |

## Pending
- Part 11 results and images: results append-only with a batch digest in the audit chain; record images copied to the plant server before the line quota removes them (PART11-DESIGN.md D14)
- Part 11 owner questions (PART11-DESIGN.md section 11): Razor VisionStudio retired?; Windows domain at the plants?; signatures by default beyond section 6; standalone line PC without a plant server; lockout per device versus PT11-05; which images are records
