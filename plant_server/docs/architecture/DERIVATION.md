# State derivation and KPI definitions (implemented in `backend/derive.py`, `backend/kpi.py`)

All results are `basis: detected` (states) or `calculated` (KPIs). Nothing here is AI inference.
Derivation uses event time `ts` only, so late, buffered, duplicated or re-ordered delivery cannot change results.

## Per-second state precedence
`COMMUNICATION_ERROR` (comm_status ok=false) > `OFF` (machine_on=0) > `FAULT` (fault=1) > running states > stopped states.
Before the first event of a machine the state is `UNKNOWN` (never guessed).

**Data horizon:** derivation never looks past the last event its gateway has reported. The previous state is carried forward for `stale_after_s` (90 s), after that the state is `NO_DATA` (gateway silent: network or gateway problem, machine state unknown). Without this rule a network outage looked like a bypass. When buffered events arrive, history is re-derived from their event time.

**Live mode:** after each ingested batch the trailing window (900 s + padding) is re-derived with the same rules and committed over stored intervals; a state that began earlier keeps its true start. Expect the newest cycle to be relabelled as more data arrives.

| running signal | Derived state | Rule |
|---|---|---|
| 1 | PRODUCING | Cycles are being completed |
| 1 | BYPASSED | Running=1 but the gap between completed cycles exceeds `bypass_factor` (default 2) x standard cycle time. Reported as "signal inconsistency", never as operator misconduct |
| 0 | STARVED | Upstream queue level is 0 at either queue sample bracketing the second |
| 0 | BLOCKED | Downstream queue level equals capacity at either bracketing sample |
| 0 | IDLE | Neither explains it, or evidence conflicts (low confidence) |

Bypass boundaries are only known to within one cycle, so each is placed at the midpoint (unbiased). Confidence is stored per interval with an evidence string.

## KPI definitions (window = requested time range)
- **Planned time** = window minus unobserved time (COMMUNICATION_ERROR + UNKNOWN). 24x7 for now; shifts and planned stops arrive with shift configuration.
- **Availability** = PRODUCING seconds / planned time. Everything else (fault, bypass, starved, blocked, idle) is loss, exposed in `state_s`.
- **availability_own** = same, but starved/blocked time removed from the denominator (the machine's own availability, ignoring flow waits).
- **Performance** = standard cycle time x observed cycles / PRODUCING seconds, capped at 1.0 (`performance_raw` keeps the uncapped value).
- **Quality** = good / total observed cycles. **OEE** = Availability x Performance x Quality.
- **Counts** come from cumulative counters, so production during a comm gap is credited (`count_unobserved`). Performance and quality use observed cycles only, so they are not inflated by unobserved production.
- **Bottleneck** = machine spending the largest share of its able-to-work time producing (never waiting for material/space). `clear=true` only with a >= 5 point margin over the next machine; otherwise it is a candidate, not a conclusion.
- **Downtime events** = intervals of FAULT/BYPASSED/STARVED/BLOCKED/IDLE/OFF of at least 30 s (configurable). Categories: Upstream issue, Downstream issue, "Production signal unavailable", "Unknown (fault signal)". Causes are not guessed.

## Measured accuracy (simulator vs truth log; NOT plant accuracy)
| Test | Agreement per second |
|---|---|
| Demo factory, 3 seeds, 2 h | 99.96% - 100% |
| Sparse queue data (every 60 s) | >= 99.9% |
| Very noisy cycle times (cv 25%) | >= 99.9% |
| Many random faults + sparse queues + noisy cycles | >= 99.9% |

Test thresholds (proposed, please confirm): overall >= 99%, per-state recall >= 95% (BYPASSED >= 90%).
These are high because the simulator has clean signals. Real plants add noisy counters, missing queue sensors, and legitimately slow cycles;
expect lower numbers and re-validate on plant data (spec s51) before making any accuracy claim.

## Known limits
- Bypass shorter than about one cycle time is not detectable (tested). Legitimately slow cycles (> 2x standard) can be flagged as bypass: tune `bypass_factor` per equipment.
- STARVED/BLOCKED need queue level data (sensor or counter difference). Without it the state falls back to IDLE.
- Live derivation re-derives a trailing window (simple and provably equal to batch in tests) rather than updating incrementally; fine for hundreds of machines, revisit at scale.
- Sensor-health states (SENSOR_ERROR), OFF/IDLE causes, shifts and planned stops are not derived yet.
