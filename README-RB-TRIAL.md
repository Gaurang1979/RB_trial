# RB_trial: Vision Software line trial (factory Delta PLC, then client Allen-Bradley Micro850)

Private trial repository, made 2026-11-04. It holds the complete, buildable Vision Software:
Vision_System `main` at commit 8d890165, plus the trial changes. It is separate from the
Vision_System repository. Changes made here during the trials do not go back to Vision_System
unless they are moved over on purpose.

## What the Vision Software is (all in this repository)

| Part | Where | What it does on the line |
|---|---|---|
| Inspection engine (C++) | `engine/` → `codereader.dll`, `vs_ocr.dll`, `vs_locate.dll` | 1D/EAN, 2D (DataMatrix, QR, GS1) reading and grading; OCR/OCV of laser-printed batch codes; Locate |
| **vs-runtime** (service) | `dotnet/Runtime.Host` → `vs-runtime.exe` | Cameras, job, PLC link (Micro850/Delta trial link), results, logs |
| **vs-studio** (operator / engineer UI) | `dotnet/Studio` → `vs-studio.exe` | Run screen, results, NG pictures, batch start, job designer with 4 cameras |
| **teach** app (setup UI) | `dotnet/VisionStudio` → `teach` at `http://127.0.0.1:5090` | Cameras, jobs, Line and wiring pages |
| Camera adapter | `dotnet/HikCamera` + `third_party/hikrobot-mvs-sdk` | Hikrobot GigE / USB3, hardware trigger on Line0 |
| Installer | `tools/windows/` → `VisionSystem-Setup-1.0.0.exe` | Pre-install check, setup, self-test |
| AI models | `ml/models/*.onnx` | OCR models (run in the engine via OpenCV DNN) |
| PLC programs | `plc/delta-dvp/RBTrialDVP.st` (factory); `trial/plc/ab-micro850/` (client, unchanged) | Sensor, encoder tracking, camera triggers, light, reject, heartbeat |

Inspection in this trial: OCV/OCR of the batch code, 1D/EAN, 2D code. 4 cameras (2 × 2 MP,
2 × 0.3 MP), 225 products/min. Not used: plant server, licensing (`LicenceRequired=false`), remote
support (`SupportRelay` empty), mobile app. Their code is present, but switched off.

## The `.exe` files

The engine is C++ and needs Microsoft's compiler, so the installer is built on Windows:

1. **GitHub Actions:** Actions → *RB trial Windows installer* → *Run workflow*. Download the artifact
   `VisionSystem-RB-trial-win-x64` (installer, zip, build log). It also runs on every push to `main`.
2. **Any Windows 10/11 PC:** `git clone https://github.com/Gaurang1979/RB_trial.git C:\src\RB_trial`.
   Download the Hikrobot MVS installer into the build:
   `gh release download mvs-5.1.1 -R Gaurang1979/RB_trial -D C:\src\RB_trial\third_party\installers`
   (or RB_trial → Releases → `mvs-5.1.1`). Then double-click `tools\windows\build-windows.bat`.
   The output goes to `dist\`.

## Trial steps

1. **Factory, Delta PLC:** `trial/plc/delta-dvp/DELTA-FACTORY-TEST.md` (wiring, ISPSoft, test plan),
   with `trial/config/vs-runtime.factory-delta.json`.
2. **Client, Micro850:** `trial/README-TRIAL.md` (install, job, commissioning, live checklist),
   with `trial/config/vs-runtime.client-micro850.json` and
   `trial/plc/ab-micro850/MICRO850-REGISTER-MAP-AND-SETTINGS.md`.

The vision PC runs the same code in both places. The two PLCs use the same register map; only
`Micro850:RegisterBase` differs (4096 for Delta, 0 for Micro850).

## Changing things during the trial

Work on a branch, open a pull request in this repository, and merge after the change is tested.
The GitHub build makes a new installer from `main`.

The files `CLAUDE.md`, `AGENTS.md`, `.claude/` and `scripts/coord.sh` describe the Vision_System
team process (claims, change notes, roll-up). They came with the source and do not apply here.
Use the simple branch-and-pull-request flow above.
