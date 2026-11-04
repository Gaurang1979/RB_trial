# One image and part naming pattern for every camera, line, date, job and serial (owner, 2026-10-31)

- Status: done
- Session: image-naming, 2026-10-31
- Pull request: #210
- Section 5 item: new
- Components/files: dotnet/StationGuard (ImageName.cs new: ImageName, PartSerial; NgImageStore.cs), dotnet/VisionStudio (Services/Station.cs, Recorder.cs, Options.cs, NgImageList.cs new, Components/Pages/Run.razor and Teach.razor, appsettings.json), dotnet/VisionReports (ResultLog.cs, ExcelReport.cs), dotnet/Runtime.Host (LineRuntime.cs), tests in StationGuard.Tests, VisionStudio.Tests, VisionCore.Tests, Runtime.Host.Tests; docs/REQUIREMENTS-VISION-SYSTEM.md section 12, docs/vision-system/NG-REVIEW.md section 2, docs/vision-system/JOB-FILE-FORMAT.md 3.10
- Depends on: none

## Section 5
- New: [x] (done 2026-10-31, PR #210: `StationGuard.ImageName` builds and parses `L01_C2_J045_261031-104512_000123` and reads the old forms; `PartSerial` counts the day's parts (kept across a restart, 1 again at local midnight); NgImageStore saves named images in `<ng-images>/L01/2026-10-31/J045/C1/OK|NG/` and finds a part from any camera's name; the station, results.db (`image_id` on inspections and parts), the plant events, the runtime's image ids and GetImage (also saved images by name), the Run page's NG list, Teach links and grabs, and the Excel report all use the same string; line number is the station setting `Studio:LineNumber`; old images are not moved and still open) One naming pattern for images and part records: line, camera, PLC job number, local date and time, the day's part serial; folders per line/date/job/camera (owner, 2026-10-31).

## Timeline (section 3)
| 2026-10-31 | One image name everywhere (owner, 2026-10-31): `L01_C2_J045_261031-104512_000123` = line, camera, PLC job number, local yymmdd-hhmmss, the day's part serial; folders `<images root>/L01/2026-10-31/J045/C1/OK|NG/`; the .json beside each image holds product, OK/NG, reasons, PLC trigger and part id, time with milliseconds and time zone, batch values and the image id. Same string in NgImageStore, results.db, the plant events, the runtime's GetImage, the NG list, Teach and the Excel report; old images still open | Requirements 12; NG-REVIEW.md 2 |

## Decisions (section 4)
| A part's image id | Each camera image has its own name; the plant event's `image_id` is the name of the part's first camera whose image was kept. The store finds a part by its name without the camera, so any camera's name finds all its images and the server's single id per part keeps working. The plant event schema is unchanged: `image_id` keeps its meaning (the saved image's id) and the plant server already takes `[A-Za-z0-9._-]{1,64}` | Requirements 12 |
| Part serial | The station's part count for the day, by local midnight, kept in `data/part-serial.json` (saved by a timer, never on the inspection thread; at most half a second of parts can be lost on a power cut, and the time in the name keeps names apart even then). With camera channels each camera counts its own serial, so one part's names match while the line skips no part; the PLC part id in the .json links them for sure. Teach grabs have their own count | Requirements 12 |
| Old images | Not moved or renamed. `ImageName.ParseAny` reads the old station id (`yyyyMMddTHHmmssfffZ-nnnnnn`, with `_<camera>` on files) and the old `HHmmss_fff_<reason>` files, so the store, NG review, the NG list and Teach still find and open them. The Dashboard's hard-case folder keeps its own layout (it names no parts) | Requirements 12 |
| Folder verdict | `OK/` or `NG/` is the camera image's own verdict (the image saved); the part's verdict is in results.db and the plant event | Requirements 12 |

## Update log (section 8)
| 2026-10-31 | Requirements section 12 (Results and data): the image naming pattern and folders in the owner's words; NG-REVIEW.md section 2: image id and NgImageStore files; JOB-FILE-FORMAT.md 3.10: names and folders are the station's, LineNumber setting | Claude (Mac, branch claude/image-naming) |

## Pending
- Studio Parts list: show each part's image name (it is in PartResult.ImageId already; needs a column string in the 15 languages).
- PR #200 (PLC NG 100 images): its held images take the part's ImageName, whichever of #200 and #210 merges second.
