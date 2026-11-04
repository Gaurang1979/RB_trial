using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

public enum ThermalInspectionMode
{
    BottleInductionSeal = 0,
    PouchHeatSeal = 1,
    HotMeltGlue = 2,
    BlisterPocket = 3
}

/// <summary>Thermal packaging seal and bead inspection (tool spec 8.4, Sun Teknovation eSaksham).</summary>
public sealed class ThermalToolConfig : ToolConfig
{
    public ThermalInspectionMode Mode { get; set; } = ThermalInspectionMode.BottleInductionSeal;

    // Common / Calibration
    public float TMinCalib { get; set; } = 20.0f;
    public float TMaxCalib { get; set; } = 120.0f;

    // Bottle induction parameters
    public float BottleInnerRadiusPx { get; set; } = 25.0f;
    public float BottleOuterRadiusPx { get; set; } = 45.0f;
    public float BottleMinSealTempC { get; set; } = 60.0f;
    public float BottleMaxSealTempC { get; set; } = 95.0f;
    public float BottleMissingFoilTempC { get; set; } = 40.0f;
    public float BottleMaxColdGapDeg { get; set; } = 15.0f;
    public float BottleMaxTiltTempDelta { get; set; } = 15.0f;

    // Pouch seam parameters
    public float PouchMinSealTempC { get; set; } = 65.0f;
    public float PouchMinSealWidthPx { get; set; } = 10.0f;
    public float PouchMaxVoidLengthPx { get; set; } = 4.0f;
    public float PouchContaminationDropC { get; set; } = 12.0f;
    public bool PouchIsVertical { get; set; } = false;

    // Hot melt adhesive parameters
    public int GlueExpectedBeads { get; set; } = 2;
    public float GlueMinTempC { get; set; } = 75.0f;
    public float GlueMinBeadLengthPx { get; set; } = 40.0f;
    public float GlueMaxBeadGapPx { get; set; } = 8.0f;
    public bool GlueIsVertical { get; set; } = false;

    // Blister pack parameters
    public int BlisterRows { get; set; } = 2;
    public int BlisterCols { get; set; } = 3;
    public float BlisterPocketWPx { get; set; } = 30.0f;
    public float BlisterPocketHPx { get; set; } = 20.0f;
    public float BlisterPitchXPx { get; set; } = 70.0f;
    public float BlisterPitchYPx { get; set; } = 50.0f;
    public float BlisterMinBarrierWidthPx { get; set; } = 8.0f;
    public float BlisterMinSealTempC { get; set; } = 60.0f;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class ThermalTool : ITool
{
    private readonly ThermalToolConfig _cfg;

    public ThermalTool(ThermalToolConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public ToolConfig Config => _cfg;

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>();
        var details = new List<string>();

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 1) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds);
        int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

        byte[] roiPixels = ToolArea.Copy(image, area);
        int stride = w;

        // a mode this software does not know checks nothing: NG (AUD-03)
        bool pass = false;
        string summary = $"Unknown mode {_cfg.Mode}: nothing was checked";
        string? explainKey = "explain.tool.error";

        switch (_cfg.Mode)
        {
            case ThermalInspectionMode.BottleInductionSeal:
            {
                var bp = ThermalEngine.DefaultBottleParams();
                bp.ExpectedCx = w * 0.5f;
                bp.ExpectedCy = h * 0.5f;
                bp.InnerRadiusPx = _cfg.BottleInnerRadiusPx;
                bp.OuterRadiusPx = _cfg.BottleOuterRadiusPx;
                bp.MinSealTempC = _cfg.BottleMinSealTempC;
                bp.MaxSealTempC = _cfg.BottleMaxSealTempC;
                bp.MissingFoilTempC = _cfg.BottleMissingFoilTempC;
                bp.MaxColdGapDeg = _cfg.BottleMaxColdGapDeg;
                bp.MaxTiltTempDelta = _cfg.BottleMaxTiltTempDelta;
                bp.TMinCalib = _cfg.TMinCalib;
                bp.TMaxCalib = _cfg.TMaxCalib;

                var res = ThermalEngine.InspectBottleSeal(roiPixels, w, h, stride, bp);
                var verdict = (ThermalNative.ThermalVerdict)res.Verdict;
                pass = verdict == ThermalNative.ThermalVerdict.Pass;

                values["mean_temp"] = res.MeanSealTempC;
                values["peak_temp"] = res.PeakSealTempC;
                values["cold_gap_deg"] = res.ColdGapDeg;
                values["integrity_pct"] = res.SealIntegrityPct;

                float cx = x0 + res.DetectedCx;
                float cy = y0 + res.DetectedCy;
                var ringPts = GenerateCirclePoints(cx, cy, _cfg.BottleOuterRadiusPx, 16);
                graphics.Add(new Graphic(ringPts, pass ? GraphicKind.Ok : GraphicKind.Fail, pass ? "Seal OK" : verdict.ToString()));

                if (pass)
                {
                    summary = $"Induction seal OK (Mean {res.MeanSealTempC:0.0}°C, Integrity {res.SealIntegrityPct:0.0}%)";
                    details.Add($"✓ Mean seal temperature: {res.MeanSealTempC:0.1}°C (Peak: {res.PeakSealTempC:0.1}°C)");
                    details.Add($"✓ Cold gap: {res.ColdGapDeg:0.0}° (tolerance: {_cfg.BottleMaxColdGapDeg:0.0}°)");
                    details.Add($"✓ Seal integrity: {res.SealIntegrityPct:0.0}%");
                }
                else
                {
                    summary = $"Induction seal NG: {verdict} (Mean {res.MeanSealTempC:0.0}°C, Cold Gap {res.ColdGapDeg:0.0}°)";
                    explainKey = verdict switch
                    {
                        ThermalNative.ThermalVerdict.NgMissingFoil => "explain.thermal.ng_missing_foil",
                        ThermalNative.ThermalVerdict.NgIncompleteSeal => "explain.thermal.ng_incomplete_seal",
                        ThermalNative.ThermalVerdict.NgOverheated => "explain.thermal.ng_overheated",
                        _ => "explain.thermal.ng_generic"
                    };
                    details.Add($"✗ Defect: {verdict}");
                    details.Add($"✗ Mean temperature: {res.MeanSealTempC:0.1}°C, Peak: {res.PeakSealTempC:0.1}°C");
                    details.Add($"✗ Cold gap: {res.ColdGapDeg:0.0}°");

                    var (diagCode, diagAction, diagSev) = ThermalEngine.DiagnoseTooling(
                        packagingType: 0,
                        verdict: verdict,
                        asymmetryMetric: res.TiltDeltaC > 0 ? res.TiltDeltaC : res.ColdGapDeg);
                    details.Add($"Tooling Diagnostic [{diagCode}] (Severity {diagSev}): {diagAction}");
                }
                break;
            }

            case ThermalInspectionMode.PouchHeatSeal:
            {
                var pp = ThermalEngine.DefaultPouchParams();
                pp.SeamRoiW = w;
                pp.SeamRoiH = h;
                pp.IsVertical = _cfg.PouchIsVertical ? 1 : 0;
                pp.MinSealTempC = _cfg.PouchMinSealTempC;
                pp.MinSealWidthPx = _cfg.PouchMinSealWidthPx;
                pp.MaxVoidLengthPx = _cfg.PouchMaxVoidLengthPx;
                pp.ContaminationDropC = _cfg.PouchContaminationDropC;
                pp.TMinCalib = _cfg.TMinCalib;
                pp.TMaxCalib = _cfg.TMaxCalib;

                var res = ThermalEngine.InspectPouchSeam(roiPixels, w, h, stride, pp);
                var verdict = (ThermalNative.ThermalVerdict)res.Verdict;
                pass = verdict == ThermalNative.ThermalVerdict.Pass;

                values["mean_temp"] = res.MeanSeamTempC;
                values["mean_width_px"] = res.MeanSeamWidthPx;
                values["min_width_px"] = res.MinSeamWidthPx;
                values["void_count"] = res.VoidCount;
                values["integrity_pct"] = res.SealIntegrityPct;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Pouch Seam OK" : verdict.ToString()));

                if (pass)
                {
                    summary = $"Pouch seal OK (Width {res.MeanSeamWidthPx:0.1}px, Integrity {res.SealIntegrityPct:0.0}%)";
                    details.Add($"✓ Seam temperature: {res.MeanSeamTempC:0.1}°C");
                    details.Add($"✓ Mean width: {res.MeanSeamWidthPx:0.1}px (Min: {res.MinSeamWidthPx:0.1}px)");
                    details.Add($"✓ Continuous seal: 0 leak voids");
                }
                else
                {
                    summary = $"Pouch seal NG: {verdict} ({res.VoidCount} voids, Min width {res.MinSeamWidthPx:0.1}px)";
                    explainKey = verdict switch
                    {
                        ThermalNative.ThermalVerdict.NgSeamVoid => "explain.thermal.ng_seam_void",
                        _ => "explain.thermal.ng_generic"
                    };
                    details.Add($"✗ Defect: {verdict}");
                    details.Add($"✗ Voids detected: {res.VoidCount} (Worst void: {res.WorstVoidLengthPx:0.1}px at x={res.WorstVoidX:0.0})");

                    var (diagCode, diagAction, diagSev) = ThermalEngine.DiagnoseTooling(
                        packagingType: 1,
                        verdict: verdict,
                        asymmetryMetric: res.WorstVoidLengthPx);
                    details.Add($"Tooling Diagnostic [{diagCode}] (Severity {diagSev}): {diagAction}");
                }
                break;
            }

            case ThermalInspectionMode.HotMeltGlue:
            {
                var gp = ThermalEngine.DefaultGlueParams();
                gp.RoiW = w;
                gp.RoiH = h;
                gp.IsVertical = _cfg.GlueIsVertical ? 1 : 0;
                gp.ExpectedBeadCount = _cfg.GlueExpectedBeads;
                gp.MinGlueTempC = _cfg.GlueMinTempC;
                gp.MinBeadLengthPx = _cfg.GlueMinBeadLengthPx;
                gp.MaxBeadGapPx = _cfg.GlueMaxBeadGapPx;
                gp.TMinCalib = _cfg.TMinCalib;
                gp.TMaxCalib = _cfg.TMaxCalib;

                var res = ThermalEngine.InspectHotGlue(roiPixels, w, h, stride, gp);
                var verdict = (ThermalNative.ThermalVerdict)res.Verdict;
                pass = verdict == ThermalNative.ThermalVerdict.Pass;

                values["bead_count"] = res.DetectedBeadCount;
                values["min_length_px"] = res.MinBeadLengthPx;
                values["mean_temp"] = res.MeanGlueTempC;
                values["confidence_pct"] = res.BondingConfidence;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? $"{res.DetectedBeadCount} Beads OK" : verdict.ToString()));

                if (pass)
                {
                    summary = $"Hot glue OK ({res.DetectedBeadCount} beads, Min length {res.MinBeadLengthPx:0.1}px)";
                    details.Add($"✓ Dispensed beads: {res.DetectedBeadCount} of {_cfg.GlueExpectedBeads} expected");
                    details.Add($"✓ Min bead length: {res.MinBeadLengthPx:0.1}px (Confidence: {res.BondingConfidence:0.0}%)");
                }
                else
                {
                    summary = $"Hot glue NG: {verdict} ({res.DetectedBeadCount} of {_cfg.GlueExpectedBeads} beads found)";
                    explainKey = verdict switch
                    {
                        ThermalNative.ThermalVerdict.NgMissingBead => "explain.thermal.ng_missing_bead",
                        _ => "explain.thermal.ng_generic"
                    };
                    details.Add($"✗ Defect: {verdict}");
                    details.Add($"✗ Detected beads: {res.DetectedBeadCount} of {_cfg.GlueExpectedBeads} expected");

                    var (diagCode, diagAction, diagSev) = ThermalEngine.DiagnoseTooling(
                        packagingType: 2,
                        verdict: verdict,
                        asymmetryMetric: _cfg.GlueExpectedBeads - res.DetectedBeadCount);
                    details.Add($"Tooling Diagnostic [{diagCode}] (Severity {diagSev}): {diagAction}");
                }
                break;
            }

            case ThermalInspectionMode.BlisterPocket:
            {
                var bp = ThermalEngine.DefaultBlisterParams();
                bp.CardRoiW = w;
                bp.CardRoiH = h;
                bp.Rows = _cfg.BlisterRows;
                bp.Cols = _cfg.BlisterCols;
                bp.PocketWPx = _cfg.BlisterPocketWPx;
                bp.PocketHPx = _cfg.BlisterPocketHPx;
                bp.PitchXPx = _cfg.BlisterPitchXPx;
                bp.PitchYPx = _cfg.BlisterPitchYPx;
                bp.MinBarrierWidthPx = _cfg.BlisterMinBarrierWidthPx;
                bp.MinSealTempC = _cfg.BlisterMinSealTempC;
                bp.TMinCalib = _cfg.TMinCalib;
                bp.TMaxCalib = _cfg.TMaxCalib;

                var res = ThermalEngine.InspectBlister(roiPixels, w, h, stride, bp);
                var verdict = (ThermalNative.ThermalVerdict)res.Verdict;
                pass = verdict == ThermalNative.ThermalVerdict.Pass;

                values["min_barrier_px"] = res.MinBarrierWidthPx;
                values["compromised_pocket"] = res.CompromisedCavityIdx;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Blister Flange OK" : verdict.ToString()));

                if (pass)
                {
                    summary = $"Blister seal OK (Min barrier {res.MinBarrierWidthPx:0.1}px)";
                    details.Add($"✓ All {_cfg.BlisterRows * _cfg.BlisterCols} cavity moisture barriers intact");
                    details.Add($"✓ Min flange barrier width: {res.MinBarrierWidthPx:0.1}px");
                }
                else
                {
                    summary = $"Blister seal NG: Cavity {res.CompromisedCavityIdx} barrier breach";
                    explainKey = "explain.thermal.ng_blister_barrier_breach";
                    details.Add($"✗ Defect: {verdict}");
                    details.Add($"✗ Breached pocket index: {res.CompromisedCavityIdx}");
                    details.Add($"✗ Barrier width: {res.MinBarrierWidthPx:0.1}px (tolerance: {_cfg.BlisterMinBarrierWidthPx:0.1}px)");

                    var (diagCode, diagAction, diagSev) = ThermalEngine.DiagnoseTooling(
                        packagingType: 3,
                        verdict: verdict,
                        asymmetryMetric: 0.0f);
                    details.Add($"Tooling Diagnostic [{diagCode}] (Severity {diagSev}): {diagAction}");
                }
                break;
            }
        }

        double ms = sw.Elapsed.TotalMilliseconds;
        return new ToolResult(_cfg.Name, pass, summary, details, ms, graphics)
        {
            Explain = explainKey,
            Values = values
        };
    }

    private static PointF[] GenerateCirclePoints(float cx, float cy, float radius, int steps)
    {
        var pts = new PointF[steps];
        float angleStep = (MathF.PI * 2.0f) / steps;
        for (int i = 0; i < steps; i++)
        {
            float angle = i * angleStep;
            pts[i] = new PointF(cx + radius * MathF.Cos(angle), cy + radius * MathF.Sin(angle));
        }
        return pts;
    }

    public void Dispose()
    {
        // No unmanaged resources held across calls
    }
}
