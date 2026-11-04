# Roadmap and requirement: auto learning, auto tuning, drag-and-drop tools, auto find of fields, best-in-world methods

- Status: done
- Session: roadmap-auto-setup, 2026-10-30
- Pull request: #145
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (new section 16, a link in section 15), docs/vision-system/ROADMAP.md (new), docs/vision-system/README.md (link)
- Depends on: none

## Section 5
- New: [x] (done 2026-10-30, PR #145: requirements section 16 AS-01 to AS-12, docs/vision-system/ROADMAP.md) Requirement and roadmap for automatic setup (auto-find with field types, auto-learn, auto-tune, learning on the line, drag and drop) and learning from Cognex, HALCON, Teledyne DALSA, Omron, National Instruments and others, aiming at near-100% accuracy on the owner's images with line safety first.

## Timeline (section 3)
| 2026-10-30 | Owner's goal for automatic setup and best-in-world methods written as requirements section 16 (AS-01 to AS-12); roadmap written with what is built, partly built and not built for each capability the owner named, and seven ordered steps with what "done" means | 4, 5, 6, 13, 15, 16 |

## Decisions (section 4)
| Learning from other products | Cognex, MVTec, Teledyne DALSA, Omron, National Instruments, Keyence, Zebra (Matrox) and SICK are benchmarks and sources of ideas from their published manuals, papers and patents only; no copied, decompiled or reused code or models; patents in force are designed around or licensed (owner, 2026-10-30) | 16 |
| "Almost 100%" | Measured on the owner's sample images at the line time targets, per product batch, recorded per release: codes at least 99.9% of visible codes with no wrong read, text at least 99.5% of characters, no bad part passing (owner, 2026-10-30) | 13, 16 |

## Update log (section 8)
| 2026-10-30 | Requirements section 16 (automatic setup and best-in-world methods) and a roadmap link in section 15; new docs/vision-system/ROADMAP.md; README reading list row 15 | Claude (Mac, branch claude/roadmap-auto-setup) |

## Pending
- AS-01 Auto-find on the whole image without drawing a box: every code with its type, every text block with each line's field type, the part and labels, offered as suggestions (AI-DEEP-LEARNING.md 2.1); in the Avalonia Vision Studio.
- AS-02 One-tap auto-learn for every tool type, and auto-tolerance from 10-30 good parts for every tool (today: Read code, Read text and Find the part; print-quality limits in Check text).
- AS-03 Auto-tune each tool and the camera on saved good and bad images for the widest margin within the time target (today only the code reader's Calibrator and TuningAgent).
- AS-05 Drag-and-drop job designer in the Avalonia Vision Studio: toolbox onto the image with snapping, flow view with wiring, templates (UI-UX-SPEC.md 4.7 and 5).
- AS-07 Written studies of Teledyne DALSA, Omron, National Instruments, Keyence and Zebra (Matrox), and of every tool family other than code reading.
- AS-10 Side-by-side accuracy run against a bought Cognex licence on the owner's images, recorded per release.
- AS-11 Close the code-reading gaps at line time targets: 48 DM 61/95, shrink-wrapped excise labels 129/214, 24 QR 372/384, 48 QR 843/857 (re-shoot on the current camera first).
