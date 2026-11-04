// integrity.hpp - runtime anti-tamper for the shipped engine.
//
// Owner, 2026-11-01: the shipped software must resist reverse engineering and copying, and a
// protection failure must never give a silent wrong read (line safety, requirements section 12).
// This layer detects tampering AFTER the library is loaded and running:
//   - a critical code region changed in memory (an inline hook or a live patch), and
//   - a debugger / tracer attached to our process.
// On any positive detection it latches a process-global state that never clears, so every later
// decode returns NG and the line software can raise a clear tamper alarm.
//
// What this does NOT do (honest limits; see the change note): it does not detect a patched file at
// rest, and it is not unbreakable. The baseline checksum is computed in this process at load, so it
// catches changes made after load, not a binary that was already patched before it ran - that is the
// signed load-time check (protect-harden-build). A skilled attacker with full control of the process
// can defeat any in-process check; the point is to raise the cost and to fail closed when it trips.
#pragma once

#include <cstdint>

namespace cr {

enum class TamperReason : int {
    None        = 0,
    Debugger    = 1, // a debugger / tracer is attached to this process
    CodePatched = 2, // a critical code region changed in memory after load (hook / live patch)
};

// The latched state. Cheap: reads the flag, runs no checks. Once a tamper is seen it never clears.
TamperReason tamperState();
inline bool tamperDetected() { return tamperState() != TamperReason::None; }

// Record the baseline checksum of the critical code region. Called once from cr_create; later calls
// are a no-op, so the first live reader sets the baseline. Never throws.
void integrityBaseline();

// Run the runtime checks now and latch on a positive detection. Cheap, never throws, never blocks;
// returns the latched state after the run. An inconclusive check (e.g. a platform read failed) does
// NOT latch - we latch only on positive evidence, so a quirk never bricks a good line.
TamperReason runIntegrityChecks();

// True when this decode should run the full check (first decode, then every Nth); the C API uses it
// to keep per-frame latency flat. Advances an internal counter, so call it once per decode.
bool integrityDueThisDecode();

namespace detail {

// The automatic per-decode checks. Production leaves them on; the engine unit tests turn them off so
// a developer running the suite under lldb/gdb does not trip the latch for unrelated decode tests.
void setAutoChecks(bool on);
bool autoChecks();

// How many decodes between full checks (debugger + checksum). The latched-state read is every call.
int autoCheckEveryDecodes();

// Latch a tamper directly, for tests. One-way, exactly like a real detection, so it is safe even if
// it were ever reached in production: it can only make the engine fail closed, never open it.
void simulateTamper(TamperReason r);

#ifdef CR_TEST_HOOKS
// Clear the latch and baseline - ONLY in the test build (CR_TEST_HOOKS). Production has no way to
// clear a tamper latch.
void resetForTest();
#endif

} // namespace detail
} // namespace cr
