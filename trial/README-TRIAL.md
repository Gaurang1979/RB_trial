# Vision Software line trial: Micro850 PLC, 4 Hikrobot cameras, 225/min

Part of the **RB_trial** repository: Vision_System `origin/main` commit **8d890165** plus the trial changes below.
Factory rehearsal with a Delta PLC first: `trial/plc/delta-dvp/DELTA-FACTORY-TEST.md`.

Scope: OCV/OCR of the laser-printed batch code, 1D/EAN, 2D code. Line speed 225 products/min
(one every 267 ms). Cameras: 2 × 2 MP + 2 × 0.3 MP Hikrobot GigE. PLC: Allen-Bradley Micro850 with
the existing program `HARPIK08042026_Final_141026`, **unchanged**.

Not built or used in this trial: plant server, licensing (`LicenceRequired=false`), remote support
(`SupportRelay` empty), mobile app.

## Trial files

| Path | What |
|---|---|
| `trial/config/vs-runtime.client-micro850.json` | Runtime settings at the client: Micro850 link on (`RegisterBase` 0), licence off, remote support and plant link off |
| `trial/config/vs-runtime.factory-delta.json` | The same for the factory Delta PLC (`RegisterBase` 4096) |
| `trial/config/Trial 4 cameras OCR EAN 2D.json` | 4-camera job template. Re-teach every ROI, exposure and serial |
| `trial/plc/ab-micro850/` | The client's CCW project (unchanged) and its decoded register map, setpoints, wiring and **safety rules** |
| `trial/plc/delta-dvp/DELTA-FACTORY-TEST.md` | Factory Delta PLC: parts, terminals, ISPSoft, test plan. Program: `plc/delta-dvp/RBTrialDVP.st` |
| `trial/tools/micro850_sim.py` | PLC stand-in (Python 3, no packages) for a bench test: `--base 4096` for the Delta map |

## Why code changes were needed (summary)

1. **PLC link:** the vision software is a Modbus TCP *server* (built for Delta). Your Micro850
   program is also a server, so they cannot talk. New `Micro850Bridge` (vs-runtime): a Modbus TCP
   *client*. It writes PASS → register 3 and FAIL → register 4, toggles the heartbeat (register 43
   bit 1) and logs the PLC alarms.
2. **Hardware trigger:** the runtime fired cameras by software. The PLC fires them itself
   (`DO_00`, `DO_09`, `P1_DO_01` at encoder distances). With `Micro850:Enabled`, the cameras use
   `Line0` and every frame that arrives is a product.
3. **Product = 4 frames:** the PLC sends no product number. The bridge groups the frames by time
   (`CameraDelayMs`, `ToleranceMs`). Each camera is inspected on its own thread.
   - **PASS:** every camera passed, in time.
   - **FAIL:** any camera failed.
   - **Nothing written:** a camera is missing, a frame is ambiguous, or the deadline passed. The
     PLC then rejects the product itself.
4. **4 cameras:** the job limit was 3. It is now 4 (VisionCore `Job.MaxCameras` and the
   vs-studio designer).

| File | Change |
|---|---|
| `dotnet/Runtime.Host/Micro850Bridge.cs` | **New**: options (incl. `RegisterBase` for the Delta map), Modbus client, product grouper, bridge |
| `plc/delta-dvp/RBTrialDVP.st` | **New**: factory Delta program with the Micro850 register map |
| `dotnet/Runtime.Host/Cameras.cs` | Hardware-trigger mode of the camera intake (no software trigger, oldest frame first) |
| `dotnet/Runtime.Host/RuntimeHost.cs` | Registers the bridge; cameras on `Line0` when it is on |
| `dotnet/Runtime.Host/LineRuntime.cs` | Station runs in PLC mode when the bridge is on |
| `dotnet/Runtime.Host/vs-runtime.json` | `Micro850` section, off by default |
| `dotnet/VisionCore/Jobs/Job.cs` | `MaxCameras` 3 → 4 |
| `dotnet/VisionStudio/Services/Station.cs` | Camera locks sized by `MaxCameras` (was a fixed 3) |
| `dotnet/Studio/ViewModels/JobDraft.cs`, `Studio/i18n/en.json` | Designer allows 4 cameras |
| `dotnet/Runtime.Host.Tests/Micro850BridgeTests.cs` | **New**: 7 tests (grouping, missing or double frames, Modbus, fired intake) |
| `dotnet/Studio.Tests/DesignerTests.cs` | Limit test 3 → 4 |

With `Micro850:Enabled=false` (the default), the software behaves exactly as before.

## Step 1. Build (Windows)

Either:

- **GitHub:** RB_trial → Actions → "RB trial Windows installer" → Run workflow. When it finishes,
  download the artifact `VisionSystem-RB-trial-win-x64`. It holds `VisionSystem-Setup-1.0.0.exe`,
  the zip and the build log.
- **Any Windows 10/11 x64 PC:**
  ```
  git clone https://github.com/Gaurang1979/RB_trial.git C:\src\RB_trial
  C:\src\RB_trial\tools\windows\build-windows.bat
  ```
  The script offers to install the tools (VS 2022 Build Tools C++, .NET 10 SDK, Inno Setup).
  The first build compiles OpenCV (20–40 min). The result is in `C:\src\RB_trial\dist`.

The remote-service code is included as plain files (no submodule), so no other repository access
is needed. Put the Hikrobot MVS installer in `third_party\installers\` first if the installer
should carry it.

## Step 2. Line PC network

| Card | PC address | Device |
|---|---|---|
| PLC card | 192.168.1.10/24 | Micro850 192.168.1.5 (port 502) |
| Camera card 1–4 | 192.168.10.1 … 192.168.13.1 /24 | One GigE camera each (192.168.1x.2), jumbo frames 9014, PoE |

Four cameras need four GigE ports (or a 4-port Intel I350 PoE card). The two 2 MP cameras must not
share a port at this speed.

## Step 3. Install and configure

1. Run the installer on the line PC (part 2 of `docs/WINDOWS-INSTALL-AND-TEST.md`). Answer **4 GigE
   cameras** at the pre-install check. When setup asks, give the PLC card and the PLC's IP. Setup
   then sets the card's fixed IP. Its inbound port-502 firewall rule is not used: the PC connects
   out to the PLC. Leave the PLC page's protocol **Off**: the Micro850 link replaces the Modbus
   server.
2. Replace `C:\Program Files\VisionSystem\vs-runtime\vs-runtime.json` (the folder setup used) with
   `trial\config\vs-runtime.client-micro850.json` (factory: `vs-runtime.factory-delta.json`), renamed to `vs-runtime.json`. Keep the `JobsFolder` and `DataFolder` that setup wrote, if they
   differ. Set `Micro850:PlcIp`.
3. Open MVS once and check all 4 cameras. Set each camera's IP, and note its serial.
4. Wire the triggers as in `trial\plc\ab-micro850\MICRO850-REGISTER-MAP-AND-SETTINGS.md` section 5.
5. Restart the `vs-runtime` service. Its log says `Micro850 192.168.1.5:502 connected`.

Bench check without the PLC: on any PC run `python trial\tools\micro850_sim.py` (`--base 4096` for the Delta map), point `PlcIp` at it, and
watch `pass=` and `fail=` count up. It prints `SOFTWARE CRASH` if the heartbeat stops.

## Step 4. Job

1. Copy `trial\config\Trial 4 cameras OCR EAN 2D.json` to `C:\ProgramData\VisionSystem\jobs\`.
2. In vs-studio **Engineer → Designer**, set each camera's serial and exposure. With motion at
   225/min, keep the exposure short (start at 100–200 µs with the PLC-switched light).
3. Teach pictures: the cameras wait for the PLC trigger. Run products past them with the conveyor
   running. With the line stopped in vision, the PLC still rejects every product, so this is safe.
4. Teach the ROIs. Then teach the batch-code OCV text (`B.NO. {BATCH}`, `EXP {EXP:MM/YY}`: edit
   to the real print), the 2D code and the EAN.
5. Keep the time budgets so that each camera finishes **under 100 ms** (the template has 60 ms for
   2D and 30 ms for EAN).

## Step 5. PLC settings check (before running product)

Do the five rules in `trial\plc\ab-micro850\MICRO850-REGISTER-MAP-AND-SETTINGS.md` section 6 with the PLC engineer.
Measure the product pitch in encoder pulses and the ms per pulse at 225/min, then set:

- `ResultNotBeforeMs` > (reg47 − reg0) × ms-per-pulse
- `ResultDeadlineMs` < (reg44 − reg0) × ms-per-pulse, minus about 20 ms margin
- `ToleranceMs` ≤ one quarter of the pitch in ms (about 60 at 225/min; 40 is the default)

## Step 6. Commissioning run (good products)

1. Start the batch and run the line from vs-studio.
2. Open `C:\ProgramData\VisionSystem\data\micro850\products-<date>.csv`. Each row is one product:
   `written` (PASS, FAIL or none), `flag` (ambiguous), `result_ms_after_product`, and per camera
   `result;offset_ms;why`.
3. Set `CameraDelayMs` to the median `offset_ms` of each camera (camera 1 = 0), and restart.
   Rows with `missing` or `ambiguous` must then stop.
4. Check `result_ms_after_product` stays below `ResultDeadlineMs` (and that the PLC rejects
   nothing on good product).

## Step 7. Live test checklist

| # | Test | Expect |
|---|---|---|
| 1 | 500 good products at 225/min | 0 rejects. Product log all PASS, `result_ms_after_product` p99 under the deadline |
| 2 | Batch code missing, wrong digit, smudged (20 each) | All rejected; PLC reject verification OK |
| 3 | EAN missing or covered, 2D missing or wrong (20 each) | All rejected |
| 4 | Cover one camera lens | Every product rejected (that camera fails) |
| 5 | Pull one camera cable | Every product rejected, camera error shown |
| 6 | Pull the PLC cable from the PC | Products rejected; conveyor stops within 5 s (heartbeat) |
| 7 | Stop vs-runtime (service stop) | Conveyor stops within 5 s |
| 8 | Stop inspection in vs-studio, line running | Every product rejected |
| 9 | 10 bad products in a row | Line stops (consecutive rejects, register 39) |
| 10 | 2-hour soak | No missing or ambiguous rows; memory steady |

Send back the product logs, `vs-runtime` log, the build log, the setpoints used and the
results of the table.

## Verified, and not verified

| | |
|---|---|
| Built here (macOS, .NET 10) | vs-runtime and vs-studio build with 0 warnings |
| New tests | 7/7 pass |
| Existing tests | Studio 60/60; VisionCore job tests 109/109 with the 4-camera template loaded. Runtime line tests give the same result as unmodified main (14 need the native engine, which is not built here) |
| Smoke test | vs-runtime against `micro850_sim.py`, Micro850 map and Delta map (`RegisterBase` 4096): connects, writes the setpoint, heartbeat kept |
| Delta program | `RBTrialDVP.st` passes the rusty syntax and type check; not compiled in ISPSoft |
| **Not tested** | Real cameras with hardware trigger, real Micro850 and Delta PLCs, the 4-camera layout of the vs-studio Run screen, Windows build, timing on the line PC. These are the trial's job |

## Known limits of this trial build

- Product pairing is by time only, because the PLC program sends no product number. It is safe in
  the "reject" direction, but needs a steady line speed. If the speed changes much, set
  `CameraDelayMs` again. Section 7 of the PLC sheet gives the permanent fix.
- vs-studio counts each camera's inspection as a part. The product counts are in the product log
  and the PLC counters.
- The vs-studio PLC page shows "PLC off": the Micro850 link is not on that page. Use the
  `vs-runtime` log.
- Teach pictures need a product passing the camera (hardware trigger).
