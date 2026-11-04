cmake_minimum_required(VERSION 3.20)
project(codereader VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# Windows: install both with vcpkg (opencv4[core], zxing-cpp) and pass
#   -DCMAKE_TOOLCHAIN_FILE=<vcpkg>/scripts/buildsystems/vcpkg.cmake
find_package(OpenCV REQUIRED COMPONENTS core imgproc imgcodecs dnn)
find_package(ZXing REQUIRED)

if(MSVC)
  add_compile_options(/utf-8 /W3)
endif()

# Sanitizer build (engine/build-asan, scripts/fuzz-engine.sh): AddressSanitizer and
# UndefinedBehaviorSanitizer on every target; any error stops the program. Not for the line.
option(VS_SANITIZE "build with -fsanitize=address,undefined (testing only)" OFF)
if(VS_SANITIZE)
  add_compile_options(-fsanitize=address,undefined -fno-sanitize-recover=all -fno-omit-frame-pointer -g)
  add_link_options(-fsanitize=address,undefined)
endif()

# Release hardening (docs/vision-system/PACKAGE-PROTECTION.md): the shipped tool libraries must
# carry no symbol names or source hints and be slow to reverse. ON for a plain Release build; a
# sanitizer or debug build leaves it off so the symbols stay for the developer. It touches only the
# shipped libraries (codereader, vs_ocr, vs_locate, vs_measure, vs_blob), never the tests, bench or
# fuzzers, which must stay readable. The exported C API (visibility("default") / dllexport, see
# codereader.h) survives the strip; only the internal (local) symbol names are removed.
option(VS_HARDEN "strip and whole-program-optimise the shipped libraries in Release" ON)
# -fno-rtti is OFF by default: OpenCV dnn and some zxing paths use typeid/dynamic_cast, so turning it
# on can fail to build or link. Turn it on only after a clean hardened build proves it compiles.
option(VS_NO_RTTI "build the shipped libraries without RTTI (see PACKAGE-PROTECTION.md)" OFF)
set(VS_HARDENED_LIBS)   # filled as each shipped library is defined; hardened at the end of the file

if(VS_NO_RTTI AND NOT VS_SANITIZE)
  if(MSVC)
    add_compile_options($<$<COMPILE_LANGUAGE:CXX>:/GR->)
  else()
    add_compile_options($<$<COMPILE_LANGUAGE:CXX>:-fno-rtti>)
  endif()
endif()

# Under-resolved QR solver (underres.cpp): after Cognex US11151346, in force in the US, DE, CN and
# JP (no Indian member found). OFF builds a reader without it, e.g. for export (docs/PATENTS.md).
option(CR_UNDERRES "under-resolved QR solver (US11151346 territories: see docs/PATENTS.md)" ON)
if(NOT CR_UNDERRES)
  add_compile_definitions(CR_NO_UNDERRES)
endif()

set(ENGINE_SOURCES
  src/decode.cpp
  src/locate.cpp
  src/ai_locator.cpp
  src/quality.cpp
  src/gridfit.cpp
  src/qrgrid.cpp
  src/match.cpp
  src/dmmatch.cpp
  src/lattice.cpp
  src/qrsoft.cpp
  src/underres.cpp
  src/multiframe.cpp
  src/learned.cpp
  src/diagnose.cpp
  src/psf.cpp
  src/linear.cpp
  src/slots.cpp
  src/calibration.cpp
  src/grade1d.cpp
  src/grade_pdf417.cpp
  src/grade_dpm.cpp
  src/codewords.cpp
  src/decodability.cpp
  src/rules1d.cpp
  src/zxformats.cpp
  src/pdf417.cpp
  src/microqr.cpp
  src/pharmacode.cpp
  src/dotcode.cpp
  src/integrity.cpp
  third_party/qrcodegen/qrcodegen.cpp)
add_library(codereader SHARED src/c_api.cpp ${ENGINE_SOURCES})
target_include_directories(codereader PUBLIC include)
target_include_directories(codereader PRIVATE third_party/qrcodegen)
target_compile_definitions(codereader PRIVATE CODEREADER_BUILD)
target_link_libraries(codereader PRIVATE opencv_core opencv_imgproc opencv_dnn ZXing::ZXing)

# OpenCV objdetect (QR finder-pattern detectors) for the model-based QR step; optional
find_package(OpenCV QUIET COMPONENTS objdetect)
if(OpenCV_objdetect_FOUND OR TARGET opencv_objdetect)
  target_link_libraries(codereader PRIVATE opencv_objdetect)
  target_compile_definitions(codereader PRIVATE CR_HAVE_OBJDETECT)
  message(STATUS "OpenCV objdetect: model-based QR step enabled")
endif()

# libdmtx (BSD-2): extra Data Matrix decoder for hard regions; optional
find_path(DMTX_INCLUDE_DIR dmtx.h)
find_library(DMTX_LIBRARY NAMES dmtx libdmtx)
if(DMTX_INCLUDE_DIR AND DMTX_LIBRARY)
  target_include_directories(codereader PRIVATE ${DMTX_INCLUDE_DIR})
  target_link_libraries(codereader PRIVATE ${DMTX_LIBRARY})
  target_compile_definitions(codereader PRIVATE CR_HAVE_DMTX)
  message(STATUS "libdmtx: ${DMTX_LIBRARY}")
else()
  message(STATUS "libdmtx not found: Data Matrix fallback disabled")
endif()

# lens calibration (cr_calibrate_chessboard): OpenCV 5 calib + objdetect, when installed
find_package(OpenCV QUIET COMPONENTS calib objdetect)
if(TARGET opencv_calib AND TARGET opencv_objdetect)
  target_link_libraries(codereader PRIVATE opencv_calib opencv_objdetect)
  target_compile_definitions(codereader PRIVATE CR_HAVE_CALIB)
endif()

add_executable(bench ../tools/bench/bench.cpp)
target_link_libraries(bench PRIVATE codereader opencv_core opencv_imgcodecs)

# the bench's adversarial batch (tools/bench/batch_check.py): synthetic cases that must give no
# read or only their printed codes; QR from qrcodegen, Data Matrix from zint, codeword cells from
# codewords.cpp
add_executable(adversarial ../tools/bench/adversarial.cpp src/codewords.cpp third_party/qrcodegen/qrcodegen.cpp)
target_include_directories(adversarial PRIVATE include src third_party/qrcodegen)
target_link_libraries(adversarial PRIVATE opencv_core opencv_imgproc opencv_imgcodecs opencv_dnn ZXing::ZXing)

# unit tests for engine internals (Reed-Solomon, Data Matrix soft decoding): build/engine_tests
add_executable(engine_tests tests/engine_tests.cpp src/c_api.cpp ${ENGINE_SOURCES})
target_include_directories(engine_tests PRIVATE include src third_party/qrcodegen)
target_compile_definitions(engine_tests PRIVATE CODEREADER_BUILD # c_api.cpp defines the CR_API functions
  CR_TEST_HOOKS # integrity.cpp exposes resetForTest() so the anti-tamper test can restore state
  CR_MODEL_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../ml/models" CR_FUZZ_DIR="${CMAKE_CURRENT_SOURCE_DIR}/tests/fuzz")
target_link_libraries(engine_tests PRIVATE opencv_core opencv_imgproc opencv_dnn ZXing::ZXing)
if(TARGET opencv_calib AND TARGET opencv_objdetect)
  target_link_libraries(engine_tests PRIVATE opencv_calib)
  target_compile_definitions(engine_tests PRIVATE CR_HAVE_CALIB)
endif()
if(OpenCV_objdetect_FOUND OR TARGET opencv_objdetect)
  target_link_libraries(engine_tests PRIVATE opencv_objdetect)
  target_compile_definitions(engine_tests PRIVATE CR_HAVE_OBJDETECT)
endif()
if(DMTX_INCLUDE_DIR AND DMTX_LIBRARY)
  target_include_directories(engine_tests PRIVATE ${DMTX_INCLUDE_DIR})
  target_link_libraries(engine_tests PRIVATE ${DMTX_LIBRARY})
  target_compile_definitions(engine_tests PRIVATE CR_HAVE_DMTX)
endif()
# OCR / OCV tool (vs_ocr.h): line finder + CTC line recogniser (ocrnet.onnx) + expected-text check
set(OCR_SOURCES
  src/ocr/lines.cpp
  src/ocr/recognize.cpp
  src/ocr/verify.cpp
  src/ocr/quality.cpp
  src/ocr/font.cpp
  src/ocr/format.cpp
  src/ocr/layout.cpp
  src/ocr/c_api_ocr.cpp)
add_library(vs_ocr SHARED ${OCR_SOURCES})
target_include_directories(vs_ocr PUBLIC include PRIVATE src/ocr)
target_compile_definitions(vs_ocr PRIVATE VS_OCR_BUILD)
target_link_libraries(vs_ocr PRIVATE opencv_core opencv_imgproc opencv_dnn)

# the bench and the tests compile the sources in, so they can reach the line finder directly
add_executable(ocr_bench ../tools/bench/ocr_bench.cpp ${OCR_SOURCES})
target_include_directories(ocr_bench PRIVATE include src/ocr)
target_compile_definitions(ocr_bench PRIVATE VS_OCR_BUILD)
target_link_libraries(ocr_bench PRIVATE opencv_core opencv_imgproc opencv_imgcodecs opencv_dnn)

add_executable(ocr_tests tests/ocr_tests.cpp ${OCR_SOURCES})
target_include_directories(ocr_tests PRIVATE include src/ocr)
target_compile_definitions(ocr_tests PRIVATE VS_OCR_BUILD OCR_MODEL_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../ml/models")
target_link_libraries(ocr_tests PRIVATE opencv_core opencv_imgproc opencv_imgcodecs opencv_dnn)

# Locate tool (vs_locate.h): find the part, so the other tools can follow it
add_library(vs_locate SHARED src/fixture/locate_tool.cpp)
target_include_directories(vs_locate PUBLIC include)
target_compile_definitions(vs_locate PRIVATE VS_LOCATE_BUILD)
target_link_libraries(vs_locate PRIVATE opencv_core opencv_imgproc)
add_executable(locate_tests tests/locate_tests.cpp src/fixture/locate_tool.cpp)
target_include_directories(locate_tests PRIVATE include)
target_compile_definitions(locate_tests PRIVATE VS_LOCATE_BUILD)
target_link_libraries(locate_tests PRIVATE opencv_core opencv_imgproc opencv_imgcodecs)

# Measure tool (vs_measure.h): calipers, line, circle and ellipse fit, corner, gap, pin pitch, edge count; px and mm
add_library(vs_measure SHARED src/measure/measure_tool.cpp)
target_include_directories(vs_measure PUBLIC include)
target_compile_definitions(vs_measure PRIVATE VS_MEASURE_BUILD)
target_link_libraries(vs_measure PRIVATE opencv_core)
add_executable(measure_tests tests/measure_tests.cpp src/measure/measure_tool.cpp)
target_include_directories(measure_tests PRIVATE include)
target_compile_definitions(measure_tests PRIVATE VS_MEASURE_BUILD)
target_link_libraries(measure_tests PRIVATE opencv_core opencv_imgproc)
# Blob tool (vs_blob.h): threshold, connected components, per-blob measures, filters and sort
add_library(vs_blob SHARED src/blob/blob_tool.cpp)
target_include_directories(vs_blob PUBLIC include)
target_compile_definitions(vs_blob PRIVATE VS_BLOB_BUILD)
target_link_libraries(vs_blob PRIVATE opencv_core opencv_imgproc)
add_executable(blob_tests tests/blob_tests.cpp src/blob/blob_tool.cpp)
target_include_directories(blob_tests PRIVATE include)
target_compile_definitions(blob_tests PRIVATE VS_BLOB_BUILD)
target_link_libraries(blob_tests PRIVATE opencv_core opencv_imgproc)

# Inspect tool (vs_inspect.h): golden template & variation model inspection
add_library(vs_inspect SHARED src/inspect.cpp)
target_include_directories(vs_inspect PUBLIC include)
target_compile_definitions(vs_inspect PRIVATE VS_INSPECT_BUILD)
target_link_libraries(vs_inspect PRIVATE opencv_core)
add_executable(inspect_tests tests/inspect_tests.cpp)
target_include_directories(inspect_tests PRIVATE include)
target_link_libraries(inspect_tests PRIVATE vs_inspect)

# In-memory PatchCore anomaly detection tool (vs_anomaly.h, AG-02, Tool 10): HNSW vector index
add_library(vs_anomaly SHARED src/anomaly/patchcore.cpp)
target_include_directories(vs_anomaly PUBLIC include PRIVATE src/anomaly)
target_compile_definitions(vs_anomaly PRIVATE VS_ANOMALY_BUILD)
add_executable(anomaly_tests tests/anomaly_tests.cpp src/anomaly/patchcore.cpp)
target_include_directories(anomaly_tests PRIVATE include src/anomaly)
target_compile_definitions(anomaly_tests PRIVATE VS_ANOMALY_BUILD)

# Thermal packaging seal inspection tool (vs_thermal.h): bottle induction, pouch seam, hot glue
add_library(vs_thermal SHARED src/thermal.cpp)
target_include_directories(vs_thermal PUBLIC include)
target_compile_definitions(vs_thermal PRIVATE VS_THERMAL_BUILD)
target_link_libraries(vs_thermal PRIVATE opencv_core)
add_executable(thermal_tests tests/thermal_tests.cpp)
target_include_directories(thermal_tests PRIVATE include)
target_link_libraries(thermal_tests PRIVATE vs_thermal)

# Infrared Inspection tool (vs_infrared.h): see-through fill, metameric inks, SWIR moisture, solar EL cracks
add_library(vs_infrared SHARED src/infrared.cpp)
target_include_directories(vs_infrared PUBLIC include)
target_compile_definitions(vs_infrared PRIVATE VS_INFRARED_BUILD)
add_executable(infrared_tests tests/infrared_tests.cpp src/infrared.cpp)
target_include_directories(infrared_tests PRIVATE include)
target_compile_definitions(infrared_tests PRIVATE VS_INFRARED_BUILD)

# Polarimetric Decoupler (vs_polar.h): Stokes decomposition, glare removal, stress birefringence
add_library(vs_polar SHARED src/polar.cpp)
target_include_directories(vs_polar PUBLIC include)
target_compile_definitions(vs_polar PRIVATE VS_POLAR_BUILD)
add_executable(polar_tests tests/polar_tests.cpp src/polar.cpp)
target_include_directories(polar_tests PRIVATE include)
target_compile_definitions(polar_tests PRIVATE VS_POLAR_BUILD)

# Photometric Stereo tool (vs_photometric.h): LumiTrax/SurfaceFX albedo and 3D curvature decoupling
add_library(vs_photometric SHARED src/photometric.cpp)
target_include_directories(vs_photometric PUBLIC include)
target_compile_definitions(vs_photometric PRIVATE VS_PHOTOMETRIC_BUILD)
add_executable(photometric_tests tests/photometric_tests.cpp src/photometric.cpp)
target_include_directories(photometric_tests PRIVATE include)
target_compile_definitions(photometric_tests PRIVATE VS_PHOTOMETRIC_BUILD)

# Wavelet tool (vs_wavelet.h): homomorphic scratch and micro-defect filter on periodic texture
add_library(vs_wavelet SHARED src/wavelet.cpp)
target_include_directories(vs_wavelet PUBLIC include)
target_compile_definitions(vs_wavelet PRIVATE VS_WAVELET_BUILD)
add_executable(wavelet_tests tests/wavelet_tests.cpp src/wavelet.cpp)
target_include_directories(wavelet_tests PRIVATE include)
target_compile_definitions(wavelet_tests PRIVATE VS_WAVELET_BUILD)

# Specular highlight glare inpainter tool (vs_inpaint.h, AG-07): Fast Marching gradient inpainting
add_library(vs_inpaint SHARED src/inpaint.cpp)
target_include_directories(vs_inpaint PUBLIC include)
target_compile_definitions(vs_inpaint PRIVATE VS_INPAINT_BUILD)
add_executable(inpaint_tests tests/inpaint_tests.cpp src/inpaint.cpp)
target_include_directories(inpaint_tests PRIVATE include)
target_compile_definitions(inpaint_tests PRIVATE VS_INPAINT_BUILD)

# Conveyor rolling-shutter rectification tool (vs_rectify.h, AG-06): line velocity compensation
add_library(vs_rectify SHARED src/rectify.cpp)
target_include_directories(vs_rectify PUBLIC include)
target_compile_definitions(vs_rectify PRIVATE VS_RECTIFY_BUILD)
add_executable(rectify_tests tests/rectify_tests.cpp src/rectify.cpp)
target_include_directories(rectify_tests PRIVATE include)
target_compile_definitions(rectify_tests PRIVATE VS_RECTIFY_BUILD)

# Opaque Runtime Host & Asset Vault (corert.h, corert64)
add_library(corert64 SHARED src/corert.cpp)
target_include_directories(corert64 PUBLIC include)
target_compile_definitions(corert64 PRIVATE CORERT_BUILD)
target_link_libraries(corert64 PRIVATE)
add_executable(corert_tests tests/test_corert.cpp src/corert.cpp)
target_include_directories(corert_tests PRIVATE include)
target_compile_definitions(corert_tests PRIVATE CORERT_BUILD)
target_link_libraries(corert_tests PRIVATE)

# Optical drift detector (vs_drift.h, AG-12, AS-13): Mahalanobis baseline condition monitoring
add_library(vs_drift SHARED src/drift.cpp)
target_include_directories(vs_drift PUBLIC include)
target_compile_definitions(vs_drift PRIVATE VS_DRIFT_BUILD)
add_executable(drift_tests tests/drift_tests.cpp src/drift.cpp)
target_include_directories(drift_tests PRIVATE include)
target_compile_definitions(drift_tests PRIVATE VS_DRIFT_BUILD)

# Non-rigid deformable mesh matcher (vs_deform.h)
add_library(vs_deform SHARED src/deform.cpp)
target_include_directories(vs_deform PUBLIC include)
target_compile_definitions(vs_deform PRIVATE VS_DEFORM_EXPORTS)
add_executable(deform_tests tests/deform_tests.cpp src/deform.cpp)
target_include_directories(deform_tests PRIVATE include)

# Phase Correlation tool (vs_phase.h): log-polar Fourier sub-pixel registration & contrast-fade invariance
add_library(vs_phase SHARED src/phase.cpp)
target_include_directories(vs_phase PUBLIC include)
target_compile_definitions(vs_phase PRIVATE VS_PHASE_BUILD)
target_link_libraries(vs_phase PRIVATE opencv_core opencv_imgproc)
add_executable(phase_tests tests/phase_tests.cpp src/phase.cpp)
target_include_directories(phase_tests PRIVATE include)
target_compile_definitions(phase_tests PRIVATE VS_PHASE_BUILD)
target_link_libraries(phase_tests PRIVATE opencv_core opencv_imgproc)

# Metamorphic robustness & fuzzing harness (vs_metamorphic.h)
add_library(vs_metamorphic SHARED src/metamorphic.cpp)
target_include_directories(vs_metamorphic PUBLIC include)
target_compile_definitions(vs_metamorphic PRIVATE VS_METAMORPHIC_EXPORTS)
add_executable(metamorphic_tests tests/metamorphic_tests.cpp src/metamorphic.cpp)
target_include_directories(metamorphic_tests PRIVATE include)

# Haystack 2D latent space explorer tool (vs_haystack.h)
add_library(vs_haystack SHARED src/vs_haystack.cpp)
target_include_directories(vs_haystack PUBLIC include)
target_compile_definitions(vs_haystack PRIVATE VS_HAYSTACK_BUILD)
target_link_libraries(vs_haystack PRIVATE)
add_executable(haystack_tests tests/test_haystack.cpp src/vs_haystack.cpp)
target_include_directories(haystack_tests PRIVATE include)
target_compile_definitions(haystack_tests PRIVATE VS_HAYSTACK_BUILD)
target_link_libraries(haystack_tests PRIVATE)

# Environmental auto-tune, optical health & upstream tooling advisor (vs_autotune.h)
add_library(vs_autotune SHARED src/autotune.cpp)
target_include_directories(vs_autotune PUBLIC include)
target_compile_definitions(vs_autotune PRIVATE VS_AUTOTUNE_BUILD)
target_link_libraries(vs_autotune PRIVATE)
add_executable(autotune_tests tests/autotune_tests.cpp src/autotune.cpp)
target_include_directories(autotune_tests PRIVATE include)
target_compile_definitions(autotune_tests PRIVATE VS_AUTOTUNE_BUILD)
target_link_libraries(autotune_tests PRIVATE)

enable_testing()
# ISO/IEC TR 29158 (DPM) grading on synthetic marks: build/dpm_tests
add_executable(dpm_tests tests/dpm_tests.cpp src/c_api.cpp ${ENGINE_SOURCES})
target_include_directories(dpm_tests PRIVATE include src third_party/qrcodegen)
target_compile_definitions(dpm_tests PRIVATE CODEREADER_BUILD)
target_link_libraries(dpm_tests PRIVATE opencv_core opencv_imgproc opencv_dnn ZXing::ZXing)
if(DMTX_INCLUDE_DIR AND DMTX_LIBRARY)
  target_include_directories(dpm_tests PRIVATE ${DMTX_INCLUDE_DIR})
  target_link_libraries(dpm_tests PRIVATE ${DMTX_LIBRARY})
  target_compile_definitions(dpm_tests PRIVATE CR_HAVE_DMTX)
endif()
add_test(NAME engine_tests COMMAND engine_tests)
add_test(NAME dpm_tests COMMAND dpm_tests)
add_test(NAME ocr_tests COMMAND ocr_tests)
add_test(NAME locate_tests COMMAND locate_tests)
add_test(NAME measure_tests COMMAND measure_tests)
add_test(NAME blob_tests COMMAND blob_tests)
add_test(NAME corert_tests COMMAND corert_tests)
add_test(NAME inspect_tests COMMAND inspect_tests)
add_test(NAME anomaly_tests COMMAND anomaly_tests)
add_test(NAME thermal_tests COMMAND thermal_tests)
add_test(NAME infrared_tests COMMAND infrared_tests)
add_test(NAME polar_tests COMMAND polar_tests)
add_test(NAME photometric_tests COMMAND photometric_tests)
add_test(NAME wavelet_tests COMMAND wavelet_tests)
add_test(NAME inpaint_tests COMMAND inpaint_tests)
add_test(NAME rectify_tests COMMAND rectify_tests)
add_test(NAME drift_tests COMMAND drift_tests)
add_test(NAME deform_tests COMMAND deform_tests)
add_test(NAME phase_tests COMMAND phase_tests)
add_test(NAME metamorphic_tests COMMAND metamorphic_tests)
add_test(NAME haystack_tests COMMAND haystack_tests)
add_test(NAME autotune_tests COMMAND autotune_tests)

# several readers, OCR and locate tools at once, as on a two-camera line: build/stress_tests [seconds].
# In a -fsanitize=thread build (build-tsan: the header of tests/stress_tests.cpp) it finds data races and deadlocks.
add_executable(stress_tests tests/stress_tests.cpp third_party/qrcodegen/qrcodegen.cpp)
target_include_directories(stress_tests PRIVATE include third_party/qrcodegen)
target_compile_definitions(stress_tests PRIVATE CR_MODEL_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../ml/models")
target_link_libraries(stress_tests PRIVATE codereader vs_ocr vs_locate opencv_core opencv_imgproc)
add_test(NAME stress_tests COMMAND stress_tests 10)

# Tests that check a time budget (latency asserts, or reads that give up when time_budget_ms runs
# out) run alone, so "ctest -j N" cannot slow them into a false failure (Req 17.10 AUD-10).
# corert_tests and autotune_tests check no time and may run in parallel.
set_tests_properties(
  engine_tests dpm_tests ocr_tests locate_tests measure_tests blob_tests inspect_tests
  anomaly_tests thermal_tests infrared_tests polar_tests photometric_tests wavelet_tests
  inpaint_tests rectify_tests drift_tests deform_tests phase_tests metamorphic_tests
  haystack_tests stress_tests
  PROPERTIES RUN_SERIAL TRUE)

# C API fuzzers (engine/tests/fuzz/, run by scripts/fuzz-engine.sh): bad images, settings and text
# blobs through codereader.h, vs_ocr.h and vs_locate.h, as a caller hands them over
option(VS_FUZZ "build the C API fuzzers" OFF)
if(VS_FUZZ)
  add_executable(fuzz_codereader tests/fuzz/fuzz_codereader.cpp third_party/qrcodegen/qrcodegen.cpp)
  target_include_directories(fuzz_codereader PRIVATE third_party/qrcodegen)
  target_link_libraries(fuzz_codereader PRIVATE codereader)
  add_executable(fuzz_ocr tests/fuzz/fuzz_ocr.cpp)
  target_compile_definitions(fuzz_ocr PRIVATE OCR_MODEL_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../ml/models")
  target_link_libraries(fuzz_ocr PRIVATE vs_ocr opencv_core opencv_imgproc)
  add_executable(fuzz_locate tests/fuzz/fuzz_locate.cpp)
  target_link_libraries(fuzz_locate PRIVATE vs_locate opencv_core opencv_imgproc)
endif()

# ---------------------------------------------------------------------------
# Apply the release hardening (VS_HARDEN, above) to the shipped tool libraries
# only. Tests, the bench, the adversarial tool and the fuzzers are left alone
# so a developer keeps their symbols. See docs/vision-system/PACKAGE-PROTECTION.md.
# ---------------------------------------------------------------------------
# Every add_library(... SHARED) above that ships; keep in step when a library is added.
set(VS_HARDENED_LIBS
  codereader vs_ocr vs_locate vs_measure vs_blob vs_inspect vs_anomaly vs_thermal vs_infrared
  vs_polar vs_photometric vs_wavelet vs_inpaint vs_rectify corert64 vs_drift vs_deform vs_phase
  vs_metamorphic vs_haystack vs_autotune)
if(VS_HARDEN AND NOT VS_SANITIZE AND CMAKE_BUILD_TYPE STREQUAL "Release")
  # Whole-program / link-time optimisation when the toolchain supports it: more inlining (fewer
  # function boundaries to read) and, on MSVC, /GL + /LTCG.
  include(CheckIPOSupported)
  check_ipo_supported(RESULT VS_IPO_OK OUTPUT VS_IPO_MSG)
  foreach(lib IN LISTS VS_HARDENED_LIBS)
    if(VS_IPO_OK)
      set_property(TARGET ${lib} PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)
    endif()
    if(MSVC)
      # Release default already emits no PDB; drop any debug data and fold identical code so the
      # binary gives up less structure. /guard:cf is a cheap anti-tamper hardening.
      target_compile_options(${lib} PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/Gy /guard:cf>)
      target_link_options(${lib} PRIVATE /DEBUG:NONE /OPT:REF /OPT:ICF /guard:cf)
    elseif(APPLE)
      # Mach-O: strip local (internal) symbol names and dead code; keep the exported C API (.dynsym).
      target_compile_options(${lib} PRIVATE $<$<COMPILE_LANGUAGE:CXX>:-fvisibility-inlines-hidden>)
      target_link_options(${lib} PRIVATE -Wl,-x -Wl,-dead_strip)
    else()
      # ELF (gcc/clang): -Wl,-s removes the symbol table but keeps the dynamic (exported) symbols;
      # --gc-sections drops unused code with -ffunction-sections/-fdata-sections.
      target_compile_options(${lib} PRIVATE $<$<COMPILE_LANGUAGE:CXX>:-fvisibility-inlines-hidden -ffunction-sections -fdata-sections>)
      target_link_options(${lib} PRIVATE -Wl,-s -Wl,--gc-sections)
    endif()
  endforeach()
  message(STATUS "VS_HARDEN: shipped libraries stripped and optimised (IPO: ${VS_IPO_OK})")
endif()
