using Dashboard.Services;
using VisionCore.Telemetry;
using Xunit;

namespace Dashboard.Tests;

public class SundaramDashboardTests
{
    [Fact]
    public void SundaramTelemetryService_InitializesDefaultTopologyAndSampleClusters()
    {
        var service = new SundaramTelemetryService();

        var facilities = service.GetFacilities();
        Assert.Equal(3, facilities.Count);
        Assert.Contains(facilities, f => f.FacilityName == "Sundaram Chennai");
        Assert.Contains(facilities, f => f.FacilityName == "Sundaram Hosur");
        Assert.Contains(facilities, f => f.FacilityName == "Sundaram Pune");

        var clusters = service.GetClusters();
        Assert.True(clusters.Count >= 3, "Expected sample clusters to be initialized.");

        // Check cross-plant cluster
        var camCluster = clusters.FirstOrDefault(c => c.FaultCode == "ERR_CAM_TIMEOUT");
        Assert.NotNull(camCluster);
        Assert.True(camCluster.TotalOccurrences >= 2);
        Assert.Contains("Sundaram Chennai", camCluster.AffectedFacilities);
        Assert.Contains("Sundaram Hosur", camCluster.AffectedFacilities);
    }

    [Fact]
    public void SundaramTelemetryService_IngestIncident_GroupsWithExistingCluster()
    {
        var service = new SundaramTelemetryService();
        int initialCount = service.GetClusters().First(c => c.FaultCode == "ERR_CAM_TIMEOUT").TotalOccurrences;

        // Ingest another identical camera timeout from Sundaram Pune
        var exCam = new TimeoutException("Hikrobot MV-CS050 frame grab timed out after 500ms.");
        var pkg = ClaudeDiagnosticPackage.Create(
            exCam, "Sundaram Pune", "Line 02", "Station 01",
            FaultCategory.Camera, "ERR_CAM_TIMEOUT", "vs_thermal");

        service.IngestIncident(pkg);

        var updatedCluster = service.GetClusters().First(c => c.FaultCode == "ERR_CAM_TIMEOUT");
        Assert.Equal(initialCount + 1, updatedCluster.TotalOccurrences);
        Assert.Contains("Sundaram Pune", updatedCluster.AffectedFacilities);
    }

    [Fact]
    public void SundaramTelemetryService_UpdateClusterStatus_PersistsState()
    {
        var service = new SundaramTelemetryService();
        var cluster = service.GetClusters().First();

        service.UpdateClusterStatus(cluster.ClusterId, ClusterStatus.Investigating);
        Assert.Equal(ClusterStatus.Investigating, service.GetCluster(cluster.ClusterId)?.Status);

        service.UpdateClusterStatus(cluster.ClusterId, ClusterStatus.Resolved);
        Assert.Equal(ClusterStatus.Resolved, service.GetCluster(cluster.ClusterId)?.Status);
    }

    [Fact]
    public void SundaramTelemetryService_PromoteReleaseStage_UpdatesAssignedStage()
    {
        var service = new SundaramTelemetryService();
        var releases = service.GetReleases();
        var v240 = releases.First(r => r.Version == "v2.4.0");

        Assert.Equal(ReleaseStage.Staged50, v240.Stage);

        service.PromoteReleaseStage("v2.4.0", ReleaseStage.GeneralAvailability);

        var updatedReleases = service.GetReleases();
        var updatedV240 = updatedReleases.First(r => r.Version == "v2.4.0");
        Assert.Equal(ReleaseStage.GeneralAvailability, updatedV240.Stage);
    }

    [Fact]
    public void SundaramTelemetryService_TriggerRollback_ResetsLineToStableGA()
    {
        var service = new SundaramTelemetryService();
        var hosur = service.GetFacilities().First(f => f.FacilityName == "Sundaram Hosur");
        var line4 = hosur.Lines.First(l => l.Line == "Line 04");

        // Line 4 started on v2.4.1-canary
        Assert.Equal("v2.4.1-canary", line4.InstalledVersion);

        // Trigger rollback
        service.TriggerRollback("Sundaram Hosur", "Line 04");

        Assert.Equal("v2.3.9", line4.InstalledVersion);
        Assert.Equal(DeploymentStatus.Synced, line4.Status);
        Assert.Equal(0, line4.ErrorCountSinceDeploy);
    }
}
