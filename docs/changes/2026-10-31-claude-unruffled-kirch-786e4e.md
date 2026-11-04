# Make `LineGradingTests.ToolGradesOnlyTheSampledParts` and `RuntimeLinkTests.A_runtime_that_stops_answering_while_streams_hang_is_dropped_by_the_probe` pass on a loaded machine

- Status: done
- Session: load-flaky-tests, 2026-10-31
- Pull request: #165
- Section 5 item: Make `LineGradingTests.ToolGradesOnlyTheSampledParts` (Pending in docs/changes/2026-10-30-claude-full-system-test.md)
- Components/files: VisionCore.Tests (LineGradingTests), Studio.Tests (RuntimeLinkTests); Studio (Runtime/RuntimeLink.cs probe), Studio.Tests (RuntimeLinkTests, Doubles)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #165: read budget made generous where sampling is the point; the Studio probe now reports a late runtime error as itself instead of as a timeout; each test 20 / 20 under load) Make `LineGradingTests.ToolGradesOnlyTheSampledParts` and `RuntimeLinkTests.A_runtime_that_stops_answering_while_streams_hang_is_dropped_by_the_probe` pass on a loaded machine.

## Timeline (section 3)
| 2026-10-31 | Two tests that failed only on a loaded Mac made independent of its speed: `ToolGradesOnlyTheSampledParts` gets a 10 s read budget (the 100 ms default plus engine start-up ran out at load 25); the Studio runtime probe treated any error that came back after its deadline as "did not answer" (TimeoutException), so a crashed runtime's IOException was misreported. Only a cancelled call is now a timeout; new test `An_error_that_comes_back_after_the_probe_time_is_reported_as_itself` reproduces it without load. Each test 20 / 20 with `yes` on 8 cores (load 32–116); the new test fails on the old probe code (TimeoutException). | 12 |

## Decisions (section 4)
| Studio probe timeout | A status probe is a timeout only when the call was cancelled at its deadline; an error that arrives late keeps its own type. Both still drop the link, so line safety is unchanged; only the reason shown is right. | 12 |

## Update log (section 8)
