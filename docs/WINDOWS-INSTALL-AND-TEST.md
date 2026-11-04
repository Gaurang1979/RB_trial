# Windows line PC: build, install and live test

For the owner's test on a Windows 10/11 x64 PC: two cameras (GigE or USB3), the Delta PLC over
Modbus TCP, the light switched by the PLC, at **225 products per minute** (one product every
**267 ms**). "Line PC to get" says what to buy. Part 1 makes the installer, part 2 installs it,
part 3 makes the job, part 4 is the live test checklist and part 5 says what to send back.

## What is in this build, and what is not

| In the package | |
| --- | --- |
| `vs-runtime` | The runtime: cameras, job, PLC link (Modbus TCP server, port 502), results. Runs as the Windows service `vs-runtime`, or in a window with `vs-runtime.exe --console`. |
| `vs-studio` | Vision Studio: run the line, see results, cycle time, NG pictures. Connects to `vs-runtime` on this PC. |
| `teach` | The teach (setup) app: make the job, set the cameras, the PLC and Line & wiring pages. Opens in the browser at `http://127.0.0.1:5090`, on this PC only. |
| Engine | `codereader.dll`, `vs_ocr.dll`, `vs_locate.dll` (OpenCV, zxing-cpp and libdmtx built in; AI models run on OpenCV's DNN module, no separate ONNX Runtime), the C runtime DLLs next to them, and the AI models (`models\*.onnx`). |
| `tools` | `bench.exe`, `run-bench.bat`: read rate and time on this PC with the sample images. |
| .NET | Self-contained .NET 10: nothing to install. |

**This is the owner's test build, not a customer release** (requirements section 11, INS-06). The
customer-release protection of requirements section 18 and the AGENTS.md protection rules is not
built into the code yet, so it is not in this package:
- the single dispatcher DLL instead of the named tool DLLs (`vs_*.dll`);
- encrypted models (open pull request #222);
- Native AOT or single-file publishing with symbols stripped;
- signing;
- the anti-tamper runtime (open pull request #263).

Do not install this package on a customer PC.

**Not in this build (deferred by the owner):**

- **Licence check is off.** Setup writes `Studio:LicenceRequired=false`. No licence server is needed
  for this test. Licensing comes with the Vision Package work (requirements section 10).
- **No signed Vision Package** for other programs (PR #114, #116). This is a plain installer for the
  owner's test PC. The installer is not code-signed: Windows SmartScreen shows "Windows protected
  your PC". Click **More info**, then **Run anyway**.
- **Cameras: Hikrobot only, through the Hikrobot MVS SDK.** That covers Hikrobot GigE Vision and USB3
  Vision cameras. The vendor-free GenICam/GenTL camera layer (`vs_cam`, ARCHITECTURE.md section 3)
  is not built yet, so cameras from other makers do not open.
  The runtime counts missed frames but Vision Studio does not show the count yet; each missed frame
  is an NG product with the reason "No picture from the camera".

## Line PC to get (2 cameras, 225 products per minute)

**These are estimates.** No 225-per-minute timing has been measured on a Windows line PC yet:
that is the work of PR #148 (two-camera line at 225 parts/min, timing budget and soak). They follow
the T1/T2 tiers of the requirements (section 11) and the engine's time budgets. Each product gives
each camera 267 ms for its picture, its inspection and its answer to the PLC. Buy towards the
"recommended" column. The pre-install check refuses a PC below the "refused below" column.

| Part | Recommended | Refused below (pre-install check) |
| --- | --- | --- |
| CPU | Intel Core i7 12th generation or newer, 8+ cores (or Core i5 with 6 performance cores, then check the cycle time) | 4 cores (warning below 8) |
| Memory | 16 GB (32 GB if AI tools are added later) | 8 GB (warning below 16) |
| Disk | 256-512 GB NVMe SSD | 15 GB free (warning below 100 GB; warning on a hard disk) |
| Windows | Windows 10/11 **IoT Enterprise LTSC** (updates held back on a line), or Pro, 64-bit | Windows 10 1809 (build 17763); Home gets a warning |
| GigE cameras | **One Intel GigE port per camera with PoE** (an Intel I210/I350 PoE card, or a PC with PoE ports), jumbo frames 9014 | Warning for a port without jumbo frames or below 1 Gbit/s |
| PLC | **A separate network port** for the PLC | Warning when cameras and PLC must share a port |
| USB3 cameras | One USB3 host controller per camera (a PCIe USB3 card with one controller per port); blue ports on the PC itself, no hub | Refused with USB3 cameras and no USB3 controller |
| Screen | Touch screen, 1920x1080 (or 1366x768) | Warning below 1366x768 |
| GPU | Not needed: the AI models run on the CPU in this build | — |

A fanless industrial box PC or panel PC with these parts and an IP65 front is the usual choice on
a packing line (requirements section 11).

## Part 1. Build the installer (on any Windows 10/11 x64 PC)

This can be the line PC or an office PC. It needs about 15 GB free and an internet connection.

1. Install **Git** (https://git-scm.com, default answers) if it is not there.
2. Get the source: in a command prompt,
   `git clone https://github.com/Gaurang1979/Vision_System.git C:\src\Vision_System`
   (GitHub asks you to sign in; the repository is private).
   Later, to update: `git -C C:\src\Vision_System pull`.
3. Double-click `C:\src\Vision_System\tools\windows\build-windows.bat`.
   - It checks for **Visual Studio 2022 Build Tools** (C++), **Git**, the **.NET 10 SDK** and
     **Inno Setup 6**. If any is missing it lists them and asks
     "Install them now with winget?". Answer **Y** and approve the Administrator prompt.
     If it then says a tool is still not found, close the window, restart the PC and double-click
     again.
   - The first build compiles OpenCV: **20-40 minutes**. Later builds take a few minutes.
   - It runs the engine tests and the .NET tests of the line PC programs. A failed test stops the
     build. Send the log back.
4. When it says **BUILD OK**, the results are in `C:\src\Vision_System\dist`:

| File | Use |
| --- | --- |
| `VisionSystem-Setup-1.0.0.exe` | The installer: copy it to the line PC |
| `VisionSystem-1.0.0-win-x64.zip` | The same, as a zip (use it if Inno Setup is not installed) |
| `build-<date>.log` | Everything the build printed: send it back if the build failed |

## Part 2. Install on the line PC

### 2.1 Before you start

- Windows 10 (1809 or newer) or 11, 64-bit, signed in as an Administrator.
- **Hikrobot MVS** (the camera SDK runtime with the GigE filter driver and USB3 driver), version
  4.8.2 or newer of its camera SDK (tested: MVS 5.1.0 with SDK 4.8.2). Either install it yourself
  (download "Machine Vision Software MVS" for Windows from the Hikrobot download centre, default
  answers), or let the pre-install check install it: put the downloaded MVS `.exe` in
  `third_party\installers\` on the build PC before part 1 (it goes into the installer), or in a
  `prereqs` folder next to `Setup.bat` (zip). The check runs it only if it carries a valid
  Hikrobot signature. If you add a `SHA256SUMS.txt` next to it (`<sha256>  <file name>`), the
  checksum must match too. After the install, open the MVS client once and check that both
  cameras show up.
- Network plan (an example; any free subnets work):

| Card | PC address | Connects to | Device address |
| --- | --- | --- | --- |
| PLC card | 192.168.1.10/24 | Delta PLC (DVP12SE11T) | 192.168.1.5 |
| Camera card 1 | 192.168.10.1/24 | GigE camera 1 | 192.168.10.2 |
| Camera card 2 | 192.168.11.1/24 | GigE camera 2 | 192.168.11.2 |

  One card per GigE camera is best at this speed. USB3 cameras need no card: plug them into a
  **blue USB 3 port on the PC itself**, never a hub, one camera per port.
  If the PLC and cameras share one industrial switch (PLC-DELTA-DVP.md section 1), use one card
  and one subnet, and answer the setup questions with that card for both.

### 2.2 Pre-install check (runs first, by itself)

Owner, 2026-10-30: supporting software and hardware are checked, and missing software installed,
before the vision software is installed. When you start the installer (or `Setup.bat`), a window
asks how many GigE and USB3 cameras the line has (default 2 GigE). Then it checks, before
anything is copied:

| Group | Checked | Minimum (tested) |
| --- | --- | --- |
| Software | Windows 64-bit, build, edition | build 17763 (19045, 22631); IoT Enterprise, Pro or Enterprise |
| Software | Windows PowerShell | 5.1 |
| Software | Hikrobot MVS camera SDK runtime (`MvCameraControl.dll`) | 4.8.2 (MVS 5.1.0): installed from `prereqs` if missing or older, signature checked, then checked again |
| Software | GenICam GenTL producers | listed; not used by this build |
| Software | .NET 10 runtime, Visual C++ runtime | carried by the package: nothing to install |
| Software | ONNX Runtime | not used: the models run on OpenCV in the engine |
| Hardware | CPU, memory, GPU | see "Line PC to get" |
| Hardware | Free space on the system disk, disk type | 15 GB free, SSD |
| Hardware | Network ports: count, link speed, jumbo frames, driver | one per GigE camera + one for the PLC, 1 Gbit/s, jumbo 9000+ |
| Hardware | USB3 controllers | one per USB3 camera |
| Hardware | Screen resolution | 1366x768 |

Each line is PASS, WARN (shown, the install goes on) or FAIL. **One FAIL stops the install: nothing
of the vision software is copied.** Fix it and start again. The window stays open until you press
Enter.

The report of every check, with versions and results, is
`C:\ProgramData\VisionSystem\data\install-report.txt` (and `.json`). Setup adds to it the PLC
answering a ping and the self-test lines (DLLs, models, cameras found, job serials).

### 2.3 Install

1. Run `VisionSystem-Setup-1.0.0.exe` (SmartScreen: **More info**, **Run anyway**). The pre-install
   check (2.2) runs first. Then it installs to `C:\Program Files\VisionSystem`. Leave **Run the
   first-run setup now** ticked.
   With the zip instead: unzip it, right-click `Setup.bat`, **Run as administrator**. It runs the
   pre-install check first too.
2. The first-run setup asks:

| Question | Answer |
| --- | --- |
| Which card goes to the PLC? | The number of the PLC card in the list it shows |
| Which card(s) go to GigE cameras? | Their numbers, e.g. `3,4` (empty for USB3 cameras) |
| Fixed IP for each card | As in the plan, e.g. `192.168.1.10/24` (empty keeps what is set) |
| The PLC's IP address | e.g. `192.168.1.5`: port 502 then answers only the PLC |
| Create an Engineer + Admin user? | **Y**, a short name and a PIN: you sign in with it in Vision Studio |

3. Setup then does this by itself:
   - **Service:** `vs-runtime` starts automatically with Windows (LocalService account) and
     restarts by itself after a crash.
   - **Firewall (group "Vision System"):** TCP port 502 in, only on the PLC card (and only from the
     PLC's IP); the GigE camera stream (UDP) in, only on the camera cards. Nothing else is opened.
   - **Camera cards:** jumbo frames (largest the card offers, 9014 on most), receive buffers to
     maximum, power saving and Energy Efficient Ethernet off.
   - **Power:** High performance plan, no sleep, USB selective suspend off, USB hubs never switched
     off, PCI Express power saving off.
   - **Settings:** jobs in `C:\ProgramData\VisionSystem\jobs`, data and `results.db` in
     `C:\ProgramData\VisionSystem\data`, licence check off.
   - **Desktop shortcuts:** Vision Studio, Teach (setup), Self-test, Collect report.
   - A log of every step: `C:\ProgramData\VisionSystem\setup-<date>.log`.
   Setup can be run again at any time (Start menu, **First-run setup**). It checks before changing.

### 2.4 Camera IP addresses (GigE only)

In the MVS client, for each camera: right-click it, **Modify IP**, choose **Static IP**, and enter
its address from the plan (e.g. `192.168.10.2`, mask `255.255.255.0`, gateway empty). Then
**Persistent IP**, so it keeps the address after a power cycle. Close the MVS client afterwards:
a camera opened in MVS cannot be opened by Vision System.

Write down each camera's **serial number** (MVS shows it). The job needs it in part 3.

### 2.5 Self-test

Double-click **Vision System - Self-test**. Every line should start with `OK`:

```
OK    codereader.dll
OK    vs_ocr.dll
OK    vs_locate.dll
OK    engine version: 0.1.0
OK    codenet.onnx (code locator)
OK    enhancer.onnx (code enhancer)
OK    ocrnet.onnx (OCR reader)
OK    jobs folder: C:\ProgramData\VisionSystem\jobs
OK    data folder: C:\ProgramData\VisionSystem\data
OK    camera: Camera DA1234567 (serial DA1234567)
OK    camera: Camera DA7654321 (serial DA7654321)
INFO  PLC port 502: in use: normal while the vs-runtime service runs ...
self-test passed
```

The result is also saved in `C:\ProgramData\VisionSystem\selftest.txt`.

**When the self-test fails:**

| FAIL line | What to do |
| --- | --- |
| `xxx.dll: missing` or `does not load` | Install again; if it stays, send `selftest.txt` back |
| `xxx.onnx: missing` | Install again (the models are in `vs-runtime\models`) |
| `folder cannot be written` | Run First-run setup again (it sets the folder rights) |
| `camera driver: MVS camera runtime not installed` | Install Hikrobot MVS (2.1), restart the PC |
| `cameras: none found` | Power and cable; GigE: card IP and camera IP in the same subnet (2.4); close the MVS client |
| `job ..., Camera 2: serial ... not found` | The serial in the job differs from the camera's: fix it in Vision Studio's Engineer workspace (part 3) |

## Part 3. Make the two-camera job and set up the PLC

1. **The job, in Vision Studio** (PR #147). Sign in as the Engineer from 2.3 and open the
   **Engineer workspace**. Type a name and press **New job**: a new job has two cameras. For each
   camera enter its **Camera serial number** from 2.4 (with two cameras, never leave it empty), its
   exposure, and its tools. Try the tools on pictures, then **Save**. The runtime service keeps
   running for this.
2. **The PLC and line settings, in the teach app.** Vision Studio does not have these pages yet.
   Double-click **Vision System - Teach (setup)**. It stops the `vs-runtime` service (the cameras
   and port 502 belong to one program at a time), opens the teach app in the browser, and starts
   the service again when you close the teach app's black window.
3. **PLC page:** protocol **Modbus TCP**, port **502**, watchdog **1000 ms**, PLC model
   **Delta DVP12SE11T** (or your AS model). Save.
4. **Line & wiring page:** cameras along the line **2**; camera 2 triggered by its own sensor or by
   camera 1's sensor (front and back of the pack); camera trigger delays; **light start delay and
   on time** (start with on time **0** = light on while the line runs, then shorten it, see 4.5);
   reject timing (PLC-DELTA-DVP.md section 6).
5. **PLC program:** load `plc/delta-dvp/VisionLineDVP.st` (or `plc/delta-as/VisionLine.st`) in
   ISPSoft / ISPSoft 3 and set the Ethernet data exchange to the vision PC's PLC-card address,
   port 502 (PLC-DELTA-DVP.md section 4). Do the commissioning checklist in PLC-DELTA-DVP.md
   section 8 first.
6. Close the teach app's window. Run **Self-test** again: the job lines must say
   `serial ... found` for both cameras.
7. **Camera timing without the line:** run `Self-test.bat grab 300` from the install folder
   (Start menu: **Self-test**, or a command prompt in `C:\Program Files\VisionSystem`). It fires each
   camera 300 times and prints the frames missed and the trigger-to-frame time. Expect
   `0 missed` and a 95 % time well under 100 ms.

## Part 4. Live test at 225 products per minute

At 225 per minute a product comes every **267 ms**. Each camera must take its picture, inspect it
and answer the PLC well inside that time, every time. The PLC rejects a product that a camera
calls NOT OK, that a camera does not answer within 1 s, or that a camera never saw.

Open **Vision Studio**, sign in with the user from 2.3, choose the job and start. Start the line
from the PLC. Fill in the table as you go; it is what we need back.

### 4.1 Warm-up (slow)

- [ ] Line at about a third of the speed for 5 minutes, good products only. Every product **OK**
      on both cameras; Vision Studio's count equals the PLC's part count.
- [ ] Pictures on both cameras are sharp and evenly lit; the code is in the middle of the view.

### 4.2 Speed: 225 products per minute, 30 minutes (about 6,750 products)

| Check | Where | Pass | Result |
| --- | --- | --- | --- |
| Frames missed | NG products with the reason "No picture from the camera" (Vision Studio NG list; each is also in `results.db`) | **0** | |
| Cycle time | Vision Studio **Cycle time** and its **max** | typical ≤ 200 ms, **max < 267 ms** | |
| Products counted | PLC part count vs Vision Studio count (each camera) | equal | |
| Good products NG | Vision Studio NG count with good products only | 0 (each NG: note the reason) | |
| Not answered in time | PLC diagnostics: camera timeouts | 0 | |
| CPU | Task Manager, whole PC | < 70 % | |
| Memory | Task Manager, `vs-runtime.exe` | steady (not climbing) | |

### 4.3 NG and reject check (the line must never pass a bad product)

- [ ] Mix in **20 bad products** (code covered, wrong code, or wrong batch in Start batch), spread
      over a running batch at full speed. **Every one** is rejected and seen in the chute; no good
      product is rejected.
- [ ] Bad on **camera 2 only** (cover the code seen by camera 2): exactly that product is rejected.
- [ ] Two products touching: both rejected with the reason "touching products".

### 4.4 Watchdog and failures (each must reject or stop, never pass)

- [ ] Pull the **PLC's Ethernet cable** while running: heartbeat lost within **1 s**, the line
      stops, red lamp. Plug it back: the line can be started again.
- [ ] Stop the service while running (`sc stop vs-runtime` in an Administrator prompt): the PLC sees
      the vision heartbeat stop, products in between are rejected, the line stops. `sc start
      vs-runtime`: Vision Studio reconnects.
- [ ] End `vs-runtime.exe` in Task Manager while running: same as above, and the service restarts
      by itself within about 5 s.
- [ ] Pull **one camera's cable** while running: its products are NG ("No picture from the
      camera") and rejected; the line is told. Plug it back and restart the job.
- [ ] Restart the PC: `vs-runtime` starts by itself; Vision Studio connects; the job runs.

### 4.5 Light timing (light switched by the PLC)

- [ ] With light on time **0** (on while the line runs): all pictures evenly lit. This is the
      reference.
- [ ] Set the **light start delay** and **on time** on the Line & wiring page. The PLC triggers the
      camera over Modbus TCP, which adds up to about 20 ms of jitter, so the on time must cover the
      camera trigger delay + 30 ms + the exposure time. Run 5 minutes at full speed: **no dark or
      half-lit pictures** (look at the NG pictures in Vision Studio: dark pictures show as
      "no code found"). If any, lengthen the on time.
- [ ] Light on time + start delay is shorter than 267 ms, so the light is off between products.

### 4.6 Bench on this PC (read rate and time with the sample images)

Copy the sample images to a local folder (for example `C:\VS_IMAGES`; customer images stay on
this PC, never in the repository). Then in a command prompt:

```
cd "C:\Program Files\VisionSystem\tools"
run-bench.bat "C:\VS_IMAGES"
```

It writes `bench-result.txt` and `bench-result.csv` in that folder (Collect report picks them up).

## Part 5. What to send back

Double-click **Vision System - Collect report** after the test (stop the line first; it stops the
service for a few seconds for a clean copy of the results). It makes
`VisionSystem-report-<PC>-<date>.zip` on the desktop with:

- `VERSION.txt` (build and commit), a fresh `selftest.txt`, the setup logs;
- `data\install-report.txt` and `.json` (pre-install check, PLC, self-test: every check with versions);
- `data\results.db` (every inspection with its time and result), `plc.json`, `line.json`;
- the job files (without their pictures);
- `events.csv` (vs-runtime warnings and errors, crashes) and `service-events.csv`;
- `pc.txt` (CPU, memory, network cards with jumbo frame and buffer settings, firewall rules, power
  plan);
- `bench-result.txt` / `.csv` if you ran 4.6.

It holds no product images and no user or token files. Send the zip, the filled-in tables of
part 4, and a few NG pictures (Vision Studio NG viewer, Save) if something was rejected that should
not have been.

**For more detail** (when asked): stop the service and run the runtime in a window, writing
everything it prints to a file that Collect report picks up:

```
sc stop vs-runtime
cd "C:\Program Files\VisionSystem\vs-runtime"
vs-runtime.exe --console > "C:\ProgramData\VisionSystem\data\console.log" 2>&1
```

Stop it with Ctrl+C, then `sc start vs-runtime`.

## Removing it

Start menu or Settings, **Apps**: uninstall **Vision System**. That removes the service and the
"Vision System" firewall rules. Jobs and results stay in `C:\ProgramData\VisionSystem`. Network
card and power settings stay as set.
