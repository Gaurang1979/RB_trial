# Harden the build and package against reverse engineering and copying

- Status: done
- Session: magical-haibt-712634, 2026-11-01 (finished 2026-11-03)
- Pull request: #221
- Section 5 item: new
- Components/files: engine/CMakeLists.txt (release strip/LTO/no-RTTI option);
  dotnet/Directory.Build.props (Release: no symbols, deterministic);
  dotnet/VisionCore/Integrity (signed package manifest + load-time verify);
  dotnet/PackageSign (build-time manifest signer, not shipped);
  dotnet/VisionStudio + dotnet/Runtime.Host (integrity gate wired into the
  production start, alarm 931); docs/vision-system/PACKAGE-PROTECTION.md.
- Depends on: #146 (Windows package build/sign) — merged. Complementary to
  #220 (threat model/requirements, merged) and #222 (model encryption, merged);
  kept in its own files so they do not conflict.

## Section 5
- [x] (done 2026-11-03, PR #221: native engine Release build strips internal symbols and turns on LTO; .NET Release assemblies ship with no debug symbols; a signed package manifest is verified at start and production refuses to run — alarm 931, line held NG — if any shipped file was altered. The .NET obfuscation question is decided: ship no symbols now, do not blanket-rename IL (it breaks reflection/DI/gRPC/Avalonia), and relocate the few genuinely secret gates into native code — named below as a follow-up.) Harden the build and package against reverse engineering and copying

## Timeline (section 3)
| 2026-11-03 | Package-protection build hardening finished: engine Release strip/LTO verified on macOS (36 cr_* exports kept, 0 internal symbols); signed-manifest integrity gate verified fails safe (production refuses to run on tamper, alarm 931); .NET obfuscation decision recorded. | 12, 18 |

## Decisions (section 4)
| .NET IL protection | Ship Release assemblies with no debug symbols/source paths/XML docs (done, zero risk). Do NOT blanket rename/encrypt the IL: the only free MSBuild obfuscator (Obfuscar) does not do control-flow flattening, and renaming breaks ASP.NET DI, gRPC types, System.Text.Json by-name (de)serialisation and Avalonia XAML binding — a wrong result on the line, which line safety forbids. Instead anchor the genuinely secret decisions in native code (already true for OK/NG). Left as an opt-in the owner can enable after its own test pass. | 12, 18 |
| Package integrity = anti-tamper gate | A signed manifest (ECDSA P-256 / SHA-256, same scheme as the licence) lists every shipped file's hash; verified once at start. A swapped/patched/missing file, a forged or edited manifest, or a wrong signing key all fail. On failure production will not start (Station gate), alarm 931 fires and the PLC holds the line NG — never a silent wrong read, never altered code run. Off by default (IntegrityRequired) so a source/dev build still runs; the shipped package turns it on. | 12, 18 |
| RTTI off is opt-in | engine VS_NO_RTTI defaults OFF: OpenCV dnn and some zxing paths use typeid/dynamic_cast, so -fno-rtti can fail to build/link. Stripping already removes the internal names (the real win); turn RTTI off only after a clean hardened build proves it compiles on the ship toolchain. | 18 |

## Update log (section 8)
| 2026-11-03 | Added docs/vision-system/PACKAGE-PROTECTION.md (build/package hardening design and the .NET decision) and linked it from docs/vision-system/README.md. | Claude (magical-haibt-712634, branch claude/magical-haibt-712634) |

## Pending
- Native cr_attest() so the integrity and licence gates cannot be patched out in IL, and pin the package-public.pem fingerprint in the engine (PACKAGE-PROTECTION.md §3c, §5). Named assemblies to relocate the gate logic from: VisionCore.Integrity, VisionCore.Licensing, and the VisionStudio Station production-start gate.
- Opt-in .NET obfuscation (renaming + string encryption) for assemblies that do not rely on reflection/DI/gRPC/Avalonia binding, with per-assembly exclusion lists and its own test pass before it is line-safe (PACKAGE-PROTECTION.md §3b).
- Add the newer shipped engine libraries (vs_photometric, vs_polar, vs_deform, vs_phase, vs_wavelet, vs_drift, vs_metamorphic, vs_haystack, vs_autotune, corert64, …) to VS_HARDENED_LIBS in engine/CMakeLists.txt as their PRs (#225–#239) merge, so every shipped library is stripped/optimised, not only the original five.
- Wire `PackageSign sign` into the Windows package build and ship IntegrityRequired: true in the package config (coordinate with the Windows package build, PR #146 merged).
- Verify the MSVC hardening flags (/GL /LTCG /guard:cf, /DEBUG:NONE /OPT:REF /OPT:ICF) and the signed-manifest load path on Windows — not run on this Mac.
