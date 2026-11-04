# Dual-Head Remote Assistance via support.sundaramtech.com & Zero-Knowledge DB Security (REQ-15, REQ-16, PROT-30, PROT-31)

- Status: done
- Session: dual-head-remote-and-db-security, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / remote-collaboration / database-protection
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, design/prototypes/vision-studio/index.html
- Depends on: REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, PROT-30, PROT-31

## Summary
Enforces the mandatory directives established by the owner on 2026-11-03:
1. *"while engineer is working from remote place vis support.sundaramtech.com can line see operator login instead of blank screen? OEM engineer see different engineer screen and settings while operator see operator screen. Operator atleast can help in adjust product etc if it is visible to operator."*
2. *"Line databse password also need to be secured. Operator lerns it and can directly change data in database."*

## Architecture & Implementation Details

1. **Dual-Head Independent Concurrent Remote Collaboration (REQ-15 / PROT-30)**:
   - **Active Operator Screen (Never Blank):** When a remote Sundaram support engineer connects via `support.sundaramtech.com`, the physical plant floor monitor does NOT go blank, freeze, or display a lock screen.
   - **Dual-Session Architecture:** The remote engineer's web browser renders the OEM Engineering Console (VisionPro tools, calibration sliders, raw diagnostics) independently, while the physical 24" line PC monitor remains 100% active in Operator Run mode.
   - **Shop-Floor Assisted Maintenance:** The plant operator can observe live camera streams, real-time bounding boxes, and alignment guidance (`Lead Screw: Turn 1.85 turns CCW`), enabling them to physically help adjust the product, re-align guides, or wipe lens covers while receiving immediate visual feedback.
   - **Competitor Screen Shielding:** Visiting competitors or unauthorized plant floor observers see only clean, generic DIN/IEC labels (`Position 1`, `2D Code`), preventing intellectual property exposure.

2. **Zero-Knowledge Line Database Security & Cryptographic Tamper-Proof Ledger (REQ-16 / PROT-31)**:
   - **Zero Cleartext Password Policy:** Database passwords and keys are strictly prohibited from plaintext configuration files (`.json`, `.config`, `.ini`, or registry keys).
   - **OS Service-Level NTFS Isolation:** The database directory (`%ProgramData%\Sundaram\VisionSystem\data\`) is owned exclusively by `NT SERVICE\SundaramVisionSvc` and `SYSTEM`. Standard Windows desktop user accounts (`Operator`) are configured with explicit NTFS `Deny` permissions, preventing direct browsing, copying, or opening of SQLite/SQLCipher files.
   - **AES-256-GCM Encryption at Rest:** Local database files are encrypted using SQLCipher. The encryption key is generated and unwrapped at runtime via Windows DPAPI and bound to the motherboard TPM 2.0 security chip.
   - **Authenticated IPC:** Front-end UI processes (Operator Run, Supervisor Approval) communicate exclusively through an authenticated Windows named pipe (`\\.\pipe\vs_secure_ipc`); the UI process possesses no database connection string or direct file handle.
   - **HMAC-SHA256 Chained Hash Ledger:** Every batch counter, inspection verdict, and audit log record is cryptographically linked with HMAC-SHA256 signatures. If an operator or attacker alters a database cell using external hex/sqlite editors, the hash chain is broken; the system detects tampering at startup, raises `ALM-801: Database Tampering Detected`, and fails closed per `PROT-05`.

3. **Interactive Prototype Verification**:
   - Operator top bar updated with interactive `Remote Collab: Active` badge (explaining dual-head assistance via `support.sundaramtech.com`) and `DB: Secured` chip.
   - OEM banner updated to reflect live session on `https://support.sundaramtech.com/line/GJ-04-LINE2` with active factory operator mode indication.
   - OEM Hardware & DB Security tab provides interactive audit button verifying AES-256 SQLCipher encryption, DPAPI/TPM binding, OS service isolation, and HMAC-SHA256 hash ledger integrity.
   - Syntax and interactivity verified with zero JavaScript errors.
