# Package protection: anti–reverse-engineering and anti-copy

Owner, 2026-11-01: the shipped software must resist reverse engineering and copying. This document
is how the build and the package are hardened, and the decisions behind it. It sits next to the
licence design ([LICENSING.md](LICENSING.md), [LICENCE-SERVER-ERPNEXT.md](LICENCE-SERVER-ERPNEXT.md),
which stop a copy from *running*) and covers the other half: making the binaries themselves hard to
read, change or clone. The threat model and the requirement wording are owned by a sibling item
(software-protection requirements); this document is the build and the enforcement, and honours the
line-safety rule regardless of when that spec lands.

**Line safety first (requirements section 12).** Every gate here fails safe: a failed integrity or
signature check never lets a bad part pass and never runs altered code. The line is told (alarm, NG),
the software refuses to run production, and nothing is silently wrong.

## 1. What is weak, and what we do about it

| Layer | Exposure | What this item does |
| --- | --- | --- |
| Native C++ engine (`codereader`, `vs_ocr`, `vs_locate`, `vs_measure`, `vs_blob`) | A `.dll`/`.dylib` is machine code — hard to read — but symbol names and debug info hand a reader the map. | Release build strips internal symbols and debug info, turns on whole-program optimisation and inlining (fewer function boundaries), hides inline symbols. The exported C API stays (callers need it); nothing else does. §2 |
| .NET assemblies (Vision Studio, runtime, VisionCore, …) | **The weak point.** IL decompiles back to near-source C#, with names and comments-worth of structure. | Release assemblies ship with no debug symbols and no source paths. The strong IL protection (renaming, string encryption, control-flow flattening) is *decided* below, not blindly turned on — it breaks the reflection/DI/gRPC/Avalonia this code leans on, and the genuinely secret decisions belong in native code anyway. §3 |
| The package as a whole | A file could be swapped or patched (a cracked assembly, a fake model, a patched gate). | A signed manifest lists every shipped file and its SHA-256; the software verifies it at start and refuses to run production if anything was changed. §4 |
| Running without a licence | A straight copy to another PC. | The licence client (ERPNext) — not this item, but the integrity gate protects the licence check from being patched out. |

## 2. Native engine: strip and optimise

`engine/CMakeLists.txt`, option `VS_HARDEN` (ON for a plain `Release` build; off for sanitizer and
debug builds so a developer keeps symbols). It touches only the **shipped libraries**, never the
tests, bench, adversarial tool or fuzzers.

- **Strip internal symbols, keep the exported C API.** Visibility is already `hidden` by default
  (`CMAKE_CXX_VISIBILITY_PRESET hidden`); the C API is `visibility("default")` / `dllexport`
  (`codereader.h`). The linker strips the local symbol table but keeps the dynamic (exported)
  symbols, so P/Invoke still binds while internal function names are gone.
  - macOS (Mach-O): `-Wl,-x -Wl,-dead_strip`.
  - Linux/ELF: `-Wl,-s -Wl,--gc-sections` with `-ffunction-sections -fdata-sections`.
  - Windows (MSVC): Release already emits no PDB; `/DEBUG:NONE /OPT:REF /OPT:ICF`, `/Gy`, and
    `/guard:cf` (control-flow guard, a cheap anti-tamper).
- **Whole-program / link-time optimisation** (`INTERPROCEDURAL_OPTIMIZATION_RELEASE`, i.e. LTO on
  clang/gcc, `/GL`+`/LTCG` on MSVC): more inlining means fewer clean function boundaries to read.
- **`-fvisibility-inlines-hidden`**: inline template instantiations do not become public symbols.
- **RTTI** (`VS_NO_RTTI`) is **off by default**: OpenCV `dnn` and some zxing paths use
  `typeid`/`dynamic_cast`, so `-fno-rtti` can fail to build or link. Stripping already removes the
  symbol *names*, which is the real win; turn `VS_NO_RTTI` on only after a clean hardened build
  proves it compiles on the ship toolchain.

Verified on macOS: the hardened `libcodereader.dylib` keeps all 34 `cr_*` exports and has **zero**
internal (local) text symbols; no internal C++ names (`locate`, `gridfit`, `underres`, …) remain.
All engine tests pass on the hardened build (§6).

## 3. .NET: the decision

The scope was: integrate an IL obfuscator, **or** recommend moving the sensitive decision logic into
the native engine. The decision is a deliberate split, because a blanket obfuscator here is both
risky and low-value:

**(a) Ship no debug symbols (done, zero risk).** `dotnet/Directory.Build.props` builds every Release
assembly with `DebugType=none`, no `.pdb`, deterministic, no XML doc file. This removes the source
file names, line numbers and doc comments a decompiler leans on first. It applies only to Release, so
`dotnet test` and developer builds keep full diagnostics.

**(b) Do _not_ blanket-rename/flatten the IL (decided against, for now).** The only free,
MSBuild-integrable obfuscator (Obfuscar) does renaming and string encryption but not control-flow
flattening; the strong commercial tools (Eazfuscator, Dotfuscator) need a licence that cannot be
committed to CI. More to the point, **renaming breaks this codebase**: ASP.NET DI, gRPC-generated
types, `System.Text.Json` (de)serialisation by property name, and Avalonia XAML binding all resolve
members by name at runtime. Turning renaming on without extensive per-assembly exclusion lists would
risk a wrong result on the line — which the line-safety rule forbids. So obfuscation is left as an
opt-in the owner can enable *after* that testing (see Pending), not switched on blind.

**(c) Anchor the genuinely sensitive decisions in native code (recommended; not done in this item).**
What actually must not be cloned or bypassed is small, and most of it is already native:

| Logic | Where it is now | Recommendation |
| --- | --- | --- |
| The OK/NG inspection decision (decode, grading, rules) | **Native** (`engine/`) — already hard to reverse | Keep it native. ✔ already right |
| Package-integrity enforcement gate | .NET (`VisionCore.Integrity`, `PackageIntegrityService`, `Station.CheckIntegrity`) | The native primitive now exists: PR #263 added `cr_integrity_check()` / `cr_integrity_state()` (runtime self-integrity + debugger detection, latching NG inside the engine). The remaining step is to have the .NET gate also call it, so patching the IL gate out still fails. This on-disk signed-manifest gate is complementary — it catches a swapped DLL/model/patched file *before* run; #263 catches tampering *at* run. |
| Licence verification | .NET (`VisionCore.Licensing`) | Move the signature check (or a second, redundant one) into native, for the same reason. |

Named assemblies to relocate from, if the owner wants the strongest bar: `VisionCore.Integrity`,
`VisionCore.Licensing`, and the `Station` production-start gate. **This item does not perform the
move** (scope: note which assemblies). It makes the move cheap by keeping the gate logic small and in
one place.

## 4. Package integrity: signed manifest + verify-before-run

The build lists every shipped file with its SHA-256 in a **manifest**, signs the manifest with the
vendor's **package signing key**, and ships the manifest and the public key in the package. At start
the software verifies the signature and every file; if anything was changed it will not run.

- **Signature scheme:** ECDSA P-256, SHA-256, DER signature — the same scheme and code shape as the
  licence answer ([LICENSING.md](LICENSING.md)), a separate key pair. The **public** key ships as
  `package-public.pem`; the **private** key never leaves the vendor's signing machine and is never in
  the repository.
- **Manifest** `package.manifest.json` (next to the program): `{ payload: base64(JSON), signature:
  base64(DER) }`, where the JSON is `{ version, created, files: { "<relative path>": "<sha256>" } }`.
- **Build-time tool** `dotnet/PackageSign` (not shipped): `keygen`, `sign <root> <private.pem>`
  (hashes everything under the package root except the manifest and the public key), and
  `verify <root> <public.pem>`. The Windows package build (PR #146, `tools/windows/build-package.ps1`)
  calls `PackageSign sign` after it publishes and copies the engine DLLs and models, as its last step.
- **Load-time check** `VisionCore.Integrity.PackageIntegrity.Check` + `PackageIntegrityService`:
  reads `package-public.pem` and `package.manifest.json`, verifies the signature, then hashes every
  file the manifest covers and compares. The check runs **once** at start (cached). A swapped file,
  a deleted file, an edited manifest or a manifest signed by another key all fail — the attacker
  cannot re-sign without the private key.

### Line-safety behaviour on failure

| When | What happens |
| --- | --- |
| `Studio:IntegrityRequired` is false (development/demo PCs) | The check is not enforced; the result is logged only. |
| Required, and the check passes | Normal operation. |
| Required, and the check fails | `Station.Start` refuses production (`CheckIntegrity` throws with the reason); the runtime raises **alarm 931** ("The software has failed its integrity check and will not run production"); the PLC heartbeat does not run, so the PLC holds the line as NG. **No part is inspected with altered code; nothing is silently wrong.** |

This mirrors the licence gate (`Station.CheckLicence`, alarm 930) exactly, so the line behaves the
same way for a tampered install as for an unlicensed one: it stops, and it says why.

## 5. Keys and secrets

- The package private key, like the licence private key, lives only on the vendor's signing machine
  (or the ERPNext/HSM signing service). It is **never** committed (AGENTS.md: keys and licence
  secrets out of the repository).
- `package-public.pem` is a trust anchor, not package content: the signer excludes it from the
  manifest, and the software reads it from the program folder. (Pinning its fingerprint inside the
  native engine — so swapping the public key is also caught — is part of the native-attest
  recommendation in §3c.)

## 6. Tested (this item)

- Engine: hardened `Release` build on macOS (`VS_HARDEN=ON`); all 23 ctest executables pass. The
  shipped `libcodereader.dylib` keeps all 38 `cr_*` exports (including #263's `cr_integrity_check` /
  `cr_integrity_state`) and has **zero** internal (local) text symbols. The read bench
  (`batch_check.py`) passes on a quiet Mac: 0 wrong reads, every batch at or above baseline — the
  LTO/strip flags do not change reads. (A run on a busy Mac shows the usual per-image time-budget
  shortfall of one or two faint codes, never a wrong read; it reads at baseline once the load drops.)
- .NET: `PackageSign`, `VisionCore`, `VisionStudio`, `Runtime.Host`, `Dashboard` build.
  `VisionCore.Tests` (750), `VisionStudio.Tests` (67, including the integrity-service line-safety
  cases) and `Runtime.Host.Tests` (68) pass. The integrity gate is verified to fail safe: a tampered
  or unsigned package makes `PackageIntegrityService.Ok` false, `Station` refuses production, and the
  runtime raises alarm 931.
- Windows MSVC strip/LTCG flags and the full package build (PR #146) are not run on this Mac; that is
  verified on Windows with #146.

## 7. Pending

- Wire the .NET integrity/licence gates to the native anti-tamper primitive PR #263 added
  (`cr_integrity_check()` / `cr_integrity_state()`) so the gates cannot be patched out in IL, and pin
  the `package-public.pem` fingerprint in the engine (§3c, §5). The native primitive now exists; the
  wiring from `Station`/`PackageIntegrityService` into it is the remaining work.
- Opt-in .NET obfuscation (renaming + string encryption) for the assemblies that do **not** rely on
  reflection/DI/gRPC/Avalonia binding, with per-assembly exclusion lists and its own test pass before
  it is line-safe (§3b).
- Wire `PackageSign sign` into the Windows package build and ship `IntegrityRequired: true` in the
  package config (coordinate with PR #146).
- Verify the MSVC hardening flags and the signed-manifest load path on Windows.
