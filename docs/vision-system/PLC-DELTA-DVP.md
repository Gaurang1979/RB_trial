# Delta DVP line PLC: wiring, set-up and program

This guide connects a line with one to three cameras (at the same place or at different places along
the conveyor) and one rejector to a Delta DVP slim PLC: camera trigger sensors, reject sensors, the
encoder, rejector, light, tower lamp, the conveyor's Delta VFD, and Vision Studio over Ethernet.
The program is [`plc/delta-dvp/VisionLineDVP.st`](../../plc/delta-dvp/VisionLineDVP.st). The
data blocks are in [PLC-INTERFACE.md](PLC-INTERFACE.md), sections 3.1–3.5. Vision Studio's **Line &
wiring** page draws the same wiring for the chosen PLC model and shows the condition of every
instrument live.

The program has **not been compiled in ISPSoft yet** (no ISPSoft on the development PC). The points
to check on the first build are listed in the program header and in section 4.

## 1. Parts

| Part | Model | Why |
| --- | --- | --- |
| PLC CPU | Delta **DVP12SE11T** | Slim CPU with built-in Ethernet (Modbus TCP) and RS-485 |
| I/O module | Delta **DVP16SP11T** (8 DI / 8 DO, transistor) | Right-side expansion, numbered X20–X27 / Y20–Y27 |
| Power | 24 V DC SMPS, rating = PLC + sensors + valve + lamps + 30 % (typically 5 A) | One supply for the control circuit |
| Conveyor drive | Delta VFD (VFD-E, MS300 or similar), or contactors: direct-on-line (KM1) or star-delta (KM1, KM2, KM3) with an overload relay | VFD: run / stop / speed over RS-485 or by a run output; contactors through interposing relays |
| Incoming supply | 2P MCB (230 V AC to the SMPS), 3P MCCB (motor circuit), earth bar | As in the usual panel: L1 L2 L3 N E |
| Push buttons | START green NO (3–4), STOP red NC (1–2) | Start and stop the line from the panel |
| Interposing relays | 24 V DC coil, with flyback diode, contact rated for the contactor coil | PLC output → relay → contactor coil (KM1 / KM2 / KM3) |
| Encoder | Incremental, 24 V, A/B, 500–2000 pulses per revolution, on the conveyor shaft or a measuring wheel | Part tracking to the rejector |
| Sensors | Photoelectric, 24 V PNP, NO, 3-wire | One trigger sensor per camera, reject position, reject verification, jam, bin full |
| Rejector | 24 V DC solenoid valve with flyback diode, air blast or pusher | |
| Tower lamp | 24 V LED, green / amber / red + buzzer | |
| Network | Industrial Ethernet switch | PLC, vision PC, GigE cameras |

**Why not the DVP14SS211T:** the SS2 CPU takes no Ethernet module (Delta lists DVP-EN01-SL /
EN02-SL for SA2, SE, SV2, SX2 only). With an SS2 the vision link would have to be RS-485 Modbus RTU
through a USB-RS485 adapter on the vision PC; Vision Studio does not have that link yet. The
**Line & wiring** page keeps an SS2 profile for that case.

## 2. Terminals (default assignment)

Change a terminal by editing the profile in `dotnet/VisionCore/Plc/LineProfiles.cs` and the `AT`
addresses at the top of the program together.

| Terminal | Instrument | Notes |
| --- | --- | --- |
| X0 / X1 | Encoder A / B | High-speed counter C251 (A/B phase). Check the counter number and input limit for your CPU in the DVP manual |
| X2 | Camera 1 trigger sensor | Product at camera 1; every part gets its number and encoder position here |
| X3 | Reject position sensor | Product just before the rejector; used to learn the distance and to check tracking |
| X4 | Reject verification sensor | Across the reject chute: confirms the rejected product left the line |
| X5 | Jam sensor | After the rejector: product backing up |
| X6 | Air pressure OK | Pressure switch, closed = OK |
| X7 | E-stop circuit OK | Auxiliary contact of the safety relay, closed = healthy |
| X20 | Auto mode | Selector or start / stop circuit: line in automatic |
| X21 | Reject bin full | |
| X22 | Alarm reset | Push button |
| X23 | Conveyor drive fault / motor overload | VFD fault relay (RA–RC NO or RB–RC NC) or overload relay (95–96 NC or 97–98 NO); choose NC / NO on the Line page (NC recommended: a broken wire stops the line) |
| X24 | Camera 2 trigger sensor | Only when camera 2 stands at its own place (not when it shares camera 1's sensor) |
| X25 | Camera 3 trigger sensor | Only when camera 3 stands at its own place |
| X26 | Start push button | Green, NO 3–4: +24 V through the contact |
| X27 | Stop push button | Red, NC 1–2: on = not pressed; pressing it (or a broken wire) stops the line |
| Y0 | Rejector valve | |
| Y1 | Vision light on / off | Light controller enable input |
| Y2 | Vision ready | Relay to the upstream machine |
| Y3 | Line stop | Relay; NC contact in the machine's stop circuit |
| Y20–Y23 | Tower green, amber, red, buzzer | |
| Y24 | Motor run | KM1 main contactor (direct-on-line, star-delta) or VFD FWD (run by terminal), through an interposing relay |
| Y25 | Delta contactor KM2 | Star-delta only |
| Y26 | Star contactor KM3 | Star-delta only |
| COM2 | Conveyor VFD, RS-485 | PLC RS-485 + / − to drive SG+ / SG− |
| Ethernet | Vision PC (Modbus TCP server, port 502) | Through the switch |

## 3. Wiring rules

- **Incoming supply.** L1, L2, L3, N and E bars. A 2P MCB feeds the 24 V SMPS (230 V AC L / N); a 3P
  MCCB feeds the motor circuit (VFD R / S / T, or KM1). Earth to the SMPS FG, the PLC / panel, the VFD
  E terminal, the motor body and the encoder shield (PLC end only).
- **Start / stop.** START (NO 3–4) and STOP (NC 1–2) from +24 V to X26 / X27. The PLC latches Start;
  Stop, an E-stop or a drive fault drops it, and Start must be pressed again. A jam stop restarts by
  itself when the jam clears (line stays started).
- **Contactor drives.** PLC outputs drive interposing relays; their contacts switch the contactor
  coils (A1 from L through a fuse, A2 to N, or 24 V coils from +24 V / 0 V). The E-stop also opens the
  coil supply. Overload relay in KM1's line, set to the motor current (star-delta: 0.58 × rated
  current); its NC 95–96 to X23 (set "NC" on the Line page) or its NO 97–98 (set "NO").
- **Star-delta.** KM1 + KM3 (star) for the star time, then both star and delta open for the
  changeover gap (100 ms default), then KM1 + KM2 (delta). KM2 and KM3 interlocked with each other's
  NC auxiliary contact as well as in the program.
- **Relay-output PLCs** (DVP12SE11R, DVP14SS211R, DVP16SP11R). Each common (C0, C1, C2 …) feeds its
  group; the load goes between the output and 0 V; do not mix 24 V DC and 230 V AC in one group. A
  relay output wears out when the rejector fires many times a second: use the transistor (T) models
  for fast rejectors.
- **Supply.** SMPS +24 V and 0 V to the CPU and module supply terminals; a fuse per circuit
  (sensors, outputs, valves). Keep the PLC and sensor 0 V common.
- **Inputs (PNP sensors).** Sensor brown to +24 V, blue to 0 V, black to the X terminal. The input
  common **S/S goes to 0 V** for PNP sensors (to +24 V only for NPN sensors).
- **Outputs (transistor NPN, sinking).** Load between +24 V and the Y terminal; the output common to
  0 V. Every coil (valve, relay) gets a flyback diode. Keep each output within the module's current
  rating; drive larger loads through a relay.
- **Encoder.** Shielded cable, shield to earth at the PLC end only, away from motor and VFD output
  cables. Push-pull or PNP outputs to X0 / X1.
- **VFD.** RS-485 twisted pair with shield, terminated at the far end. Set the drive's communication
  parameters to match COM2 (station 1, 9600 bit/s, RTU 8-E-1 as used in section 5) and its command and
  frequency source to "communication" (see the drive manual). Wire the E-stop into the drive's
  stop / safe-torque-off circuit as well: stopping only over RS-485 is not a safety function.
- **Camera trigger** is not a wire: the PLC sets each camera's trigger bit over Ethernet and the
  vision PC fires that camera.
- **Cameras at different places.** Camera 1 must be the first camera on the line: parts are numbered
  there. Use reject timing by encoder: each camera sensor's distance from camera 1 is learned on the
  first part after every auto start, and a sensor edge is matched to the part expected at that
  position (±20 %). A missed edge then costs only that part (rejected, not inspected), and an extra
  edge (reflection) is ignored; neither shifts the following parts. Parts must stay in order (no
  overtaking) and be at least 20 % of the camera distance apart.

## 4. ISPSoft set-up

1. New project, CPU DVP12SE11T, add DVP16SP11T on the right. Note the module's I/O numbers.
2. **Ethernet:** fixed IP in the same subnet as the vision PC. **Ethernet data exchange**
   (Modbus TCP client), remote = vision PC, port 502, unit 1:

   | # | Direction | PLC registers | Vision registers | Words | Cycle |
   | --- | --- | --- | --- | --- | --- |
   | 1 | write | D1000–D1039 | 0–39 (input block + camera channels in) | 40 | 10 ms |
   | 2 | read | D1100–D1199 | 100–199 (output block 1) | 100 | 10 ms |
   | 3 | read | D1200–D1243 | 200–243 (output block 2 + camera channels out) | 44 | 10 ms |
   | 4 | write | D1040–D1095 | 40–95 (line diagnostics) | 56 | 100 ms |
   | 5 | write | D1440–D1487 | 400–447 (part log) | 48 | 20 ms |
   | 6 | read | D1300–D1331 | 300–331 (line settings) | 32 | 500 ms |

   Blocks are kept to 100 words; combine 2 and 3 if your firmware allows longer blocks.
3. Add a **Structured Text** POU and paste `VisionLineDVP.st`. Map the `AT` variables to the
   devices if your ISPSoft version wants device names (`X2`, `Y0`, `D1000`).
4. First-scan ladder rung: start the A/B counter (`DCNT C251 K2147483647`) and copy C251 to
   `EncoderCount` every scan (`DMOV C251 EncoderCount`).
5. Compile. Check the IEC timer / edge blocks (`TON`, `R_TRIG`) exist in your library; replace with
   `TMR` rungs if not.

## 5. VFD over RS-485 (ladder, COM2)

```
M1002 (first scan): MOV H87 D1120     ; COM2 9600 bit/s, 8-E-1 (check the table in the DVP manual)
                    SET M1120         ; keep the settings
                    SET M1143         ; RTU mode
                    MOV K100 D1129    ; time-out 100 ms
every 100 ms, alternating:
  write command:    MODRW K1 H6 H2000 D1400 K1   ; 2000H: 1 = stop, 2 = run
  write frequency:  MODRW K1 H6 H2001 D1401 K1   ; 2001H: Hz x 100
  read status:      MODRW K1 H3 H2100 D1410 K2   ; 2100H error code, 2101H status
  M1127 (reply received) -> reset the 1 s watchdog, set M1410 (VfdOnline)
  watchdog 1 s elapsed     -> reset M1410
```

`2000H`, `2001H` and `2100H` are the standard Delta drive communication addresses; confirm them and
the command word values in your drive's manual. The ST program writes `D1400` (command) and `D1401`
(frequency) and reads `M1410`.

## 6. Line settings (Vision Studio → PLC)

Edited on the **Line & wiring** page and sent to the PLC (registers 300–331). The PLC takes them over
within half a second.

| Setting | Meaning |
| --- | --- |
| Conveyor speed | Hz sent to the drive |
| Cameras along the line | 1–3 |
| Camera 2 / 3 triggered by | Its own sensor (X24 / X25, own place on the line) or another camera's sensor (same place, e.g. front and back of the pack: both cameras inspect the same part at the same time) |
| Camera trigger delay | Camera's trigger sensor edge → that camera's trigger, so the part reaches the centre of the view |
| Light start delay / on time | Light on after the delay for the on time; on time 0 = on while the line runs |
| Reject timing | **Encoder distance** (parts tracked in a queue by encoder position) or **reject sensor + delay time** |
| Distance | Camera 1 trigger sensor → reject sensor, in encoder pulses: **learned on the first part of every auto start**, or entered by hand |
| Reject sensor → firing point | Pulses (encoder mode; may be negative) |
| Reject delay | ms after the reject sensor edge (time mode); each NOT OK part gets its own firing, also when the next one arrives within the delay (up to 8 waiting; more is a reject failure alarm) |
| Reject on time | Valve on time |
| Reject verification window | Each rejected part must be seen in the chute within this time (one verification sensor edge per reject; products touching the one rejected before them count as one) |
| Jam time / restart delay | Jam sensor on this long stops the conveyor; when it clears, the line restarts after the delay |
| Stop after NOT OK in a row | Line stop and red lamp |
| Stop when a reject is not seen | Line stop on a failed reject |
| Encoder pulses per metre | Scale for lengths in mm; the page calculates it from pulses per turn (as counted, ×4 for A/B counting) and the roller diameter |
| Product length / tolerance | Nominal length in mm (0 = learned: shortest of the first 5 products after every start) and ± % |
| Minimum gap | A smaller gap is reported (not rejected) |
| Reject touching / out of length | Reject products that came touching, or out of the length tolerance |
| Motor drive | VFD over RS-485, VFD run by Y24, direct-on-line (KM1 on Y24) or star-delta (Y24 KM1, Y25 KM2 delta, Y26 KM3 star) |
| Star time / changeover gap | Star-delta timing |
| Drive fault / overload contact | NC (95–96, RB–RC, recommended) or NO (97–98, RA–RC) |

**Learning.** When the line enters automatic (and learning is on), the first part's encoder position
is stored at the trigger sensor; when it reaches the reject sensor the difference is the distance.
It is used from the next part on and shown on the Line page ("learned: n"), where it can be taken as a
fixed value. "Learn again on the next part" repeats it without stopping.

**Queue and camera-wise rejection.** Up to 16 parts between camera 1 and the rejector. Each part
carries one result per camera. Each trigger sensor's edge belongs to the part expected at that
sensor (by encoder position, see the wiring rules); every camera on that sensor is triggered with the part number and its answer is stored on that part.
Typical layouts: three cameras at one place (one sensor); front + back at one place and a third
camera further on (two sensors); three places (three sensors). When the part reaches the firing point (encoder) or the reject sensor (time mode) it is
rejected if **any** camera said NOT OK, did not answer within 1 s, was not ready, refused the
trigger (`TriggerRejected`), or never saw it (the part passed a camera without its sensor seeing it).
An answer counts only when its `TriggerIDEcho` is that part's number and `Done` comes with `OK` and
not `NG`; a camera takes its next part only after the vision PC dropped `Done` and
`TriggerRejected`, so nothing left from one part answers for the next (Req 17.10 MRG-05). Late
answers for a part that has already left are dropped. The part log marks a part with no answer
(timeout, refused, busy, not ready) "not inspected" and one with an answer "camera NOT OK".

**Length, gap and touching products.** The PLC measures every product with the encoder while it
passes the camera 1 sensor (length) and the distance since the product before (gap). When products
come touching, the sensor sees one long product and gives one edge: a signal of about 2 or more
nominal lengths is split into that many products, the hidden ones are queued behind the first and
all are rejected with the reason "touching products". Too short or too long single products (broken,
half pack, lying, fallen) are rejected as "length out of tolerance". The valve stays on while
products to reject follow each other closely. Each product leaving the line is sent to Vision Studio
(part log, interface section 3.6) with its length, gap and reasons, for the reports.

**Bypass (admin).** A QA user ticks the instruments the installation does not have: reject
verification sensor, jam sensor, bin full sensor, air pressure switch, reject position sensor,
E-stop status. The PLC then ignores the input (no verification, no jam stop, no alarm) and the Line
page shows the instrument as **bypassed**. Without a reject position sensor the reject is always by
encoder distance entered by hand (no learning, no tracking check).

## 7. Instrument conditions

The PLC counts what each instrument does (diagnostics block, section 3.3 of the interface). Vision
Studio compares counters that must move together and shows each instrument as **OK**, **misses
sometimes (x of y)**, **fault** or **not fitted**, on the Line page and as chips on the Run screen.

| Instrument | Misses sometimes when | Fault when |
| --- | --- | --- |
| Camera 1 trigger sensor | Parts counted differ from the reject sensor by more than the parts in transit | On for more than 3 s (blocked, dirty) |
| Camera 2 / 3 trigger sensor | Parts counted differ from camera 1 by more than the parts in transit | |
| Reject position sensor | A part arrives outside the expected encoder window | Tracking alarm active |
| Reject verification sensor / rejector | A rejected part is not seen in the chute | Not one of 10+ rejects seen (sensor or rejector dead) |
| Encoder | Stops without pulses; pulses per part vary by more than 30 % | No pulses while the conveyor runs |
| Jam sensor | Jams since start | Jam now |
| Conveyor drive / overload | | Drive fault or overload tripped, or no RS-485 answer (RS-485 drive only) |
| Start / stop buttons, contactors | | Shown with their signal (pressed / released, on / off) |
| Vision link | Heartbeat losses, triggers while vision not ready | Vision heartbeat lost now |
| Air, bin full, E-stop | | Pressure low, bin full, E-stop pressed |
| Any bypassed instrument | shown as **bypassed**, not judged | |

## 8. Commissioning checklist

- [ ] Every input: block / press it and see it change on the Line page (signal column).
- [ ] Every output: "Send test OK / NOT OK" on the PLC page; force outputs from ISPSoft monitor.
- [ ] Encoder: jog the conveyor forward; the count rises. Reversed: swap A and B.
- [ ] Drive: speed set on the Line page reaches the drive; drive status answers (Conveyor drive OK).
- [ ] Start / stop: Start runs the line, Stop stops it, pull the Stop wire: the line stops.
- [ ] Star-delta: on start KM1 + KM3, after the star time KM3 drops and KM2 picks up; KM2 and KM3 never
      on together (watch the Line page signals); trip the overload relay: line stops, red lamp.
- [ ] Cameras at different places: pass 10 parts; each camera's sensor count equals camera 1's; mark
      one part NOT OK at camera 2 only (e.g. cover its code); exactly that part is rejected.
- [ ] Camera 2 with no answer: in Vision Studio stop camera 2 (or unplug it) and pass 3 parts; all 3
      are rejected, "not inspected" in the part log; plug it back: the next parts pass.
- [ ] Two rejects close together: pass two NOT OK parts close together (not touching) and block the
      verification sensor's view of the second one; Rejector shows 1 miss (red lamp when "Stop when a
      reject is not seen" is on).
- [ ] Learn: start automatic, pass one part; the learned distance appears and matches a measurement.
- [ ] Reject: run 20 parts marked NOT OK (wrong batch in Start batch); every one leaves the line and is seen
      by the verification sensor; Rejector shows OK with 0 misses.
- [ ] Jam: block the jam sensor longer than the jam time; conveyor stops, red lamp, buzzer; clear it;
      line restarts after the restart delay.
- [ ] Vision link: pull the PLC's Ethernet cable; vision heartbeat lost within 1 s, line stops.

## 9. Delta AS series (AS228T-A, AS332T-A, AS218TX-A)

The program [`plc/delta-as/VisionLine.st`](../../plc/delta-as/VisionLine.st) has the same logic as the DVP
program; only the terminals, the encoder counter and the Modbus TCP client table differ (listed in its
header). Change both programs together.

| Terminal | Instrument | Terminal | Instrument |
| --- | --- | --- | --- |
| X0.0 / X0.1 | Encoder A / B (high-speed counter in HWCONFIG) | Y0.0 | Rejector valve |
| X0.2 | Camera 1 trigger sensor | Y0.1 | Vision light |
| X0.3 | Reject position sensor | Y0.2 | Vision ready |
| X0.4 | Reject verification sensor | Y0.3 | Line stop |
| X0.5 | Jam sensor | Y0.4–Y0.7 | Tower green, amber, red, buzzer |
| X0.6 / X0.7 | Bin full / air pressure OK | COM1 | Conveyor VFD, RS-485 |
| X0.8 / X0.9 / X0.10 | Auto mode / E-stop OK / alarm reset | | |
| X0.11 / X0.12 | Camera 2 / 3 trigger sensor | | |
| X0.13 | Conveyor drive fault / overload | Y0.8 / Y0.9 / Y0.10 | Motor run (KM1 / VFD FWD), KM2 delta, KM3 star |
| X0.14 / X0.15 | Start (NO) / stop (NC) push buttons | | |

The AS218TX-A (8 DI / 6 DO) has no room for bin full, E-stop status, reset, camera 2/3 sensors, drive
fault, amber lamp or buzzer: bypass bin full and E-stop on the Line page, and use one trigger sensor
(cameras at one place).
