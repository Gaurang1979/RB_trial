# Dual-Tier OTP Authentication for OEM Factory Build & Mandatory Audit Warning (REQ-21, PROT-36)

- Status: done
- Session: oem-factory-otp-and-audit-warning, 2026-11-03
- Pull request: open for owner manual merge (PR #262)
- Section 5 item: governance / security / authentication / dual-tier-rolling-otp
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md, erpnext/vision_licence/vision_licence/core.py, erpnext/vision_licence/vision_licence/api.py, erpnext/vision_licence/tests/test_otp.py, mobile/plant_core/lib/src/oem_otp.dart, mobile/plant_core/test/oem_otp_test.dart, dotnet/VisionCore/Licensing/Licence.cs, dotnet/VisionCore.Tests/OemOtpVerifierTests.cs, design/prototypes/vision-studio/index.html
- Depends on: REQ-14, REQ-17, REQ-21, PROT-32, PROT-36

## Summary
Enforces the mandatory directive established by the owner on 2026-11-03:
*"OEM Factory (Build) (Sundaram factory assembly builders) authentication will be also from OTP as sometimes after shipping machine changes required and at that physical key will not be available. Only selected employees will be given this doctype right in erpnext for factory setting OTP. (Show notice to OEM settings user - "Do not share OTP with other users as change log is generated with values, It will be audited in your name - Risky.")"*

## Architecture & Implementation Details

1. **Elimination of Physical Key Bottleneck & Dual-Tier Rolling OTP (REQ-21 / PROT-36)**:
   - For shipped machines operating remotely at customer facilities, physical dongles or hardware keys cannot be plugged in when machine-level reconfiguration (hardware mapping, encoder calibration, software module licensing) is required.
   - Dual-tier cryptographic rolling OTP architecture:
     - **Tier 3 Service Support OTP:** Service secret (held outside the repository). Generated daily for certified field service engineers and remote support technicians via ERPNext / Mobile App. Permits optical tuning, camera exposure/gain adjustments, recipe fine-tuning, and assisted line adjustment.
     - **Tier 4 OEM Factory Setting OTP:** Separate factory secret (held outside the repository). Generated daily exclusively for authorized Sundaram factory assembly builders/admins. Unlocks physical hardware component selection, encoder pulse multipliers, camera-defect master inspection mapping matrix, and software module entitlements.
   - Cryptographic segregation guarantees that a Service OTP cannot unlock OEM Factory settings and vice-versa.

2. **ERPNext Role-Based Governance (`Vision Factory Setting OTP`)**:
   - `get_factory_setting_otp(license_id, target_date)` in `erpnext/vision_licence/vision_licence/api.py`:
     - Checks user roles for `"OEM Factory Admin"` or `"System Manager"`. Standard `"OEM Service Engineer"` role is denied with `frappe.PermissionError`.
     - Logs each OTP issuance to `Vision Factory OTP Issuance Log` with employee ID, user, timestamp, target date, and machine license.
     - Returns OTP along with mandatory warning notice.

3. **Mandatory Security Warning & Parametric Change Log**:
   - Mandatory notice rendered in high-visibility amber box across ERPNext, mobile, C# line core, and Line PC UI:
     `"Do not share OTP with other users as change log is generated with values, It will be audited in your name - Risky."`
   - Every parameter change in Tier 4 OEM Factory mode generates an audit log entry recording:
     `{ timestamp, authorized_user, component, parameter, previous_value, new_value, verification_hash }`.

4. **Multi-Platform Implementations & Test Verification**:
   - **Python / ERPNext (`erpnext/vision_licence`):**
     - `core.py`: Added the factory OTP functions (secret from site_config.json; HMAC-SHA256 after the merge review), `AUDIT_WARNING_NOTICE`, `calculate_factory_otp()`, and `verify_factory_otp()`.
     - `api.py`: Added `get_factory_setting_otp()` with role verification and audit issuance logging.
     - `tests/test_otp.py`: 7/7 unit tests passing (100%).
   - **Flutter / Dart (`mobile/plant_core`):**
     - `lib/src/oem_otp.dart`: Added the factory OTP constants (removed in the merge review: the app holds no secret), `auditWarningNotice`, `calculateFactoryOtp()`, and `verifyFactoryOtp()`.
     - `test/oem_otp_test.dart`: 6/6 unit tests passing (100%).
   - **C# / .NET 10 (`dotnet/VisionCore`):**
     - `Licensing/Licence.cs`: Added the factory OTP verifier (secret from the environment after the merge review), `AuditWarningNotice`, `CalculateFactoryOtp()`, and `VerifyFactoryOtp()` to `OemOtpVerifier`.
     - `dotnet/VisionCore.Tests/OemOtpVerifierTests.cs`: Verified cryptographic vector and verification. 746/746 tests passing in `VisionCore.Tests`.
   - **Interactive Prototype (`design/prototypes/vision-studio/index.html`):**
     - Replaced dongle requirement with rolling OTP entry and auto-fill helper.
     - Added prominent warning box with the exact mandatory string.
     - Added persistent top-banner audit notice in OEM Factory mode.
     - Verified clean HTML with 0 duplicate IDs and balanced JS delimiters.
