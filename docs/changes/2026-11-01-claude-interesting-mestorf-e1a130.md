# Anti-tamper runtime: detect in-memory patching and instrumentation, fail closed to NG + alarm

- Status: done
- Session: interesting-mestorf-e1a130, 2026-11-01
- Pull request: #263
- Section 5 item: new
- Components/files: engine (`src/integrity.{hpp,cpp}`, `src/c_api.cpp`, `include/codereader.h`, `tests/engine_tests.cpp`, `CMakeLists.txt`); .NET CodeReader (`CodeReader.cs`, `Native.cs`, `CodeReader.Tests/AntiTamperTests.cs`)
- Depends on: none. Boundary agreed with protect-harden-build (#221): that item owns the LOAD-TIME signature/strip/obfuscate (incl. verifying the binary at rest); this item owns the RUNTIME (post-load, in-memory) checks. No file overlap (`scripts/coord.sh overlap` clean); either merge order.

## What and why

Owner, 2026-11-01: the shipped software must resist reverse engineering and copying, and a protection
failure must never give a silent wrong read (line safety, requirements section 12). This is item 4 of
the 2026-11-01 software-protection set (with model encryption #222 and build hardening #221); the
realistic bar is the one in secure_me §5Z — raise the attacker's cost, honest framing, not
"unbreakable".

A new engine module `cr::integrity` watches for tampering **after the library is loaded and running**:
- **In-memory code integrity:** an FNV checksum of a small critical code region (an anchor leaf
  function), captured as a baseline at `cr_create` and re-checked on a per-decode cadence. A mismatch
  means the code was patched in memory after load (an inline hook / live patch).
- **Debugger / instrumentation detection**, per shipped platform: Windows
  `IsDebuggerPresent` + `CheckRemoteDebuggerPresent`, macOS `P_TRACED` via `sysctl`, Linux
  `/proc/self/status` `TracerPid`.

On any **positive** detection it latches a process-global state that never clears (fail closed, one
way). While latched, `cr_decode` / `cr_decode_frames` return `CR_DECODE_TAMPER` (-3) with **no codes**
— NG, never a silent wrong read, never a crash, never a hang. New C API `cr_integrity_check()` /
`cr_integrity_state()` let the line software poll the state and raise a clear **tamper alarm** even
when not decoding. On the .NET seam, -3 maps to a distinct `TamperDetectedException` (still an
`InvalidOperationException`, so the host's existing fault→NG path keeps working) and
`CodeReader.IntegrityState` / `RunIntegrityCheck` expose the state.

The automatic checks are disabled in the engine unit-test binary so a developer running the suite
under lldb/gdb does not trip the latch; the dedicated test drives the latch directly.

**Honest limits (as required).** Runtime anti-tamper raises the attacker's cost; it is not
unbreakable. The baseline checksum is computed in-process at load, so it detects tampering made
**after** load (hooks, live patches, attached debuggers), **not** a binary patched **at rest** — that
is the signed load-time check (protect-harden-build, #221). The checksum is FNV, not cryptographic: it
catches an accidental or casual patch, not a crafted collision. An attacker with full control of the
process can read and forge any in-process check. The value is cost + fail-closed behaviour, not proof.

## Section 5
- [ ] (done 2026-11-01, PR #263: runtime anti-tamper — in-memory code-integrity checksum and
  debugger/instrumentation detection in the engine; on detection a latched fail-closed state so every
  decode returns NG with a tamper alarm, never a silent wrong read, crash or hang; C API
  `cr_integrity_check`/`cr_integrity_state` and .NET `TamperDetectedException`/`IntegrityState`.
  Complements load-time hardening #221 and model encryption #222.) Runtime anti-tamper for the shipped
  engine (requirements section 12; owner 2026-11-01).

## Timeline (section 3)
| 2026-11-01 | Added a runtime anti-tamper layer to the engine: in-memory code-integrity checksum + debugger detection, latched fail-closed to NG + tamper alarm; C API and .NET contract; engine_tests + CodeReader.Tests. | 12 |

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| Runtime vs load-time integrity | This item does the post-load, in-memory checks (code patched in memory, debugger attached); the signed check of the binary at rest is the load-time item (#221). Complementary, no overlap. | 12 |
| Fail-closed, one-way latch | A positive detection latches a process-global state that never clears, so every later decode is NG. A bad part must never pass once the code may be patched. We latch only on **positive** evidence (an inconclusive platform read does not latch), so a quirk does not brick a good line. | 12 |
| Tamper is a distinct, catchable NG | cr_decode returns -3 (CR_DECODE_TAMPER) and .NET throws `TamperDetectedException : InvalidOperationException`, so the existing fault→NG path still works and a host can also raise a specific tamper alarm. | 12 |
| Honest bar | FNV (not cryptographic) checksum of our own code; raises cost, not unbreakable; does not detect patch-at-rest. Framed per secure_me §5Z. | 12 |

## Update log (section 8)
| 2026-11-01 | Documented the runtime anti-tamper C API (cr_integrity_check/state, CR_DECODE_TAMPER) in codereader.h and the behaviour + limits in this note. | Claude (Mac, branch claude/interesting-mestorf-e1a130) |
| 2026-11-03 | Merged origin/main (resolved a doc-comment conflict in CodeReader.cs with #222). Rebuilt; engine tests 411 ok / 0 FAIL (incl. anti-tamper). Ran the bench — see Verified. | Claude (Mac, branch claude/interesting-mestorf-e1a130) |

## Verified
- Engine build (CMake, Release) after merging origin/main: OK. `engine_tests`: 411 ok / 0 FAIL,
  including the anti-tamper cases (tamper -> -3 NG + alarm + 0 codes for code-patch and debugger;
  reset restores normal decode).
- `.NET`/`CodeReader.Tests`: 210 passed / 0 failed earlier for the same managed contract (unchanged
  by the merge; the merge only reconciled a doc comment).
- Bench (`python3 tools/bench/batch_check.py`, samples = main checkout `samples/Barcode Images`,
  81 images / 1868 codes), run under the heavy-test lock. **0 wrong reads in every batch across all
  runs** (the line-safety property holds — no silent wrong read). Three runs settle it:
  1. This branch, run while the Mac was **busy** (1-min load ~4.7, 5-min ~8 — three merge-fix
     sessions building): exit 1, three batches a few below baseline (24 QR 374<376, 48 DM 72<75,
     other QR 132<133).
  2. Clean `origin/main` (detached scratch worktree), `--force`, run **quiet** (load ~3.5): exit 0,
     all batches at/above baseline — 24 QR 376, 48 DM 77, other QR 133. Main is **not** below the
     baseline.
  3. This branch again, `--force`, run **quiet** (load ~3.3): **exit 0, all batches at/above
     baseline** — 24 QR 376, 48 DM 78, other QR 134; total 1754/1868 (93.9 %), 2 more than clean
     main's 1752.
  **Conclusion: the change is read-neutral and passes the bench cleanly.** Run #1's shortfall was a
  time-budget artifact of the busy Mac (reads run against a per-image time budget, so a loaded
  machine drops borderline faint reads), not this change and not a stale baseline — both the quiet
  branch run and the quiet main run meet the baseline. (The anti-tamper gate also runs
  `runIntegrityChecks()` only every 32nd decode — a handful of ~96-byte FNV checksums plus one
  `sysctl` across the whole run — and adds no state the decoder reads.)
- CI: not run (billing) — the red "Windows build" check is the known GitHub Actions billing halt
  (the job never starts), not a result of this change.

## Pending
- **Baseline is loose (follow-up for someone else, not this PR).** The quiet confirmation runs show
  current main reads *more* than the 2026-11-01 baseline on two batches: 48 DM 77 vs 75, other QR 134
  vs 133 — the engine has drifted better since the baseline was set. The baseline could be refreshed
  in its own PR (`--update`, with the numbers and which engine PRs moved them) to tighten the guard.
  Not done here deliberately: this change is not a read/speed change, and refreshing off this PR's run
  would have *lowered* the baseline to the busy-Mac artifact (374/72/132) and permanently hidden ~5
  faint reads the engine actually gets. Only the owner merges.
- Wire the tamper state into the line PC runtime (Runtime.Host) so a detection raises the PLC/plant
  tamper alarm and light tower, and emits a plant event (plant event schema + mobile mirror — Opus /
  main, align with protect-requirements once its spec lands). Today the engine returns NG and the host
  can poll `cr_integrity_state`; the explicit PLC/plant alarm wiring is not yet done.
- The OCR engine (`vs_ocr`, separate shared library) has no runtime integrity check yet; extend the
  same latch to it (shared tamper state across both libraries).
- Consider a cheaper/portable debugger signal for the Windows line PC (timing checks, hardware
  breakpoint register scan) and a periodic background thread so a mid-run attach is caught faster than
  the per-decode cadence.
