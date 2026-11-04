# rMQR unused error correction: left on zxing's value until the ISO/IEC 23941 table is supplied (Req 17.1 GRD-03)

- Status: done
- Session: competent-torvalds-ebebf5 (batch 3), 2026-11-03
- Pull request: #308
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (17.1 GRD-03), this note. No code change.
- Depends on: none

## Section 5
- [ ] rMQR unused error correction: take off the misdecode-protection codewords of the small single-block rMQR symbols once the ISO/IEC 23941 table is supplied; add the rMQR cases to `isoUec` (engine/src/quality.cpp) and a grading test on drawn rMQR symbols (Req 17.1 GRD-03)

## Timeline (section 3)
| 2026-11-03 | rMQR unused error correction checked: the misdecode-protection count per size is not in the repo or in zxing-cpp's block table, so it stays zxing's value with the M 15 % / H 30 % approximation; no code changed. | 17.1 GRD-03 |

## Decisions (section 4)
| rMQR misdecode protection left out | The count p per size comes only from the ISO/IEC 23941 table, which is not at hand; a guessed p could grade a damaged symbol too high (line safety). Owner chose to leave it (2026-11-03). Effect: only the unused-error-correction part of the grade, slightly high for the few single-block sizes; read time unchanged. | 17.1 GRD-03 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.1 GRD-03 registered | Claude (Mac, branch claude/competent-torvalds-ebebf5) |

## Verified
- Documentation only; no build or test needed. CI: not run (billing).

## Pending
- rMQR unused error correction with the ISO/IEC 23941 table (see Section 5)
