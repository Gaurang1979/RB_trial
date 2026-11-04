# Plant server: reasons for activate and role changes, Admin re-anchor of the audit chain

- Status: done
- Session: plant-part11-admin, 2026-11-04
- Pull request: #334
- Section 5 item: new
- Components/files: plant_server/backend (api.py, audit.py), plant_server tests (new test_user_reasons_reanchor.py; test_api_rbac.py sends the reasons), plant_server/docs/api/API_CONTRACT.md, docs/vision-system/PART11-DESIGN.md, requirements section 12 (PT11-09a)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-04, PR #334: activate, role add and role remove need a reason of 3 to 500 characters and are audit entries with old and new values; POST /admin/audit/reanchor lets a signed-in Admin, with a reason and the password again as the e-signature, re-anchor the server's audit chain after an investigated break: a sign entry with the signature record, then a chain.reanchor entry naming each break it covers and the old anchor; later checks still list each covered break as investigated, a second change at the same place or a new break is found as before; non-Admins and service keys refused and audited, wrong passwords are sign.failed and count towards the sign-in lock) Plant server: reasons for the remaining user changes and an Admin re-anchor of the audit chain (Req 12 PT11-09a)`

## Timeline (section 3)
| 2026-11-04 | Plant server, follow-ups of PR #306: reasons for activate, role add and role remove, audited as `edit` with old and new values; Admin re-anchor of the audit chain after an investigated break (`POST /admin/audit/reanchor`, reason and password signature, `sign` and `chain.reanchor` entries, the break stays listed as investigated). Tests: new test_user_reasons_reanchor.py, test_api_rbac.py updated | 12 (PT11-09a, PT11-21, PT11-22, PT11-23, PT11-31) |

## Decisions (section 4)
| Re-anchor keeps the break | A re-anchor never removes, rewrites or skips silently: it is a `chain.reanchor` entry, signed by a `sign` entry (meaning `responsibility`, content hash of the findings), that records each break (position, the row's seq, its stored hash and the hash of its bytes) and the old anchored head. The check walks past a covered break only while its row is exactly as recorded and lists it under "investigated", so a second change at the same place, or any new break, is found and alarmed as before. One Admin signs; the second-Admin approval of user, role and policy changes (PART11-DESIGN 6) is a later step-2 item | 12 (PT11-09a, PT11-23, PT11-31) |
| Reason on DELETE | Removing a role takes the reason as a JSON body on `DELETE /admin/users/{id}/roles/{aid}`, like the other user changes, not as a query string | 12 (PT11-22) |

## Update log (section 8)
| 2026-11-04 | REQUIREMENTS-VISION-SYSTEM.md 12: PT11-09a; PART11-DESIGN.md D6, section 5 actions (`chain.reanchor`) and build order step 2; plant_server API_CONTRACT.md: reasons and the re-anchor | Claude (Mac, branch claude/plant-part11-admin-ec0c4b) |

## Pending
- Plant server: user, role and policy changes approved by a second Admin (PART11-DESIGN 6, build order step 2); today one Admin with a reason.
- Plant server: audit entry for creating a user (with a reason) and a reason for the first roles given at creation.
- Plant server: the rest of PART11-DESIGN build order step 2: upload from the line PCs (`/audit/v1/entries`), query filters and export (PDF and CSV), identity snapshot, persistent lockout, password policy, `/sign/v1` with a signature table, the Part 11 compliance report PDF, head check from the heartbeat, `service`/`anon` refused for GMP edits.
- Plant dashboard: an Admin page for users, roles and the audit trail (check, re-anchor) on top of these endpoints; today they are API only.
