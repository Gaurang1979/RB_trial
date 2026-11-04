# Unified Notification Hub, Reusable Cross-Process IPC Bus & Comprehensive Printer/Motion Defect Diagnostics (REQ-18..20, PROT-33..35)

- Status: done
- Session: unified-notification-hub-and-printer-diagnostics, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / diagnostics / notifications / printer-defect-inspection
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, dotnet/PlantEvents/PlantNotification.cs, dotnet/PlantEvents.Tests/PlantNotificationTests.cs, design/prototypes/vision-studio/index.html
- Depends on: REQ-08, REQ-09, REQ-10, REQ-12, REQ-13, REQ-14, REQ-17, REQ-18, REQ-19, REQ-20, PROT-33, PROT-34, PROT-35

## Summary
Implements the comprehensive hardware, printer, and motion notification architecture requested by the owner on 2026-11-03:
1. Unified Expandable Notification Hub Drawer aggregating hardware, software, printer, and motion alerts into a single panel with Acknowledge, Ignore/Snooze, and Step-by-Step Diagnostic Guide actions.
2. Cross-Process IPC Publisher Bus (`\\.\pipe\SundaramNotificationBus` and HTTP REST) allowing external companion software (e.g. Track & Trace, serialization engines, feeder PLCs, SCADA) to publish into the exact same UI panel.
3. OEM Tab Sliding-Window Duplicate Code Detection per camera channel (50–5,000 counts) and Camera-to-Issue Master Inspection Mapping & Scheduling Matrix.
4. Comprehensive vision defect diagnostic suite for Thermal Transfer Overprinters (TTO), Continuous Inkjet (CIJ), Thermal Inkjet (TIJ), Motion Encoders, Substrate Moisture, and Hologram Applicators.

## Architecture & Implementation Details

1. **Unified Expandable Notification Hub (REQ-18 / PROT-33)**:
   - Header bell indicator with real-time active alert count (`🔔 X Alerts ▼`).
   - Full-width expandable drawer with live statistics (Active, Acknowledged, Snoozed).
   - Category filter pills: All, Printer Diagnostics, Motion & Encoders, Substrate & Moisture, Hardware & Optics, External IPC.
   - Multilingual toggle (EN, हिन्दी, ગુજરાતી, தமிழ்) updating all advice immediately.
   - Action buttons on each alert card:
     - `✓ Acknowledge`: Sets state to Acknowledged, captures user ID and timestamp.
     - `⏸ Snooze`: Silences alarm for 15 minutes.
     - `📖 Diagnostic Guide`: Opens step-by-step resolution modal with required tools, physical root cause, and 4-step mechanical procedure.
   - Simulated external IPC button: `+ Simulate External IPC Event (\pipe\SundaramNotificationBus)` allowing shop-floor testing of external software alerts.

2. **Cross-Process Notification Publisher Bus Contract (`dotnet/PlantEvents/PlantNotification.cs`)**:
   - `PlantNotification` record schema exposing `Id`, `Ts`, `SourceProcess`, `Plant`, `Line`, `Station`, `Camera`, `Category`, `Severity`, `Printer`, `Code`, `Title`, `MessageEn`, `LocalizedGuidance`, `Metrics`, `RecommendedAction`, `AckRequired`, `State`, `AcknowledgedBy`, and `AcknowledgedAt`.
   - IPC Transport constants: `DefaultPipeName = "SundaramNotificationBus"`, `DefaultSocketName = "sundaram_notif_bus.sock"`, and `DefaultRestPort = 52199`.
   - 100% roundtrip serialization unit tests passing in `dotnet/PlantEvents.Tests/PlantNotificationTests.cs`.

3. **OEM Factory Tab Upgrades (REQ-19 / PROT-34)**:
   - **Sliding-Window Duplicate Code Detection Configuration**:
     - Independent sliding-window buffer setting per camera channel (e.g. Cam 1: 250 counts, Cam 2: 100 counts, Cam 3: 500 counts, Cam 4: Disabled).
     - Configurable duplicate trigger actions (Reject Part & Signal Alarm, Reject & Pause Line, Alert Only).
     - Hardware hash acceleration using sliding FIFO CRC-32 ring buffer.
   - **Camera-to-Issue Master Inspection Mapping Matrix**:
     - Assigns specific defect detectors to specific physical camera channels based on what the camera inspects.
     - Configurable inspection trigger frequency: `Continuous (100% parts)`, `Periodic Interval (Every N counts / Random)`, or `Start-of-Job Baseline (One-time audit)`.

4. **Comprehensive Defect Diagnostics Suite (REQ-20 / PROT-35)**:
   - **TTO Barcode & Print Diagnostics**:
     - Printhead Element Burnout: Continuous white void line on dot row 84 -> Replace ceramic printhead.
     - Printhead Dirt / Wax Buildup: Contrast loss and soft edge fade -> Clean with 99% IPA swab.
     - GS1 Syntax & Format Errors: Missing FNC1 delimiter `[)>` -> Update layout template.
     - Over-Darkness & Thermal Bleed: Bar dilation +38% -> Reduce darkness energy by 12%.
     - Ribbon Wrinkle & Diagonal Voids: Lightning-shaped unprinted line -> Adjust ribbon mandrel tension.
   - **CIJ Printer Diagnostics**:
     - Head Angle Skew & Product Crabbing: Rotation error θ = +2.4° -> Square head and narrow guide rails ±1mm.
     - Nozzle Clog & Satellite Droplets: Vertical raster distortion -> Perform automated backflush cycle.
     - Line Speed Pitch Mismatch: Elongated or compressed dots -> Calibrate encoder pulse scaling.
   - **TIJ Printer Diagnostics**:
     - Head Away Blur (>2.0mm throw distance): Dot scattering and halo -> Move cartridge to 1.0mm gap.
     - Head Touching Smear (<0.5mm interference): Drag tails and ink smear -> Back off cartridge bracket 1.5mm.
     - Conveyor Guide Taper / Wedge Print: Diagonal product movement -> Re-align guide rails parallel to belt.
     - Decap Dry-Out: Horizontal orifice gap after line stop -> Purge and wipe nozzle plate.
   - **Motion Encoder & Conveyor Diagnostics**:
     - Scaling Mismatch: 2D DataMatrix rectangular (W/H=1.18) -> Adjust encoder divider in OEM Factory.
     - Encoder Wheel Slipping: Image stretch during acceleration -> Inspect spring arm & replace O-ring.
     - Conveyor Speed Sag: Belt speed drops 12% under infeed bunch -> Check drive motor capacity & belt tension.
   - **Applicator & Substrate Diagnostics**:
     - Hologram Centering Drift: Peel blade displacement -> Turn lead screw X by 1.85 turns CCW.
     - Target Surface Moisture: Bottle sweating / water droplets -> Wipe surface and engage air knife.
     - Illumination Falloff: Quadrant intensity drops > 20% -> Clean diffuser & inspect LED segment.
