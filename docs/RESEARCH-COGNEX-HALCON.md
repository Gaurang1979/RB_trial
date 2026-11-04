# How Cognex and HALCON read codes, and what this reader takes from them

Research notes, 2026-10-23. Public documentation only: neither vendor publishes its algorithms,
so the "how" below is what their manuals and announcements say, plus what follows from it.

## The common pipeline

Both follow the same stages; they differ in how much they learn and how hard they search.

| Stage | Cognex (DataMan, VisionPro ID) | MVTec HALCON (`find_data_code_2d`) | This reader |
| --- | --- | --- | --- |
| Image setup | Setup Tool auto-tuning of exposure, gain, light and reading height; application assistants for trigger and exposure | Done in the application (camera parameters), not in the code reader | **Auto calibrate** (#10): exposure and gain, crop, motion blur limit, trigger delay, light timing |
| Region find | Shape search for the finder and clock patterns; **PowerGrid**: a second, texture-based search for module-like texture when the finder, clocks or quiet zone are damaged | Candidate search; `candidate_selection` default / all / extensive | AI locator (CodeNet) with count, size and type priors; classical locator only when short |
| Correction | Image filters (smoothing, morphology), 2DMax for damaged and DPM codes | `contrast_tolerance` (glare), `small_modules_robustness`, `finder_pattern_tolerance`, `module_grid` fixed or variable, curved-code rectification (QR 25.05, ECC 200 26.05) | Lighting flattening, deblur, 4x Lanczos, flexible grid, lens-aware deblur, fixed threshold levels |
| Decoding | 2DMax / 1DMax, IDQuick for speed | ECC 200, QR, ... decoders; ISO 15415 grading | zxing-cpp, libdmtx, own grid fit (QR), **lattice** (DM), **candidate matching** (QR, DM) |
| Learning | Auto-tune per job | **Training**: `find_data_code_2d` with `train` = `all` restricts the model to the sample symbols' size, polarity, contrast, module size, mirroring; later samples widen the ranges. Presets standard / enhanced / maximum recognition | Learned per product: code size range, symbols per code, QR size, printer encoding, step order, whole-image pass cost |

## What we adopted

1. **PowerGrid-style inside-out reading** (`engine/src/lattice.cpp`, step `Lattice`). Cognex
   describes it as finding "clusters of texture that resemble data modules", estimating module and
   code size, then decoding. Ours: the module pitch and grid angle from the periodicity of the
   module edges (Fourier peak of the summed edge profile), the grid phase from the module-centre
   samples, then every n x n window whose L finder fits is decoded; Reed-Solomon rejects wrong
   windows. No finder, clock or quiet zone needed. On the crate of liquor caps (label border
   touching the code, bleeding print, lens blur at the corners) it decodes 10 of 23 unread caps.
2. **Trained ranges, as HALCON's `train`**: the pitch search is limited to +-15% of the product's
   learned module size; the code size, count, type, QR size and printer encoding are learned from
   decoded codes, so the search stays narrow (HALCON: "restrict the parameters as narrow as
   possible", otherwise run time grows).
3. **Robustness modes as fixed steps**, like HALCON's `contrast_tolerance` / `small_modules_robustness`:
   lighting flattening against glare, 4x upscaling for small modules, deblur for the lens corners.
4. **Variable module grid** (HALCON `module_grid variable`, QR/ECC 200 rectification): our
   flexible grid for wrinkled shrink-wrap (#5).
5. **Daily auto-tuning** (Cognex Setup Tool): the Calibrate page (#10).

6. **Virtual perimeter and erasure decoding** (from `docs/reference/barcode_decoding_analysis.pdf`,
   Cognex's "inside-out" pipeline): the Data Matrix L finder and clock tracks are never read but
   taken as known, each data module gets a confidence (distance of its mean from the dark/light
   split), and the least certain codewords are passed to Reed-Solomon as erasures. RS corrects
   e errors and f erasures while 2e + f <= ecc, so this doubles what a damaged code can lose. Two
   check symbols stay in reserve against miscorrection and the corrected symbol must parse
   (`softDecodeDm`, `rsDecodeDm`; unit tests in `engine/tests`). On the crate caps the lattice
   reader goes from 38 to 42 of 47 with it.

## Reference documents

- `docs/reference/barcode_decoding_analysis.pdf`: "2D Barcode Decoding Architecture: Cognex vs.
  MVTec HALCON" (owner, 2026-10-23). Its suggestions for AI training (a texture head and a
  structure head; cylindrical-warp augmentation; predicting the grid intersections, not only a
  box) are the plan for the next locator / enhancer models.

## What we do that they do not advertise

- **Candidate matching** (#9, #11): the codes of one case are a serial run, so a code too faint to
  decode is matched against the gaps of the run, drawn exactly as the printer encodes them.
- **Count prior**: the expected number of codes per case drives how hard the finder searches.

## Still open

- Multi-exposure capture (two exposures per trigger) for dark labels and glare, as readers with
  multiple light settings do.
- Lattice decoding for QR codes whose finder patterns are damaged.
- Speed: within 500 ms the crate still reads 22-24 of 47 (38 with 3 s); the plain steps take the
  whole budget on 47 regions before lattice and matching start.

## Sources

- HALCON `get_data_code_2d_param` (25.11): https://www.mvtec.com/doc/halcon/2511/en/get_data_code_2d_param.html
- HALCON `find_data_code_2d` training: https://www.mvtec.com/doc/halcon/1905/en/find_data_code_2d.html
- HALCON `set_data_code_2d_param` presets: https://www.mvtec.com/doc/halcon/2011/en/set_data_code_2d_param.html
- HALCON release notes 18.11 (ECC 200 +5%, disturbed quiet zone): https://www.mvtec.com/products/halcon/documentation/release-notes-1811-0
- HALCON previous versions (24.11 optimized QR reader): https://www.mvtec.com/products/halcon/product-information/editions/halcon-progress/previous-versions
- Cognex 2DMax with PowerGrid: https://www.cognex.com/products/leading-technology/2dmax-with-powergrid-technology
- Cognex PowerGrid announcement (2014): https://www.cognex.com/company/press-releases/2014/cognex-revs-up-2-d-barcode-reading-read-2-d-matrix-and-challenging-dpm-codes
- Cognex DataMan Setup Tool: https://www.cognex.com/products/leading-technology/dataman-setup-tool-software
