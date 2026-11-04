# Part 11 owner answers: sign-in providers, default signatures, plant server required, lockout per device, NG image storage

- Status: done
- Session: part11-owner-answers, 2026-10-31
- Pull request: #192
- Section 5 item: Part 11 owner questions
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (section 12), docs/vision-system/PART11-DESIGN.md
- Depends on: none

## Section 5
- [x] (done 2026-10-31, PR #192: owner answers written in requirements section 12 (PT11-03 directory sign-in controlled by the plant server, PT11-05 lock per device, PT11-30 more default signatures, PT11-40a NG images 7 to 30 days reduced size, PT11-45 plant server required) and in PART11-DESIGN.md (sign-in providers, D1, D9, D14, section 6, section 11 answers)) Part 11 owner questions (PART11-DESIGN.md section 11): Razor VisionStudio retired?; Windows domain at the plants?; signatures by default beyond section 6; standalone line PC without a plant server; lockout per device versus PT11-05; which images are records
- "Part 11 results and images": images are no longer copied before the line quota removes them; reduced-size NG images go to the plant server at low priority and are kept 7 days by default, at most 30, rolling (PT11-40a, D14). Results stay append-only with image hashes and batch digests.

## Timeline (section 3)
| 2026-10-31 | Part 11 owner answers in the requirements and the design: directory sign-in from the plant server, more default signatures, plant server required, lock per device, NG images 7 to 30 days reduced size | 12 |

## Decisions (section 4)
| Part 11 sign-in providers | Owner, 2026-10-31: "keep options for various domain login like windows and other which is being used by various organisation, also keep other login options (needs to be controlled by plant server)." Windows AD and OIDC, SAML or LDAP company sign-in next to own accounts and card + PIN; on or off per provider and role in the plant server only; directory passwords never reach a line PC | 12 |
| Part 11 default signatures | Owner, 2026-10-31: batch start, line clearance, alarm acknowledge and report export are e-signed by default, besides those already required. A refused signature leaves the line stopped | 12 |
| Part 11 needs a plant server | Owner, 2026-10-31: a line PC without a plant server is not offered as Part 11 compliant | 12 |
| Part 11 lockout | Owner, 2026-10-31: per user and device, as decided 2026-10-30 | 12 |
| Part 11 images | Owner, 2026-10-31: "Only 7 days NG images will be stored by default. User can increase the storage maximum 30 days... Store only reduced size image on server... God images can be stored as a option." Images are kept short; the result with the image hash is the 1-year record | 12 |
| Razor VisionStudio | Owner, 2026-10-31: retired; no Part 11 work | 12 |

## Update log (section 8)
| 2026-10-31 | REQUIREMENTS-VISION-SYSTEM.md PT11-03, 05, 30, 40, 40a, 45; PART11-DESIGN.md sign-in providers, D1, D2, D9, D14, section 6, section 11 | Claude (Mac, branch claude/part11-owner-answers) |

## Pending
- Remove the retired Razor app dotnet/VisionStudio from the solution and the docs
