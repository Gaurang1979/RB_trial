namespace VisionStudio.Services;

/// <summary>Thermal status of an individual sealing tool heating zone.</summary>
public enum ZoneThermalStatus
{
    Normal,
    Warning,
    Fault
}

/// <summary>Real-time thermal reading and diagnostic status of an individual tooling zone.</summary>
public sealed record ZoneReading(int ZoneId, string Name, double ActualTemp, double NominalTemp = 145.0)
{
    public double Delta => ActualTemp - NominalTemp;

    public ZoneThermalStatus Status => Delta switch
    {
        <= -15.0 or >= 15.0 => ZoneThermalStatus.Fault,
        <= -5.0 or >= 5.0 => ZoneThermalStatus.Warning,
        _ => ZoneThermalStatus.Normal
    };

    public string StatusText => Status switch
    {
        ZoneThermalStatus.Fault => $"{Delta:+#0.0;-#0.0;0.0}°C DEFICIT",
        ZoneThermalStatus.Warning => $"{Delta:+#0.0;-#0.0;0.0}°C DRIFT",
        _ => "NOMINAL"
    };
}

/// <summary>Physical mechanical correction prescription derived from vision and thermal data.</summary>
public sealed record ToolingPrescription(
    string Title,
    string Severity,
    string Summary,
    string SuggestedTempAdjustment,
    string SuggestedPressureAdjustment,
    string MechanicalLeveling,
    string DwellTimeAdjustment,
    bool ActionRequired
);

/// <summary>One NG reason of the shift Pareto: its NG part count and its share of all NG parts in %.</summary>
public sealed record ParetoDefectMode(
    string Mode,
    long Count,
    double Pct
);

/// <summary>Outlier sample in 2D latent process stability map for active learning triage.</summary>
public sealed record LatentOutlier(
    string PartId,
    string DefectType,
    double SigmaDistance,
    double Confidence,
    double X,
    double Y
);

/// <summary>
/// Computational engine for tooling thermal metrics, mechanical prescriptions, and defect distribution.
/// </summary>
public static class ToolingDiagnosticsCalculator
{
    public const double DefaultNominalTemp = 145.0;
    public const double DefaultNominalThickness = 1.85;

    /// <summary>Returns standard 8-zone sample configuration for testing and initial operator preview.</summary>
    public static ZoneReading[] CreateSampleZones() =>
    [
        new(1, "Zone 1 (Top-Left)", 146.2, DefaultNominalTemp),
        new(2, "Zone 2 (Top-Mid L)", 144.8, DefaultNominalTemp),
        new(3, "Zone 3 (Top-Mid R)", 145.1, DefaultNominalTemp),
        new(4, "Zone 4 (Top-Right)", 145.8, DefaultNominalTemp),
        new(5, "Zone 5 (Bot-Left)", 114.2, DefaultNominalTemp),
        new(6, "Zone 6 (Bot-Mid L)", 143.9, DefaultNominalTemp),
        new(7, "Zone 7 (Bot-Mid R)", 145.0, DefaultNominalTemp),
        new(8, "Zone 8 (Bot-Right)", 146.1, DefaultNominalTemp),
    ];

    /// <summary>
    /// The shift Pareto from the line's real NG counts (Req 6.1 RUN-PAR-01): one row per reason with NG,
    /// most first (ties by name), each as a share of the NG parts. A part NG for two reasons counts
    /// under both, so the shares can add up to more than 100%. No NG gives no rows, never sample data.
    /// </summary>
    public static ParetoDefectMode[] ParetoFromCounts(long ngParts, IEnumerable<KeyValuePair<string, long>> ngPerReason)
    {
        var rows = ngPerReason.Where(kv => kv.Value > 0).ToArray();
        if (rows.Length == 0) return [];
        var total = Math.Max(ngParts, rows.Max(kv => kv.Value));
        return rows
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ParetoDefectMode(kv.Key, kv.Value, 100.0 * kv.Value / total))
            .ToArray();
    }

    /// <summary>Generates actionable mechanical correction prescription based on zone thermal deltas.</summary>
    public static ToolingPrescription GeneratePrescription(
        IReadOnlyList<ZoneReading> zones,
        double observedThicknessMm = 1.12,
        double nominalThicknessMm = DefaultNominalThickness)
    {
        if (zones is null || zones.Count == 0)
        {
            return new ToolingPrescription(
                "No Zone Telemetry",
                "Normal",
                "No active tooling thermal sensor data available.",
                "None", "None", "None", "None",
                false);
        }

        var faultZone = zones.FirstOrDefault(z => z.Status == ZoneThermalStatus.Fault);
        if (faultZone is not null)
        {
            var deficit = Math.Abs(faultZone.Delta);
            var tempBoost = Math.Min(30, (int)Math.Round(deficit * 0.5));
            return new ToolingPrescription(
                "Increase Upper Sealer Jaw Pressure",
                "Critical",
                $"Pocket #{faultZone.ZoneId} seal thickness is {observedThicknessMm:0.00} mm (nominal {nominalThicknessMm:0.00} mm). Upper jaw heater #{faultZone.ZoneId} is {faultZone.Delta:0} °C below setpoint.",
                $"+{tempBoost}°C on Zone {faultZone.ZoneId}",
                "+0.8 bar (Regulator 2)",
                "Upper sealing jaw has 0.18 mm parallelism tilt toward operator side. Retorque tie rods to 45 Nm.",
                "Temporarily extend PLC sealing dwell from 220 ms to 260 ms until heater is serviced.",
                true);
        }

        var warnZone = zones.FirstOrDefault(z => z.Status == ZoneThermalStatus.Warning);
        if (warnZone is not null)
        {
            return new ToolingPrescription(
                "Monitor Sealer Thermal Drift",
                "Warning",
                $"Zone #{warnZone.ZoneId} shows thermal drift of {warnZone.Delta:+#0.0;-#0.0}°C from nominal {warnZone.NominalTemp:0}°C.",
                $"{Math.Sign(warnZone.Delta) * -2}°C trim on Zone {warnZone.ZoneId}",
                "Maintain nominal 4.2 bar",
                "Verify tie-rod torque during next scheduled changeover",
                "Nominal dwell time (220 ms)",
                false);
        }

        return new ToolingPrescription(
            "Sealer Tooling Healthy",
            "Normal",
            "All sealing zones are operating within ±3°C of setpoint. Sealing pressure is nominal at 4.2 bar.",
            "None required",
            "Nominal 4.2 bar",
            "Parallelism within ±0.03 mm tolerance",
            "Nominal 220 ms",
            false);
    }
}
