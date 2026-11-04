using VisionStudio.Services;
using Xunit;

namespace VisionStudio.Tests;

public class ToolingDiagnosticsTests
{
    [Fact]
    public void SampleZones_ContainsEightZonesWithConfiguredNominalAndZoneFiveFault()
    {
        var zones = ToolingDiagnosticsCalculator.CreateSampleZones();
        Assert.Equal(8, zones.Length);

        var zone5 = zones.First(z => z.ZoneId == 5);
        Assert.Equal(ZoneThermalStatus.Fault, zone5.Status);
        Assert.True(zone5.Delta < -25.0);
        Assert.Contains("DEFICIT", zone5.StatusText);

        var zone1 = zones.First(z => z.ZoneId == 1);
        Assert.Equal(ZoneThermalStatus.Normal, zone1.Status);
        Assert.Equal("NOMINAL", zone1.StatusText);
    }

    [Theory]
    [InlineData(145.0, 145.0, 0.0, ZoneThermalStatus.Normal)]
    [InlineData(148.0, 145.0, 3.0, ZoneThermalStatus.Normal)]
    [InlineData(152.0, 145.0, 7.0, ZoneThermalStatus.Warning)]
    [InlineData(138.0, 145.0, -7.0, ZoneThermalStatus.Warning)]
    [InlineData(114.2, 145.0, -30.8, ZoneThermalStatus.Fault)]
    [InlineData(165.0, 145.0, 20.0, ZoneThermalStatus.Fault)]
    public void ZoneReading_CalculatesStatusAndDeltaCorrectly(
        double actual, double nominal, double expectedDelta, ZoneThermalStatus expectedStatus)
    {
        var reading = new ZoneReading(1, "Test Zone", actual, nominal);
        Assert.Equal(expectedDelta, reading.Delta, precision: 1);
        Assert.Equal(expectedStatus, reading.Status);
    }

    [Fact]
    public void GeneratePrescription_WhenFaultPresent_RecommendsMechanicalActionAndPressureBoost()
    {
        var zones = ToolingDiagnosticsCalculator.CreateSampleZones();
        var prescription = ToolingDiagnosticsCalculator.GeneratePrescription(zones, observedThicknessMm: 1.12, nominalThicknessMm: 1.85);

        Assert.True(prescription.ActionRequired);
        Assert.Equal("Critical", prescription.Severity);
        Assert.Contains("Increase Upper Sealer Jaw Pressure", prescription.Title);
        Assert.Contains("Zone 5", prescription.SuggestedTempAdjustment);
        Assert.Contains("+0.8 bar", prescription.SuggestedPressureAdjustment);
        Assert.Contains("45 Nm", prescription.MechanicalLeveling);
        Assert.Contains("260 ms", prescription.DwellTimeAdjustment);
    }

    [Fact]
    public void GeneratePrescription_WhenAllZonesNominal_ReturnsHealthyPrescription()
    {
        var healthyZones = new ZoneReading[]
        {
            new(1, "Zone 1", 145.2, 145.0),
            new(2, "Zone 2", 144.9, 145.0),
            new(3, "Zone 3", 145.1, 145.0),
            new(4, "Zone 4", 145.0, 145.0)
        };

        var prescription = ToolingDiagnosticsCalculator.GeneratePrescription(healthyZones);

        Assert.False(prescription.ActionRequired);
        Assert.Equal("Normal", prescription.Severity);
        Assert.Equal("Sealer Tooling Healthy", prescription.Title);
    }

    [Fact]
    public void GeneratePrescription_WhenWarningPresent_ReturnsDriftMonitoring()
    {
        var driftZones = new ZoneReading[]
        {
            new(1, "Zone 1", 145.0, 145.0),
            new(2, "Zone 2", 152.0, 145.0), // +7.0 drift
            new(3, "Zone 3", 145.0, 145.0)
        };

        var prescription = ToolingDiagnosticsCalculator.GeneratePrescription(driftZones);

        Assert.False(prescription.ActionRequired);
        Assert.Equal("Warning", prescription.Severity);
        Assert.Contains("Monitor Sealer Thermal Drift", prescription.Title);
        Assert.Contains("Zone #2", prescription.Summary);
    }

    [Fact]
    public void Pareto_NoNg_GivesNoRows()
    {
        Assert.Empty(ToolingDiagnosticsCalculator.ParetoFromCounts(0, []));
        Assert.Empty(ToolingDiagnosticsCalculator.ParetoFromCounts(0, [KeyValuePair.Create("no_code", 0L)]));
    }

    [Fact]
    public void Pareto_OneReason_IsAllNg()
    {
        var p = Assert.Single(ToolingDiagnosticsCalculator.ParetoFromCounts(7, [KeyValuePair.Create("no_code", 7L)]));
        Assert.Equal(new ParetoDefectMode("no_code", 7, 100.0), p);
    }

    [Fact]
    public void Pareto_SeveralReasons_MostFirstWithShareOfNgParts()
    {
        var p = ToolingDiagnosticsCalculator.ParetoFromCounts(8,
        [
            KeyValuePair.Create("too_small", 2L),
            KeyValuePair.Create("no_code", 6L),
            KeyValuePair.Create("duplicate", 2L),
            KeyValuePair.Create("unused", 0L),
        ]);
        Assert.Equal(["no_code", "duplicate", "too_small"], p.Select(m => m.Mode));
        Assert.Equal([6L, 2L, 2L], p.Select(m => m.Count));
        Assert.Equal([75.0, 25.0, 25.0], p.Select(m => m.Pct));
    }

    [Fact]
    public void Pareto_ReasonAboveNgPartCount_NeverPassesHundredPercent()
    {
        var p = ToolingDiagnosticsCalculator.ParetoFromCounts(0, [KeyValuePair.Create("unknown", 3L)]);
        Assert.Equal(100.0, Assert.Single(p).Pct);
    }
}
