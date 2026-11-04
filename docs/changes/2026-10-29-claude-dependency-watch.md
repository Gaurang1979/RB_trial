# Dependency watch: check every tool, library, SDK, bundle and database for new releases monthly, upgrade, test, and remind the owner

- Status: done
- Session: Claude cloud session, 2026-10-29
- Pull request: #63
- Section 5 item: new
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md (new requirement), docs/vision-system/DEPENDENCIES.md (new), docs/vision-system/README.md, scripts/deps-check.py and scripts/deps_check_test.py (new), .github/workflows/dependency-watch.yml (new)
- Depends on: none

## Section 5
- New item: `[x] (done 2026-10-29, PR #63: scripts/deps-check.py, the monthly GitHub Action dependency-watch.yml with an issue for the owner and an optional direct e-mail, requirement DEP-01 to DEP-05, docs/vision-system/DEPENDENCIES.md) Dependency watch: check every tool, library, SDK, image and database monthly against its newest release and end of support, and remind the owner by e-mail.`
- [ ] Upgrade .NET 8 to .NET 10 LTS in every line-PC project and the Windows build before .NET 8's end of support on 2026-11-10, with Microsoft.Data.Sqlite 10, Hosting.WindowsServices 10, Microsoft.NET.Test.Sdk 18 and xunit 2.9.3.
- [ ] Upgrade the plant server's Python packages (minimums raised to the tested versions) and its image to Python 3.14.
- [ ] Upgrade the mobile app: Dart SDK minimum, http 1.6; flutter_secure_storage 11 as its own step, tested on Android and iOS.
- [ ] Upgrade the engine's vcpkg baseline while keeping OpenCV 4, through the benchmark image sets; evaluate OpenCV 5 separately.
- [ ] Plan PostgreSQL 16 to 17 or 18 (supported to 2028-11-09), with a migration and backup procedure for installed sites.
- [ ] SkiaSharp 2.88 to its current major, checked on the rendered Run and Teach images.

## Timeline (section 3)
| 2026-10-29 | Dependency watch: monthly version and end-of-support check, report issue and e-mail to the owner; first report: .NET 8 support ends 2026-11-10, 25 of 36 components behind | REQUIREMENTS 13 (DEP-01 to DEP-05) |

## Decisions (section 4)
| Upgrades are pull requests made by sessions, not Dependabot | Automatic pull requests would bypass the claim, review and testing-session process, and an upgrade of an image or code library can change results; the monthly report reminds, sessions upgrade | REQUIREMENTS 13 DEP-03, DEP-04 |
| Reminder by e-mail | The report opens as an issue assigned to the owner (GitHub e-mails the assignee) and, when SMTP secrets are set, is also e-mailed directly; no password in the repository | REQUIREMENTS 13 DEP-02 |

## Update log (section 8)
| 2026-10-29 | REQUIREMENTS section 13: Dependencies row and DEP-01 to DEP-05; section 14: .NET current LTS; new DEPENDENCIES.md | Claude (cloud, branch claude/dependency-watch) |

## Pending
- Owner: set the SMTP_* and REPORT_TO repository secrets for the direct e-mail, or rely on GitHub's issue e-mail.
