using System.Collections.Concurrent;

namespace VisionCore.Telemetry;

/// <summary>
/// In-process telemetry reporter and circular buffer for vision system errors.
/// Can be invoked directly throughout VisionCore, VisionStudio, LineRunner, and StationGuard.
/// </summary>
public sealed class ErrorReporter
{
    private static readonly Lazy<ErrorReporter> _instance = new(() => new ErrorReporter());
    public static ErrorReporter Instance => _instance.Value;

    private readonly ConcurrentQueue<ClaudeDiagnosticPackage> _recentIncidents = new();
    private readonly ConcurrentDictionary<string, ErrorCluster> _clusters = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxStoredIncidents = 500;

    public event Action<ClaudeDiagnosticPackage>? IncidentRecorded;

    public ClaudeDiagnosticPackage RecordException(
        Exception ex,
        string facility,
        string line,
        string station,
        FaultCategory category,
        string faultCode,
        string? rawToolName = null,
        IReadOnlyList<string>? recentEvents = null,
        string? diagnosticHint = null)
    {
        var pkg = ClaudeDiagnosticPackage.Create(
            ex, facility, line, station, category, faultCode, rawToolName, recentEvents, diagnosticHint);

        RecordPackage(pkg);
        return pkg;
    }

    public void RecordPackage(ClaudeDiagnosticPackage pkg)
    {
        _recentIncidents.Enqueue(pkg);
        while (_recentIncidents.Count > MaxStoredIncidents && _recentIncidents.TryDequeue(out _)) { }

        string sig = pkg.CalculateSignature();
        _clusters.AddOrUpdate(
            sig,
            _ => ErrorCluster.Create(pkg),
            (_, cluster) =>
            {
                cluster.AddIncident(pkg);
                return cluster;
            });

        IncidentRecorded?.Invoke(pkg);
    }

    public IReadOnlyList<ClaudeDiagnosticPackage> GetRecentIncidents(int max = 50)
    {
        return _recentIncidents.Reverse().Take(max).ToList();
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

    public ClaudeDiagnosticPackage? GetIncident(string incidentId)
    {
        return _recentIncidents.FirstOrDefault(i => string.Equals(i.IncidentId, incidentId, StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        while (_recentIncidents.TryDequeue(out _)) { }
        _clusters.Clear();
    }
}
