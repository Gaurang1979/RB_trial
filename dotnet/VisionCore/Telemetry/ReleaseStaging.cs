namespace VisionCore.Telemetry;

public enum ReleaseStage
{
    Canary = 0,
    Pilot = 1,
    Staged25 = 2,
    Staged50 = 3,
    GeneralAvailability = 4
}

public enum DeploymentStatus
{
    Synced,
    Updating,
    PendingApproval,
    RollbackRequired
}

public sealed record ReleasePackage
{
    public string Version { get; init; } = "v2.4.0";
    public ReleaseStage Stage { get; init; } = ReleaseStage.Pilot;
    public string GitCommit { get; init; } = "HEAD";
    public string ReleaseNotes { get; init; } = "";
    public DateTime ReleaseDateUtc { get; init; } = DateTime.UtcNow;
    public bool IsActive { get; init; } = true;
    public int TargetLinesTotal { get; init; } = 8;
    public int TargetLinesUpdated { get; init; } = 4;
}

public sealed class LineDeployment
{
    public string Facility { get; init; } = "";
    public string Line { get; init; } = "";
    public string InstalledVersion { get; set; } = "v2.3.9";
    public string TargetVersion { get; set; } = "v2.4.0";
    public ReleaseStage AssignedStage { get; set; } = ReleaseStage.Pilot;
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Synced;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public int ErrorCountSinceDeploy { get; set; } = 0;
    public int MaxAllowedErrors { get; set; } = 5;

    public void RecordIncident()
    {
        ErrorCountSinceDeploy++;
        if (ErrorCountSinceDeploy >= MaxAllowedErrors && Status != DeploymentStatus.RollbackRequired)
        {
            Status = DeploymentStatus.RollbackRequired;
        }
    }

    public void TriggerRollback(string rollbackVersion)
    {
        TargetVersion = rollbackVersion;
        InstalledVersion = rollbackVersion;
        Status = DeploymentStatus.Synced;
        ErrorCountSinceDeploy = 0;
        LastUpdatedUtc = DateTime.UtcNow;
    }
}

public sealed class FacilityStatus
{
    public string FacilityId { get; init; } = "";
    public string FacilityName { get; init; } = "";
    public string Location { get; init; } = "";
    public int TotalLines { get; init; } = 3;
    public int ActiveLines { get; set; } = 3;
    public int CriticalErrors24h { get; set; } = 0;
    public bool IsOnline { get; set; } = true;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
    public List<LineDeployment> Lines { get; } = new();
}
