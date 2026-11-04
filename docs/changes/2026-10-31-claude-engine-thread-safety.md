# Engine thread safety: several readers at once (TSan stress test)

- Status: done
- Session: engine-thread-safety, 2026-10-31
- Pull request: #176
- Section 5 item: new
- Components/files: engine/tests/stress_tests.cpp (new), engine/CMakeLists.txt (stress_tests target and ctest entry), .gitignore (engine/build-*/); no engine source changed
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #176: thread sanitizer stress test, no data race or deadlock found; kept as ctest `stress_tests`) Engine thread safety: several readers at once (TSan stress test)

## Timeline (section 3)
| 2026-10-31 | Engine thread safety with several readers at once: `engine/tests/stress_tests.cpp` runs 2-4 camera readers (codenet.onnx, each re-created every 7 parts with its learned state carried over), 2 OCR tools (ocrnet.onnx), a locate tool and a thread creating and destroying readers, OCR and locate tools, all at once; it checks that no reader returns a code of another camera, that every part is read in full, that OCR and locate keep their answers. Under `-fsanitize=thread` (engine/build-tsan, recipe in the test's header): 3 readers 90 s (2,308 parts), 4 readers 240 s (6,328 parts), 4 readers 120 s with CR_POOL=1 (4,752 parts): every part read in full, 0 foreign codes, OCR 10,452/10,452 OK, locate 7,442/7,442 in place, 1,688 churn cycles, no ThreadSanitizer report (sanitizer checked live on a deliberate race). No engine change needed | 12 |

## Decisions (section 4)
| Shared state in the engine | Named and checked for two cameras at once: (1) OpenCV's thread pool (TBB) is process-wide: the decode never calls cv::setNumThreads (PR #148) and holds a counted parallel_for_ region open (CvSerialGuard in decode.cpp, a never-destroyed holder under a mutex); (2) the QR reference-symbol cache (qrsoft.cpp) is a static map under a mutex, entries never erased, so returned references stay valid; (3) OpenCV QR detectors (gridfit.cpp), QR masks (match.cpp) and zxing's read scope (zxformats.cpp) are thread_local; (4) learned state (StepStats) is per reader, all atomics; (5) no model session is shared: every reader loads its own locator net, one enhancer net per decode thread, every OCR tool its own ocrnet, so a camera never waits on another's forward pass; (6) the C APIs allow one thread per handle, and the .NET side keeps to it: each tool belongs to one camera, JobRunner.Run(camera) runs only that camera's tools, Station.InspectCamera holds a per-camera lock, RunCycle runs cameras in parallel each on its own tools, and a job load is refused while the line runs (vs-runtime uses the same Station) | 12 |

## Update log (section 8)
| 2026-10-31 | engine/tests/stress_tests.cpp header: how to build and run the thread sanitizer stress test | Claude (Mac, branch claude/engine-thread-safety) |

## Pending
- cr_create calls cv::setNumThreads when the diagnostic CR_CV_THREADS is set: a reader created while another camera decodes could rebuild OpenCV's TBB arena under it (the PR #148 crash). Never set on the line; drop the variable or apply it only before the first reader
- Stress test on real sample images (VS_IMAGES) and with the enhancer model loaded: the drawn cases read on the easy steps, so the hard paths (enhancer, soft QR, libdmtx, multi-frame, miss diagnosis) ran little under the sanitizer
