# Experiments (Python prototypes)

Prototypes behind engine features, kept for reference. Each was tried here first, then ported
to C++ in `engine/src` when it helped. Run from this folder with the `ml` virtual environment
(`segno`, `qrcodegen`, `reedsolo`, `scipy`, `zxingcpp`).

| File | What | Engine |
| --- | --- | --- |
| `finders.py`, `gridfit2.py`, `flexgrid.py` | own QR finder search, straight and flexible module grid | `qrgrid.cpp`, `gridfit.cpp` |
| `match.py` | QR candidate matching against the case's serial run | `match.cpp` |
| `dmenc.py`, `dmfit.py`, `dmmatch.py` | Data Matrix encoder, square search, candidate matching | `dmmatch.cpp` |
| `lattice.py` | inside-out Data Matrix reading (module grid from the texture) | `lattice.cpp` |
| `dmsoft.py`, `qrsoft.py` | soft decoding with Reed-Solomon erasures | `dmmatch.cpp`, `qrsoft.cpp` |
| `knownqr.py` | known-content QR decoding: codewords shared with the case's other codes fixed, grid registered on them, pixel classifier trained on them | `qrsoft.cpp` (`knownDecodeQr`) |
| `dmknown.py` | known-content Data Matrix decoding from module means (classifier on 3 x 3 neighbourhoods) | `dmmatch.cpp` (`knownSoftDecodeDm`), used by `lattice.cpp` |
| `psf.py`, `psf_qr.py`, `psf_region.py`, `psf_holdout.py` | the lens as a kernel (R9 study, 2026-10-27): PSF (Gaussian sigma + print growth) fitted on every read code through its homography, sigma by radius and zone; the unread bent cap of the 48-bottle image re-scored through its neighbours' kernel on the data modules (0.65 -> 0.78, lead kept); hold-out on the 47 read codes (true first 47/47, best impostor 0.67) | `psf.cpp` (`measurePsfSigma`, `psfScore`), `matchQr` re-score |
| `synth1d.py`, `band1d.py`, `gen1d.py`, `fp1d.py`, `fp1d_engine.py` | 1D case codes (2026-10-27, synthetic frames): SSCC / EAN-13 generator and degradation sweep; bands of bars at any angle with a median profile; generative SSCC decoding confirmed by both check digits; false-read checks | `linear.cpp` |
| `slots.py` | the case's slot lattice from the read codes (2026-10-29, fixturing as NI Vision Builder / DALSA Sherlock): rigid cases sit within 0.04-0.1 pitch of a lattice, shrink-wrapped cans 0.2-0.37; unread codes fall on its empty nodes | `slots.cpp` |
| `dots.py`, `dots_wrong.py`, `dots_replay.py` | dot-printed codes (inkjet on caps), voids, specks, dot jitter, in scenes of six codes (2026-10-29, NI Vision "cell filters"); the hunt that found a libdmtx phantom read ("Y62") | `decode.cpp` (`CR_STEP_DOTS`, the libdmtx plausibility gate) |
| `polarity.py` | light-on-dark codes (laser-marked caps, white ink on dark labels) against the same codes dark-on-light, in scenes of six (2026-10-29): the strong decoders assume dark modules | `decode.cpp` (product polarity, the image flipped once) |
| `cylinder.py` | codes wrapped around a cylinder (bottle shoulder, cap edge), NI Vision's "unwrap" case (2026-10-29): the engine already reads Data Matrix and QR wrapped up to 138 degrees around, at 4, 3 and 2.5 px modules, 18/18 each, no wrong reads, so no unwrap step was built; kept as a regression check | none needed |
| `anomaly.py`, `anomaly2.py` | anomaly detection per case slot (DALSA Astrocyte's idea, 2026-10-29), defects injected into real rigid-case images (product missing, torn label, foreign object, another product's slot): hand features per slot zone (code, label ring, surroundings) with robust z against the other images of the product, then against the slot's siblings in the same case. Not good enough to ship: on these dark old-camera images good slots spread 25-60% per feature (light falls off across the case), so a missing product scores 3-4 spreads; at 0% false alarms only 12/56 missing, 0/56 torn, 0/56 objects, 23/53 other products were caught. Needs real good cases on the new camera and a learned model | not ported |
| `qrlattice.py` | inside-out QR (no gain on the samples) | not ported |
| `lsq.py` | least-squares module values for under-resolved codes (small gain) | not ported |
| `underres.py`, `underres_test.py` | discrete solver for under-resolved QR (after Cognex US11151346): overlap model, 3 x 3 pattern tests, flip cost as confidence, RS erasures | `underres.cpp` |
| `labelrect.py` | flatten a tilted label before decoding (no gain on the samples) | not ported |
| `qzmask.py` | artificial quiet zone (no gain) | not ported |

No sample images are stored here: they are customer data and stay outside the repository.
