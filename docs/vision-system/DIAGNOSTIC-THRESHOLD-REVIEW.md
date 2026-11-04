# Diagnostic threshold review record

Part of the requirement document, section 17.16 (HND-06, HND-07). Defaults live in REQ-20 (section 17.10). This file only records the other values that have been proposed and the owner's answer each time the defaults are reviewed.

**Rule (owner, 2026-11-04):** the owner is asked once a month, and whenever someone works on REQ-20 diagnostics, whether to change any default. Each review adds a row to the log below. A changed default is written into REQ-20 in the same pull request.

## Values under review

| Signature | Default (REQ-20) | Handover 2026.11-MASTER | Notes |
|---|---|---|---|
| CIJ head angle / skew | 1.5° | 2.0° | |
| DataMatrix aspect ratio W/H | 1.00 ± 0.05 | 1.00 ± 0.04 | |
| Conveyor speed sag under load | > 8% | > 10% (encoder tick interval) | |
| TIJ head-away gap | > 2.0 mm (target 1.0 mm) | > 2.5 mm (nominal 1.0–1.5 mm) | The guidance text uses 1.0–1.5 mm (HND-06) |
| TTO bar width drift (over/under darkness) | bar dilation > +35% | bar/space ratio beyond ± 25% | |
| TTO under-darkness contrast | < 40% | < 40% | Same |
| Camera axis tilt | keystoning > 1.5° | zones 1–3 MTF > 80, zones 7–9 < 35 | Both kept (HND-07) |

## Signatures added from the handover (HND-07), first values

| Signature | Default |
|---|---|
| CIJ satellites, quiet-zone SNR | < 18 dB |
| TIJ decap dry-out, line stop | > 30 s |
| TIJ wedge print, MTF left/right | > 2.5 |
| TIJ head-away edge width / dot blur | > 6 px / > 40% |
| Hologram radial offset | > 1.2 mm |
| Surface water, I_peak / I_ring | > 250 / < 40 |
| Mount tilt 9-zone MTF | zones 1–3 > 80, zones 7–9 < 35 |

## Review log

| Date | Asked by | Owner's answer | Defaults changed |
|---|---|---|---|
| 2026-11-04 | Claude (handover comparison) | Keep the REQ-20 values as defaults, OEM-adjustable; keep the handover values here | none |
