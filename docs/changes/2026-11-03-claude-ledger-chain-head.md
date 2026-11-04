# Results ledger: chain head kept outside results.db, and the ledger checked at runtime start (ALM-801, no start on failure) (Req 17.10 MRG-08 item 5, REQ-16 item 5)

- Status: done
- Session: ledger-chain-head, 2026-11-03
- Pull request: #303
- Section 5 item: new
- Components/files: dotnet/VisionReports (DatabaseSecurity.cs, ResultLog.cs), dotnet/VisionStudio (Services/LedgerGate.cs, Services/Station.cs, Program.cs), dotnet/Runtime.Host (LineRuntime.cs, RuntimeHost.cs), dotnet/VisionCore.Tests (DatabaseSecurityTests.cs), dotnet/VisionStudio.Tests (LedgerGateTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (MRG-08 items 5-7)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #303: chain head in results.ledger-head, signed with the ledger key; deleting the newest rows or blanking the newest hash fails the check) Results ledger: keep the chain head (newest id and hash per table) outside results.db, so deleting the newest rows or blanking the newest hash is seen (Req 17.10 MRG-08 item 5).
- [x] (done 2026-11-03, PR #303: checked when the runtime starts and at every production start; a failure refuses the start, alarm 801 and plant alarm ledger_tampered) Results ledger: run VerifyLedger at runtime start and on a failure raise ALM-801 and refuse line start (Req 17.10 REQ-16 item 5); nothing calls it yet.

## Timeline (section 3)
| 2026-11-03 | Results ledger chain head (PR #303): the newest chained row of each table (id, hash) is kept in results.ledger-head beside results.db, HMAC-signed with the ledger key and moved after each append's commit; VerifyLedger needs the head's row still in the chain with the head's hash, so deleting the newest rows, deleting all rows, blanking the newest hash, or a missing or changed head fails. AnchorHead anchors a log chained before (only when it verifies). LedgerGate checks the ledger when the runtime starts and at every production start; a failure refuses the start ("ALM-801: ..."), shows alarm 801 in the runtime status and raises the plant alarm ledger_tampered. Tests: 9 new ledger cases (clean log, 5 tamper forms, missing/changed/rolled-back head, head one row behind, anchoring) and 5 start-gate cases | 17.10 REQ-16, MRG-08 |

## Decisions (section 4)
| Results ledger chain head | A signed file beside results.db, not a second database or the registry: a second station writing the same results.db over the network shares it. Moved after the commit, never before, so it never names a row the file does not have; the file may be ahead of its head (crash, failed write, two writers racing) and those rows are chained by the key, so no false alarm. A missing head while chained rows exist is never rewritten by the next row | 17.10 MRG-08 |
| Results ledger check failure | Fail closed: a check that fails or cannot run refuses every production start (alarm 801), never only a warning; stored-picture runs are not production and are not refused, as with the integrity check | 17.10 REQ-16, section 12 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.10: MRG-08 items 5-7 (chain head outside results.db, check before production with ALM-801, remaining limits) | Claude (Mac, branch claude/ledger-chain-head) |

## Pending
- Results ledger: a commissioning step in Vision Studio or the installer that runs AnchorHead for a results.db chained before PR #303, and a guided "start a new ledger" for the engineer after alarm 801 (Req 17.10 MRG-08).
- Results ledger: a copy of the chain head off the line PC (plant server), so putting back an older copy of both results.db and its head is seen (Req 17.10 MRG-08 item 7).
- Alarm 801 and the ledger_tampered plant alarm text in the 15 languages (Req 17.10 REQ-16).
