# Four-Tier Role Segregation: Service Support vs. OEM Factory Setting (REQ-17, PROT-32)

- Status: done
- Session: four-tier-roles-service-and-oem-factory, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / authentication / four-tier-navigation
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, dotnet/VisionStudio/Services/UserStore.cs, design/prototypes/vision-studio/index.html
- Depends on: REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, REQ-17, PROT-32

## Summary
Enforces the mandatory directive established by the owner on 2026-11-03:
*"I need to add one more tab / login. Service tab. Service login will be for service team and support team. OEM tab will be for factory setting while manufacturing machine. OEM login will not be available to service team. OEM settings will be based on what hardware is used and all one time hardcode settings requirement, software features selection etc."*

## Architecture & Implementation Details

1. **Four-Tier Role Segregation (REQ-17 / PROT-32)**:
   - **Tier 1 — Operator Tab / Login:**
     - Dedicated strictly to running the production line, live camera streaming, real-time parts counting, non-critical warning acknowledgement, and Golden Sample shift-start verification.
     - Operators see zero vision tools or raw parameters.
     - Sensitive changes (recipe changes, counter resets, tolerance nudges, inspection bypass) strictly require 4-Eyes Supervisor authorization via on-screen PIN/RFID modal.
   - **Tier 2 — Supervisor Tab / Login:**
     - Customer's senior person (e.g. Senior Supervisor V. Patel).
     - Manages daily production scheduling, recipe assignment, pending 4-eyes operator approval requests, Golden Sample compliance audits, batch OEE & Upstream Cavity Pareto heatmaps, safe narrow-band tolerance nudging (±5% trim), and 21 CFR Part 11 electronic audit trail sign-offs.
     - Customer supervisors cannot edit notification text or access internal service/OEM settings.
   - **Tier 3 — Service Tab / Login (Field Service & Remote Support Team):**
     - Dedicated to Sundaram field service engineers and remote support personnel.
     - **Authentication:** Unlocked exclusively via dynamic Time-Based Daily Rolling OTP (REQ-14) or authenticated remote web portal (`support.sundaramtech.com`).
     - **Capabilities:** Field troubleshooting, optical focus verification, lens transmission check, camera exposure/gain trim, strobe trim, diagnostics, recipe fine-tuning, error telemetry dump, and live assisted line adjustments (while the shop-floor monitor stays active in Operator Run mode).
     - **Strict Factory Isolation:** Service engineers have NO credentials to access or alter factory manufacturing settings.
   - **Tier 4 — OEM Tab / Login (Machine Manufacturing & Assembly Build):**
     - Dedicated exclusively to Sundaram factory machine assembly builders while manufacturing the machine at the factory.
     - **Strictly NOT available to the field service team, remote support team, or customer personnel.**
     - **Authentication:** Factory Master Hardware Provisioning Certificate / Hardware Dongle.
     - **Capabilities:**
       1. *Hardware Selection & Mapping:* Selecting the physical hardware used for this machine build—camera brand & model (Hikrobot 20 MP, Basler ace 2, Daheng Mercury), optical lens focal length (25mm f/1.4, 35mm, 50mm), strobe controller model (SmartStrobe 4-Ch, Gardasoft), PLC brand & model (Delta AS300 EtherNet/IP, Delta DVP-ES2, Siemens S7-1200, Rockwell Micro850), and conveyor encoder divider.
       2. *Software Features Selection & Entitlements:* Selecting and locking the software features and inspection modules enabled on this machine build (1D Barcode, 2D DataMatrix, OCR/OCV, Target Surface Moisture, Upstream Cavity Heatmap, Thermal Packaging Seal, 3D Photometric Surface Texture, 21 CFR Part 11). Once locked at the factory, service engineers cannot toggle hardware types or licensed features without factory re-licensing.
       3. *One-Time Hardcode Settings Requirement:* One-time factory hardcoding of master notification dictionaries, machine serial numbers, hardware part serial number bindings, and customer License ID provisioning.

2. **Prototype Implementation (`design/prototypes/vision-studio/index.html`)**:
   - Added all 4 distinct screen switch tabs: `1. Operator Run`, `2. Supervisor`, `3. Service (Support)`, and `4. OEM Factory (Build)`.
   - Wired independent authentication modals:
     - `Service Login Modal`: Dynamic 6-digit Daily Rolling OTP (changes daily with date, time, License ID).
     - `OEM Factory Build Modal`: Factory Dongle / Master Manufacturing Key (`FACTORY-BUILD-PROV-2026`).
   - Built complete `mountSvc()` subviews (VisionPro Workbench, Optical & Strobe Tuning, Diagnostics & DB Audit).
   - Built complete `mountOEM()` factory subviews (Hardware Selection & Mapping, Software Features Selection & Entitlements, One-Time Hardcoded Settings & Part Binding).
   - Validated syntax with zero JavaScript errors, balanced braces/parens, and 100% interactive event listeners.

3. **Backend & Architecture Synchronization**:
   - Registered `REQ-17` and `PROT-32` in `docs/REQUIREMENTS-VISION-SYSTEM.md`.
   - Updated `CLAUDE.md`, `AGENTS.md`, and `GEMINI.md` with Four-Tier Role definitions.
   - Updated `dotnet/VisionStudio/Services/UserStore.cs` roles to include `Roles.Operator`, `Roles.Supervisor`, `Roles.Service`, and `Roles.OEM`.
