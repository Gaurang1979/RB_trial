# Vision Studio: "Use password" and card reader sign-in, auto sign-out after the idle time

- Status: done
- Session: studio-signin-card, 2026-10-30
- Pull request: #137
- Section 5 item: new (from the Pending of docs/changes/2026-10-30-claude-hybrid-2-studio-shell.md; the side menu part is not in this item)
- Components/files: proto/visionstudio/remote/v1/runtime.proto (additive: SignInRequest.card, UserInfo.has_card, WhoAmIResponse.idle_sign_out_seconds), dotnet/RuntimeContract/RuntimeAccess.cs, dotnet/Runtime.Host (Access.cs, RuntimeOptions.cs, Program.cs, README), dotnet/Studio (sign-in view and view-model, CardWedge, MainWindow, MainViewModel, gRPC client, en.json), dotnet/Studio.Tests, dotnet/Runtime.Host.Tests
- Depends on: none

## Section 5
<!-- When done (forms in the README's roll-up rules): "- [x] (done YYYY-MM-DD, PR #n: ...) <the item's text>" replaces the item named above; "- [ ] ..." adds a new item; any other line is added under the item as an update. -->
- [x] (done 2026-10-30, PR #137: "Use password" with name and password; badge sign-in from a USB keyboard-wedge reader anywhere in the app, through the runtime's access rules with an additive `card` secret; auto sign-out after the plant's idle time from WhoAmI, default 10 minutes; `vs-runtime add-user --card`; 18 new Studio tests and 7 new runtime tests) Vision Studio: "Use password" and card reader sign-in, auto sign-out after the idle time.

## Timeline (section 3)
<!-- | YYYY-MM-DD | what was built and verified | requirement sections | -->
| 2026-10-30 | Vision Studio signs in by PIN, password or badge (keyboard-wedge reader) and signs out after the plant's idle time; runtime keeps cards as hashes, local endpoint only; reviewed, Studio, runtime and contract tests pass on macOS | 6, 12 |

## Decisions (section 4)
<!-- | Topic | Decision and reason | requirement sections | -->
| Badge sign-in in the contract | Additive: `card = 5` in SignInRequest's secret oneof (no user name; the runtime finds whose card it is), `UserInfo.has_card = 6`. An older runtime or the stub answers "wrong", so the app stays signed out. | 6, 12 |
| Card storage | PBKDF2 like PINs, but all cards share one salt so a card is found with one hash, not one per user. Card numbers are short, so a salt per user would barely slow a stolen file; the data folder's permissions protect it. Numbers are trimmed and in capitals (hex readers and Caps Lock), at least 6 characters, one card per user; a broken or short stored hash matches nothing. | 12 |
| Card only at the line PC | The runtime refuses a card over the network: a card number there is only something to guess. Unknown cards on the line PC are counted together (5 in a minute lock card sign-in, not the PIN pad), and a good card does not reset the count. | 12 |
| Telling a reader from a person | Every key of the burst, Enter included, within 50 ms of the one before, at least 6 characters, at most 64. Any machine-fast Enter is swallowed, so a badge never presses the focused button (Start). A card read replaces the signed-in user only when the runtime knows it, so a barcode scanner on the same PC signs nobody out. Card number, PIN and password are never logged or shown and are cleared after each try; the Name box too after a card. | 6, 12 |
| Idle sign-out | The plant sets `Runtime:StudioIdleSignOut` (default 10 minutes) on the runtime; WhoAmI tells Vision Studio, which measures touches and keys (the runtime only sees calls). 0 from an older runtime means 10 minutes. Change-product and confirm panels close with the user; a sign-in left open forgets its PIN or password. | 6, 12 |

## Update log (section 8)
<!-- | YYYY-MM-DD | what this pull request changed in the docs | Claude (<machine>, branch <branch>) | -->
| 2026-10-30 | dotnet/Runtime.Host/README.md: `add-user --card`, how cards are stored, `Runtime:StudioIdleSignOut` | Claude (Mac, branch claude/studio-signin-card) |

## Pending
<!-- Follow-ups this session leaves, one per line: "- Unit tests for ...". Each becomes an open section 5 item. Leave the section out if none. -->
- Vision Studio sign-in on the line PC: try a real keyboard-wedge badge reader and the Windows touch keyboard for "Use password" (only checked with tests on macOS).
- Sign-in lock as UI-UX-SPEC.md 4.5 asks: 5 minutes instead of 1, and a notice in the audit trail (runtime today: 1 minute, no audit; PIN, password and card).
- Vision Studio: Windows account sign-in under "Use password" (UI-UX-SPEC.md 4.5), and HID (not keyboard-wedge) card readers.
- vs-runtime: a command to remove a user's card (AccessStore.SaveUser takes an empty card already).
- Vision Studio: the side menu for the setup screens (Hybrid 3), left from the same Pending line.
