# AS-01 Auto-find on the whole image: every code with its type and every text line with its field type, as suggestions (VisionCore)

- Status: done
- Session: roadmap-auto-setup (claim), auto-find-whole (build), 2026-10-30
- Pull request: #152
- Section 5 item: new
- Components/files: dotnet/VisionCore/Tools/AutoFind.cs and TextTeach.cs (new), dotnet/VisionStudio/Components/Pages/Teach.razor (text auto find now calls TextTeach), dotnet/VisionStudio/Services/MonthLanguages.cs, dotnet/VisionCore.Tests/AutoFindTests.cs
- Depends on: none (requirement AS-01 is in PR #145, merged)

## Section 5
- New: [x] (done 2026-10-30, PR #152: VisionCore `AutoFind.Suggest(image, models)` finds, on the whole image with nothing drawn, one Read code tool per code type (locked to type, count and length) and one Read text tool per block of text lines, each line with a field type: production date, expiry date, date, time, lot / batch, price, number / code or text; suggestions only, nothing joins a job until the user accepts it) AS-01 Auto-find on the whole image: every code with its type and every text line with its field type, as suggestions.
- "AS-01 Auto-find on the whole image without drawing a box": codes and text are found by VisionCore `AutoFind` (PR #152); still open: showing the suggestions in Vision Studio (Teach page and Avalonia), and finding the part and labels.

## Timeline (section 3)
| 2026-10-30 | AS-01 auto-find on the whole image in VisionCore (`AutoFind`), and the Teach page's text teaching moved into VisionCore (`TextTeach`) with no change in behaviour. A drawn label (Code 39 and three printed lines) gives one Code 39 suggestion and one text block with production date, expiry date and lot / batch. | 16 (AS-01, AS-12) |

## Decisions (section 4)
| Auto find gives suggestions only | `AutoFind.Suggest` returns ready tools but never adds them to a job: the user accepts each one (AS-01, line safety AS-12). Text under a barcode (inside the code's box padded by a quarter) is part of the code, not a text field. | 16 (AS-01, AS-12) |
| Text blocks on the whole image | Lines read at every letter height (14 to 95 px) are kept, the better-read one where two overlap, so print of several sizes is found; lines join one block when letter heights are within 35%, they are under 1.5 letter heights apart up or down, and they overlap side to side or are under 2 letter heights apart. | 16 (AS-01) |
| Lot label run into the number | The reader often drops the dot and space after B.NO. ("B.NOAB123"), so LOT, BATCH, B.NO and B/N at the start of a line mark a lot / batch line even with no space after them. | 16 (AS-01) |
| Owner's teaching workflow | Owner, 2026-10-30: "user will capture 15-20 images good image (user should remove bad images, studio should prompt to remove visual bad images), draw a box on any image, AI will auto find contents, apply tools and show results in side, user can add Tools (can drag from bar/set and drop on actual image)... then user will run the job in offline mode from saved images to teach the AI for every parameter including locate... If any bad image is stored intentionally / unintentionally by user for AI learning which is affecting over all performance then studio software should ask to remove that image / recheck and again tune in... System will auto save job. System will also allow to reverse last 5 versions of job with time/date." Auto find (this PR) is the "AI will auto find contents" step; drag and drop is AS-05, tuning on saved images AS-03 (PR #154); the new parts are listed under Pending. | 16 (AS-01, AS-03, AS-05), 5 |

## Pending
- "Find everything" on the Teach page and in the Avalonia Vision Studio: show AutoFind's suggestions beside the image with their values, and add a suggestion as a tool when the user accepts it (English strings first).
- Auto find of holes, edges, colour and presence once those tools exist (AS-01 asks for them; AutoFind finds codes and text only today).
- Teach images: when the user captures 15 to 20 good images, the studio checks each one (blurred, too dark or bright, part missing or cut off) and asks the user to remove a bad one before teaching (owner, 2026-10-30).
- Tuning on saved images (AS-03): find a saved image that pulls the tuning down (a bad image saved as good, by mistake or not), ask the user to remove or recheck it, then tune again (owner, 2026-10-30).
- The job is saved by itself while the user edits it, and the user can go back to any of its last 5 versions, listed with date and time (owner, 2026-10-30).
