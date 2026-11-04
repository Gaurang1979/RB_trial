# Add every shipped engine library to VS_HARDENED_LIBS (Req 18.3 item 2)

- Status: done
- Session: hardened-libs-ba8065, 2026-11-03
- Pull request: #319
- Section 5 item: new
- Components/files: engine/CMakeLists.txt (VS_HARDENED_LIBS list only)
- Depends on: none (#221 merged)

## Section 5
- [x] (done 2026-11-03, PR #319: VS_HARDENED_LIBS lists all 21 shipped add_library(... SHARED) targets, so the Release strip/LTO/dead-strip hardening covers vs_inspect, vs_anomaly, vs_thermal, vs_infrared, vs_polar, vs_photometric, vs_wavelet, vs_inpaint, vs_rectify, corert64, vs_drift, vs_deform, vs_phase, vs_metamorphic, vs_haystack and vs_autotune as well as the first five; tests, bench and fuzzers stay out. macOS Release build: 21/21 dylibs with 0 internal symbols, ctest 23/23.) Add the newer shipped engine libraries to VS_HARDENED_LIBS so every shipped library is stripped and optimised

## Timeline (section 3)
| 2026-11-03 | Release hardening extended to every shipped engine library (21, was 5); macOS Release build with IPO: all 21 dylibs keep only their exported C API (0 internal symbols), ctest 23/23 passed. MSVC flags not checked (no Windows). | 18 |
