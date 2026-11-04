# Make `CardSignInTests.A_broken_or_cut_short_hash_matches_nothing` and `LineTests.Replay_run_streams_NG_results_with_full_resolution_images_and_counts_them` (Runtime.Host.Tests) pass every time

- Status: done
- Session: flaky-host-tests, 2026-10-31
- Pull request: #198
- Section 5 item: new (Pending in docs/changes/2026-10-31-claude-job-load-hardening.md and 2026-10-30-claude-line-225-soak.md)
- Components/files: dotnet/Runtime.Host (LineRuntime.Start), dotnet/Runtime.Host.Tests (CardSignInTests, LineTests incl. IntakeCameraTests)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-01, PR #198: card test edits the JSON, batch counters reset before the station starts, intake tests wait for a frame on a loaded Mac; each test 20 / 20, the project 10 / 10 at load 10-70)` Make the flaky Runtime.Host.Tests (card sign-in broken hash, replay run counts, intake camera grabs) pass every time.

## Timeline (section 3)
| 2026-11-01 | Runtime.Host.Tests made steady. Card sign-in: the test broke and restored the hash by text replace, but the users file escapes '+' as +, so with a '+' in the hash nothing was broken and the restore of the cut-short value ("100000$salt$") appended to the good hash: the store's reload was right, the test corrupted the file; it also checked a card nobody held. It now edits the JSON, gives every write a later file time and checks the real card. Replay run: the runtime reset the batch counters after the station had started, so parts already inspected and streamed could be dropped from the counts; counters now reset before the start (and come back if the start is refused). Intake camera tests waited 1 s for a simulated frame, which a loaded Mac missed in 6 of 10 full runs; they now wait up to 10 s. Each fixed test 20 / 20, the project 10 / 10 (55 tests) and 57 / 57 after merging main, at load 10-70. | 12 |

## Decisions (section 4)
| Runtime batch counters | Start resets the counters before the station starts, not after: a part inspected in the first milliseconds of a batch is counted in that batch. A refused start puts the last batch's counters back. | 12 |

## Update log (section 8)
