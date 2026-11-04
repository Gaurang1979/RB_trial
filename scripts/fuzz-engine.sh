#!/usr/bin/env bash
# fuzz-engine.sh - the engine's C API against bad input, under AddressSanitizer and
# UndefinedBehaviorSanitizer (engine/tests/fuzz/). A crash on the line stops the line.
#
#   scripts/fuzz-engine.sh                      build engine/build-asan, run the engine tests and the
#                                               three fuzzers 10 minutes each (side by side)
#   scripts/fuzz-engine.sh --seconds 120        shorter run
#   scripts/fuzz-engine.sh --only ocr           one fuzzer (codereader, ocr, locate)
#   scripts/fuzz-engine.sh --no-tests           skip the engine test executables
#   scripts/fuzz-engine.sh --no-leaks           skip the leak check (macOS: leaks on a plain build)
#   scripts/fuzz-engine.sh --seed 42            fixed seed base (default: the time)
#
# Replay one case from a crash report ("== fuzz case seed N"):
#   engine/build-asan/fuzz_<name> --replay N
#
# Fuzzing is not timed, so it does not take the heavy-test lock (AGENTS.md rule 13); it is heavy CPU
# work, so it waits to start while another run holds the lock, and then runs. Logs:
# engine/build-asan/fuzz-<name>.log. Exit 0 = nothing found; 1 = a fuzzer, test or leak check failed.
set -uo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
SECONDS_EACH=600
ONLY=""
TESTS=1
LEAKS=1
SEED=$(date +%s)
while [ $# -gt 0 ]; do
  case "$1" in
    --seconds) SECONDS_EACH=$2; shift 2 ;;
    --only) ONLY=$2; shift 2 ;;
    --no-tests) TESTS=0; shift ;;
    --no-leaks) LEAKS=0; shift ;;
    --seed) SEED=$2; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
FUZZERS="codereader ocr locate"
[ -n "$ONLY" ] && FUZZERS=$ONLY

# no heavy CPU work while a timed run holds the lock (a lock older than 45 minutes is stale)
LOCK=/tmp/vs-heavy-test.lock
while [ -d "$LOCK" ] && [ -z "$(find "$LOCK" -maxdepth 0 -mmin +45 2>/dev/null)" ]; do
  echo "waiting for a timed run to finish: $(cat "$LOCK/owner" 2>/dev/null)"
  sleep 60
done

JOBS=$(sysctl -n hw.ncpu 2>/dev/null || nproc)
BUILD="$ROOT/engine/build-asan"
cmake -S "$ROOT/engine" -B "$BUILD" -DCMAKE_BUILD_TYPE=RelWithDebInfo -DVS_SANITIZE=ON -DVS_FUZZ=ON >/dev/null || exit 1
TARGETS=""
for f in $FUZZERS; do TARGETS="$TARGETS fuzz_$f"; done
[ "$TESTS" = 1 ] && TARGETS="$TARGETS engine_tests dpm_tests ocr_tests locate_tests"
# shellcheck disable=SC2086
cmake --build "$BUILD" -j "$JOBS" --target $TARGETS 2>&1 | grep -E "error|warning: .*(sanitize|undefined)" ; [ "${PIPESTATUS[0]}" = 0 ] || exit 1

# halt on the first error; OpenCV is not instrumented, so container-overflow checks would see its
# vectors half-annotated (false reports); leaks are LeakSanitizer's on Linux, `leaks` on macOS
export ASAN_OPTIONS="halt_on_error=1:detect_container_overflow=0:detect_stack_use_after_return=1:${ASAN_OPTIONS:-}"
export UBSAN_OPTIONS="print_stacktrace=1:halt_on_error=1:${UBSAN_OPTIONS:-}"
[ "$(uname)" = Linux ] && export ASAN_OPTIONS="detect_leaks=1:$ASAN_OPTIONS"

FAIL=0
if [ "$TESTS" = 1 ]; then
  for t in engine_tests dpm_tests ocr_tests locate_tests; do
    if (cd "$BUILD" && "./$t" > "$BUILD/$t-asan.log" 2>&1); then
      echo "PASS $t (ASan/UBSan): $(tail -1 "$BUILD/$t-asan.log")"
    else
      echo "FAIL $t (ASan/UBSan): see $BUILD/$t-asan.log"; tail -30 "$BUILD/$t-asan.log"; FAIL=1
    fi
  done
fi

# the fuzzers side by side, each for SECONDS_EACH
PIDS=""
for f in $FUZZERS; do
  "$BUILD/fuzz_$f" --seconds "$SECONDS_EACH" --seed "$SEED" > "$BUILD/fuzz-$f.log" 2>&1 &
  PIDS="$PIDS $!:$f"
done
for pf in $PIDS; do
  pid=${pf%%:*}; f=${pf#*:}
  if wait "$pid"; then
    echo "PASS fuzz_$f: $(tail -1 "$BUILD/fuzz-$f.log")"
  else
    echo "FAIL fuzz_$f: see $BUILD/fuzz-$f.log"; grep -E "ERROR|runtime error|HANG|fuzz case seed" "$BUILD/fuzz-$f.log" | head -5; FAIL=1
  fi
done

# leaks (macOS): a plain build of the fuzzers, a few hundred cases each under `leaks --atExit`
if [ "$LEAKS" = 1 ] && [ "$(uname)" = Darwin ]; then
  PLAIN="$ROOT/engine/build-fuzz"
  cmake -S "$ROOT/engine" -B "$PLAIN" -DCMAKE_BUILD_TYPE=RelWithDebInfo -DVS_FUZZ=ON >/dev/null || exit 1
  # shellcheck disable=SC2086
  cmake --build "$PLAIN" -j "$JOBS" --target $(for f in $FUZZERS; do echo "fuzz_$f"; done) 2>&1 | grep -E "error" ; [ "${PIPESTATUS[0]}" = 0 ] || exit 1
  for f in $FUZZERS; do
    if MallocStackLogging=1 leaks --atExit -- "$PLAIN/fuzz_$f" --cases 300 --seed "$SEED" > "$PLAIN/leaks-$f.log" 2>&1; then
      echo "PASS leaks fuzz_$f: $(grep -E "leaks for" "$PLAIN/leaks-$f.log" | head -1)"
    else
      echo "FAIL leaks fuzz_$f: see $PLAIN/leaks-$f.log"; grep -E "leaks for|Leak:" "$PLAIN/leaks-$f.log" | head -5; FAIL=1
    fi
  done
fi
exit $FAIL
