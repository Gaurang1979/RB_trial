using VisionCore.Telemetry;
using Xunit;

namespace VisionCore.Tests;

public class SundaramTelemetryTests
{
    [Fact]
    public void ClaudeDiagnosticPackage_GeneratesActionableMarkdownPrompt()
    {
        var ex = new TimeoutException("Hikrobot MV-CS050 frame grab timed out after 500ms.");
        var pkg = ClaudeDiagnosticPackage.Create(
            ex,
            facility: "Sundaram Chennai",
            line: "Line 02",
            station: "Station 01",
            category: FaultCategory.Camera,
            faultCode: "ERR_CAM_TIMEOUT",
            rawToolName: "vs_thermal",
            recentEvents: new[] { "Trigger DI_01 pulse received", "Frame grab started", "500ms timeout expired" },
            diagnosticHint: "Check hardware trigger line or increase trigger debounce window.");

        string prompt = pkg.ToClaudeMarkdownPrompt();

        // Check key prompt sections exist
        Assert.Contains("# CLAUDE DIAGNOSTIC REPORT: VISION SYSTEM INCIDENT", prompt);
        Assert.Contains("Sundaram Chennai", prompt);
        Assert.Contains("Line 02", prompt);
        Assert.Contains("ERR_CAM_TIMEOUT", prompt);
        Assert.Contains("TimeoutException", prompt);
        Assert.Contains("Hikrobot MV-CS050", prompt);
        Assert.Contains("Seal Quality Check", prompt); // IP protection: vs_thermal camouflaged!
        Assert.DoesNotContain("vs_thermal", prompt);   // Must never leak internal algorithm name
        Assert.Contains("Actionable Instructions for Claude", prompt);
        Assert.Contains("Root Cause Analysis", prompt);
        Assert.Contains("Line Safety Check", prompt);
        Assert.Contains("Drop-in Code Fix", prompt);
    }

    [Fact]
    public void ClaudeDiagnosticPackage_CalculateSignature_GroupsSimilarErrorsAcrossPlants()
    {
        var ex1 = new InvalidOperationException("PLC queue full on Delta DVP");
        var pkgChennai = ClaudeDiagnosticPackage.Create(
            ex1, "Sundaram Chennai", "Line 01", "Station 01",
            FaultCategory.PlcCommunication, "ERR_PLC_QUEUE_OVERRUN");

        var ex2 = new InvalidOperationException("PLC queue full on Delta DVP");
        var pkgHosur = ClaudeDiagnosticPackage.Create(
            ex2, "Sundaram Hosur", "Line 04", "Station 02",
            FaultCategory.PlcCommunication, "ERR_PLC_QUEUE_OVERRUN");

        // The fingerprints must be identical despite coming from different plants/lines/timestamps
        string sigChennai = pkgChennai.CalculateSignature();
        string sigHosur = pkgHosur.CalculateSignature();

        Assert.Equal(sigChennai, sigHosur);
    }

    [Fact]
    public void ClaudeDiagnosticPackage_CalculateSignature_DifferentiatesDifferentErrors()
    {
        var ex1 = new TimeoutException("Camera timeout");
        var pkgCam = ClaudeDiagnosticPackage.Create(
            ex1, "Sundaram Chennai", "Line 01", "Station 01",
            FaultCategory.Camera, "ERR_CAM_TIMEOUT");

        var ex2 = new OutOfMemoryException("Memory limit exceeded");
        var pkgMem = ClaudeDiagnosticPackage.Create(
            ex2, "Sundaram Chennai", "Line 01", "Station 01",
            FaultCategory.SystemResources, "ERR_MEMORY_PRESSURE");

        Assert.NotEqual(pkgCam.CalculateSignature(), pkgMem.CalculateSignature());
    }

    [Fact]
    public void ErrorCluster_AggregatesOccurrencesAndGeneratesEnterpriseClaudePrompt()
    {
        var ex = new IOException("Corrupted recipe stream payload");
        var pkg1 = ClaudeDiagnosticPackage.Create(
            ex, "Sundaram Chennai", "Line 01", "Station 01",
            FaultCategory.Configuration, "ERR_RECIPE_CORRUPT");
        var pkg2 = ClaudeDiagnosticPackage.Create(
            ex, "Sundaram Hosur", "Line 02", "Station 01",
            FaultCategory.Configuration, "ERR_RECIPE_CORRUPT");
        var pkg3 = ClaudeDiagnosticPackage.Create(
            ex, "Sundaram Pune", "Line 01", "Station 03",
            FaultCategory.Configuration, "ERR_RECIPE_CORRUPT");

        var cluster = ErrorCluster.Create(pkg1);
        cluster.AddIncident(pkg2);
        cluster.AddIncident(pkg3);

        Assert.Equal(3, cluster.TotalOccurrences);
        Assert.Equal(3, cluster.AffectedFacilities.Count);
        Assert.Contains("Sundaram Chennai", cluster.AffectedFacilities);
        Assert.Contains("Sundaram Hosur", cluster.AffectedFacilities);
        Assert.Contains("Sundaram Pune", cluster.AffectedFacilities);

        string aggPrompt = cluster.ToAggregatedClaudePrompt();
        Assert.Contains("# CLAUDE DIAGNOSTIC REPORT: AGGREGATED ENTERPRISE INCIDENT CLUSTER", aggPrompt);
        Assert.Contains("**Total Occurrences**: **3**", aggPrompt);
        Assert.Contains("Sundaram Chennai", aggPrompt);
        Assert.Contains("Sundaram Hosur", aggPrompt);
        Assert.Contains("Sundaram Pune", aggPrompt);
        Assert.Contains("Required Claude Resolution", aggPrompt);
    }

    [Fact]
    public void ErrorReporter_MaintainsClusterRegistryAndIncidentBuffer()
    {
        var reporter = ErrorReporter.Instance;
        reporter.Clear();

        var ex = new ArithmeticException("Divide by zero in caliper locator");
        reporter.RecordException(
            ex, "Sundaram Hosur", "Line 01", "Station 01",
            FaultCategory.ToolExecution, "ERR_CALIPER_DIV0", "vs_locate");

        reporter.RecordException(
            ex, "Sundaram Hosur", "Line 03", "Station 01",
            FaultCategory.ToolExecution, "ERR_CALIPER_DIV0", "vs_locate");

        var clusters = reporter.GetClusters();
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].TotalOccurrences);

        var incidents = reporter.GetRecentIncidents();
        Assert.Equal(2, incidents.Count);
    }

    [Fact]
    public void LineDeployment_TriggersRollbackWhenErrorThresholdExceeded()
    {
        var deploy = new LineDeployment
        {
            Facility = "Sundaram Chennai",
            Line = "Line 01",
            InstalledVersion = "v2.4.0-canary",
            TargetVersion = "v2.4.0-canary",
            AssignedStage = ReleaseStage.Canary,
            MaxAllowedErrors = 3
        };

        deploy.RecordIncident();
        Assert.Equal(DeploymentStatus.Synced, deploy.Status);

        deploy.RecordIncident();
        Assert.Equal(DeploymentStatus.Synced, deploy.Status);

        deploy.RecordIncident(); // 3rd incident = threshold reached
        Assert.Equal(DeploymentStatus.RollbackRequired, deploy.Status);

        // Execute rollback
        deploy.TriggerRollback("v2.3.9");
        Assert.Equal("v2.3.9", deploy.InstalledVersion);
        Assert.Equal(DeploymentStatus.Synced, deploy.Status);
        Assert.Equal(0, deploy.ErrorCountSinceDeploy);
    }
}
