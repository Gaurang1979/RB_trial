# Metamorphic Robustness & Fuzzing Test Suite (`vs_metamorphic`, Roadmap Priority 2 / Tool H)

- Status: done
- Session: metamorphic-robustness-harness, 2026-11-02
- Pull request: #237
- Section 5 item: new
- Components/files: engine/include/vs_metamorphic.h, engine/src/metamorphic.cpp, engine/tests/metamorphic_tests.cpp, engine/CMakeLists.txt, .github/workflows/windows-build.yml, dotnet/VisionCore/MetamorphicNative.cs, dotnet/VisionCore/MetamorphicEngine.cs, dotnet/VisionCore.Tests/MetamorphicTests.cs
- Depends on: none

## Section 5
- New: [x] Metamorphic Robustness & Fuzzing Suite (`vs_metamorphic`): Mathematical metamorphic relation validation (continuous rotational stability, photometric monotonicity, affine scale invariance, additive sensor noise resilience, quiet-zone monotonicity) for zero-regression industrial deployment.

## Timeline (section 3)
- 2026-11-02: Claimed and designed Metamorphic Robustness & Fuzzing Engine (`vs_metamorphic`).
- 2026-11-02: Implemented native C++ perturbation generator and fuzzing test harness (`metamorphic.cpp`) with Box-Muller sensor noise generator, sub-pixel bilinear sampling, and metamorphic relation verifiers.
- 2026-11-02: Added native verification test suite (`metamorphic_tests.cpp`) covering rotational equivariance, photometric monotonicity, scale invariance, sensor noise resilience, quiet-zone expansion, and performance benchmarking (12.9 ms across 19 variants).
- 2026-11-02: Implemented C# P/Invoke bindings (`MetamorphicNative.cs`) and safe managed API (`MetamorphicEngine.cs`).
- 2026-11-02: Added comprehensive .NET xUnit tests (`MetamorphicTests.cs`) - 3/3 passed.
- 2026-11-02: Integrated CI test and package artifacts in `.github/workflows/windows-build.yml`.

## Decisions (section 4)
- **5-Pillar Industrial Metamorphic Relations**:
  1. *MR-1 (Rotational Equivariance & Invariance)*: Sweeps $\theta \in [-3^\circ, +3^\circ]$. Asserts 0 verdict flips and position coordinates rotate predictably with jitter $\le 0.75\text{ px}$.
  2. *MR-2 (Photometric Monotonicity)*: Sweeps contrast gain $\gamma \in [0.90, 1.10]$ and brightness offset. Asserts confidence stability.
  3. *MR-3 (Affine Scale Invariance)*: Sweeps $s \in [0.95, 1.05]$. Asserts detected coordinates scale linearly.
  4. *MR-4 (Additive Thermal Noise Tolerance)*: Injects Gaussian noise $\sigma \le 5.0\text{ grey levels}$. Asserts zero decision flips.
  5. *MR-5 (Quiet-Zone Monotonicity)*: Expands uniform boundary padding. Confirms that quiet-zone expansion never decreases tool recall.
- **Deterministic Fuzzing Reproducibility**: Employed a seeded pseudo-random Box-Muller generator to ensure exact reproducible fuzz test runs across multiple OS platforms and CI pipelines.

## Update log (section 8)
- Added `engine/include/vs_metamorphic.h` with C API (`vs_metamorphic_apply_transform`, `vs_metamorphic_run_fuzzing_suite`, `vs_metamorphic_default_config`).
- Added `engine/src/metamorphic.cpp` with perturbation math, affine transformations, noise synthesis, and automated relation checkers.
- Added `engine/tests/metamorphic_tests.cpp` (21 assertions passed, 12.9 ms for full 19-variant suite).
- Added `dotnet/VisionCore/MetamorphicNative.cs` and `dotnet/VisionCore/MetamorphicEngine.cs`.
- Added `dotnet/VisionCore.Tests/MetamorphicTests.cs` (3/3 passed).
- Updated CMake and GitHub Actions Windows CI configuration.

## Verification
- C++ CTest: 100% pass (21/21 assertions across 5 suites).
- .NET xUnit: 3/3 tests passed.
- Execution speed: $12.9\text{ ms}$ for complete 19-perturbation suite ($< 35\text{ ms}$ budget).
