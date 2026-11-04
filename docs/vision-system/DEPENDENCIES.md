# Dependencies: what we use, how we keep it current

2026-10-29 · process and inventory · owner request of 2026-10-29: "Check for upgrades and new
version release for all tools, methods, modules, software, bundle and databases. Need to update
the same and complete testing. Add this in requirement and make workflow to check the same and
remind me by email."

The requirement is section 13 of [REQUIREMENTS-VISION-SYSTEM.md](../REQUIREMENTS-VISION-SYSTEM.md)
("Dependencies and upgrades"). This document says what is checked, when, and how an upgrade is
made and tested.

## 1. The monthly check

`scripts/deps-check.py` reads every manifest in the repository and asks each registry for its
newest stable release. It needs nothing beyond the Python standard library.

| What | Read from | Compared with |
| --- | --- | --- |
| .NET runtime and SDK | `TargetFramework` of every `dotnet/**/*.csproj`; `dotnet-version` in workflows | Newest released .NET (NuGet `Microsoft.NETCore.App.Ref`) |
| NuGet packages | `PackageReference` in every project | nuget.org |
| Python packages | `requirements*.txt` (plant server, edge gateway, tools) | pypi.org |
| Flutter and Dart packages | `mobile/*/pubspec.yaml` | pub.dev; the Flutter stable channel and its Dart SDK |
| Server images and databases | `Dockerfile`, `docker-compose*.yml`, workflow service images (PostgreSQL, Python, Mosquitto, TimescaleDB) | Docker Hub tags |
| C++ engine libraries | `engine/vcpkg.json` (OpenCV, zxing-cpp, libdmtx) and its vcpkg baseline | vcpkg releases |
| Build and CI | `uses:` actions in `.github/workflows` | GitHub releases |
| End of support | .NET, Python, PostgreSQL | The makers' published dates, kept in the script (`END_OF_SUPPORT`) |

The GitHub Action `.github/workflows/dependency-watch.yml` runs it at 04:23 UTC on the 1st of every
month, and whenever it is started by hand (Actions > Dependency watch > Run workflow). It:

1. runs the checker's own tests (`scripts/deps_check_test.py`);
2. writes the report, with an **Act now** list for anything near or past its end of support;
3. opens it as an issue labelled `dependency-report`, assigned to the repository owner, and closes
   last month's. GitHub e-mails the assignee;
4. sends the report by e-mail itself when the repository secrets `SMTP_HOST`, `SMTP_PORT`,
   `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` and `REPORT_TO` are set. The password lives only in
   the secrets, never in the repository.

The workflow never changes code. Automatic upgrade pull requests (Dependabot) are not used: they
would bypass the claim, review and testing-session process (AGENTS.md), and an upgrade of a
library that reads codes or images can change results.

## 2. Acting on the report

| Finding | When | How |
| --- | --- | --- |
| Security fix, or end of support within 120 days | At once; done before the end-of-support date | Its own pull request, first in the queue |
| Patch and minor releases | Each month | One pull request per area (.NET, plant server, mobile app, engine, CI) |
| Major releases | Planned: within two monthly cycles, or recorded as a decision to wait | Its own pull request per component, with the migration notes read and followed |
| A brand-new major runtime (.NET, Python, Flutter, PostgreSQL) | Not before its first bug-fix release, unless a security fix needs it | As a major release |

Every upgrade is a normal work item: claimed, built, tested once by its session (`checker` and
the bench), reviewed, and marked ready only when nothing more needs testing; then the owner
merges it. In addition:

- **Line safety.** An upgrade of anything that can change a result (the engine's libraries,
  OpenCV, zxing-cpp, libdmtx, ONNX Runtime, the AI training stack) must pass the benchmark image
  sets with read rate, false reject and escape no worse than before. A doubt means it waits.
- **Plant server lock.** Sites install the plant server from `plant_server/requirements.lock`
  (exact versions with hashes). An upgrade changes `requirements.txt`, makes the lock again with
  `plant_server/deploy/make-lock.sh` and runs the tests on an install from the new lock.
- **Databases.** A PostgreSQL or TimescaleDB major upgrade ships with a tested migration (dump and
  restore or `pg_upgrade`), a backup taken first, and a way back.
- **Installed sites** get upgrades as signed updates with rollback (section 13, "Updates").
- Licences of new or changed open-source components are checked as in section 14.

## 3. State on 2026-10-29

The first report (37 components) found:
- **.NET 8 support ends on 10 November 2026.** All 15 line-PC projects and the Windows build use it.
- 11 components are a major release behind, 11 a minor release and 3 a patch release.

| Area | In use | Newest | Plan |
| --- | --- | --- | --- |
| .NET (line PC) | .NET 8 LTS | .NET 10 LTS (10.0.14, supported to 2028-11-14) | **Urgent: move every project and the Windows build to .NET 10 before 2026-11-10.** Packages move with it: Microsoft.Data.Sqlite 10, Hosting.WindowsServices 10, Microsoft.NET.Test.Sdk 18 and xunit 2.9.3. SkiaSharp 2.88 to its current major is a separate step, checked on the rendered Run and Teach images |
| Plant server (Python) | Python 3.12, FastAPI 0.110+, uvicorn 0.27+, psycopg 3.1+, pymodbus 3.9+, asyncua 1.1+, pytest 7+ | Python 3.15, FastAPI 0.162, uvicorn 0.54, psycopg 3.3, pymodbus 3.16, asyncua 2.1, pytest 9 | Raise the minimums to the tested versions. Move the image to Python 3.14 (3.15 after its first bug-fix release). asyncua 2 is a major release: its own step with the OPC UA tests |
| Database | PostgreSQL 16 (supported to 2028-11-09) | PostgreSQL 18 | Not urgent. Plan 17 or 18 with a migration and backup procedure for installed sites; TimescaleDB to match |
| Mobile app (Flutter) | Dart 3.5 minimum, http 1.2, flutter_secure_storage 9 | Flutter 3.47.8 (Dart 3.13), http 1.6, flutter_secure_storage 11 | Raise the SDK minimum and http. flutter_secure_storage 11 is a major release: its own step, tested on Android and iOS |
| C++ engine | vcpkg baseline a073c28 (OpenCV 4, zxing-cpp, libdmtx) | Newer vcpkg; OpenCV 5 | Move the baseline while keeping OpenCV 4, through the benchmark sets. OpenCV 5 is a major release with its own evaluation |
| Build and CI | checkout v4, setup-dotnet v4, setup-python v5, upload-artifact v4, cache v4, flutter-action v2 | Checked by the workflow, with its token | Follow the report |
