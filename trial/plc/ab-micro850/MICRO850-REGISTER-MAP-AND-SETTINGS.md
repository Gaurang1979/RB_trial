# Micro850 program HARPIK08042026_Final_141026: what the vision PC needs to know

Decoded from the CCW project in `HARPIK08042026_Final_141026.zip` (PROG1, Modbus server map, initial
values). **The PLC program does not need to change for this trial.** The vision PC is the Modbus TCP
**client**; the Micro850 stays the server, as it is today.

## 1. Modbus TCP server (PLC side)

- Holding registers 400001–400050 = `mod_address[0..49]` (INT). Modbus offset = register − 400001.
- The Micro850's Ethernet port must have Modbus TCP server on (it is in the project: `MbSrvConf.xml`).
- Set the PLC's IP in CCW (Controller → Ethernet). The trial config uses `192.168.1.5`.

## 2. Registers the vision PC writes

| Offset | Name in PROG1 | Vision PC writes | PLC does |
|---|---|---|---|
| 3 | `MOD4_RESULT_PASS` | `1` = product OK | Latches PASS for the slot whose window is open, clears to 0 after 50 ms |
| 4 | `MOD3_RESULT_FAIL` | `1` = product NG | Latches FAIL (reject), clears to 0 after 100 ms; also counts consecutive rejects |
| 43 bit 1 | heartbeat | toggles every 1 s | Bit unchanged 5 s while the conveyor runs → `SOFTWA_CRUSH` → line stops (alarm 37.15) |
| 43 bit 0 | soft reset | 0 (not used) | Reset, same as the DI_12 button |

## 3. Setpoints (initial values in the project)

Distances are **encoder pulses** (HSC on `D504_ENCODER_ACT`), measured from the product sensor
(`_IO_EM_DI_02`, falling edge). The PLC keeps them as they are unless `WriteSetpointsOnConnect` is on.

| Offset | Name | Default | Meaning |
|---|---|---|---|
| 0 | `MOD0_CAMTRIG_DELAY` | 30 | Trigger output 1 (`_IO_EM_DO_00`) after this distance |
| 46 | `MOD1_TRIG_DELLAY_MM2` | 35 | Trigger output 2 (`_IO_EM_DO_09`) |
| 47 | `MOD1_TRIG_DELLAY_MM3` | 170 | Trigger output 3 (`_IO_P1_DO_01`), **and the result window opens here** |
| 44 | `WAIT_FROM_CAMERA_MM` | 400 | **Result window closes**: no PASS/FAIL by here → product rejected |
| 1 | `MOD1_REJ_DELAY_PULSE` | 500 | Reject output (`_IO_EM_DO_02`) at this distance |
| 2 | `MOD2_REJ_ONTIME` | 40 | Reject pulse, ms |
| 28 | `TIME_SET_VARI` | 200 | Reject-verification sensor (`_IO_P1_DI_01`) time, ms |
| 39 | `MOD39_CONSU` | 10 | Consecutive rejects that stop the line |
| 40, 41, 42 | `TIME_1_D..3_D` | 5 | Infeed jam, outfeed jam, bin full timers |
| 48 | `FLASH_ON_DELAY_PULSE` | 1 | Light output (`_IO_EM_DO_01`) after this distance |
| 49 | `TRIG_ON_FLASH` | 200 | Light on time, ms |

Each trigger output is a 140 ms pulse. Cameras trigger on the rising edge.

## 4. Status the vision PC reads (log only)

25 encoder, 26–27 counters, 35 inputs, 36 outputs, 37 alarms (bit 8 infeed jam, 11 reject
verification failed, 12 outfeed jam, 13 bin full, 15 vision heartbeat lost), 43 flags.

## 5. Four cameras on three trigger outputs

| PLC output | Distance register | Cameras (suggested) |
|---|---|---|
| `_IO_EM_DO_00` | 0 | Cam1 (2 MP, batch code) **and** Cam2 (2 MP, 2D code), wired in parallel |
| `_IO_EM_DO_09` | 46 | Cam3 (0.3 MP, EAN) |
| `_IO_P1_DO_01` | 47 | Cam4 (0.3 MP, EAN) |

- **Wiring:** PLC output (24 V DC source) → camera Line0 opto input (+). PLC 0 V → camera opto
  ground (−). Check the pins in the Hikrobot camera's I/O cable sheet. Two camera inputs on one
  output are fine (a few mA each).
- **Light:** `_IO_EM_DO_01` already switches the light, so the cameras do not strobe.
- Place each camera where its trigger distance puts the product in the middle of its view. Set
  `CameraDelayMs` in `vs-runtime.json` from the product log (README, step 6).

## 6. Rules the settings must meet (line safety)

The PLC knows which product a result belongs to **only by the encoder window**. The vision PC
sends no product number. So:

1. **Window shorter than the product pitch:** `reg44 − reg47` < pitch in pulses.
   If two products are in the window at once, one PASS is credited to both, and a product whose
   result was lost could pass. Measure the pitch: product sensor to product sensor, in encoder
   pulses (watch register 25).
2. **At most 5 products between sensor and reject:** `reg1` < 5 × pitch. The program has 5
   tracking slots (`CAM3_SEQ1..5`).
3. **Last camera fires before the window opens:** register 47 must be the largest trigger
   distance. A PASS sent before the window opens is ignored, and the product is rejected.
4. **The vision PC answers inside the window:** `ResultDeadlineMs` <
   (`reg44` − `reg0`) × ms-per-pulse. `ResultNotBeforeMs` > (`reg47` − `reg0`) × ms-per-pulse. The
   vision PC never writes after its deadline (the PLC rejects the product itself), so a late PASS
   cannot land on the next product.
5. **No result means reject.** Line stopped, PC down, link lost or a camera frame missing: the
   product gets no PASS and is rejected. A heartbeat stuck for 5 s stops the conveyor.

## 7. Optional PLC changes (not needed for the trial; for the PLC engineer later)

- Have the PLC write a product number with the trigger, and the PC echo it with the result. This
  removes the timing-only pairing in rule 1.
- A 4th trigger output, so each camera can be placed on its own.
- Mark `mod_address` setpoints RETAIN if they are tuned from the PC.
