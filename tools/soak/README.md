# vs-runtime soak on a PC without cameras or a PLC

A soak of the real `vs-runtime` (`dotnet/Runtime.Host`, Release) with two simulated cameras and a
simulated Modbus TCP PLC that runs the camera-channel handshake (PLC-INTERFACE 3.5) at a fixed
part rate. It records the runtime's resources every minute and judges the run. The first run is in
`docs/changes/2026-10-31-claude-runtime-soak-30min.md`; the setup comes from PR #148.

```bash
tools/soak/run.sh <workDir> "$VS_IMAGES" 1800 225 100   # seconds, parts/min, ms budget per camera
SOAK_VMMAP="3 10 17 24" tools/soak/run.sh ...            # also vmmap -summary at those minutes, leaks at the end
dotnet <workDir>/ctl/soakctl.dll loop <workDir>/jobs SOAK2CAM ml/models <workDir>/cam1 <workDir>/cam2 1200 loop.csv
                                                         # the engine alone on the same frames (no runtime, no PLC)
```

- Takes the heavy-test lock (`/tmp/vs-heavy-test.lock`, AGENTS.md rule 13) for the whole run and
  releases it on exit, also on failure. Keep a held run under 40 minutes (1800 s soak plus about
  3 minutes of build and checks).
- Builds the engine libraries, vs-runtime and `soakctl` (`ctl/`), then plays the image folder:
  every other file to camera 1, the rest to camera 2. Each frame is the 1920 × 1200 centre of an
  image, as an 8-bit grey BMP (`soakctl crop`): the size of a 225/min line camera; the sample case
  photos are 20 MP and take 200–400 ms each, more than one part period. The crops are customer data:
  they stay in `<workDir>`, never in the repository. The sixth argument `full` plays the images as
  they are. The job `SOAK2CAM` has one QR tool
  per camera that needs at least one code.
- Each simulated frame comes as late as the line camera would send it: the job's exposure plus
  the frame over the link (RT-SF-01), 25 ms for a 1920 × 1200 frame at 2 ms over GigE, 8 ms with
  `SOAK_LINK=USB3`. Both cameras fire in the same PLC
  scan, with ±20 ms jitter on the part period (the worst case for the CPU).
- Needs `dotnet-counters` for the managed heap and GC counts
  (`dotnet tool install dotnet-counters --tool-path <dir>`, then `DOTNET_COUNTERS=<dir>/dotnet-counters`).

| File in `<workDir>/out` | What |
| --- | --- |
| `samples.csv` | Per minute: working set, managed heap, GC committed, GC counts gen0/1/2, threads, open file descriptors, results.db, NG image store (MB and files), guard pressure, restart recommended, frames missed, alarms, parts, NG, missed, timed out, late, trigger to Done p50/p99/max, load |
| `plc.csv` | Per part at the PLC: camera, part id, OK/NG/TIMEOUT/MISSED, trigger to Done |
| `results.csv` | The contract's result stream (a Vision Studio client stays connected all run) |
| `ref-cam1.txt`, `ref-cam2.txt` | Each frame's verdict from a fresh reader (`soakctl local`) |
| `report.txt` | `analyze.py` (no OK that should be NG, from the runtime's results.db in order against each frame's reference) and `report.py` (flat, no part lost or late, guard quiet) |
| `vmmap-*.txt`, `leaks.txt` | With `SOAK_VMMAP`: memory by region; live malloc bytes (`ALLOCATED`) tell a leak from freed memory the macOS allocator keeps, which the working set includes |

Judge timing only on a quiet PC (AGENTS.md rule 11: load under half the cores before and during the
run); on a busy one, missed and late parts are "not judged". The working set on macOS includes
freed memory the allocator keeps and drops when the system compresses the process: for a leak,
look at the managed heap and live malloc bytes. `results/` holds the 2026-10-31 records.

Pass: after a 5-minute warm-up no resource grows (`report.py` prints each slope per hour); no
part missed, timed out or later than one part period; no OK where the fresh reader gives NG; the
resource guard at Normal, no restart recommended and no alarm in every minute.
