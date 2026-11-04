# Screen De-Identification, Surface Moisture & Line PC Upgrades (REQ-08..10, PROT-20..25)

- Status: done
- Session: screen-deidentification-and-ui-upgrades, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / ui / shop-floor inspection
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, design/prototypes/vision-studio/index.html
- Depends on: REQ-07

## Summary
Enforces the mandatory directives established by the owner on 2026-11-03:
1. *"there is no need to show exact process and feature names in engineer or oem service tab also. Support team may be working remotely and physically competitor can see what is happening on production line."*
2. *"antigravity suggested many upgrades and changes and product improvement but all are not shows in UI/UX. now suggest what can be done (no changes now)."*
3. *"Do not process Secure_Me in this chat now onwards. This session is only for Vision System."*
4. *"such instructions can be considered user requirements. and normal chatting can give many inputs as user requirements. can be saved in docuements for future need."*
5. *"i think there is some confusion. i dont need conveyor Moisture Alarm feature but moisture on inspecting surface like, label, barcode, bottle etc is required."*
6. *"For Part 1 using generic names is ok."*
7. *"For Part 2, i like Upstream Tooling Cavity Heatmap, Golden Sample Wizard, Hardware Health Indicators."*

## Architectural & Prototype Changes

1. **Common Master Requirements Document Updated (`docs/REQUIREMENTS-VISION-SYSTEM.md`)**:
   - Registered `REQ-08` (Complete Physical Screen De-Identification & Remote Diagnostic Decoupling), `REQ-09` (Target Surface Moisture Inspection on Labels, Barcodes & Bottles), and `REQ-10` (Upstream Machine Tooling Cavity Heatmap, Golden Sample Wizard & Hardware Health HUD) in Section 17.6.
   - Expanded Section 18.9 (`PROT-20` to `PROT-25`) to specify:
     * `PROT-20`: Silent background optical auto-tuning.
     * `PROT-21`: Complete physical screen de-identification with neutral generic labels (`Position 1`, `2D Code`, `1D Barcode`, `Text Line`, `Image Check`, `Surface Check`, `Seal Check`, `Batch Monitor`, `Machine Alignment`).
     * `PROT-22`: Target surface moisture inspection on inspected parts (cold-fill bottle condensation, label moisture bubbling, barcode optical distortion).
     * `PROT-23`: Actionable mechanical guidance (`Turn Lead Screw X by 1.85 turns CCW`).
     * `PROT-24`: Remote diagnostic decoupling (diagnostic streams routed strictly to remote support workstations over encrypted tunnel `127.0.0.1:5005/diag`).
     * `PROT-25`: Upstream Tooling Cavity Heatmap (8-head rotary attribution), Golden Sample Challenge Wizard (shift handover enforcement), and Hardware Health HUD (strobe lumen decay, encoder sync).

2. **Agent Rule Files Synchronized**:
   - `CLAUDE.md`: Rule 0 updated with REQ-08..10 citations, screen de-identification, surface moisture, and tooling/health HUD specifications.
   - `AGENTS.md`: Section 3 updated with PROT-20 to PROT-25 protocols.
   - `GEMINI.md`: Core Rule 4 updated with physical screen de-identification and surface moisture inspection rules.

3. **Vision Studio Workbench Prototype (`design/prototypes/vision-studio/index.html`) & Artifact Updated**:
   - **Operator Run Bar**: Added Hardware Health indicators (`Light: Balanced`, `Optics: 95% Sharp`, `Strobe: 98%`, `Line Sync: 100%`).
   - **Operator Toolbar**: Added `Golden Sample` button opening the Shift Handover Golden Sample verification wizard modal.
   - **Tool Verification Table & Alarms**: Neutral generic labels throughout; target surface moisture check (`Label/Bottle Dry ✓`, `Sweating on bottle neck`) added to inspection table and alarm logs.
   - **Operator Bottom Tabs**: Added `Tooling Cavity Heatmap` tab showing real-time 8-Head rotary capper/filler attribution highlighting Head #3 drift (84% NG).
   - **Engineer Workspace**: All proprietary algorithm and vendor names in tree, toolbox, overlays, property sheets, and status bar replaced with neutral generic DIN/IEC terms (`Position`, `2D Code`, `1D Barcode`, `Text Line`, `Image Check`, `Surface Check`, `Seal Check`, `Batch Monitor`).
   - **Diagnostic Overlays & Status Bar**: Mathematical plots (MTF 9-zone grid, raw split wipe, latent scatter) decoupled to remote diagnostic stream notice (`127.0.0.1:5005/diag`).
