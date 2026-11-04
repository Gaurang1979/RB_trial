# Vision Studio UI/UX screen map and owner UI inputs checked against the requirements

- Status: done
- Session: studio-ui-map (Claude, Mac), 2026-10-30
- Pull request: #143
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, docs/vision-system/UI-UX-SPEC.md (wording only, no code)
- Depends on: none (merge after #144, which also edits the requirements; no shared lines)

## Section 5
- [x] (done 2026-10-30, PR #143: screen map of Vision Studio, real screenshots and spec mockups in a private page for the owner; owner UI/UX inputs added to requirements 3, 6.1, 6.4, 7 and UI-UX-SPEC 4.15, 4.18, 9) Vision Studio UI/UX screen map and owner UI inputs checked against the requirements.

## Timeline (section 3)
| 2026-10-30 | Vision Studio screen map: Run, Sign in and Change product built; NG viewer, What do I do? and Language and colours built in part; 14 spec screens designed only; 6 old web-app pages with no Studio design. Screens rendered from dotnet/Studio with Avalonia headless (1366 × 768 and 1920 × 1080, dark, light, high contrast, one against the stub runtime) | 6 |

## Decisions (section 4)
| Application name | Vision Studio, on screen, in the installer and in licences (owner, 2026-10-30) | 3 |
| Daily Auto calibrate | On the Run screen; it also learns from the line's good and bad images and tunes the tools (owner, 2026-10-30) | 6.1 |
| Each part's result | Listed and kept at any line speed, because the report needs it (owner, 2026-10-30) | 6.1 |
| Operator side menu, Stop reason | The Operator has no side menu; Stop asks no reason (owner, 2026-10-30) | 6 |
| Next language after English | Not now (owner, 2026-10-30) | 7 |
| Job saving and reload | Owner, 2026-10-30: "Job saving and reload option also required." Save, Save as, Open job, Reload (drop unsaved changes), last job loaded after a restart | 8 |
| Old web pages in Vision Studio | Every operating page of the code-reader Dashboard and the browser VisionStudio is mapped to a Vision Studio screen (UI-UX-SPEC 4.18); Products, Start batch, Tuning, daily Auto calibrate, "why not read" advice and Wiring and instruments have no Studio design yet; Products, Start batch and Wiring and instruments get their own screens (owner, 2026-10-30) | 3, 6 |

## Update log (section 8)
| 2026-10-30 | Requirements 3 (Vision Studio, VisionPro-like, one application), 6.1 (live panels, NG counts, duplicate orange, daily Auto calibrate), 6.4 (SecureMe look, one token file, gloves), 7 (14 languages deferred, requirement kept), 8 (job saving and reload), owner answers on name, Auto calibrate and per-part results; UI-UX-SPEC 4.15 Code Reader settings, new 4.18 old pages, Job menu in 4.7, Operator menu and Stop in 2 and 4.1, open questions answered in 9 | Claude (Mac, branch claude/studio-ui-map) |

## Pending
- Vision Studio top bar: side menu and speaker buttons (UI-UX-SPEC 2, 3).
- Vision Studio NG image viewer: Show good part, Use for teaching, Was OK, AI heatmap (UI-UX-SPEC 4.3).
- Vision Studio "What do I do?": picture per step, Try again, Call supervisor, Read aloud (UI-UX-SPEC 4.4).
- Vision Studio Change product: Now tag, "Use this product", search and scan, Not ready and lock states (UI-UX-SPEC 4.2).
- Vision Studio Run: NG-reason and PC-health panels, Reset counts, Adjust strictness (UI-UX-SPEC 4.1).
- Vision Studio Sign in: keep the PIN pad compact at 1920 × 1080 (it spreads out vertically today).
- Vision Studio Teach wizard, job designer and tool properties in Avalonia (UI-UX-SPEC 4.6-4.8).
- Vision Studio PLC signals, Reports, Settings and Users in Avalonia (UI-UX-SPEC 4.11, 4.12, 4.14, 4.15).
- Rename the application to Vision Studio wherever "Vision Core" is shown to users (owner, 2026-10-30).
- Design and build the Products, Start batch and Wiring and instruments screens (UI-UX-SPEC 4.18).
- Daily Auto calibrate on the Run screen that learns from good and bad images and tunes the tools (requirements 6.1).
- Per-part result list in Vision Studio and the reports at full line speed (requirements 6.1).
- Job menu in Vision Studio: Open job, Save as, Reload, Export, Import (UI-UX-SPEC 4.7, requirements 8).
