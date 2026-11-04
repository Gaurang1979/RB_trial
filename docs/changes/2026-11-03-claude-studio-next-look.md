# Vision Studio Next, step 1: look base (hud colour, fonts, control states, sounds, spoken alarms, reduce motion)

- Status: done
- Session: Claude (studio-next-look), 2026-11-03
- Pull request: #309
- Section 5 item: new
- Components/files: design/tokens/vision-tokens.json, tools/tokens/gen_tokens.py, dotnet/Studio (Theme, Assets/Fonts, Sound, Settings, Display panel, Run cues), dotnet/Runtime.Host/Web/tokens.g.css (generated), dotnet/Studio.Tests, design/prototypes/vision-studio-next, docs (requirements 17.15 and 6.5, UI-UX-SPEC 7, THIRD-PARTY-LICENCES)
- Depends on: none

Requirement: Req 17.15 NXT-01..06 (registered in this pull request).

## Section 5
- [x] (done 2026-11-03, PR #309: hud and hudDim tokens in every theme, Rajdhani / Noto Sans / JetBrains Mono embedded as the display, body and mono faces, app-wide button states with hover brackets and glow, 1 px press, hud focus ring, faded disabled with its tooltip; synthesized sound service with a null output for tests, spoken alarms through Windows speech, Sound and motion in the Display panel, Reduce motion with the Windows setting) Vision Studio Next step 1: the look base every later screen uses (requirements 17.15 NXT-01..06)

## Timeline (section 3)
| 2026-11-03 | Vision Studio Next step 1 (look base): instrument colour hud, three embedded OFL faces, app-wide control states, synthesized sounds (bad part, alarm, start, stop, saved, good part, tap, hover) with settings, spoken alarms, Reduce motion. Studio.Tests 353 passed; Runtime.Host.Tests WebDashboardTests 11 passed | 17.15, 6.4, UI-UX-SPEC 7 |

## Decisions (section 4)
| Sounds made in code | The approved sketch's WebAudio notes are rebuilt as PCM in `SoundSynth` and played from memory with winmm PlaySound on Windows; no audio files and no audio library ship. Elsewhere and in tests the output is null. A new sound cuts the one playing. | 17.15 NXT-04 |
| Spoken alarms | System.Speech (SAPI, Windows) behind `ISpeech`; a voice of the user's language when installed, else the default voice; a missing voice is silent. | 17.15 NXT-05 |
| Look settings per PC | Sounds, spoken alarms and Reduce motion are kept by Vision Studio in `%LOCALAPPDATA%\VisionStudio\ui-settings.json`, not in the job or runtime: they never change a result. | 17.15 NXT-04..06 |
| Body face and scripts | Only Noto Sans (Latin, Greek, Cyrillic) is embedded; Indian scripts, Urdu and Chinese fall back to Nirmala UI, Segoe UI and Microsoft YaHei UI on Windows. Rajdhani also holds Devanagari. | 17.15 NXT-02 |

## Update log (section 8)
| 2026-11-03 | Requirements 17.15 (Vision Studio Next look, sound and motion) added; 6.5 fonts line updated; UI-UX-SPEC 7 Font, Feedback, Sound and Motion rows; THIRD-PARTY-LICENCES.md started | Claude (Mac, branch claude/studio-next-look) |

## Pending
- Vision Studio Next step 2: the operator Run screen (result pop, scan sweep, telemetry, pulsing status, NG strip, "What do I do?" with steps and voice), with one bad-part sound per part from the part results instead of per camera frame.
- Vision Studio Next step 3: supervisor settings in plain words, tried live on saved pictures, with a save bar and Undo all; play the Saved sound there and in the job designer's Save.
- Vision Studio Next step 4: engineer workspace polish and snapshots in all themes.
- Embed the Noto Sans script fonts (Devanagari, Bengali, Gujarati, Gurmukhi, Tamil, Telugu, Kannada, Malayalam, Odia, Nastaliq Urdu, SC) when the size is agreed, so a line PC without Nirmala UI shows every language in the same face.
- Try the sounds and spoken alarms on a Windows line PC with its speakers (only the null output ran on the Mac).
