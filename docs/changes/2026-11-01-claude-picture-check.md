# Picture check per corner and centre (AS-13)

- Status: done
- Session: picture-check (side session of camera-setup-checks), 2026-11-01
- Pull request: #215
- Section 5 item: Picture check per corner and centre (AS-13)
- Components/files: VisionCore (Tools/PictureCheck.cs, new), Runtime.Host (JobDesigner.TryJob), runtime contract (proto TryJobResponse field 20, new messages PictureCheck, PictureZone, PictureAdvice), Vision Studio (ViewModels/DesignerPictureCheck.cs new; small hooks in DesignerViewModel.DrawGraphics, DesignerWorkspace AfterShow/AfterClear, DesignerView.axaml, Runtime/IJobDesignClient.cs, Runtime/GrpcRuntimeClient.cs, i18n/en.json); tests in VisionCore.Tests, Studio.Tests, Runtime.Host.Tests
- Depends on: none. Overlaps #194, #195, #216, #217 in the Designer, the TryJob contract and en.json (merge order in the pull request)

## Section 5
- [x] (done 2026-11-01, PR #215: VisionCore measure and advice on every tried picture, Studio "Picture check" toggle with marks, best sharpness so far and one advice line; advice only. Grab, taught pictures, run drift and a live view are their own items) Picture check per corner and centre (AS-13): sharpness and brightness of the four corners and the centre on each tried picture, with advice (focus, tilt, clean the lens, aperture, light), and the best sharpness so far while the lens is turned (UI-UX-SPEC 4.19 "Picture check")

## Timeline (section 3)
| 2026-11-01 | Picture check per corner and centre (AS-13): VisionCore `PictureCheck` measures sharpness (edge slope over local contrast) and brightness in the four corners and the centre of a 3 x 3 grid and gives the six AS-13 advice kinds; TryJob carries it (contract field 20); Vision Studio's Engineer workspace has a "Picture check" toggle with green, yellow or red marks and values, the best sharpness so far, and one advice line above the picture and in Messages. Advice only; "Try all pictures" skips it. 16 VisionCore tests on drawn pictures, 8 Studio tests, Runtime.Host TryJob assertions; tried on the owner's 58 sample pictures, 45 to 142 ms processor time (median 55) on a 20 MP picture | 16 (AS-13), 12 |

## Decisions (section 4)
| Picture check measure (AS-13) | Sharpness of a zone = 90th percentile of (Sobel gradient / grey range in the 15 x 15 window), over pixels whose range is clearly above the noise (at least 24 grey levels and 8 noise sigmas), times 200: 100 is a one-pixel edge, about 40 an edge of Gaussian sigma 2. Dividing by the local range makes a dark or faint zone read as sharp as a bright one when its edges are as narrow, which a plain gradient or Laplacian energy cannot do; it needs no reference picture. A zone with too few edges is "no detail" (grey mark, no advice). Large cells are measured on their middle 768 x 768 pixels. Brightness shown is the mean grey; dark is judged on the 99th percentile and only where there is detail (a plain dark zone is the background), vignetting on each corner's outer quarter (95th percentile) against the centre, glare on the share of pixels at 250 and more, dullness on (p95 - p5) / (p95 + p5) | 16 (AS-13) |
| Where the picture check runs | In VisionCore, pure C#, called by the runtime on the full picture in every TryJob; Studio only shows it. The Studio sees a scaled JPEG, on which sharpness cannot be measured; the engine's C API is unchanged (an advisory measure needs no native code). Contract: additive TryJobResponse field 20, older runtimes send none and Studio draws nothing | 16 (AS-13), 12 |
| Picture check advice rules | Every zone with detail blurred (also a part alone on a plain background), or the centre blurred: Focus, or Clean the lens when those zones are also dull. Corners only (one, a side, all four) blurred with the centre sharp: Tilt (camera or lens not square, part not flat, or the lens not sharp at its edge). Then light: Vignetting, Dark, Bright. Shown as one line; written to Messages once each time it changes | 16 (AS-13) |
| Picture check cost | Measured on every TryJob (about 55 ms processor time on a 20 MP picture, beside the tools' own time) so turning the toggle on needs no new try; "Try all pictures" sends TryJobRequest.skip_picture_check, as it keeps only pass and fail | 16 (AS-13) |
| Picture check starting limits | Good at sharpness 30 or more, Bad under 22; also yellow under 0.7 and red under 0.55 of the sharpest zone (shows a tilt while every zone still reads); dark under 50 (yellow under 80) on the 99th percentile; glare over 1 % (red over 5 %) saturated; vignetting when every corner's light is under 0.75 of the centre's; dull under 0.3. Calibrated on the owner's 58 sample pictures: zones with detail on pictures that read score 30 to 56. Not yet per job (see Pending) | 16 (AS-13) |

## Update log (section 8)
| 2026-11-01 | runtime.proto: TryJobResponse field 20 `picture_check` with messages PictureCheck, PictureZone, PictureAdvice; TryJobRequest field 13 `skip_picture_check` (AS-13) | Claude (Mac, branch claude/picture-check) |

## Pending
- Picture check on by itself after Grab (UI-UX-SPEC 4.19) and on the Find everything picture: set the toggle after #194's Grab and add `picture_check` to FindToolsResponse (whichever of #194 and #215 merges second)
- Picture check on taught pictures: flag a picture much worse than the first before it is taught (AS-13, UI-UX-SPEC 4.6 step 4)
- Picture check during a run: a picture clearly worse than the taught ones warns through the drift monitor (AS-13, requirements section 5)
- Picture check limits per job: the sharpness a job needs from its tools' smallest detail (AS-14 pixels per module, letter height) instead of the starting limits, tried on real line pictures (samples/line-images)
- Picture check live: feed the Designer's live view (when built) through the check, so the marks follow the lens without pressing Try
