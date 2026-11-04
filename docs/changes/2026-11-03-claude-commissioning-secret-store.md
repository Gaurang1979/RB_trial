# Line PC: store the OEM OTP secrets and the ledger key protected at commissioning; wire Service / OEM logins to OemOtpVerifier (Req 17.10 MRG-01, MRG-04)

- Status: done
- Session: commissioning-secret-store, 2026-11-03
- Pull request: #296
- Section 5 item: Line PC: store VS_SERVICE_OTP_SECRET
- Components/files: dotnet/VisionCore/Licensing (CommissioningSecrets.cs new, Licence.cs), dotnet/VisionStudio (Program.cs, Services/OemSignIn.cs, Services/SecretsCommand.cs, Login.razor, i18n/en.json), dotnet/Runtime.Host/RuntimeHost.cs, tests, docs/REQUIREMENTS-VISION-SYSTEM.md (MRG-09)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #296: protected store `station.dat` behind `ISecretProtector`, DPAPI machine scope on Windows, test double in tests; `VisionStudio secrets` command reads values from standard input; Service and OEM roles only with today's OTP, 5 wrong codes lock that user's codes for 15 minutes; ledger key from the store in Vision Studio and the runtime; Req 17.10 MRG-09) Line PC: store VS_SERVICE_OTP_SECRET, VS_FACTORY_OTP_SECRET and VS_LEDGER_KEY protected (DPAPI) at commissioning, and wire the Service / OEM logins to OemOtpVerifier (Req 17.10 MRG-01, MRG-04).

## Timeline (section 3)
| 2026-11-03 | Commissioning secrets stored protected: one DPAPI blob (`station.dat`) holds the service and factory OTP secrets and the ledger key; store first, environment second, an unreadable store gives nothing; `VisionStudio secrets status/set/new-ledger-key`; Service and OEM sign-in need today's OTP, 5 wrong codes lock the codes for 15 minutes; tests with a test double (DPAPI not run on the Mac) | 17.10 MRG-01, MRG-04, MRG-09 |

## Decisions (section 4)
| Commissioning secret protection | DPAPI machine scope with a fixed entropy, not CurrentUser: Vision Studio and the runtime run as different service accounts and commissioning runs as an administrator; the data folder's NTFS rights (REQ-16 item 2) keep other users out, and a copy cannot be opened on another PC. The whole file is one blob so the names are hidden too. Where there is no platform protection the store neither reads nor writes; the environment still serves bench PCs. | 17.10 MRG-09 |
| Unreadable store | A store that exists but cannot be opened gives no secret at all, not the environment's: OTP sign-in is refused and the ledger runs unchained (rows kept, `ledger_off`), never a guessed key. | 17.10 MRG-09, 12 |
| Ledger key replacement | An existing ledger key is replaced only with `--replace`, because a new key leaves the rows chained before it unverifiable; OTP secrets rotate freely. | 17.10 MRG-04, MRG-09 |
| Service / OEM sign-in | The password signs the user in; the Service role needs today's service OTP, the OEM role today's factory OTP for this PC's licence key. Without the code the user keeps only their other roles; with none left the sign-in is refused. The code is never logged; each grant or refusal is logged with the user and tier. | 17.10 MRG-09, REQ-14, REQ-21 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.10: MRG-09 (commissioning secrets kept protected, Service/OEM sign-in with OTP, wrong-code limit) | Claude (Mac, branch claude/commissioning-secret-store) |

## Pending
- Try the DPAPI store once on a Windows line PC: `VisionStudio secrets set` / `new-ledger-key` as administrator, then Vision Studio and LineRunner (other service accounts) read the values; a copy of `station.dat` on another PC gives no value (Req 17.10 MRG-09).
- Installer: set the data folder's NTFS rights (service accounts and SYSTEM only) and run `VisionStudio secrets` at commissioning (Req 17.10 MRG-09, REQ-16 item 2).
- Keep the wrong-OTP count across a Vision Studio restart (today it is in memory) (Req 17.10 MRG-09 item 5).
- Gate the Service and OEM screens on the `Service` / `OEM` roles once those screens exist in Vision Studio (Req 17.10 REQ-14, REQ-21).
- Translate login.otp, login.otp_warning, login.no_role and login.otp_locked into the other 14 languages (Req 7).
