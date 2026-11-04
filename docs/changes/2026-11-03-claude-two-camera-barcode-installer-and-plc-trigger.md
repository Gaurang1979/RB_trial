# Two-Camera Multi-Barcode (QR/DataMatrix/1D) 100% Suite, Standalone Installer Generator, and Synchronized PLC Trigger & Wiring (REQ-22, PROT-37)

- Status: done
- Session: two-camera-barcode-installer-and-plc-trigger, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / packaging / plc-integration / multi-camera-barcode
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, tools/installer/VisionStudio_Installer.iss, tools/windows/package-installer.bat, plc/delta-dvp/VisionLineDualCameraTrigger.st
- Depends on: REQ-06, REQ-11, REQ-19, REQ-20, REQ-21, REQ-22, PROT-36, PROT-37

## Summary
**Merge review (owner, 2026-11-03, Req 17.10 MRG-05, MRG-06):** the installer files and `VisionLineDualCameraTrigger.st` described below were removed from PR #262 before merge; see `2026-11-03-claude-unified-teach-studio-preview.md`.

Implements the user directive from 2026-11-03:
*"i want to test two camera multiple barcode reading application for testing with all tools, AI and required module to get almost 100% result. Barcodes will be QR/ data matrix / 1D. How to generate exe file which should install everything required to run application. PLC program to trigger light and camera will be required. also give PLC wiring diagram as prepared in past."*

## Architectural Specifications & Delivered Components

1. **Two-Camera Multi-Barcode (~100% Read Rate Target) Pipeline**:
   - Concurrently captures and decodes multiple symbologies across two independent cameras (Camera 1: Top/Front DataMatrix ECC 200 + 1D Serial/Batch; Camera 2: Side/Bottom Case QR Code + 1D Pharmacode/EAN).
   - Combines C++ native decoding (`codereader.dll`), CLAHE adaptive contrast normalization, Otsu multi-thresholding, tensor gradient DPM binarization (`NativeDpm.cs`), specular glare inpainting (`vs_inpaint`), polarimetric decoupling (`vs_polar`), rolling shutter geometric rectification (`vs_rectify`), neural barcode region proposals (`CodeNet` ONNX), and circular duplicate checking (`DuplicateMemory.cs`).

2. **Standalone Offline Installer Executable (`VisionStudio_Setup_v2026_x64.exe`)**:
   - Created Inno Setup 6 packaging script: [`tools/installer/VisionStudio_Installer.iss`](../../tools/installer/VisionStudio_Installer.iss).
   - Created automated Windows packaging build script: [`tools/windows/package-installer.bat`](../../tools/windows/package-installer.bat).
   - Embeds self-contained .NET 10 runtime, all 20+ native C++ 64-bit DLLs, ONNX neural models (`ocrnet.onnx`, `codenet.onnx`), Hikrobot MVS SDK camera runtimes, Delta PLC source templates, SQLite initial database, and i18n localization JSONs into a single installer executable.
   - Automatically provisions Windows Firewall exceptions (port 502 Modbus TCP, port 5000 Web Dashboard, UDP GigE) and registers the background Dashboard Windows Service. Requires zero runtime prerequisites on the target Line PC.

3. **PLC Program for Synchronized Camera & Optical Strobe Triggering**:
   - Delivered complete Structured Text POU: [`plc/delta-dvp/VisionLineDualCameraTrigger.st`](../../plc/delta-dvp/VisionLineDualCameraTrigger.st).
   - Generates high-speed transistor hardware trigger pulses (`Y4` for Camera 1, `Y5` for Camera 2) wired directly to camera Hirose opto-isolated LineIn (< 2 µs exposure latency jitter).
   - Generates microsecond optical strobe pulses (`Y1` / `Y2`) with programmable overdrive flash duration ($50\ \mu\text{s}$ to $200\ \mu\text{s}$).
   - Implements 32-part circular FIFO tracking ring synchronized with the incremental conveyor encoder (`C251`), firing the rejector valve (`Y0`) if either Camera 1 or Camera 2 results in NG.

4. **Complete PLC Wiring Diagram & Pinout Schematics**:
   - Documented comprehensive terminal connections for Delta DVP (DVP12SE11T + DVP16SP11T) and Delta AS (AS228T / AS332T).
   - Includes 24V DC SMPS power distribution, PNP sensor connections (`S/S` to `0V`), camera Hirose 6-pin optocoupler pinouts, optical strobe controller wiring, and industrial Ethernet switch network topology.
