# Three-Tier Role Navigation, Zero-Software Remote Support & Notification Dictionary (REQ-11..13, PROT-26..28)

- Status: done
- Session: three-tier-roles-and-notification-engine, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / ui / shop-floor inspection / localization
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, design/prototypes/vision-studio/index.html
- Depends on: REQ-08, REQ-09, REQ-10

## Summary
Enforces the mandatory directives established by the owner on 2026-11-03:
1. *"build the suggested. do i need any remote software like anydesk? or without any software can i take online?"*
2. *"it is good that user will not see any tools."*
3. *"I think there will be 3 tabs required 1. Operator who can operate line and change certain things (supervisor approval may be required). 2. Supervisor Tab for setting / approval etc. 3. OEM tab for detailed service support and setting. You need to decide which options to be given to whom and what approval workflow is required."*
4. *"4. I also need to write / approve text for each notification. OEM will enter once to show the details in english, Other languages can be added automatically."*

## Architecture & Implementation Details

1. **Zero-Software Remote Support Portal (REQ-11, PROT-26)**:
   - **No AnyDesk, TeamViewer, or VNC Needed**: Third-party remote desktop software is eliminated. The Line PC runtime exposes an embedded secure HTTPS / WebRTC micro-portal (`https://support.sundaram.com/line/GJ-04-LINE2` or local `https://line-pc:5443`).
   - **Display Isolation & Competitor Protection**: Remote OEM engineers access the full service console directly in their private desktop web browser via 2FA/TLS authentication. The physical factory monitor remains locked to generic Operator/Supervisor views with neutral DIN/IEC labels (`Position 1`, `2D Code`, `Image Check`), completely preventing visiting competitors from observing remote troubleshooting sessions.

2. **Three-Tier Role Navigation & 4-Eyes Dual-Control Approval Workflow (REQ-12, PROT-27)**:
   - **Operator Tab**:
     * Line operation, live dual camera streams, part counts (Good, NG, Reject), shift-start Golden Sample verification wizard, and non-critical alarm acknowledgement.
     * Zero visibility of vision tools, neural network models, or raw calibration parameters.
     * Sensitive operations (recipe changes, batch counter resets, tolerance nudges, inspection check bypass, clearing critical interlocks) are locked behind an on-screen **Supervisor Approval Modal**.
     * Operator initiates action $\to$ Modal requests Supervisor PIN (`4402` in demo) or RFID badge scan + reason code $\to$ authorized action executes and logs to the 21 CFR Part 11 audit trail.
   - **Supervisor Tab**:
     * Batch scheduling & active recipe configuration (`Whisky 750 ml`, `Whisky 1000 ml`, `Gin 750 ml`).
     * **Pending Approvals Queue**: Live table of operator change requests with 1-click `Authorize (PIN)` or `Reject` actions.
     * **Safe Narrow-Band Line Trims**: Supervisor can trim running line parameters within narrow pre-calibrated bounds ($\pm 5\%$ Ambient Light Trim slider, Consecutive NG Stop Interlock: 1 to 10 parts).
     * **Tooling Cavity Heatmap**: 8-head rotary capper attribution heatmap with Head #3 highlighted (84% NG) and direct maintenance dispatch action.
     * **Notification Review & Approval Gate**: Formal review portal where plant supervisor reviews and approves English notification text authored by OEM.
     * **21 CFR Part 11 Electronic Audit Log**: HMAC-SHA256 digitally signed event log.
   - **OEM Service Tab**:
     * Zero-Software Remote Support status indicator (`https://support.sundaram.com/line/GJ-04-LINE2` online).
     * VisionPro-style Engineering Workbench with generic neutral tool tree and property sheets.
     * Hardware calibration (strobe duration $\mu s$, camera exposure, gain, encoder divider).
     * Notification dictionary authoring with live auto-localization previews.

3. **Central Notification & Alarm Dictionary (REQ-13, PROT-28)**:
   - **OEM English Authoring**: OEM enters notification text once in clear, non-technical English with actionable physical shop-floor guidance (e.g. `ALM-101 Applicator Centering Drift`, `ALM-102 Target Surface Moisture`, `ALM-103 Strobe Output Warning`, `ALM-104 2D Code Print Degradation`).
   - **Owner / Supervisor Approval Gate**: Notifications enter `Pending Approval` until reviewed and formally approved by the plant owner or supervisor. Only approved notifications can be published to the running line.
   - **Automatic Multi-Language Localization Engine (i18n)**: Once English text is approved, the system automatically translates notifications into regional factory languages:
     * English (Master)
     * Hindi (हिन्दी)
     * Gujarati (ગુજરાતી)
     * Tamil (தமிழ்)
   - **1-Click Line Language Switcher**: Operators on the shop floor can switch their display language via top-bar pills (`EN`, `हिन्दी`, `ગુજરાતી`, `தமிழ்`), instantly rendering live alarms and guidance in their native language.

4. **Master Documentation & Rule Synchronization**:
   - Registered `REQ-11`, `REQ-12`, and `REQ-13` in Section 17.6 and `PROT-26`, `PROT-27`, and `PROT-28` in Section 18.9 of `docs/REQUIREMENTS-VISION-SYSTEM.md`.
   - Synchronized rule files `CLAUDE.md`, `AGENTS.md`, and `GEMINI.md`.
   - Updated prototype `design/prototypes/vision-studio/index.html` and synchronized reference artifact `vision-studio-workbench-reference.html`.
