# Factory test with a Delta DVP PLC

This is the factory rehearsal before the client site. Program:
[`plc/delta-dvp/RBTrialDVP.st`](../../../plc/delta-dvp/RBTrialDVP.st).

It does towards the vision PC what the client's Micro850 program does, with **the same register
map** (`D0`–`D49` = `mod_address[0..49]`). So the vision PC runs the same Micro850 link in both
places. Only one setting differs: `Micro850:RegisterBase` is `4096` here (Delta `D0` = Modbus
16#1000) and `0` at the client.

Use `trial/config/vs-runtime.factory-delta.json` here and `vs-runtime.client-micro850.json` at the
client.

The program is **not compiled in ISPSoft yet**. It passes the rusty syntax and type check
(`plc/st_check.sh` method, 2026-11-04). Do the first-build checks in the program header.

## Parts

| Part | Model |
|---|---|
| PLC | Delta DVP12SE11T (Ethernet, Modbus TCP server) + DVP16SP11T (8 DI / 8 DO, transistor) |
| Encoder | Incremental A/B, 24 V push-pull or PNP, on the conveyor shaft or a measuring wheel |
| Product sensor | Photoelectric, 24 V PNP, NO |
| Reject | 24 V solenoid valve with flyback diode; reject verification sensor across the chute |
| Cameras | 2 × 2 MP + 2 × 0.3 MP Hikrobot GigE, trigger on Line0 (opto input) |
| Light | Light controller with 24 V enable input |

## Terminals

| Terminal | Signal | Same as Micro850 |
|---|---|---|
| X0 / X1 | Encoder A / B (C251, A/B phase) | HSC encoder |
| X2 | Product sensor (falling edge starts tracking) | `_IO_EM_DI_02` |
| X3 | Reject verification sensor | `_IO_P1_DI_01` |
| X4 | Start push button (NO) | |
| X5 | Stop push button (NC) | |
| X6 | Alarm reset push button | `_IO_EM_DI_12` |
| X7 | E-stop OK (safety relay aux, NC) | `_IO_EM_DI_13` |
| X20 / X21 | Infeed jam / outfeed jam sensors | |
| X22 | Reject bin full | `_IO_P1_DI_03` |
| X23 | Air pressure OK | |
| **Y0** | **Camera trigger 1 → Cam1 + Cam2 Line0** (at D0) | `_IO_EM_DO_00` |
| **Y1** | **Camera trigger 2 → Cam3 Line0** (at D46) | `_IO_EM_DO_09` |
| **Y2** | **Camera trigger 3 → Cam4 Line0** (at D47) | `_IO_P1_DO_01` |
| **Y3** | **Light** (at D48, for D49 ms) | `_IO_EM_DO_01` |
| Y20 | Rejector (at D1, for D2 ms) | `_IO_EM_DO_02` |
| Y21 | Conveyor run (relay to contactor, or VFD FWD) | |
| Y22–Y25 | Tower green, amber, red, buzzer | |

### Wiring

- **Camera trigger:** Y0–Y2 are NPN sinking outputs on the DVP12SE11T. A Hikrobot opto input
  needs current through it, so wire it like this:
  - +24 V → camera Line0+ (opto anode);
  - camera Line0− (opto cathode) → the Y terminal;
  - output common C0 → 0 V.

  Two cameras on Y0 are wired in parallel. Check the pins in the camera's I/O cable sheet.
- **Inputs (PNP sensors):** S/S to 0 V. Sensor brown +24 V, blue 0 V, black to X.
- **Encoder:** shielded cable, shield earthed at the PLC end only. Keep it away from motor cables.
- Every coil gets a flyback diode.

## ISPSoft

1. New project: DVP12SE11T, plus DVP16SP11T on the right.
2. **Ethernet:** fixed IP (for example 192.168.1.5/24). The **Modbus TCP server** is on by default
   on the SE. **No data-exchange table is needed:** the vision PC reads and writes D0–D49 itself.
3. Ladder rung, first scan and every scan:
   ```
   M1002 ── DCNT C251 K2147483647      ; A/B phase counter on X0/X1 (check the counter number for your CPU)
   M1000 ── DMOV C251 D200             ; encoder to EncoderCount (D200/D201) every scan
   ```
4. Add a Structured Text POU and paste `RBTrialDVP.st`. Map the `AT` variables to `X`, `Y` and
   `D` if your ISPSoft wants device names: `Reg` = D0–D49, `EncoderCount` = D200 (32-bit).
5. Compile and download. Check the scan time (below 5 ms).

## Vision PC

`vs-runtime.json` → `Micro850`: `"PlcIp": "<Delta IP>"`, `"RegisterBase": 4096`.

On connect, the log says `Micro850 <ip>:502 connected`. The PLC alarm register D37 shows in the
log when it changes.

Bench check without the PLC: `python trial/tools/micro850_sim.py --base 4096`.

## Factory test plan

Do the client test plan with this PLC: `trial/README-TRIAL.md` steps 5–7 (settings rules,
commissioning, live checklist). Then also check:

| # | Test | Expect |
|---|---|---|
| F1 | Two products closer than the result window | Alarm "window too long" (D37 bit 0); both rejected |
| F2 | Encoder unplugged while running | Products are never triggered. Stop the line and fix it: the program does not watch the encoder (add it if needed) |
| F3 | Stop and start the line | Tracked products are dropped on stop. The first products after a start are tracked again |

## Differences from the Micro850 program (on purpose)

- A PASS that arrives while two products are in the window goes to **neither** (safer). The
  Micro850 gives it to both.
- 8 tracking slots (the Micro850 has 5).
- Trigger pulse length is set in D29 (default 40 ms). The Micro850 uses a fixed 140 ms. Cameras
  trigger on the rising edge, so both work.
