# Mandatory Requirement-First Protocol for All Agents

- Status: done
- Session: mandatory-requirement-first-rule, 2026-11-03
- Pull request: direct/main
- Section 5 item: governance
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md
- Depends on: none

## Summary
Enforces the mandatory requirement-first protocol established by the owner on 2026-11-03:
"any change, decision or request must be registered in common requirement document first before acting. Make this rule for all agents."

1. **Registered in Common Requirements Document**:
   - Added Section 17.6 (Mandatory Requirement-First Protocol: REQ-01..04) in `docs/REQUIREMENTS-VISION-SYSTEM.md`.
   - Added Section 17.7 (Factory Environment Disturbances and Inline Auto-Tuning: ENV-01..07) capturing the 2026-11-02 owner directive on physical factory disturbances.
2. **CLAUDE.md Updated**:
   - Added Rule 0 (Requirement-First Protocol) at the very start of the numbered rules.
3. **Multi-Agent Rules (`AGENTS.md` and `GEMINI.md`)**:
   - Created root-level `AGENTS.md` and `GEMINI.md` guaranteeing that Antigravity, Gemini, Claude, and all subagents load and enforce the requirement-first protocol.
4. **Mirrored in `secure_me`**:
   - Mirrored the exact same rule and files in the `secure_me` repository (`docs/SecureMe-Requirements.md`, `CLAUDE.md`, `AGENTS.md`, `GEMINI.md`).
