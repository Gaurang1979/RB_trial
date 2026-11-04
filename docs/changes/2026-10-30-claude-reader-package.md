# Reader module as a full in-process package for other aggregation applications: central ERPNext licence, host-controlled signed updates, cameras and products through the API (requirements update)

- Status: done
- Session: Claude desktop (reader-package), 2026-10-30
- Pull request: #114
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (sections 1, 3, 9, 10, 15), docs/vision-system/SDK-API.md (new section 0, open questions), docs/vision-system/LICENSING.md (new section 7.4, open questions), docs/vision-system/ARCHITECTURE.md (decisions row, process diagram), docs/vision-system/AI-DEEP-LEARNING.md (link only); design only, no code
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #114: requirements section 10 rewritten as "Vision Package for other applications", sections 1, 3, 9 and 15 to match; SDK-API.md section 0, LICENSING.md section 7.4, ARCHITECTURE.md decisions) Requirements: other applications such as the aggregation software use the reader as one full Vision Package (every reading, AI and camera feature), licensed only by the ERPNext licence service and updated by the host application, instead of separate tool DLLs.

## Timeline (section 3)
| 2026-10-30 | Vision Package requirement: one signed full package (1D, 2D and QR reading with every feature, AI, cameras, products, licence client) loaded by other applications; nothing runs or decrypts without an ERPNext licence; updates delivered and switched by the host with self-test and rollback; cameras and products set through the package API | 1, 3, 9, 10, 15 |

## Decisions (section 4)
| Other applications use a full package | Owner, 2026-10-30: the DLL-only SDK of the first requirements is replaced by one full package that the host application (first the owner's aggregation software) loads in its own process, chosen over a service with a remote API. A bare tool DLL lacks the cameras, AI models, product master, learned state, duplicates and GS1 check that sit around the decoder | 1, 3, 10 |
| Package licence | Owner, 2026-10-30: "user having full package can not run or decrypt anything without issued centralised licence from erpnext service". Same licence client and Vision Licence record as Vision Studio; one licence per location, bound to its PC and cameras; the content key for models and teach data comes only inside the signed ERPNext answer, encrypted for that installation | 9, 10; LICENSING 7.4 |
| Package updates | Owner, 2026-10-30: the owner exports each package version and adds it to the aggregation software, which updates every location under its own control. The package never updates itself; it is signed, installs beside the old version, switches only at open, migrates data on a copy, runs a self-test, and can be rolled back | 10; SDK-API 0.4 |
| Setup through the package API | Owner, 2026-10-30: the host chooses cameras by role and serial and pushes products from its own product master (code type, codes per case, GTIN, camera settings), refused while running, checked, versioned and logged. Teach, tuning, AI models, calibration and users stay in Vision Studio and arrive as an encrypted product file. The Remote API's operate token is unchanged | 10; SDK-API 0.3 |
| Worker process inside the package | Line safety (requirements 12), decided by the session and open to the owner's change: the package starts `vs-runtime` in private mode as its own worker, so a native fault or library clash never closes the host, a case in flight is NG, and results equal Vision Studio's because it is the same runtime program | 3, 10, 15; SDK-API 0.2 |

## Update log (section 8)
| 2026-10-30 | Requirements sections 1 (scope, principle 4), 3 (other application, package, licence rows and diagram), 9 (package licence), 10 (rewritten), 15 (P2, P4, acceptance, risk, open questions); SDK-API.md section 0 and open questions; LICENSING.md section 7.4 and open question; ARCHITECTURE.md decisions row and diagram; AI-DEEP-LEARNING.md link to the new section 10 heading | Claude (Mac, branch claude/reader-package) |

## Pending
- Vision Package v1 for the aggregation software: VisionStudio.Package (.NET, C#), signed manifest, install beside, switch at open, data migration on a copy, self-test and rollback (SDK-API.md section 0).
- Hybrid 1 runtime: a private mode for the Vision Package worker (no Windows service, no web pages, no Remote API port; one named pipe for the host's user).
- Package setup calls in the runtime contract: list cameras, assign a camera to a role by serial, save, remove and import products, with checks, versions and the user name.
- ERPNext vision_licence: the content key in the verify answer, wrapped for the installation's public key, and the package version in the check list (LICENSING.md section 7.4).
- Vision Studio: export a product file (teach, tuning, learned state, AI model choice), encrypted, for import into the Vision Package.
- Vision Package sample host application for .NET (C#): camera choice, product push, a run, an update with rollback.
