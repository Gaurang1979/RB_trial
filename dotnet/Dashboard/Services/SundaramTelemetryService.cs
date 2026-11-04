using System.Collections.Concurrent;
using VisionCore.Telemetry;

namespace Dashboard.Services;

/// <summary>
/// Centralized telemetry and error aggregation service for Sundaram Enterprise.
/// Groups similar errors across plants/locations into deduplicated clusters,
/// provides 1-click Claude diagnostic package exports, and manages staged software rollouts.
/// </summary>
public sealed class SundaramTelemetryService
{
    private readonly ConcurrentDictionary<string, FacilityStatus> _facilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ErrorCluster> _clusters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ClaudeDiagnosticPackage> _recentIncidents = new();
    private readonly List<ReleasePackage> _releases = new();
    private readonly object _lock = new();

    public SundaramTelemetryService()
    {
        InitializeDefaultSundaramTopology();
        SeedSampleTelemetry();
    }

    private void InitializeDefaultSundaramTopology()
    {
        var chennai = new FacilityStatus
        {
            FacilityId = "SUN-CHE",
            FacilityName = "Sundaram Chennai",
            Location = "Sriperumbudur Industrial Corridor, Chennai",
            TotalLines = 3,
            ActiveLines = 3,
            IsOnline = true
        };
        chennai.Lines.Add(new LineDeployment { Facility = "Sundaram Chennai", Line = "Line 01", InstalledVersion = "v2.4.0", TargetVersion = "v2.4.0", AssignedStage = ReleaseStage.Staged50, Status = DeploymentStatus.Synced });
        chennai.Lines.Add(new LineDeployment { Facility = "Sundaram Chennai", Line = "Line 02", InstalledVersion = "v2.4.0", TargetVersion = "v2.4.0", AssignedStage = ReleaseStage.Staged50, Status = DeploymentStatus.Synced });
        chennai.Lines.Add(new LineDeployment { Facility = "Sundaram Chennai", Line = "Line 03", InstalledVersion = "v2.3.9", TargetVersion = "v2.4.0", AssignedStage = ReleaseStage.Staged50, Status = DeploymentStatus.PendingApproval });

        var hosur = new FacilityStatus
        {
            FacilityId = "SUN-HOS",
            FacilityName = "Sundaram Hosur",
            Location = "SIPCOT Phase II, Hosur",
            TotalLines = 3,
            ActiveLines = 3,
            IsOnline = true
        };
        hosur.Lines.Add(new LineDeployment { Facility = "Sundaram Hosur", Line = "Line 01", InstalledVersion = "v2.4.0", TargetVersion = "v2.4.0", AssignedStage = ReleaseStage.Staged50, Status = DeploymentStatus.Synced });
        hosur.Lines.Add(new LineDeployment { Facility = "Sundaram Hosur", Line = "Line 02", InstalledVersion = "v2.3.9", TargetVersion = "v2.4.0", AssignedStage = ReleaseStage.Staged50, Status = DeploymentStatus.PendingApproval });
        hosur.Lines.Add(new LineDeployment { Facility = "Sundaram Hosur", Line = "Line 04", InstalledVersion = "v2.4.1-canary", TargetVersion = "v2.4.1-canary", AssignedStage = ReleaseStage.Canary, Status = DeploymentStatus.Synced });

        var pune = new FacilityStatus
        {
            FacilityId = "SUN-PUN",
            FacilityName = "Sundaram Pune",
            Location = "Chakan Industrial Area, Pune",
            TotalLines = 2,
            ActiveLines = 2,
            IsOnline = true
        };
        pune.Lines.Add(new LineDeployment { Facility = "Sundaram Pune", Line = "Line 01", InstalledVersion = "v2.3.9", TargetVersion = "v2.3.9", AssignedStage = ReleaseStage.GeneralAvailability, Status = DeploymentStatus.Synced });
        pune.Lines.Add(new LineDeployment { Facility = "Sundaram Pune", Line = "Line 02", InstalledVersion = "v2.3.9", TargetVersion = "v2.3.9", AssignedStage = ReleaseStage.GeneralAvailability, Status = DeploymentStatus.Synced });

        _facilities[chennai.FacilityName] = chennai;
        _facilities[hosur.FacilityName] = hosur;
        _facilities[pune.FacilityName] = pune;

        _releases.Add(new ReleasePackage
        {
            Version = "v2.4.1-canary",
            Stage = ReleaseStage.Canary,
            GitCommit = "3717abd",
            ReleaseNotes = "Includes IP camouflage framework, corert64 opaque runtime dispatcher, and in-memory AES-256-GCM SecureAssetVault.",
            ReleaseDateUtc = DateTime.UtcNow.AddHours(-2),
            IsActive = true,
            TargetLinesTotal = 1,
            TargetLinesUpdated = 1
        });

        _releases.Add(new ReleasePackage
        {
            Version = "v2.4.0",
            Stage = ReleaseStage.Staged50,
            GitCommit = "79cba60",
            ReleaseNotes = "Overview.ai inspection tools: Haystack 2D scatter triage, SplitWipe defect inspector, Check Size calipers, Count & Spot blobs.",
            ReleaseDateUtc = DateTime.UtcNow.AddDays(-3),
            IsActive = true,
            TargetLinesTotal = 8,
            TargetLinesUpdated = 4
        });

        _releases.Add(new ReleasePackage
        {
            Version = "v2.3.9",
            Stage = ReleaseStage.GeneralAvailability,
            GitCommit = "1cb56a8",
            ReleaseNotes = "Stable base release with 1D/2D DPM barcode reader and OCR / OCV engine.",
            ReleaseDateUtc = DateTime.UtcNow.AddDays(-14),
            IsActive = false,
            TargetLinesTotal = 8,
            TargetLinesUpdated = 8
        });
    }

    private void SeedSampleTelemetry()
    {
        // 1. Camera Timeout Cluster (Affects Chennai L1 & Hosur L2)
        var exCam = new TimeoutException("Hikrobot MV-CS050 frame grab timed out after 500ms.");
        var pkg1 = ClaudeDiagnosticPackage.Create(
            exCam, "Sundaram Chennai", "Line 01", "Station 01",
            FaultCategory.Camera, "ERR_CAM_TIMEOUT", "vs_thermal",
            new[] { "DI_01 trigger received", "Frame grab dispatched", "500ms timeout expired without frame" },
            "Optocoupler debounce window too tight or optical trigger flag dirty.");
        IngestIncident(pkg1);

        var pkg2 = ClaudeDiagnosticPackage.Create(
            exCam, "Sundaram Hosur", "Line 02", "Station 01",
            FaultCategory.Camera, "ERR_CAM_TIMEOUT", "vs_thermal",
            new[] { "DI_01 trigger received", "Frame grab dispatched", "500ms timeout expired without frame" },
            "Check conveyor sync signal jitter.");
        IngestIncident(pkg2);

        // 2. PLC Handshake Timeout (Affects Pune L1 & Chennai L3)
        var exPlc = new InvalidOperationException("Delta DVP Modbus TCP handshake ACK not received within 150ms.");
        var pkgPlc1 = ClaudeDiagnosticPackage.Create(
            exPlc, "Sundaram Pune", "Line 01", "Station 01",
            FaultCategory.PlcCommunication, "ERR_PLC_TIMEOUT", "vs_anomaly",
            new[] { "NG verdict produced for part #4819", "PLC pulse write initiated on coil Y0", "ACK timeout expired" },
            "PLC ethernet switch packet loss or IP address collision.");
        IngestIncident(pkgPlc1);

        var pkgPlc2 = ClaudeDiagnosticPackage.Create(
            exPlc, "Sundaram Chennai", "Line 03", "Station 01",
            FaultCategory.PlcCommunication, "ERR_PLC_TIMEOUT", "vs_anomaly",
            new[] { "NG verdict produced for part #9102", "PLC pulse write initiated on coil Y0", "ACK timeout expired" },
            "Check DVP Ethernet module socket backlog.");
        IngestIncident(pkgPlc2);

        // 3. Corert Opaque Dispatcher Fault (Affects Hosur L4 Canary)
        var exCorert = new AccessViolationException("Native opaque runtime dispatcher encountered unaligned memory buffer for op_code 0x00000007.");
        var pkgCorert = ClaudeDiagnosticPackage.Create(
            exCorert, "Sundaram Hosur", "Line 04", "Station 02",
            FaultCategory.NativeEngine, "ERR_CORERT_DISPATCH", "vs_deform",
            new[] { "Non-rigid deformation grid loaded", "Invoking sys_dispatch(0x00000007)", "Native buffer alignment fault" },
            "Ensure 64-byte AVX-512 memory alignment for deformation projection vectors.");
        IngestIncident(pkgCorert);
    }

    public void IngestIncident(ClaudeDiagnosticPackage incident)
    {
        _recentIncidents.Enqueue(incident);
        while (_recentIncidents.Count > 500 && _recentIncidents.TryDequeue(out _)) { }

        string sig = incident.CalculateSignature();
        _clusters.AddOrUpdate(
            sig,
            _ => ErrorCluster.Create(incident),
            (_, cluster) =>
            {
                cluster.AddIncident(incident);
                return cluster;
            });

        // Update facility and line status
        if (_facilities.TryGetValue(incident.Facility, out var fac))
        {
            fac.LastHeartbeatUtc = DateTime.UtcNow;
            if (incident.FaultCategory == FaultCategory.NativeEngine || incident.FaultCode.Contains("CRASH"))
            {
                fac.CriticalErrors24h++;
            }
            var line = fac.Lines.FirstOrDefault(l => string.Equals(l.Line, incident.Line, StringComparison.OrdinalIgnoreCase));
            line?.RecordIncident();
        }
    }

    public IReadOnlyList<FacilityStatus> GetFacilities()
    {
        return _facilities.Values.OrderBy(f => f.FacilityName).ToList();
    }

    public IReadOnlyList<ErrorCluster> GetClusters()
    {
        return _clusters.Values.OrderByDescending(c => c.Severity).ThenByDescending(c => c.TotalOccurrences).ToList();
    }

    public ErrorCluster? GetCluster(string clusterId)
    {
        _clusters.TryGetValue(clusterId, out var cluster);
        return cluster;
    }

    public void UpdateClusterStatus(string clusterId, ClusterStatus status)
    {
        if (_clusters.TryGetValue(clusterId, out var cluster))
        {
            cluster.Status = status;
        }
    }

    public IReadOnlyList<ClaudeDiagnosticPackage> GetRecentIncidents(int max = 50)
    {
        return _recentIncidents.Reverse().Take(max).ToList();
    }

    public ClaudeDiagnosticPackage? GetIncident(string incidentId)
    {
        return _recentIncidents.FirstOrDefault(i => string.Equals(i.IncidentId, incidentId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ReleasePackage> GetReleases()
    {
        lock (_lock)
        {
            return _releases.OrderByDescending(r => r.ReleaseDateUtc).ToList();
        }
    }

    public void PromoteReleaseStage(string version, ReleaseStage newStage)
    {
        lock (_lock)
        {
            var rel = _releases.FirstOrDefault(r => r.Version == version);
            if (rel is not null)
            {
                int index = _releases.IndexOf(rel);
                _releases[index] = rel with { Stage = newStage };

                // Update assigned stages for lines targeting this version
                foreach (var fac in _facilities.Values)
                {
                    foreach (var line in fac.Lines)
                    {
                        if (line.TargetVersion == version)
                        {
                            line.AssignedStage = newStage;
                        }
                    }
                }
            }
        }
    }

    public void AssignTargetVersion(string facility, string lineName, string targetVersion)
    {
        if (_facilities.TryGetValue(facility, out var fac))
        {
            var line = fac.Lines.FirstOrDefault(l => string.Equals(l.Line, lineName, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                line.TargetVersion = targetVersion;
                line.Status = DeploymentStatus.Updating;
                line.LastUpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    public void TriggerRollback(string facility, string lineName)
    {
        if (_facilities.TryGetValue(facility, out var fac))
        {
            var line = fac.Lines.FirstOrDefault(l => string.Equals(l.Line, lineName, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                // Find previous stable GA version
                string stableVersion = "v2.3.9";
                line.TriggerRollback(stableVersion);
            }
        }
    }
}
