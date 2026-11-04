# Vision_System — Sundaram plant platform

One project for the whole plant (owner, 2026-10-29): the line PC vision
software (code reading, OCR / OCV, Vision Studio), the plant server
(`plant_server/`: Production Measurement, and the home of every product's
data), and one mobile app. Design: [docs/vision-system/PLANT-PLATFORM.md](docs/vision-system/PLANT-PLATFORM.md).

## Barcode_Reading (line PC code reader)

An aggregation code reader for pharma and liquor track and trace. It reads every QR or
Data Matrix code in one 20 MP image of an open case, on an Intel i5/i7 PC with no GPU, and
hands the results to your .NET line software.

Plan and design: [AI Code Reader for Aggregation](https://claude.ai/artifact/17kZ3REP3ranSQVuUwTyjG).

## How it reads a case

1. The operator sets the code type and the number of codes per case at the start of a batch.
2. A fast locator finds code-like regions on a 1/4-size image (8–20 ms).
3. Regions are decoded in parallel on every CPU core, best first. Each region gets a short
   chain of image fixes (stretch, upscale, contrast, sharpen, threshold) until one reads it.
4. Decoding stops as soon as the expected count is reached.
5. If the case is still short, whole-image passes run as a fallback, and the regions that
   did not read are reported as misses.

The parent code on the case side (2 MP side camera) uses a second reader with its own
settings; 1D formats (GS1-128 / SSCC, EAN-13) skip the locator and read the whole image.

## Layout

| Path | What |
| --- | --- |
| `engine/` | C++17 engine (OpenCV + zxing-cpp), built as `codereader` shared library |
| `engine/include/codereader.h` | Plain C API, called from .NET with P/Invoke |
| `tools/bench/` | Benchmark: read rate and time over a folder of images |
| `truth/samples.csv` | Expected code count and type per sample image |
| `dotnet/CodeReader/` | .NET 10 wrapper: `CodeReader`, `ReaderSettings`, GS1 parser |
| `dotnet/CodeReader.Tests/` | Unit tests for the wrapper |
| `dotnet/ReaderDemo/` | Console demo: decode one image as the line software would |
| `dotnet/HikCamera/` | Hikrobot camera adapter (MVS SDK): trigger, strobe, frame to engine without copying |
| `dotnet/LineRunner/` | Station runner: top camera (child codes) + side camera (parent code), one line per case |
| `dotnet/Dashboard/` | Web dashboard and REST API (see below); tests in `dotnet/Dashboard.Tests/` |
| `proto/visionstudio/remote/v1/` | gRPC contract between the runtime service and Vision Studio, the browser dashboard and plant software (see below) |
| `dotnet/RuntimeContract/` | Generated client and server code, local endpoint (named pipe / Unix socket), access rules, in-memory stub runtime |
| `dotnet/RuntimeStub/` | `vs-runtime-stub`: the stub runtime on the local endpoint, for building Vision Studio without cameras |
| `engine/include/vs_ocr.h` | OCR / OCV tool C API (`vs_ocr` shared library): read text, check text |
| `engine/src/ocr/` | Line finder (rotation, slant, polarity, dot joining), CTC recogniser, expected-text check |
| `tools/bench/ocr_bench.cpp` | OCR / OCV benchmark: read accuracy, check rate, false accepts, time |
| `ml/ocr/` | OCR recogniser training: synthetic laser / TTO / CIJ lines, real-line labelling, ONNX export |
| `dotnet/VisionCore/` | Tools (Read code, Read text, Check text), jobs, job runner, neutral camera interface |
| `dotnet/StationGuard/` | Keeps the line PC fast and stable: memory, CPU and disk watch, leak check, step-by-step clean-up between parts, NG images with counts |
| `dotnet/VisionStudio/` | Base vision software: run, start batch, teach, image test, jobs (up to 3 cameras) |
| `docs/vision-system/` | Design documents of the vision system (start with its README) |
| `plant_server/` | The plant server (Python, FastAPI, PostgreSQL): Production Measurement today (edge agent, simulator, derivation, OEE, alerts, accounts, Web Push, live dashboard); the Vision and printing modules join it (PLANT-PLATFORM.md). Its own README tells how to run it, from inside plant_server/ |
| `third_party/remote-service/` | Remote service module (git submodule, Gaurang1979/remote-service): the agent vs-runtime hosts, the relay at support.sundaramtech.com and the ERPNext app; engineers work on a line PC from a browser with no remote-desktop software ([REMOTE-SUPPORT.md](docs/vision-system/REMOTE-SUPPORT.md)). Clone with `git clone --recurse-submodules`, or run `git submodule update --init` |
| `dotnet/PlantEvents/` | Plant event schema v1: what a station sends to the plant server (JSON, validation) |
| `dotnet/StationAgent/` | Station agent: store-and-forward queue on the line PC (SQLite), gap-free sequence, send loop to the plant server |
| `docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md` | Plant Insight: live plant dashboard for every line and the mobile app (design) |
| `mobile/plant_core/` | Shared core of the plant app: light and dark theme, click and alarm sounds, charts (ring, OEE gauge, trend, bars, timeline, sparkline), server list |
| `mobile/plant_app/` | The plant app (Flutter, Android and iOS): Vision module now, Production Measurement later |
| `design/prototypes/plant-insight/` | Clickable UI design of the plant dashboard and mobile app |
| `docs/PROJECT-HISTORY.md`, `docs/changes/` | Project state, decisions, open items; one change note per pull request |
| `AGENTS.md` (imported by `CLAUDE.md` and `GEMINI.md`), `.claude/`, `scripts/` | The one rule file for every agent (Claude, Antigravity, Gemini, Codex): claims, overlap checks, test once, owner-only merge |

## How we work

Every change goes through a pull request, claimed first and tested once by the session that
made it; it is marked ready only when that test passed, and only the owner merges. The rules are in
[AGENTS.md](AGENTS.md), the state and open items in [docs/PROJECT-HISTORY.md](docs/PROJECT-HISTORY.md)
and the code rules in [docs/CODING-STANDARDS.md](docs/CODING-STANDARDS.md).

Sample images are customer data and are not stored in this repository. Keep them in a
local folder and pass its path to the tools.

## Build

### macOS (development)

```bash
brew install cmake ninja opencv zxing-cpp
cmake -S engine -B engine/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build engine/build
```

### Windows (production PC)

On a Windows 10/11 x64 PC, double-click `tools\windows\build-windows.bat` (it runs
`build-package.ps1`). It checks Visual Studio Build Tools (C++), Git, the .NET 10 SDK and
Inno Setup and offers to install what is missing with winget, builds the engine through vcpkg
(the first time 20–40 minutes for OpenCV), runs the engine and line PC .NET tests, publishes
`vs-runtime`, `vs-studio` and the teach app self-contained for win-x64, and writes to `dist\`:
`VisionSystem-Setup-<version>.exe` (Inno Setup), `VisionSystem-<version>-win-x64.zip` and the
build log. Installing, first-run setup (service, firewall, network cards, power, self-test) and
the live test checklist: [docs/WINDOWS-INSTALL-AND-TEST.md](docs/WINDOWS-INSTALL-AND-TEST.md).

GitHub Actions (`.github/workflows/windows-build.yml`) builds the older `codereader-win-x64`
artifact when the account has Actions minutes (stopped by billing since 2026-10-30). The engine
is not cross-compiled from macOS: it is built with MSVC on Windows only. The package's
`tools\run-bench.bat` measures read rate and time on the line PC:

```bat
run-bench.bat "C:\path\to\Barcode Images"
```

and send back `bench-result.txt` and `bench-result.csv`.

### .NET wrapper

```bash
dotnet test dotnet/CodeReader.Tests
dotnet build dotnet/ReaderDemo -c Release
```

## Use from C#

```csharp
using BarcodeReading;

using var reader = new CodeReader(new ReaderSettings
{
    Format = CodeFormat.QrCode,   // operator input
    ExpectedCount = 45,           // operator input: codes per case
    TimeBudgetMs = 200,
});

DecodeResult r = reader.Decode(monoPixels, width, height, stride);  // Mono8 camera buffer
foreach (var code in r.Codes)
    Save(code.Text, code.Gs1?.Gtin, code.Gs1?.Batch, code.Gs1?.Serial, code.Corners);
if (!r.Stats.Complete)
    Reject(r.Misses);  // regions that looked like codes but did not read
foreach (var why in r.MissInfo)  // why each did not read: Blur, Glare, TooSmall, ... (docs/QUALITY.md)
    Log(why.Reason, why.Tooltip);
```

One `CodeReader` per camera job; it is not thread safe, but uses all cores internally.

## Cameras (Hikrobot MV-CS200-10UM)

`HikCamera` uses the official Hikrobot .NET SDK (`MvCameraControl.Net.dll`, MVS 5.1.0 /
camera SDK 4.8.2), whose developer files are in `third_party/hikrobot-mvs-sdk`. Each line PC
still needs "Machine Vision Software MVS" for Windows (or Hikrobot's SDK runtime package)
installed for the USB3 camera driver, from the
[Hikrobot download center](https://www.hikrobotics.com/en/machinevision/service/download/).

On open, the adapter sets Mono8, fixed exposure and gain, one frame per rising edge on Line0
(the Hirose I/O, from the case sensor or PLC) with 50 µs debounce, and drives Line1 during
exposure to fire a strobe. Each frame's SDK buffer goes straight to the engine, and frames
that come up short can be saved as BMP for the learning service.

Code type, codes per case (12, 24, 45, 48 or 96) and the camera settings come from the
product master (`ProductMaster`, `ProductStore`): one JSON file per product, with every saved
version kept under `history/` for the audit trail. Your database can replace it by
implementing `IProductStore`.

```bat
LineRunner --list
LineRunner --products D:\products --add-product LIQ-750-48 --name "Whisky 750 ml" --format qr --count 48 --exposure 3500 --user gaurang
LineRunner --products D:\products --list-products
LineRunner --products D:\products --product LIQ-750-48 --child <serial> --parent <serial> --hard-cases D:\hard-cases
```

Add `--software` to trigger from the keyboard on a bench without a sensor. The IMX183 has a
rolling shutter: the case must be stopped, or lit only by a short strobe, while the image is
taken.

## Web dashboard

`dotnet/Dashboard` (ASP.NET Core + Blazor) runs on the line PC as a Windows service and is
opened from any browser on the plant network: `http://<line-pc>:5080`.

| Page | Who | What |
| --- | --- | --- |
| Live | everyone; start/stop: Operator | Pick the product, start camera or replay; OK / REJECT, codes read, time, label brightness, parent code; the case image with read codes in green and unread regions in red, each tagged with why it did not read (hover for the numbers); counts of those reasons since the start; the advisor's advice; GS1 fields of every code |
| Products | edit: Engineer / QA | Product master: code type, codes per case (12/24/45/48/96), parent code, GTIN, camera settings, reader settings with an Auto / Manual switch each; Learning or Locked |
| Tuning | apply: Engineer | What the tuning agent learned per setting; save it as a new product version |
| Trends | everyone | Read rate and median time per hour, recent cases |
| AI models | promote: QA | Models in use, the retraining agent's candidates with their test results, promotion |
| Audit trail | everyone | Every sign-in, start, stop, save, signature (and failed attempts); product versions with field-by-field changes |

Locking a product, changing a locked one and promoting a model need a QA user to re-enter
their password and give a reason (e-signature, kept in the product version and audit trail).
Passwords are stored as PBKDF2 hashes in `data/users.json`. Every short case is saved to
`data/hard-cases/<product>/` for the retraining agent, written off the inspection thread
(oldest deleted beyond `HardCaseLimit` images or `HardCasesMaxGb`, all products together).
The resource guard (`dotnet/StationGuard`) watches the station's memory (`MemoryBudgetMb`,
0 = half the PC's), the PC's memory and the data disk: under pressure only a few hard cases
are saved, or none, and the Live page says so; verdicts never change. `GET /api/health`
gives the guard's status and limits and the case counts by reason.

Settings are in `appsettings.json` (section `Station`): camera serial numbers, replay
folder, `ApiKey` for your own software, folders. Relative folders are next to the program.

```bat
Dashboard.exe add-user admin "Plant Admin" Operator,Engineer,QA
install-dashboard-service.bat      (as Administrator, in the Dashboard folder)
```

REST API for your software (signed-in cookie, or header `X-Api-Key`, which acts as
Operator + Engineer; locked products cannot be changed through the API):

| Method | Path | |
| --- | --- | --- |
| GET | `/api/station` | Running state, last case with codes and GS1 fields, advice |
| POST | `/api/station/start` | `{"product": "LIQ-750-48", "source": "Camera"}` (409 with the reason if refused) |
| POST | `/api/station/stop` | |
| GET | `/api/station/preview.jpg` | Last case image with overlays |
| GET | `/api/products`, `/api/products/{code}`, `/api/products/{code}/history` | Product master |
| PUT | `/api/products/{code}` | Save a new version (Engineer) |
| GET | `/api/cases?limit=&product=&afterId=`, `/api/cases/{id}/codes` | Case log and the codes in a case |
| GET | `/api/trends?product=&hours=` | Per-hour read rate and median time |

## Runtime contract and stub runtime

The runtime service, Vision Studio (Avalonia) and the view-only browser dashboard talk through
one gRPC contract, `proto/visionstudio/remote/v1/runtime.proto` ([ARCHITECTURE.md](docs/vision-system/ARCHITECTURE.md)
section 2): services `Session` (sign in with a PIN or password, users for the sign-in tiles) and
`VisionRuntime` (status and `WatchStatus`, jobs and products, `Start` / `Stop` / `ChangeJob`,
results, `WatchFrames` with downscaled JPEG and vector graphics, counters). On the PC it runs over
a named pipe (`vs-runtime`) on Windows and a Unix socket (`$TMPDIR/vs-runtime.sock`) elsewhere;
`VS_RUNTIME_ENDPOINT` overrides it. Who may change what is decided by the runtime: a browser
session never, plant software only with an operate token, setup only in Vision Studio.

Until the runtime service exists, `vs-runtime-stub` serves the whole contract with made-up
inspections:

```bash
dotnet run --project dotnet/RuntimeStub -- --frames <folder of .jpg> --start
```

Users: `operator` (PIN 1234), `supervisor`, `engineer`, `admin` (password = name); plant tokens
`stub-plant-operate` and `stub-plant-read`. Type `d` and Enter to drop every connection for 3 s
(to try a client's reconnect), or pass `--drop-every-s 30`. JPEG frames can be made from sample
images with `sips -s format jpeg -Z 1280 <image> --out <folder>/<name>.jpg`; they are customer
data, so keep them outside the repository.

## Vision Studio: barcode + OCR / OCV (first version)

The first piece of the vision system in `docs/vision-system/`. One job holds up to three cameras;
each camera has its own tools; all cameras fire together and the part is OK only when every
camera is OK.

| Tool | What it does |
| --- | --- |
| Read code | QR, Data Matrix, EAN-13, Code 128 / GS1-128 with the `codereader` engine; optional expected content from batch fields (`{GTIN}`) |
| Find the part | Finds a taught pattern (logo, barcode, printed patch) in each picture; the camera's other tools then move and turn their areas with the part. Part not found = NOT OK |
| Read text (OCR) | Finds every line in the area, straightens rotation and slant, reads it |
| Check text (OCV) | Checks each printed line against what should be printed; one bad character fails the part |

Check-text lines are entered in one of two ways, per line:

- **Template**: fixed text with fields, e.g. `₹ {MRP}, {BATCH}` and `¥ {MFD}, # {EXP} {TIME:HH:mm+-3}`.
  The operator enters the fields on the Start batch screen; `{TIME}` and `{DATE:MM/YY+730d}` come
  from the clock (stored images: the time in the file name).
- **Full text**: the operator types the whole line at batch start (`?` = any one character).

Print types: laser and TTO (solid strokes) and CIJ dot matrix (dots joined before reading; missing
dots, stretch and slant are tolerated). How the check works: the recogniser's per-column character
probabilities are force-aligned to the expected text (CTC), so each expected character gets its own
score 0..100; the line fails when any character is below the strictness setting, and the free read
is shown next to it ("Expected HWN937, found HWN881").

PLC: Modbus TCP server (the vision PC is the server; the PLC writes holding registers 0–31 and
reads 100–227) with the handshake of `docs/vision-system/PLC-INTERFACE.md`: Trigger → Busy → Done +
OK/NG → Trigger off, job change by number, alarm reset, run enable, two-way heartbeat with a
watchdog (error 100), trigger rejected while busy (110) or not ready (111). Results: tool pass bits,
the first failing tool, cycle time, the code read (Text1) and the first text line (Text2). Settings,
live signals, test OK / NOT OK and the register map are on the PLC page; **Start (PLC)** on the Run
page waits for PLC triggers, and **Start PLC test with stored pictures** sets the Simulation bit.
Siemens S7, Mitsubishi MC and Omron FINS clients, counter mode and the mapping screen come next.

Cameras: GigE and USB3 through a neutral driver interface (`VisionCore/Cameras.cs`). This build
has one driver (key `c1`); more makes are added as further drivers. Cameras are shown only by the
name stored in the camera and the serial number, never by maker or model, and every setting
(exposure, gain, packet size, trigger) is made by the software. Triggers are software triggers;
the PLC trigger comes with the PLC interface (`docs/vision-system/PLC-INTERFACE.md`).

### Run it

```bash
cmake --build engine/build --target codereader vs_ocr
dotnet run --project dotnet/VisionStudio -c Release
```

Open http://localhost:5090. `appsettings.json` has `"OpenMode": true` (no sign-in) for bench PCs;
set it to `false` on the line and add users with
`VisionStudio add-user <name> "<full name>" Operator,Engineer,QA <password>`.

1. **Teach**: load a stored picture (or take one), add a tool, drag the area, press **Auto-find**
   (finds the code type, or reads the text lines and proposes templates; a printed time becomes
   `{TIME:HH:mm+-3}`). Replace the parts that change with fields such as `{BATCH}`, press **Try**, then **Save**.
2. **Run**: choose the job, **Start batch** (enter the fields), then **Start** (cameras) or
   **Start (stored images)** (each camera's stored pictures folder).
3. **Image test**: runs the job over folders of stored pictures and lists every NOT OK with its
   reason and the timing (typical, p95, max).

### OCR model

`ml/models/ocrnet.onnx` (about 0.47 M parameters, convolution only, runs in OpenCV dnn) reads
`0-9 A-Z a-z`, space, `. , : / - # % ( ) + & * '` and `₹ ¥` (`ml/models/ocr_charset.txt`).
It is trained on synthetic laser, TTO, CIJ dot-print and embossed lines plus labelled real lines,
and calibrated so a wrong read scores low (see `ml/ocr/calibrate.py`):

```bash
cd ml
python -m ocr.train_ocr --out models/ocrnet.onnx --steps 6000                      # synthetic only
../engine/build/ocr_bench "<images>" --find-only --roi x,y,w,h --char-h 38 --dump <crops>
python -m ocr.label_real --crops <crops> --model models/ocrnet.onnx \
    --line "₹ 46.00, HWN{881|937}" --line "₹ 0.23 per ml" --line "¥ 10/26, # 09/28 {TIME}" \
    --out-crops <crops>/labels.tsv --out-truth ../truth/ocr_<product>.tsv
python -m ocr.train_ocr --init models/ocrnet.pt --real <crops>=<train.tsv> --test <crops>=<test.tsv>
```

Measure per print type, retrain and calibrate (the run of 2026-10-30; sets are kept outside the repository):

```bash
python -m ocr.eval_ocr --make <sets> --n 400 --seed 900          # test lines, one folder per print type
python -m ocr.eval_ocr --make <val> --n 200 --seed 1900          # validation lines (calibration)
python -m ocr.eval_ocr --make <train> --n 20000 --seed 5000      # pre-rendered training lines
python -m ocr.synth_scene <scenes> --n 150 --seed 31             # whole images for ocr_bench
python -m ocr.train_ocr --out <run>/ocrnet.onnx --init models/ocrnet.pt --steps 12000 --lr 1e-3 --workers 0 \
    --rendered <train>/laser=<train>/laser/labels.tsv ... (tto, cij, emboss) \
    --real <crops>=<train.tsv> --test <crops>=<test.tsv> --real-frac 0.15
python -m ocr.calibrate --pt <run>/ocrnet.pt --set laser=<val>/laser=<val>/laser/labels.tsv ... \
    --set real=<crops>=<train.tsv> --temp 1.5 --out models/ocrnet.onnx
python -m ocr.eval_ocr --model models/ocrnet.onnx --sets <sets> --set real=<crops>=<test.tsv>
../engine/build/ocr_bench <scenes>/cij <scenes>/cij/truth.tsv --model models/ocrnet.onnx \
    --charset models/ocr_charset.txt --read-only                 # also: --mutate for the check
```

The training needs Pillow (`uv pip install --python .venv/bin/python pillow`) and the system fonts.

Read text safety (2026-10-31): a free read passes only when a second reader reads the same text and the
calibrated line confidence (the CTC probability of the whole read, `kLineGamma` in `engine/src/ocr/ocr.hpp`)
is at or above min quality. Fit the calibration on validation lines, then measure on the test lines:

```bash
python -m ocr.eval_ocr --make <val> --n 400 --seed 901           # validation lines, with fonts.tsv
python -m ocr.eval_ocr --model models/ocrnet.onnx --sets <val> --agree shift --fit-gamma
python -m ocr.eval_ocr --model models/ocrnet.onnx --sets <sets> --set real=<crops>=<test.tsv> \
    --line-conf --agree shift                                     # or --agree <second model.onnx>
../engine/build/ocr_bench <scenes>/laser <scenes>/laser/truth.tsv --model models/ocrnet.onnx \
    --charset models/ocr_charset.txt --read-only                 # --agree off|auto|shift, --second-model m.onnx
```

### First results (laser print, Harpic 200 ml, 2048 × 1536, Apple M3)

489 stored images of one line, 23 Oct 2026, 15:38–17:00 (5 files were cut short and cannot be read;
9 show no label). The model was trained on synthetic lines plus the real lines captured before 15:58;
the images captured from 15:58 on were held out and never trained on.

| Test | Result |
| --- | --- |
| Held out (116 images): free read | 100.00% of 4,756 characters, 348 / 348 lines exact |
| Held out: check against the printed text | 116 / 116 OK (115 / 116 with the area Auto-find chose) |
| Held out: one character changed in the expected text | 0 / 116 accepted |
| Held out: time per image | median 18 ms, max 21 ms (target 100 ms) |
| All 484 readable images, job "batch HWN881, `{TIME:HH:mm+-3}`" | HWN881: 449 / 455 OK; HWN937: 20 / 20 NOT OK; no label: 9 / 9 NOT OK |

The 6 rejected HWN881 labels have a very faint or misread time digit or a cut-short line. Labels came
from the model's own reading, checked against an independent source (print time = capture time + 79 s
on this line) and by eye wherever the two disagreed (18 lines); the printer-to-camera delay is not
constant (it changed around 15:50 and 16:00), so the time rule alone is not a reliable label either.
Still to do: real CIJ and TTO images (synthetic only so far), and timing on the Windows i5 line PC.

### Reports

Every inspection, every product leaving the line (PLC part log: length, gap, reasons) and every line
event (jam, stop, alarm, batch start) goes into `data/results.db` (SQLite, `dotnet/VisionReports`).
The **Reports** page shows this shift, last shift, a day, 7 days, one batch or any range, with
findings in plain words (main reject reason, worst hour, touching products, length spread, stops,
inspection time), and downloads it as **PDF** (A4) or **Excel** (summary, reject reasons, failing
tools, per hour, cameras, length histogram, stoppages, rejected products, all products, inspections).
At the end of every shift and every batch the reports are written to `data/reports` on their own.
Shifts, line name and how long to keep reports are set on the same page.

## AI agents

| Agent | Kind | Where | What it does |
| --- | --- | --- | --- |
| Code locator | Deep learning | `ml/model.py`, `engine/src/ai_locator.cpp` | CodeNet (25k parameters) marks code areas on a 1/8-size image; its regions are decoded first |
| Code enhancer | Deep learning | `ml/enhance.py`, `engine/src/ai_locator.cpp` | Turns a dark, blurred or glare-hit code crop into clean black and white for the decoder; position per product (early / last / second pass) |
| Tuning agent | Machine learning (bandits) | `dotnet/CodeReader/TuningAgent.cs` | Learns per product which reader settings (incl. enhancer position) read most codes in least time, while the line runs; frozen for Locked products |
| Line advisor | Diagnostics | `dotnet/CodeReader/LineAdvisor.cs` | Explains misses (under-exposed, glare, not found, unreadable, slow) and suggests an exposure |
| Retraining agent | Self-training | `ml/retrain.py` | Nightly: re-reads the line's hard cases offline, fine-tunes the locator, promotes it only with no regressions and an approver |

### Training the locator

```bash
cd ml
uv venv --python 3.12 .venv && uv pip install --python .venv/bin/python torch numpy opencv-python-headless zxing-cpp onnx onnxruntime pillow
.venv/bin/python autolabel.py "<sample images>" labels.json          # engine labels every code it reads
.venv/bin/python train.py --data "<sample images>=labels.json" --out models/codenet.onnx
.venv/bin/python evaluate.py "<sample images>" labels.json models/codenet.onnx
```

Training mixes synthetic scenes (`ml/synth.py`: real QR / Data Matrix symbols on labels, with
text, 1D barcodes, box edges, glare, and simulated under-exposure down to 1.5%) with the real
images whose codes were all read. Only fully-read images are used, so a missed code is never
taught as background. The model is exported to ONNX and run by OpenCV DNN inside the engine
(`cr_load_model`, `CodeReader.LoadModel`, `bench --model`).

Locator v2 (current) adds shrink-wrap scenes (film wrinkles, blotches, sheen, haze), many
tiny codes, defocus and decorative-frame decoys, and also learns from partly-read images:
read codes are positives, texture-free areas are certain background, the rest is ignored.

```bash
.venv/bin/python train.py --data "<sample images>=labels.json" --partial --holdout "<test images>" \
    --init models/codenet.pt --lr 1.5e-3 --steps 4000 --out models/codenet.onnx
.venv/bin/python enhance.py train --out models/enhancer.onnx --steps 5000
.venv/bin/python enhance.py eval models/enhancer.onnx
```

### Retraining from the line

LineRunner's `--hard-cases <folder>` saves every short case as BMP with a `.json` sidecar.

```bash
.venv/bin/python retrain.py --golden "<sample images>=labels.json" --hard "<hard-case folder>"
.venv/bin/python retrain.py --promote models/candidates/codenet-<stamp>.onnx --approved-by <name>
```

## Benchmark

```bash
engine/build/bench "<folder with sample images>" truth/samples.csv --runs 3
```

Results on the 34 sample images (Apple M3, 8 cores, engine 0.1.0, 200 ms budget):

| Image group | Codes per case | Read | Median time |
| --- | --- | --- | --- |
| Cartons in open case (case 1–5) | 45 QR | 100% | 31 ms |
| Cans, 2 QR per label | 24 | 100% | 30–150 ms |
| Boxes with tape glare | 48 QR | 96–100% | 190 ms |
| Envelopes | 48 QR | 90–100% | 205–215 ms |
| Bottles in dividers | 48 QR | 98% | 335–350 ms |
| Bottle caps, Data Matrix | 24 / 48 DM | 92% / 50% | 220–240 ms |
| Shrink-wrapped case | unknown | 1–27 read | 95–160 ms |

Overall 881 of 921 expected codes (95.7%), 17 of 24 cases complete. A plain zxing-cpp pass
over the whole image read about 70% of the same codes. Expected counts in
`truth/samples.csv` were counted by eye and still need confirming.

The darkest images (labels at 3–12 of 255 brightness) need better exposure and lighting
first; see the plan doc.

### With the AI locator (`ml/models/codenet.onnx`)

Same 34 images, run back to back on an 8-core M3 (`bench --model ml/models/codenet.onnx --locator <mode>`):

| Locator | Codes read | Cases complete | Median time | Slowest |
| --- | --- | --- | --- | --- |
| Classical | 879 / 921 (95.4%) | 17 / 24 | 210 ms | 460 ms |
| AI, then classical (default with a model) | 884 / 921 (96.0%) | 19 / 24 | 141 ms | 362 ms |
| AI only | 872 / 921 (94.7%) | 18 / 24 | 142 ms | 667 ms |

The AI proposes about one region per code (48 for 48 bottles) where the classical locator
proposes about 200, so cans fall from about 215 ms to 55 ms and the open cartons read in
13 ms with AI only. Both bottle images are now complete (48/48). On images it never trained
on, the AI's regions cover 84% of the codes the engine can read; it is weakest on the
shrink-wrapped case from the 4096 x 2160 camera, where the classical fallback keeps the read
count. The retraining agent's first trial (7 hard cases) produced a candidate that found
fewer codes than the current model, so its gate rejected it.

### Locator v2 and the enhancer

Locator coverage (AI regions over codes the engine can read), 7 test images never trained on
(5 shrink-wrap, 48 Data Matrix caps, 1 envelope case): v1 65/103 (63%), v2 99/103 (96%).
Enhancer on 400 held-out synthetic crops (dark, blurred, glare, haze): decoder alone 33.5%,
with the enhancer 61.2%, and it lost none the decoder could read.

End to end, 34 images, 200 ms budget, 3 runs each, M3:

| Configuration | Codes read | Cases complete | Median | Slowest | Shrink-wrap codes (no count) |
| --- | --- | --- | --- | --- | --- |
| Classical | 881 / 921 | 17 / 24 | 183 ms | 797 ms | 159 |
| AI locator v1 | 884 / 921 | 19 / 24 | 144 ms | 375 ms | 164 |
| AI locator v2 | 882 / 921 | 17 / 24 | 157 ms | 469 ms | 179 |
| v2 + enhancer, last | 893 / 921 | 17 / 24 | 212 ms | 268 ms | 180 |
| v2 + enhancer, second pass | 885 / 921 | 18 / 24 | 160 ms | 358 ms | 180 |

The best enhancer position differs by product: Data Matrix caps read 35/48 with it early
(24 without), bottles 96/96 with it last, boxes and envelopes best with a second pass. With
each product on its best position the total is 898 / 921 (97.5%); the tuning agent learns
this position per product on the line. Run-to-run noise on this shared Mac is about ±2 codes.

## Roadmap

1. Engine core and benchmark (this version)
2. .NET SDK (this version: wrapper, GS1 parsing, demo)
3. AI models: CPU locator and crop enhancer with OpenVINO
4. Learning service: hard-case queue, auto-tuning, nightly retraining
5. Aggregation checks and parent–child output
6. Compliance pack: audit trail, e-signatures, IQ/OQ/PQ documents
