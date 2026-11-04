# One AES-256-GCM asset envelope; timed engine tests run serially (Req 17.10 AUD-09, AUD-10)

- Status: done
- Session: claude audit-asset-crypto, 2026-11-03
- Pull request: #269
- Section 5 item: new
- Components/files: engine/src/corert.cpp, engine/include/corert.h, engine/tests/test_corert.cpp, engine/CMakeLists.txt, dotnet/VisionCore/Security/SecureAssetVault.cs, dotnet/VisionCore.Tests/SecureAssetVaultTests.cs, dotnet/CodeReader/Protection/ContentCipher.cs (one header check)
- Depends on: #267

## Section 5
- New: [x] (done 2026-11-03, PR #269: one AES-256-GCM asset envelope, SecureAssetVault over ContentCipher, native XOR crypto retired; timed engine tests RUN_SERIAL) Req 17.10 AUD-09 (crypto part) and AUD-10.

## Timeline (section 3)
| 2026-11-03 | One asset envelope: `SecureAssetVault` is now a thin wrapper over `ContentCipher` (AES-256-GCM ".vsenc", random nonce, 32-byte key, asset name as associated data, `CryptographicException`, no fallback); the native SHA-256 XOR `sys_encrypt_asset` / `sys_decrypt_asset` and opcodes 0x4001/0x4002 are retired and return `SYS_ERR_RETIRED`; `ContentCipher` refuses a non-zero reserved header byte. Every timed engine test is `RUN_SERIAL`. Engine built, ctest -j 4, VisionCore.Tests and CodeReader.Tests run | 17.10 AUD-09, AUD-10; 18 |

## Decisions (section 4)
| Asset encryption | One envelope in the product, AES-256-GCM in .NET (`ContentCipher` from #222). The native exports `sys_encrypt_asset` / `sys_decrypt_asset` stay exported, so an old host still links, but always fail with `SYS_ERR_RETIRED` (-101) and hand back no buffer; the engine links no AES-GCM library, so the native path is not rebuilt there. `SecureAssetVault` takes a 32-byte key and an asset name; any failure to open a blob is a `CryptographicException` and the asset is not loaded (NG) | 17.10 AUD-09; 12; 18 |
| Timed engine tests | Every engine test that checks a time budget is `RUN_SERIAL TRUE`, so `ctest -j N` cannot slow it into a false failure; only `corert_tests` and `autotune_tests` (no timing) run in parallel. Load from outside ctest (other sessions) still spoils timings: judge those runs under the heavy-test lock (rule 13) | 17.10 AUD-10 |

## Update log (section 8)
| 2026-11-03 | Change note for AUD-09 (crypto) and AUD-10 | Claude (Mac, branch claude/audit-asset-crypto) |

## Pending
- Engine unit tests: replace `assert()` (compiled out in the Release build) with an always-on check in the remaining `engine/tests/*.cpp`.
- VisionCore.Tests that fail on main under load as well: `BlobToolTests.OutlineCutByTheAreaIsNotChecked` (fails on main and on this branch), `CameraChannelTests.CamerasAtDifferentPlacesTriggerIndependently` and `RenderedFontTests.LightPrintOnADarkPartReadsWithTheSameFont` (flaky on both).
