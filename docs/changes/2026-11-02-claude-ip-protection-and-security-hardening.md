# IP protection, binary camouflage, and in-memory asset vault

- Status: done
- Session: ip-protection-and-security-hardening, 2026-11-02
- Pull request: #240
- Section 5 item: new
- Components/files: CLAUDE.md, engine/include/corert.h, engine/src/corert.cpp, engine/tests/test_corert.cpp, engine/CMakeLists.txt, dotnet/VisionCore/Security/CamouflageLabels.cs, dotnet/VisionCore/Security/SecureAssetVault.cs, dotnet/VisionCore.Tests/SecureAssetVaultTests.cs, .github/workflows/windows-build.yml
- Depends on: none

## Section 5
- New: [x] (done 2026-11-02, PR #240) Intellectual property protection and camouflage architecture. Protects proprietary tool names, algorithms, and deep learning models from competitors and plant floor observers in Windows Task Manager, taskbar titles, filesystem folders, and binary export tables. Permanent mandatory security rules codified in CLAUDE.md.

## Summary of Changes
1. **Permanent Security Rule in CLAUDE.md**:
   - Codified mandatory owner directive: process/taskbar camouflage, binary monolith with opaque dispatcher (`corert64.dll`), in-memory model/recipe loading (`SecureAssetVault`), competitor-proof UI terminology, and Single-File compilation. Claude follows this pattern permanently in all future sessions without requiring user reminders.
2. **Native Opaque Dispatcher (`corert64.dll` / `corert.h` / `corert.cpp`)**:
   - Implemented `sys_dispatch(uint32_t op_code, const void* in_payload, size_t in_size, void* out_payload, size_t* out_size)`.
   - Opaque opcodes hide algorithm and tool names from exported symbol tables (`DUMPBIN /EXPORTS`, `nm`, `strings`).
   - Integrated native asset encryption/decryption (`sys_encrypt_asset`, `sys_decrypt_asset`) with zero external runtime dependencies.
3. **In-Memory Asset Vault (`SecureAssetVault.cs`)**:
   - Encrypted `.dat` blob loading using AES-256-GCM.
   - Zero plaintext disk footprint: models and recipe manifests are decrypted strictly in volatile RAM buffers.
   - Dual-tier execution: routes through high-performance `corert64.dll` native cryptor with managed AES-GCM fallback.
4. **Competitor-Proof UI Terminology (`CamouflageLabels.cs`)**:
   - Mappings for HMI, taskbar, and log entries:
     - Thermal Sealing -> "Seal Quality Check"
     - Photometric Stereo -> "Surface Texture Check"
     - Polarimetric Glare Decoupler -> "Anti-Glare Filter"
     - PatchCore Anomaly -> "Pattern Match"
     - Overview Haystack -> "Process Stability Map"
     - Non-Rigid Deformer -> "Pouch Alignment"
   - Safe process titles: `"Machine Control System — Line {N}"` instead of descriptive software brand titles.
5. **CI / CD Pipeline Integration**:
   - Updated `.github/workflows/windows-build.yml` to compile `corert64.dll`, execute `corert_tests.exe`, and bundle `corert64.dll` & `corert.h` into the deployment package.

## Verification
- C++ `corert_tests`: 100% passed (CTest suite 8/8 passed in 92.9s).
- .NET `SecureAssetVaultTests`: 5/5 unit tests passed in 13 ms.
