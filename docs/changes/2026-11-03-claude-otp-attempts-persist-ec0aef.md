# Keep the wrong-OTP count across a Vision Studio restart (Req 17.10 MRG-09 item 5)

- Status: done
- Session: otp-attempts-persist, 2026-11-03
- Pull request: #313
- Section 5 item: new
- Components/files: dotnet/VisionStudio/Services/OemSignIn.cs (OtpAttempts), dotnet/VisionStudio.Tests/OemSignInTests.cs, docs/REQUIREMENTS-VISION-SYSTEM.md (17.10 MRG-09 item 5)
- Depends on: none

## Section 5
- [x] (done 2026-11-03, PR #313: wrong Service/OEM codes and any hold kept in the protected `signin.dat`; a missing, edited, foreign, replaced or unwritable file holds every code for 15 minutes, across a restart, and is logged) Keep the wrong-OTP count across a Vision Studio restart (Req 17.10 MRG-09 item 5).

## Timeline (section 3)
| 2026-11-03 | Wrong-OTP lock survives a restart: `OtpAttempts` writes the wrong-code times and any hold to `signin.dat` in the data folder (DPAPI machine scope, as `station.dat`) after each change; a missing, unopenable, changed or unwritable file holds every user's Service and OEM codes for 15 minutes and is logged; 8 new tests, VisionStudio.Tests 119/119 | 17.10 MRG-09 |

## Decisions (section 4)
| Wrong-OTP count on disk | Tampering is answered by holding every user's Service and OEM codes for the lock window (15 minutes), which is never shorter than the lock it might clear, and the hold is written too, so deleting or editing `signin.dat` and restarting gains nothing. The other roles are never held. A first start (no file yet) also holds once, because it cannot be told from a deletion. Putting back an older copy or moving the clock as administrator is out of scope: an administrator can also replace the program. Bench PCs without DPAPI keep the count in memory | 17.10 MRG-09 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.10 MRG-09 item 5 and its acceptance: the count is kept on disk, protected, with the tamper hold | Claude (Mac, branch claude/otp-attempts-persist-ec0aef) |

## Pending
- Try `signin.dat` once on a Windows line PC with DPAPI: five wrong codes, restart the Vision Studio service, the lock holds; delete the file, restart, every code is held 15 minutes and the error is logged (Req 17.10 MRG-09 item 5).
