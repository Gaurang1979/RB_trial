# Backend OEM Daily OTP, Line DB Security & Fixed Notification Dictionary (REQ-13, REQ-14, REQ-16)

- Status: done
- Session: backend-oem-otp-db-security-and-i18n, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / authentication / database-integrity / localization
- Components/files: erpnext/vision_licence/, mobile/plant_core/, mobile/plant_app/, dotnet/VisionCore/, dotnet/VisionReports/, dotnet/VisionStudio/
- Depends on: REQ-11, REQ-12, REQ-13, REQ-14, REQ-15, REQ-16, PROT-28, PROT-29, PROT-31

## Summary
Implements the core backend, mobile, ERPNext, and database security architecture requested by the owner on 2026-11-03:
1. *"OEM login OTP changes daily with time, date and there must be fixed script to generate the same, As per licence ID, OTP will be visible to OEM engineer or support team in ERPNext or mobile application."*
2. *"Line databse password also need to be secured. Operator lerns it and can directly change data in database."*
3. *"notification text setting will be fixed by OEM not supervisor. there will not be setting required but one time hardcoding can be done... OEM will enter once to show the details in english, Other languages can be added automatically."*

## Architecture & Implementation Details

1. **Deterministic Daily Rolling OTP Engine (REQ-14 / PROT-29)**:
   - **Cross-Platform Parity:** Developed identical deterministic 32-bit signed hashing across Python (ERPNext), Dart (Flutter Mobile), C# (.NET Core), and JavaScript (HTML prototype).
   - **ERPNext Integration:**
     - Added `calculate_daily_otp()` and `verify_daily_otp()` to `erpnext/vision_licence/vision_licence/core.py`.
     - Added `@frappe.whitelist() get_oem_daily_otp()` in `erpnext/vision_licence/vision_licence/api.py` for authenticated support team queries.
     - Unit tested in `erpnext/vision_licence/tests/test_otp.py` (100% pass).
   - **Mobile App (`mobile/`):**
     - Implemented `OemOtp` in `mobile/plant_core/lib/src/oem_otp.dart` and exported via `plant_core.dart`.
     - Built `OemOtpScreen` in `mobile/plant_app/lib/modules/vision/oem_otp_screen.dart` allowing support engineers to select any customer License ID and view today's 6-digit rolling code with copy-to-clipboard.
     - Tested in `mobile/plant_core/test/oem_otp_test.dart` and `mobile/plant_app/test/oem_otp_screen_test.dart` (100% pass).
   - **.NET Windows Line Application (`dotnet/`):**
     - Implemented `OemOtpVerifier` in `dotnet/VisionCore/Licensing/Licence.cs`.
     - Unit tested in `dotnet/VisionCore.Tests/LicenceTests.cs` (100% pass, matching test vector `318917`).

2. **Line Database Security & Tamper-Evident Ledger (REQ-16 / PROT-31)**:
   - **Zero Cleartext Passwords:** Hardware-bound cryptographic key derivation in `dotnet/VisionReports/DatabaseSecurity.cs` (`DeriveHardwareBoundKey`), dynamically binding the database encryption key to machine credentials, system pepper, and License ID.
   - **Cryptographic Chained Hash Ledger:** Added `chain_hash` column to `inspections` and `parts` tables in `dotnet/VisionReports/ResultLog.cs`. Each row links to the previous row via `HMAC-SHA256(prev_hash + row_data, key)`.
   - **Tamper Detection Audit (`VerifyLedgerIntegrity`):** If an operator opens `results.db` directly and alters inspection verdicts (e.g. flipping `ok` or `rejected`), `VerifyLedgerIntegrity()` detects the broken HMAC chain and throws `DatabaseTamperingException`, halting the line per `PROT-05` (Fail Closed).
   - Unit tested in `dotnet/VisionCore.Tests/DatabaseSecurityTests.cs` (100% pass).

3. **Fixed OEM Hardcoded Notification Dictionaries & Localization (REQ-13 / PROT-28)**:
   - Hardcoded master notification dictionaries across 4 plant languages:
     - `dotnet/VisionStudio/i18n/en.json` (English)
     - `dotnet/VisionStudio/i18n/hi.json` (Hindi)
     - `dotnet/VisionStudio/i18n/gu.json` (Gujarati)
     - `dotnet/VisionStudio/i18n/ta.json` (Tamil)
   - Covered alarms: `ALM-101` (Machine Alignment / Lead Screw), `ALM-102` (Target Surface Moisture), `ALM-103` (Hardware Strobe Health), `ALM-104` (2D Code Print Quality), `ALM-105` (Upstream Tooling Cavity), and `ALM-801` (Database Tampering Detected).

4. **Three-Tier Role Navigation (REQ-12 / PROT-27)**:
   - Updated `dotnet/VisionStudio/Services/UserStore.cs` roles to include `Roles.Operator`, `Roles.Supervisor`, `Roles.OEM`.

## Test Results
- **Engine C++ Suite:** 23/23 tests passed (100%).
- **Mobile Plant Core (Dart):** 52/52 tests passed (100%).
- **Mobile Plant App (Flutter):** 87/87 tests passed (100%).
- **ERPNext Python Suite:** 4/4 tests passed (100%).
- **.NET 10 VisionCore.Tests:** 743/743 tests passed (100%).
- **.NET 10 Studio.Tests:** 246/246 tests passed (100%).
- **Total Passing Tests:** 1,155 passed, 0 failed.
