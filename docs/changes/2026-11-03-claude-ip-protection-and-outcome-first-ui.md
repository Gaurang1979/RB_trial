# IP Protection & Outcome-First UI Camouflage (REQ-07, PROT-20..23)

- Status: done
- Session: ip-protection-and-outcome-first-ui, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / ui
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, design/prototypes/vision-studio/index.html
- Depends on: REQ-06

## Summary
Enforces the mandatory directive established by the owner on 2026-11-03:
*"few tools name and charts show how we are processing images like autonomous environmental adaptation, hologram positioning, MTF zone etc. Is it good to show the same? or can work in back ground and auto tune and only show warning/suggestion. But how we are doing it should not go to competitors. make this rule for future also. now change the same as suugested in Vision System Software."*

1. **Common Requirement Document Registered**:
   - Added `REQ-07` in Section 17.6 and Section 18.9 (`PROT-20` to `PROT-23`) in `docs/REQUIREMENTS-VISION-SYSTEM.md`.
2. **Three-Tier Visibility & IP Camouflage Codified**:
   - `PROT-20`: Silent background execution for autonomous auto-tuning (ambient lux tracking, 9-zone MTF sharpness, polarimetric inpainting, latent PCA) without line stoppages.
   - `PROT-21`: Outcome-first trade names (`SmartShield™`, `OpticGuard™`, `Surface Relief`, `ClearGlare™`, `Tooling Advisor`, `Process Stability`). Raw mathematical terminology strictly banned from customer UI.
   - `PROT-22`: Actionable mechanical guidance for alarms (e.g. *"Turn Lead Screw X by 1.85 turns CCW"*), suppressing algorithm coordinates.
   - `PROT-23`: Three-tier role visibility; deep diagnostic visualizers (Split-Wipe slider, 9-zone MTF decimal grid, latent PCA plot) strictly gated inside Tier-3 OEM Service Mode.
3. **Vision Studio Workbench Prototype Updated**:
   - Updated `design/prototypes/vision-studio/index.html` and artifact `vision-studio-workbench-reference.html`.
   - Operator Run screen surfaces high-level status pills (`SmartShield™: Active`, `OpticGuard™: Healthy`) and direct shop-floor maintenance guidance in alarms.
   - Engineer Workspace displays outcome-branded tools and incorporates an OEM Service Mode toggle (`[🔒 OEM Mode: Off / 🔓 OEM Mode: Active]`), locking deep diagnostic HUDs behind Level-4 OEM authentication.
4. **Agent Rules Updated**:
   - Codified in `CLAUDE.md` (Rule 0), `AGENTS.md` (Section 3), and `GEMINI.md` (Core Rule 4).
