# Mobile app: show and change a saved server's certificate fingerprint (for a renewed self-signed certificate); today the server is removed and added again.

- Status: done
- Session: mobile-fingerprint-edit-6617cd, 2026-11-04
- Pull request: #333
- Section 5 item: Mobile app: show and change a saved server's certificate fingerprint
- Components/files: mobile/plant_core/lib/src/cert_trust.dart (fingerprint display helpers), mobile/plant_app/lib/src/home.dart (server menu entry and fingerprint dialog), tests in both packages, docs/REQUIREMENTS-VISION-SYSTEM.md 17.3 (MOB-TLS-05)
- Depends on: none

## Section 5
- [x] (done 2026-11-04, PR #333: "Certificate fingerprint" in a server's menu shows it shortened or in full and takes a new one or removes it; Req 17.3 MOB-TLS-05) Mobile app: show and change a saved server's certificate fingerprint (for a renewed self-signed certificate); today the server is removed and added again.

## Timeline (section 3)
| 2026-11-04 | Mobile app: a saved plant server's certificate fingerprint is seen and changed from the server's menu on the home screen ("Certificate fingerprint"): shown as its first and last four bytes with "Show in full", a new one checked exactly as on the add-server screen, or removed; the server's key and sign-ins stay and the new pin counts from the next connection. Widget test of the dialog and a TLS test against a throw-away certificate. | 17.3 (MOB-TLS-05) |

## Decisions (section 4)
| Changing a pinned fingerprint | Saved in place on the same server (same id), so its key and sign-ins stay; the trust reads the server list at each handshake, so the new fingerprint counts from the next connection without a restart. Save is only enabled for a valid fingerprint that differs from the current one; removing it is a separate button, and without a fingerprint a private certificate fails rather than passes. | 17.3 (MOB-TLS-02, MOB-TLS-05) |

## Update log (section 8)
| 2026-11-04 | Requirements 17.3: registered MOB-TLS-05 (see and change a saved server's certificate fingerprint). | Claude (Mac, branch claude/mobile-fingerprint-edit-6617cd) |

## Pending
- Mobile app: certificate fingerprint dialog strings (title, show in full/less, new fingerprint label and helper, remove) in the 14 languages beyond English.
