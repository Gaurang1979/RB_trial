# Results ledger: commissioning step to anchor an older chain, and a guided new ledger after ALM-801 (Req 17.10 MRG-08 item 8)

- Status: done
- Session: ledger-anchor-cli, 2026-11-03
- Pull request: #311
- Section 5 item: Results ledger: a commissioning step in Vision Studio or the installer that runs AnchorHead
- Components/files: dotnet/VisionStudio (Services/LedgerCommand.cs, Program.cs `ledger` command), dotnet/VisionStudio.Tests (LedgerCommandTests.cs), docs/REQUIREMENTS-VISION-SYSTEM.md (17.10 MRG-08 item 8)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #311: `VisionStudio ledger check|anchor|new`; anchor once, never over a head, only when the chain verifies; new ledger only when the check fails, old files moved to ledger-archive/<time>/ with why.txt and a ledger_new event; each needs --by and a typed confirmation) Results ledger: a commissioning step in Vision Studio or the installer that runs AnchorHead for a results.db chained before PR #303, and a guided "start a new ledger" for the engineer after alarm 801 (Req 17.10 MRG-08).

## Timeline (section 3)
| 2026-11-03 | Results ledger commands (PR #311): `VisionStudio ledger check` runs the start's ledger check and says what it saw; `ledger anchor --by <name>` anchors a results.db chained before the chain head was kept (once, never over an existing head, only when the whole chain verifies, recorded as a `ledger_anchored` event); `ledger new --by <name> --reason "<why>"` after alarm 801 refuses a ledger that verifies, moves results.db, its -wal/-shm and its head into `ledger-archive/<UTC time>/` with `why.txt` (nothing deleted; a failed move is put back) and starts a new results.db with a `ledger_new` event. Both need the typed confirmation on standard input. Tests: 10 new cases in LedgerCommandTests | 17.10 MRG-08 |

## Decisions (section 4)
| Results ledger anchor and new ledger | Command-line steps run by a person (name and typed confirmation), never by Vision Studio or the runtime, so alarm 801 never clears by itself. Anchor runs once and never over a head (a head naming deleted rows is the evidence); a new ledger only replaces a ledger that fails its check, and keeps every old file in ledger-archive/ instead of deleting | 17.10 MRG-08, section 12 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.10: MRG-08 item 8 (ledger check, anchor and new-ledger commands) | Claude (Mac, branch claude/ledger-anchor-cli-bd921d) |

## Pending
- Results ledger: offer the anchor and new-ledger steps on a Vision Studio engineer page (Service role) next to the alarm 801 notice, and in the installer's commissioning steps, calling the same LedgerCommand logic (Req 17.10 MRG-08 item 8).
