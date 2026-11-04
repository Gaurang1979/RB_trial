# Req 17.16 HND-01..12: Antigravity master handover compared with the requirements, owner decisions registered

- Status: done
- Session: claude (handover-gap-decisions), 2026-11-04
- Pull request: #326
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (17.16, REQ-11 and 18.9 URL), docs/vision-system/DIAGNOSTIC-THRESHOLD-REVIEW.md (new)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #326: compared the Antigravity master handover with the requirements; 12 differences decided one at a time by the owner and registered as 17.16 HND-01..12) Req 17.16: register the Antigravity master handover differences.

## Decisions (section 4)
| Antigravity master handover | OTP, notification bus and installer stay as MRG-01/02, REQ-18, MRG-06. DVP two-camera trigger and strobe go on an added output card (Y30–Y33). Operator badge only to run, two parts to change. Factory default camera mapping. REQ-20 thresholds stay the defaults and become OEM-adjustable, with handover values kept for a monthly review. New signatures, optional bracketing, auto-tune limits, sealer reject with advisory trim, network defaults, and the URL and dongle clean-ups. | 17.16 HND-01..12 |

## Update log (section 8)
| 2026-11-04 | Requirements: added 17.16 HND-01..12; fixed the support portal URL in REQ-11 and 18.9; added DIAGNOSTIC-THRESHOLD-REVIEW.md. | Claude (Mac, branch claude/handover-gap-decisions) |

## Pending
- Req 17.16 HND-03: two-camera hardware trigger + strobe outputs (Y30–Y33) on VisionLineDVP.st's handshake, and the PLC-DELTA-DVP.md output table.
- Req 17.16 HND-04: badge-only operator sign-in for running the line; a change asks for the second part.
- Req 17.16 HND-05: factory default camera-to-defect mapping in the OEM Factory tab.
- Req 17.16 HND-06/07: REQ-20 thresholds adjustable in the OEM tab (logged) and the seven new signatures with tests.
- Req 17.16 HND-08: optional two-frame exposure bracketing per camera.
- Req 17.16 HND-09: auto-tune P10/P50/P90 window with trim limits and alarm.
- Req 17.16 HND-10: LWIR sealer cold-spot NG with advisory PID trim.
- Req 17.16 HND-11: installer firewall (502 PLC card, UDP 3956 camera cards) and network defaults.
- Req 17.16 HND-06: monthly owner review of the diagnostic threshold defaults.
