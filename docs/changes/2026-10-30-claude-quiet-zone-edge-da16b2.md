# Engine 1D quiet zone at the image's border graded only inside the image (F when cut short); same for PDF417 grading

- Status: done
- Session: quiet-zone-edge, 2026-10-30
- Pull request: #109
- Section 5 item: Engine 1D quiet zone at the image's border
- Components/files: engine/src/grade1d.cpp, engine/src/grade_pdf417.cpp (grading only), engine/tests/engine_tests.cpp
- Depends on: none

## Section 5
- [x] (done 2026-10-30, PR #109: 1D scan lines and PDF417 rows note which samples lie inside the image; the quiet zone is measured only there, and one the image's edge cuts short grades F, as the .NET grade report already measures it) Engine 1D quiet zone at the image's border: grade1d.cpp samples with BORDER_REPLICATE, so a quiet zone cut off by the image's edge can grade clear. Measure it only inside the image and grade it F when it is too short (line safety). The report already does this.

## Timeline (section 3)
| 2026-10-30 | Engine grading: a 1D quiet zone is measured only inside the image (each scan line's in-image samples, as the .NET report's LinearAnalysis does), so one the image's left or right edge cuts below the type's minimum grades F and the Read code tool's lowest grade fails it; DataBar Limited's guard light the same. PDF417: a quiet-zone sample off the image counts dark and grades the quiet zone F. Engine tests: Code 39 with 5 X left at either edge F, cut at the label's edge unchanged; PDF417 with 1 X left at either edge F, 3 X kept unchanged | 12, grading |

## Decisions (section 4)
| Quiet zone at the image's edge | The engine no longer counts the repeated edge pixels beyond the image as quiet zone: a 1D scan's quiet zone ends at the image's edge, and a PDF417 quiet-zone sample off the image is dark and makes the quiet zone F. A zone that cannot be seen is in doubt, so the code fails (line safety); the engine and the .NET report now agree | 12, grading |
