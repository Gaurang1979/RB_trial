# Picture check during a run: a picture clearly worse than the taught ones warns through the drift monitor (AS-13)

- Status: done
- Session: run-picture-check, 2026-11-04
- Pull request: #342
- Section 5 item: Picture check during a run
- Components/files: dotnet/Runtime.Host (PictureDrift.cs new, LineRuntime.cs, RuntimeOptions.cs), dotnet/VisionStudio/Services/Station.cs (Pictures hook), dotnet/Runtime.Host.Tests/PictureDriftTests.cs (new), docs/REQUIREMENTS-VISION-SYSTEM.md (AS-13a)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #342: one picture in N of each camera, default 50, is measured off the inspecting thread and compared with the camera's taught picture by the setup-check limits; 3 worse in a row raise line status warning 940 with the zones and the AS-13 advice, 3 good, Stop or a job change clear it; advice only, results unchanged) Picture check during a run: a picture clearly worse than the taught ones warns through the drift monitor (AS-13, requirements section 5)

## Timeline (section 3)
| 2026-11-04 | Picture check during a run (AS-13a): `PictureDrift` samples one picture in `Runtime:PictureCheckEvery` (50) per camera from a new `Station.Pictures` hook, copies it and measures it on a background thread (a picture arriving while one is measured is skipped, so the cycle never waits), compares with the camera's teach image and warns the line (status alarm 940, Warning) after 3 worse checks in a row. Tests: a blurred run warns while every result stays the tools' own; a sharp run does not | 5, 12, AS-13 |

## Decisions (section 4)
| Picture worse than taught | A zone is worse when it is red now and was not when taught, its sharpness is under 0.55 (RelativeBad) of the taught zone's or it lost every edge, or its light is under 0.6 of the taught zone's; without a teach image the #219 limits alone judge (red zones). 3 checked pictures in a row raise and clear the warning, so one odd part does not. Code 940, level Warning: it never becomes the line's error code, never changes a result, the PLC result or the counts | AS-13, 12 |

## Update log (section 8)
| 2026-11-04 | Requirements AS-13a added under AS-13: picture check during a run (sampling, rule, warning 940, acceptance) | Claude (Mac, branch claude/run-picture-check-4c8cdd) |

## Pending
- Picture check during a run: send warning 940 to the plant server as a station alarm (needs the plant alarm catalogue entry) and show it on the browser dashboard
- Picture check during a run: jobs taught in the Runtime.Host job designer keep no camera teach image, so they are judged by the limits alone; save the first taught good picture as the camera's teach image
