// check.h - always-on check for the engine tests. assert() is compiled out by -DNDEBUG in Release,
// so a Release test run checked nothing (Req 17.10 AUD-09). VS_CHECK stays in every build.
#pragma once
#include <cstdio>
#include <cstdlib>

#define VS_CHECK(cond) \
    do { if (!(cond)) { std::fprintf(stderr, "CHECK FAILED %s:%d: %s\n", __FILE__, __LINE__, #cond); std::abort(); } } while (0)

// VS_TIME_BUDGET(cond): a wall-clock budget, judged only in an optimized build (NDEBUG: Release,
// RelWithDebInfo, MinSizeRel). The budgets are the line-speed limits of the shipped build; an
// unoptimized Debug build is several times slower, so there the time is printed by the test and
// not judged (Req 17.11 AUD-13). Wrap only the time comparison, inside the test's own check:
//   check(VS_TIME_BUDGET(ms < 15.0), "under budget");
// Correctness checks are never wrapped and run in every build.
#ifdef NDEBUG
#define VS_TIME_BUDGET_JUDGED 1
#else
#define VS_TIME_BUDGET_JUDGED 0
#endif

inline bool vs_time_budget(bool within, const char* what) {
    if (VS_TIME_BUDGET_JUDGED) return within;
    std::printf("  (time budget not judged in a Debug build: %s)\n", what);
    return true;
}
#define VS_TIME_BUDGET(cond) vs_time_budget((cond), #cond)

// vs_test_ms(ms): a deadline or engine time budget that a correctness check needs to be long enough
// (the code must get far enough to read), not a time being judged. Unoptimized Debug code is several
// times slower, so there it gets 10 times as long; an optimized build keeps ms as written.
inline int vs_test_ms(int ms) {
    if (VS_TIME_BUDGET_JUDGED || ms <= 0) return ms;
    return ms > 200000000 ? ms : ms * 10;
}
