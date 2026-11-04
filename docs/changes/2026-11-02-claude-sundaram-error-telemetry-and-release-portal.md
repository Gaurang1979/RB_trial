# Sundaram error telemetry, Claude diagnostic prompt generator, and staged version releases

- Status: done
- Session: sundaram-error-telemetry-and-release-portal, 2026-11-02
- Pull request: #241
- Section 5 item: new
- Components/files: dotnet/VisionCore/Telemetry/ClaudeDiagnosticPackage.cs, dotnet/VisionCore/Telemetry/ErrorCluster.cs, dotnet/VisionCore/Telemetry/ErrorReporter.cs, dotnet/VisionCore/Telemetry/ReleaseStaging.cs, dotnet/VisionCore.Tests/SundaramTelemetryTests.cs, dotnet/Dashboard/Services/SundaramTelemetryService.cs, dotnet/Dashboard/Components/Pages/SundaramAdmin.razor, dotnet/Dashboard/Program.cs, dotnet/Dashboard/Components/Layout/MainLayout.razor, dotnet/Dashboard.Tests/SundaramDashboardTests.cs, dotnet/VisionStudio/Components/Pages/Run.razor
- Depends on: #240

## Section 5
- New: [x] (done 2026-11-02, PR #241) Sundaram Enterprise Error Telemetry, Claude Diagnostic Prompt Generator, Cross-Facility Grouped Incident Hub, and Staged Version Release Portal. Auto-groups similar faults across multiple plants (Chennai, Hosur, Pune) into deduplicated clusters by signature hash, provides 1-click formatted Claude diagnostic prompt exports with hardware/pipeline context (IP camouflaged), and manages progressive staged software rollouts (Canary -> Pilot -> Staged 25% -> Staged 50% -> GA 100%) with automated health guard rollbacks.

## Summary of Changes
1. **Claude Diagnostic Package (`ClaudeDiagnosticPackage.cs` / `VisionCore.Telemetry`)**:
   - Standardized incident telemetry capturing timestamp, facility, line, station, fault category, error code, exception, normalized stack trace, OS/CLR metrics, memory working set, host uptime, camera frame parameters, and recent plant events.
   - 1-click `ToClaudeMarkdownPrompt()` generator formatted specifically for Claude to analyze root causes, check line safety (fail-safe to NG, zero false accepts), provide drop-in code fixes, and generate regression tests.
   - Built-in IP camouflage integration: uses `CamouflageLabels` to ensure proprietary algorithm names are never disclosed in exported reports.
2. **Deterministic Clustering Algorithm (`ErrorCluster.cs`)**:
   - Groups similar errors occurring across different machines, lines, and plant locations (e.g. Sundaram Chennai, Sundaram Hosur, Sundaram Pune) into unified incident clusters.
   - Fingerprint calculation `CLUST-{HASH}` normalizes stack frames (stripping line numbers, memory addresses, and file paths).
   - Generates aggregated enterprise prompts (`ToAggregatedClaudePrompt()`) summarizing total occurrences, first/last seen timestamps, and list of all affected lines.
3. **In-Process Telemetry Buffer (`ErrorReporter.cs`)**:
   - Thread-safe circular buffer (500 incidents) and cluster registry in `VisionCore`.
   - Event notifications for real-time UI updates.
4. **Staged Software Release System (`ReleaseStaging.cs`)**:
   - Progressive deployment lifecycle: Canary (1 line) -> Pilot (1 facility) -> Staged (25%/50%) -> General Availability (100%).
   - Line deployment tracking, target version assignment, and automated rollback triggering if incident counts exceed tolerance thresholds.
5. **Centralized Sundaram Admin Portal (`SundaramAdmin.razor` & `SundaramTelemetryService.cs`)**:
   - Dedicated management UI at `/sundaram` with 4 interactive tabs:
     - **Cross-Facility Grouped Error Clusters**: Searchable, severity-filtered table with occurrence counts, affected lines, status dropdowns, and 1-click **"📋 Copy Claude Prompt"** buttons with clipboard integration.
     - **Facilities & Line Matrix**: Real-time line-by-line status for Sundaram Chennai, Hosur, and Pune with version sync and rollback triggers.
     - **Staged Version Releases**: Visual deployment pipeline with stage promotion controls.
     - **Live Incident Stream**: Chronological feed with raw stack trace viewing and diagnostic export.
   - REST API endpoints registered in `Program.cs` (`/api/sundaram/clusters`, `/api/sundaram/facilities`, `/api/sundaram/releases`, `/api/sundaram/telemetry/errors`).
6. **VisionStudio Operator Run Integration (`Run.razor`)**:
   - Direct "📋 Copy Diagnostic Report for Claude" button rendered on line fault alerts so floor technicians can immediately capture the incident.

## Verification
- `VisionCore.Tests` (11/11 tests passed in 22 ms):
  - Verified Claude prompt formatting and IP camouflage stripping.
  - Verified cross-facility cluster signature grouping (Chennai + Hosur + Pune).
  - Verified failure threshold rollback triggering.
- `Dashboard.Tests` (14/14 tests passed in 57 ms):
  - Verified Sundaram topology initialization, cluster aggregation, status persistence, stage promotion, and rollback execution.
- `VisionStudio.Tests` (34/34 tests passed in 383 ms):
  - Verified no regressions in VisionStudio tool runners and UI.
