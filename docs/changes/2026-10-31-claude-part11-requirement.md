# 21 CFR Part 11 compliance in the requirements: user levels with rights, audit trail with old and new values

- Status: done
- Session: part11-requirement, 2026-10-31
- Pull request: #184
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (sections 12 and 15)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-31, PR #184: owner's Part 11 requirement written in requirements section 12 as PT11-01 to PT11-44: one account per person, two-part sign-in, password rules and lockout, roles with View/Create/Edit/Disable/Approve/Export rights per function, append-only chained audit trail with old and new values, reason and all stamps, e-signatures bound to the record, records, backup and validation pack; open question on Part 11 answered) 21 CFR Part 11 in the requirements`

## Timeline (section 3)
| 2026-10-31 | Requirements: 21 CFR Part 11 for the full software (PT11-01 to PT11-44: user levels and rights, audit trail with old and new values, e-signatures, records) | 12, 15 |

## Decisions (section 4)
| Part 11 everywhere | Owner, 2026-10-31: "Full software requires 21 CFR part 11 compliance in terms of multi level user login with rights assignment like view, edit and other with log generation with old values if any to new values if any with all stamps audit trail." Part 11 is on in every product (line PC, Dashboard, Vision Package, plant server, browser, mobile), no longer an optional pharma mode. A PIN or a card alone is no longer a full sign-in. | 12, 15 |

## Update log (section 8)
| 2026-10-31 | REQUIREMENTS-VISION-SYSTEM.md section 12: Part 11 requirements PT11-01 to PT11-44; section 15 open question answered | Claude (Mac, branch claude/part11-requirement) |

## Pending
- Part 11 design: one audit-trail and e-signature design shared by runtime, Studio, Dashboard, plant server and mobile (record format, hash chain, time source, rights model, sync to the plant server); Opus, before any build
- Part 11 build in the line PC runtime and Vision Studio: users, rights per function, audit trail with old and new values, e-signatures (PT11-01 to PT11-35)
- Part 11 build in the aggregation station Dashboard (PT11-01 to PT11-35)
- Part 11 build in the plant server, browser dashboard and mobile app (PT11-01 to PT11-35)
- Part 11 validation pack: URS, FS, traceability matrix, IQ/OQ scripts (PT11-44)
