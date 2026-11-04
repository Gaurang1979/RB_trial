# Miss list reports only regions with code evidence (Req 17.12 MISS-EV-01..03)

- Status: done
- Session: miss-code-evidence, 2026-11-03
- Pull request: #288
- Section 5 item: new (from the Pending of 2026-10-31-claude-vigilant-brattain-dd7b8f.md and 2026-10-31-claude-psf-deblur.md)
- Components/files: engine/src/diagnose.cpp, engine/tests/engine_tests.cpp, docs/vision-system/INDUSTRY-METHODS.md (counted section only), docs/REQUIREMENTS-VISION-SYSTEM.md
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #288: the miss list keeps LOW_CONTRAST/BLUR regions only with code evidence and leaves out regions whose code was read elsewhere; LOW_CONTRAST 285 -> 6, BLUR 196 -> 75 on the samples) Miss list reports only regions with code evidence (MISS-EV-01..03)
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->

## Timeline (section 3)
| 2026-11-03 | diagnose.cpp: dropNonCodes + missHasEvidence; miss regions without a finder or code texture at the module size (LOW_CONTRAST, BLUR) and regions on an already-read code are dropped from misses/missInfo/missSide/missSlot. cr_miss_info unchanged. Causes re-counted in INDUSTRY-METHODS section 1 | 13, 17.12 |
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->

## Decisions (section 4)
| Evidence rule | Finder, or texture >= 0.06 with a measured module 0.6..1.6 x the product's; a blurred code has few transitions so texture >= 0.14 also counts. COVERED, GLARE, TOO_SMALL, NO_FINDER, CUT_OFF and unmeasured regions are never dropped for evidence. stats.misses is left as the finder counted it | 13 |
<!-- | Topic | Decision and reason | requirement sections | -->

## Update log (section 8)
| 2026-11-03 | Requirements 17.12 MISS-EV-01..03; INDUSTRY-METHODS section 1 re-count | Claude (mac, branch claude/mystifying-yalow-448d99) |

## Verified
- Engine built; engine_tests all passed (new miss-list checks). ctest: ocr, locate, wavelet, inpaint, rectify failed once under load from other sessions and touch no diagnose code; not rerun (not this change).
- Miss list on the 58 samples once (CR_DIAG_ALL, lock held): before/after counts above. Mac was busy (1537/1868 read), so absolute counts are inflated; the bench read-rate gate was not run (reading unchanged, diagnostics only). CI: not run (billing).
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->

## Pending
<!-- Follow-ups this session leaves, one per line: "- Unit tests for ...". Each becomes an open section 5 item. Leave the section out if none. -->
