# Results ledger: chain fail_tool and reason too, and keep the chain right when two programs write results.db (Req 17.10 REQ-16)

- Status: done
- Session: ledger-chain-v2, 2026-11-03
- Pull request: #284
- Section 5 item: new
- Components/files: dotnet/VisionReports (DatabaseSecurity.cs, ResultLog.cs), dotnet/VisionCore.Tests (DatabaseSecurityTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (REQ-16)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #284: chain v2 covers every recorded field, fail_tool and reason included; one chain for all writers of results.db; old v1 rows still verify) Results ledger: chain fail_tool and reason too, and keep the chain right when two programs write results.db (Req 17.10 REQ-16, MRG-08).

## Timeline (section 3)
| 2026-11-03 | Results ledger chain v2 (PR #284): each new inspection and part row is chained (HMAC-SHA256) over every field it records, fail_tool, reason, read texts, time taken, flags, category and image name included, length-prefixed; rows carry chain_ver, so v1 rows verify by the old formula and a v2 row set back to v1 fails. Every append reads the chain head inside a BEGIN IMMEDIATE transaction, so the runtime, Vision Studio and a second station writing one results.db keep one chain. VerifyLedger() reports v1, v2 and unchained rows. Tests: tamper of fail_tool, reason and 8 other fields, two writers in turn and 4 threads on 2 logs at once, v1 compatibility, downgrade | 17.10 REQ-16, MRG-08 |

## Decisions (section 4)
| Results ledger with two writers | One chain for the whole file, serialized by SQLite's write lock (BEGIN IMMEDIATE, chain head read from the file in the same transaction), not one chain per writer: one order, one check, nothing to merge; no writer keeps the head in memory | 17.10 REQ-16, MRG-08 |
| Results ledger versions | chain_ver per row (empty = v1, "2" = v2); a version never goes back within a table; v1 rows are not re-hashed, so they stay unprotected in the fields v1 did not cover | 17.10 MRG-08 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.10: MRG-08 results ledger chain v2 (all fields, versioned, one chain for every writer, limits) | Claude (Mac, branch claude/ledger-chain-v2) |

## Pending
- Results ledger: keep the chain head (newest id and hash per table) outside results.db, so deleting the newest rows or blanking the newest hash is seen (Req 17.10 MRG-08 item 5).
- Results ledger: run VerifyLedger at runtime start and on a failure raise ALM-801 and refuse line start (Req 17.10 REQ-16 item 5); nothing calls it yet.
