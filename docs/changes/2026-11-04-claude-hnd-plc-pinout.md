# Req 17.16 HND-13/14: PLC family scope and camera Hirose wiring

- Status: done
- Session: claude (handover-gap-decisions), 2026-11-04
- Pull request: #<n>
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (17.16 HND-03, HND-13, HND-14)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #<n>: owner decided the two gaps left after PR #326) Req 17.16 HND-13/14: PLC family scope and camera Hirose wiring.

## Decisions (section 4)
| PLC families | The DVP slim CPU with expansion modules is the main PLC and is built first. Other PLCs are integrated when a machine needs one, each with its own map under the same handshake and safety rules. | 17.16 HND-13 |
| Camera Hirose wiring | Pin 1 power, pin 4 PLC trigger, pins 5 and 6 ground. The handover's pin 2/3 are not used. The pinout is recorded per camera model at commissioning. | 17.16 HND-14 |

## Update log (section 8)
| 2026-11-04 | Requirements 17.16: added HND-13 and HND-14; HND-03 now points to the HND-14 pins. | Claude (Mac, branch claude/hnd-plc-pinout) |

## Pending
- Req 17.16 HND-14: add the camera Hirose pin table to PLC-DELTA-DVP.md when the HND-03 hardware trigger is built.
