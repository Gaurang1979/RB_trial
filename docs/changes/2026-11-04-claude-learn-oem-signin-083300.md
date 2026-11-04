# OEM and Support sign-in in Vision Studio on the line PC so they can sort and start learning (AS-04h)

- Status: done
- Session: claude/learn-oem-signin-083300, 2026-11-04
- Pull request: #328
- Section 5 item: OEM and Support sign-in in Vision Studio on the line PC
- Components/files: proto/visionstudio/remote/v1/runtime.proto (additive: Role SERVICE = 6, OEM = 7; SignInRequest.code = 6; WhoAmIResponse.may_learn = 8); dotnet/RuntimeContract (RuntimeAccess.MayLearn, IsSupport; MayOperate leaves Support/OEM out); dotnet/Runtime.Host (Access.cs: SupportCodes, Sessions.OpenSupport, Learn guard, support audit; Services.cs: learning calls on MayLearn, Apply refused for Support/OEM, audit; RuntimeHost.cs: Sessions registration); dotnet/Studio (sign-in code box, SignedInUser.MayLearn, Learning button from may_learn, Stop learning in the sorting, RuntimeCalls cancel, en.json); dotnet/VisionStudio/Services/OemSignIn.cs (OtpAttempts: a refused role keeps the count); tests in RuntimeContract.Tests, Runtime.Host.Tests (SupportSignInTests new), Studio.Tests, VisionStudio.Tests; docs/REQUIREMENTS-VISION-SYSTEM.md AS-04h, docs/vision-system/UI-UX-SPEC.md 4.5
- Depends on: none. Overlaps #330 (NG review list) and #331 (Auto-tune code grade) only in docs/REQUIREMENTS-VISION-SYSTEM.md, at other items; any merge order. Reviews.cs, NgReview and RunViewModel are not changed.

## Section 5
- [x] (done 2026-11-04, PR #328: a runtime user with the Service or OEM role signs in to Vision Studio at the line PC with the password and today's code, counted and held as MRG-09; opened, the role sorts, sees the reviews, starts and stops learning and sets the learning session, never runs the line, applies or designs; every sign-in and action audited; remote Service sessions stay view-only) OEM and Support sign-in in Vision Studio on the line PC (today's OTP, as OemSignIn in VisionStudio) so they can sort and start learning (requirements AS-04h)

## Timeline (section 3)
| 2026-11-04 | OEM and Support start learning (AS-04h): the runtime knows the Service and OEM roles; Vision Studio's password sign-in takes today's code, which opens the Service role with the service OTP and the OEM role with the factory OTP for this PC's licence, only at the line PC, never with a PIN or card, five wrong codes in 15 minutes hold that user's codes (OtpAttempts on disk). Opened, the role may list and sort pictures, see the reviews, start and stop learning (new Stop learning button) and set the learning session; Apply stays with the customer's Supervisor, QA, Engineer or Admin. Every sign-in (opened, refused, held), sign-out and action is a support_access audit-trail event naming the person and role. Tested on RuntimeContract, Runtime.Host and Studio | AS-04h, AS-04c, AS-04f, MRG-09, REQ-11 |

## Decisions (section 4)
| Support and OEM rights on the line PC | Opened with today's code, they get only what learning needs (owner, 2026-11-04 named OEM/Support as starters): list and sort pictures (which is ReviewPart, so the NG review's decisions too), see the reviews, start and stop learning, and the learning session setting. Not: start, stop, change job, trigger, inputs, Apply a proposal, the job designer, the PLC test signal. Apply makes a new job version that decides OK and NG, so a customer Supervisor, QA, Engineer or Admin applies it; the runtime answers runtime.refused.supervisorApplies. | AS-04h, AS-04f, section 12 |
| Where the code is taken | Only in Vision Studio on the line PC's own endpoint and only with the password: never over the network (the browser dashboard included), so the code cannot be tried from the plant's network (PROT-32), and never with a PIN or card (a PIN is short, a card can be lent). A refusal for these reasons is not counted as a wrong code. | AS-04h, MRG-09, PROT-32 |
| Wrong-code count in the runtime | The runtime's one OtpAttempts (signin-remote.dat, its own file as #313 requires) counts Vision Studio codes per user under "studio:<name>", apart from the remote support count; a tampered file holds every code, as MRG-09 item 5. | AS-04h, MRG-09 |
| A user with a customer role too | Without the code, or with a wrong one, they sign in with their customer roles alone (as VisionStudio's OemSignIn); a user with only Support or OEM is refused. | AS-04h, MRG-09 |
| Wrong-code count for a user with both roles | Found in review: OtpAttempts cleared a user's wrong codes whenever any role opened, so a user with Service and OEM could guess the factory code without limit by signing in with the service code between guesses. Now the count clears only when no role was refused and counts only when none opened; this also fixes Vision Studio (VisionStudio) sign-in, which uses the same class. | AS-04h, MRG-09 item 5 |
| Remote service session | Unchanged from #325: a browser session through the support link, read-only, refused on the runtime contract; it has no role, so may_learn is false. | REQ-11, PROT-26 |

## Update log (section 8)
| 2026-11-04 | Requirements AS-04h (OEM and Support start learning with today's code); UI-UX-SPEC 4.5 sign-in: the today's code box | Claude (Mac, branch claude/learn-oem-signin-083300) |

## Pending
- Owner: confirm whether Support or OEM may also Apply what was learned (today a customer Supervisor, QA, Engineer or Admin applies it, AS-04h)
- Owner: confirm that Support and OEM may also decide the Run screen's NG review parts and set the learning session (AS-04h gives them sorting, which is the same review call, and the setting; AS-04b/c said Supervisor and above)
- The Vision Studio sign-in code box and Stop learning strings in the other languages when Vision Studio gets them (it ships only en.json: signin.code, sort.learn.stop*, sort.learn.stopped, runtime.refused.supervisorApplies)
- Release notes / rollback check: a runtime from before AS-04h reads a runtime-users.json holding a Service or OEM user as broken (nobody signs in, fail closed); remove such users before rolling back past this version
