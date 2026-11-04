# Requirements: Part 11 compliance report and 1-year retention on the plant server, dispatch wiring diagram and SOP, hardware chosen once at installation

- Status: done
- Session: claude/part11-report-dispatch-hw, 2026-10-31
- Pull request: #186
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (sections 11, 12, 15)
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #186) Requirements: Part 11 compliance report and storage (PT11-25 changed, PT11-27 and PT11-28 added), dispatch documents printed only (HW-10 to HW-15) and hardware chosen once at installation (HW-01 to HW-04).

## Timeline (section 3)
| 2026-10-31 | Requirements written for three owner inputs: Part 11 compliance report made on the plant server and fetched by the line; audit storage at least 1 year on the server and 1–2 weeks on the line PC, removed from the line only after the server acknowledges; Admin-only printing of the station's wiring diagrams and SOP from Vision Studio, on a physical printer only (no download, no PDF), for dispatch with the delivery challan; PLC and all hardware chosen in the first-installation setup only. Documents only. | 11, 12, 15 |

## Decisions (section 4)
| Part 11 storage and report | Owner, 2026-10-31: the plant server keeps the full audit trail and records for at least 1 year (Admin may set longer); a line PC keeps 1–2 weeks (default 14 days) and removes an entry only after the server has acknowledged it. The compliance report PDF is built on the server; the line only fetches it at low priority and offers no partial report when the server is away. Replaces the earlier default "product expiry plus one year, at least 5 years". | 12 (PT11-24 to PT11-28) |
| Dispatch documents | Owner, 2026-10-31 (second message the same day replaces "download"): an Admin prints the station's own wiring diagrams and SOP from the Vision Studio Admin panel only, on a physical printer only; no download, no PDF, no virtual printers. Pages carry station, serials, versions and page numbers, with space for hand signatures, to go signed with the delivery challan; each print is an audit entry. | 11 (HW-10 to HW-15) |
| Hardware chosen once | Owner, 2026-10-31: the PLC and all other hardware are chosen in the first-installation setup only; afterwards the software offers no hardware choice. Tuning the chosen hardware (camera settings, PLC signal mapping, line settings, bypass) is not a hardware choice. The 2026-10-29 words "after changing and selecting PLC make and model" are followed by the later words; the Vision Package host's camera-role choice is put to the owner in section 15. | 11 (HW-01 to HW-04), 12, 15 |

## Update log (section 8)
| 2026-10-31 | Requirements: section 11 "Installation and dispatch" (HW-01 to HW-04, HW-10 to HW-15); section 12 PT11-24 to PT11-26 changed, PT11-27 and PT11-28 added, PT11-40 points at PT11-25, Line & wiring notes the PLC is chosen at installation; section 15 Part 11 answer extended and three new open questions (hardware replacement, hardware choice after installation, PLC program per PLC and process) | Claude (Mac, branch claude/part11-report-dispatch-hw) |

## Pending
- Build the Part 11 compliance report PDF on the plant server (PT11-27) and the line "fetch report" button that downloads it at low priority (PT11-28).
- Local audit pruning on the line PC after plant server acknowledgement, with the 14-day default and the low-space warning (PT11-25); decide what happens when the local store is full while the server is away.
- Dispatch printing in the Vision Studio Admin panel: wiring diagrams and SOP made from the station's configuration, physical printers only (virtual and PDF printers refused), audited (HW-10 to HW-15).
- Remove hardware-choice options after installation (HW-02) from Vision Studio and the setup docs: UI-UX-SPEC 4.16 says "Each step can be revisited from Settings" (Cameras and PLC steps) and the PLC step allows "Set up later"; the Line & wiring page and PLC-INTERFACE.md text about changing the PLC make and model; the Vision Package API camera-role choice (section 10) awaits the owner (section 15).
- Owner to decide: how the PLC program follows the chosen PLC and process, and whether Vision Core generates it (section 15 open question).
