// integrity.cpp - runtime anti-tamper. See integrity.hpp for the design and its honest limits.

#include "integrity.hpp"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

#if defined(_WIN32)
#  include <windows.h>
#elif defined(__APPLE__)
#  include <sys/sysctl.h>
#  include <sys/types.h>
#  include <unistd.h>
#elif defined(__linux__)
#  include <cstdio>
#endif

namespace cr {
namespace {

// Latched reason (one-way: None -> a reason, never back, except the test-only reset). Atomic so the
// alarm poll thread and the decode threads see the same value without a lock.
std::atomic<int> g_state{static_cast<int>(TamperReason::None)};
std::atomic<bool> g_autoChecks{true};

// Baseline checksum of the critical code region, captured at load.
std::atomic<uint64_t> g_baseline{0};
std::atomic<bool> g_haveBaseline{false};

// Decode counter for the per-decode cadence of the full check.
std::atomic<unsigned> g_decodeTick{0};
constexpr int kCheckEveryDecodes = 32;

// The number of code bytes we checksum from the anchor below. Small, so it stays inside live code and
// the check is a few nanoseconds.
constexpr std::size_t kCodeWindow = 96;

void latch(TamperReason r)
{
    int expected = static_cast<int>(TamperReason::None);
    // Set once. If two threads detect at once, the first wins; both leave the engine failed closed.
    g_state.compare_exchange_strong(expected, static_cast<int>(r));
}

// FNV-1a over a byte span. Non-cryptographic: it catches an accidental or a casual patch of the code,
// not a crafted collision. A skilled attacker who can write our memory can also forge this - stated
// in the note; the goal is cost and fail-closed, not proof.
uint64_t fnv1a(const unsigned char* p, std::size_t n)
{
    uint64_t h = 1469598103934665603ull;
    for (std::size_t i = 0; i < n; ++i) {
        h ^= p[i];
        h *= 1099511628211ull;
    }
    return h;
}

// The anchor whose bytes we watch. A tiny leaf function with no calls or global references, so the
// loader applies no run-time relocations to it and its bytes are stable within a process. volatile
// keeps the compiler from folding it away or merging it with another function. Patching the check
// path (for example NOP-ing a detection) tends to touch code near here; watching our own code is a
// deliberate, modest tripwire, not full coverage.
#if defined(_MSC_VER)
#  pragma optimize("", off)
#endif
volatile int g_anchorSink = 0;
__attribute__((noinline))
#if defined(_MSC_VER)
__declspec(noinline)
#endif
int integrityAnchor(int x)
{
    int a = x ^ 0x5a5a5a5a;
    a += (a << 3);
    a ^= (a >> 5);
    g_anchorSink = a;
    return a;
}
#if defined(_MSC_VER)
#  pragma optimize("", on)
#endif

uint64_t codeChecksum()
{
    // Read from the anchor's entry. On normal platforms .text is readable; we never write it.
    const auto* code = reinterpret_cast<const unsigned char*>(reinterpret_cast<const void*>(&integrityAnchor));
    return fnv1a(code, kCodeWindow);
}

// Positive debugger detection for the platforms we ship. Returns true only on clear evidence; an
// inconclusive read returns false (we do not brick a good line on a quirk).
bool debuggerAttached()
{
#if defined(_WIN32)
    if (IsDebuggerPresent()) return true;
    BOOL present = FALSE;
    if (CheckRemoteDebuggerPresent(GetCurrentProcess(), &present) && present) return true;
    return false;
#elif defined(__APPLE__)
    // P_TRACED in the process info means a debugger is attached (Apple TN2127).
    int mib[4] = {CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()};
    struct kinfo_proc info;
    std::memset(&info, 0, sizeof(info));
    size_t size = sizeof(info);
    if (sysctl(mib, 4, &info, &size, nullptr, 0) != 0) return false; // inconclusive
    return (info.kp_proc.p_flag & P_TRACED) != 0;
#elif defined(__linux__)
    std::FILE* f = std::fopen("/proc/self/status", "r");
    if (!f) return false; // inconclusive
    char line[256];
    bool traced = false;
    while (std::fgets(line, sizeof(line), f)) {
        if (std::strncmp(line, "TracerPid:", 10) == 0) {
            long pid = 0;
            if (std::sscanf(line + 10, "%ld", &pid) == 1 && pid != 0) traced = true;
            break;
        }
    }
    std::fclose(f);
    return traced;
#else
    return false;
#endif
}

} // namespace

TamperReason tamperState()
{
    return static_cast<TamperReason>(g_state.load(std::memory_order_relaxed));
}

void integrityBaseline()
{
    bool expected = false;
    if (g_haveBaseline.compare_exchange_strong(expected, true))
        g_baseline.store(codeChecksum(), std::memory_order_relaxed);
}

TamperReason runIntegrityChecks()
{
    // Already failed closed: nothing more to do.
    if (tamperDetected()) return tamperState();

    if (debuggerAttached()) {
        latch(TamperReason::Debugger);
        return tamperState();
    }

    // Compare the critical code region with its load-time baseline. If no baseline was taken yet
    // (no reader created), take it now rather than flag a mismatch against zero.
    if (!g_haveBaseline.load(std::memory_order_relaxed)) {
        integrityBaseline();
    } else if (codeChecksum() != g_baseline.load(std::memory_order_relaxed)) {
        latch(TamperReason::CodePatched);
    }
    return tamperState();
}

namespace detail {

void setAutoChecks(bool on) { g_autoChecks.store(on, std::memory_order_relaxed); }
bool autoChecks() { return g_autoChecks.load(std::memory_order_relaxed); }
int autoCheckEveryDecodes() { return kCheckEveryDecodes; }

void simulateTamper(TamperReason r)
{
    if (r != TamperReason::None) latch(r);
}

#ifdef CR_TEST_HOOKS
void resetForTest()
{
    g_state.store(static_cast<int>(TamperReason::None), std::memory_order_relaxed);
    g_haveBaseline.store(false, std::memory_order_relaxed);
    g_baseline.store(0, std::memory_order_relaxed);
    g_decodeTick.store(0, std::memory_order_relaxed);
}
#endif

} // namespace detail

// The C API calls this once per decode. The full check (debugger + checksum) is run on the first
// decode and then every kCheckEveryDecodes-th, so per-frame latency stays flat while a debugger that
// attaches mid-run is still caught within a few dozen frames. The counter stays private to this file.
bool integrityDueThisDecode()
{
    const unsigned n = ++g_decodeTick;
    return (n % static_cast<unsigned>(kCheckEveryDecodes)) == 1u; // fire on 1, 33, 65, ...
}

} // namespace cr
