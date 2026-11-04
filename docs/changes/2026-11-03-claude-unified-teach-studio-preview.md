# Merge review of PR #262 (Antigravity): conflicts, OTP secrets, ledger key, PLC and installer

- Status: done
- Session: PR #262 merge preparation (Claude), 2026-11-03
- Pull request: #262
- Section 5 item: new
- Components/files: AGENTS.md, CLAUDE.md, GEMINI.md, docs/REQUIREMENTS-VISION-SYSTEM.md (12.6, 17.6, 17.8, 17.10), dotnet/VisionCore/Licensing/Licence.cs, dotnet/VisionReports/{DatabaseSecurity,ResultLog}.cs, erpnext/vision_licence (core.py, api.py, tests), mobile/plant_core (oem_otp.dart), mobile/plant_app (oem_otp_screen.dart), design/prototypes/vision-studio/index.html; removed plc/delta-dvp/VisionLineDualCameraTrigger.st, tools/installer/VisionStudio_Installer.iss, tools/windows/package-installer.bat
- Depends on: none

## Decisions (section 4)
| Topic | Decision and reason | requirement sections |
| --- | --- | --- |
| Rule files at the #262 merge | Main's single AGENTS.md and the CLAUDE.md / GEMINI.md stubs kept; #262's product bullets live in requirements 18.9, not in rule files (owner, 2026-11-03) | Req 17.9 AGR-01, AGR-08 |
| #262 requirements placement | REQ-06 stays in 17.6; REQ-07..22 moved unchanged to new 17.10; 17.8 and 17.9 both kept; 12.6 closed-loop kept as advisory only (owner, 2026-11-03) | Req 12.6, 17.6, 17.8, 17.10 MRG-07 |
| OTP secrets | Out of the repository, fail closed, HMAC-SHA256 shared by C# and Python, mobile app asks ERPNext, old values treated as exposed (owner, 2026-11-03) | Req 17.10 MRG-01..03 |
| Results ledger key | Per-installation key from commissioning config, no pepper in source, not tied to the PC name; no key = rows kept unchained with a ledger_off event (owner, 2026-11-03) | Req 17.10 MRG-04 |
| #262 PLC trigger and installer | Not taken: the ST file did not compile and let unread parts pass; the installer shipped named DLLs and duplicated VisionSystem.iss (owner, 2026-11-03) | Req 17.10 MRG-05, MRG-06 |

## Pending
- Build the two-camera trigger on VisionLineDVP.st's handshake: no result in time is NG, match by part ID, check the reject (X4) (Req 17.10 MRG-05).
- Add what is useful from #262's installer (firewall rules, PLC templates folder) to tools/windows/VisionSystem.iss under the camouflage rules (Req 17.10 MRG-06).
- ERPNext sign-in in the mobile app and an ERPNext OemOtpSource, then route the OEM OTP screen (Req 17.10 MRG-03).
- Line PC: store VS_SERVICE_OTP_SECRET, VS_FACTORY_OTP_SECRET and VS_LEDGER_KEY protected (DPAPI) at commissioning, and wire the Service / OEM logins to OemOtpVerifier (Req 17.10 MRG-01, MRG-04).
- ERPNext: create the "OEM Service" and "OEM Factory Admin" roles and set vision_service_otp_secret / vision_factory_otp_secret in site_config.json (Req 17.10 MRG-02).
- Results ledger: chain fail_tool and reason too, and keep the chain right when two programs write results.db (Req 17.10 REQ-16).
- Vision Studio strings alm.101..105 and alm.801 exist only in en, hi, gu, ta: add the other 11 languages (Req 7).
