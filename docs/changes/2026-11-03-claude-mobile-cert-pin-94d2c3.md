# Mobile app: trust a plant server with a private or self-signed certificate by its SHA-256 fingerprint

- Status: done
- Session: mobile-cert-pin-94d2c3, 2026-11-03
- Pull request: #318
- Section 5 item: new
- Components/files: mobile/plant_core (cert_trust.dart new, modules.dart `PcServer`, plant_core.dart export), mobile/plant_app/lib/src/app.dart (shared HTTP client), mobile/plant_app/lib/src/add_server.dart, docs/REQUIREMENTS-VISION-SYSTEM.md 17.3 (MOB-TLS-01..04)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-11-03, PR #318: PcCertTrust pins a server's certificate by SHA-256; typed on the add-server screen; Req 17.3 MOB-TLS-01..04)` Mobile app: trust for a plant server with a private or self-signed certificate (certificate fingerprint taken with the QR add flow).

## Timeline (section 3)
| 2026-11-03 | Mobile app trusts a plant server's private or self-signed certificate by its SHA-256 fingerprint: each server keeps an optional fingerprint; the app's one HTTP client accepts an untrusted certificate only when it matches a server pinned at that host and port, and trusted certificates work as before; the fingerprint is typed on the add-server screen. Tests against a local TLS server on a throw-away certificate. | 17.3 (MOB-TLS-01..04) |

## Decisions (section 4)
| Mobile certificate pinning | The fingerprint widens trust for one certificate only; it is checked in `badCertificateCallback`, so a certificate the phone trusts (public or company CA) is never blocked by it. Matching is by host and port of an added https server; a damaged stored fingerprint is dropped, so that server fails to connect rather than passing. The fingerprint is not a secret and lives with the server in the settings. | 17.3 (MOB-TLS-02, MOB-TLS-03) |

## Update log (section 8)
| 2026-11-03 | Requirements 17.3: registered MOB-TLS-01..04 (mobile trust of a private server certificate by fingerprint). | Claude (Mac, branch claude/mobile-cert-pin-94d2c3) |

## Pending
- Mobile app: take the server certificate's SHA-256 fingerprint from the server's QR code in the QR add flow (now typed by hand, MOB-TLS-04).
- Mobile app: add-server fingerprint field strings (label, helper, error) in the 14 languages beyond English.
- Mobile app: show and change a saved server's certificate fingerprint (for a renewed self-signed certificate); today the server is removed and added again.
