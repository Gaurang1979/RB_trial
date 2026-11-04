using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

public enum InfraredInspectionMode
{
    FillLevel = 0,
    MetamericInk = 1,
    MoistureBarrier = 2,
    CrackDetection = 3
}

/// <summary>Near-Infrared (NIR) and Short-Wave Infrared (SWIR) sub-surface inspection (Tool 8.5 / Tool 9).</summary>
public sealed class InfraredToolConfig : ToolConfig
{
    public InfraredInspectionMode Mode { get; set; } = InfraredInspectionMode.FillLevel;

    // Fill level inspection parameters
    public float DatumY { get; set; } = 0.0f;
    public float PxPerMm { get; set; } = 5.0f;
    public float TargetFillMm { get; set; } = 30.0f;
    public float TolUnderfillMm { get; set; } = 2.0f;
    public float TolOverfillMm { get; set; } = 2.0f;
    public float MinMeniscusContrast { get; set; } = 15.0f;
    public bool IgnoreFoam { get; set; } = true;

    // Metameric anti-counterfeit ink parameters
    public float DyeNdmiThresh { get; set; } = 0.25f;
    public float CarbonNdmiMax { get; set; } = 0.10f;
    public float MinSecurityAreaPx { get; set; } = 100.0f;

    // SWIR moisture seal barrier parameters
    public float AbsorptionThresh { get; set; } = 40.0f;
    public float MinNominalBarrierPx { get; set; } = 15.0f;
    public float MinSafeBarrierPx { get; set; } = 8.0f;

    // Solar EL / Wafer micro-crack extraction parameters
    public float HessianSigma { get; set; } = 1.5f;
    public float CrackResponseThresh { get; set; } = 8.0f;
    public int MinCrackLengthPx { get; set; } = 15;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class InfraredTool : ITool
{
    private readonly InfraredToolConfig _cfg;

    public InfraredTool(InfraredToolConfig cfg)
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

        // a mode this software does not know checks nothing: NG (AUD-03)
        bool pass = false;
        string summary = $"Unknown mode {_cfg.Mode}: nothing was checked";
        string? explainKey = "explain.tool.error";

        switch (_cfg.Mode)
        {
            case InfraredInspectionMode.FillLevel:
            {
                float datumY = _cfg.DatumY > 0 ? _cfg.DatumY : (float)h;
                var res = InfraredEngine.MeasureFillLevel(
                    roiPixels,
                    w,
                    h,
                    0,
                    0,
                    w,
                    h,
                    datumY,
                    _cfg.PxPerMm,
                    _cfg.TargetFillMm,
                    _cfg.TolUnderfillMm,
                    _cfg.TolOverfillMm,
                    _cfg.IgnoreFoam);

                pass = res.IsOk;
                values["fill_height_mm"] = res.FillHeightMm;
                values["meniscus_y_px"] = res.MeniscusYPx;
                values["meniscus_contrast"] = res.MeniscusContrast;
                values["foam_thickness_mm"] = res.FoamThicknessMm;

                float absMeniscusY = y0 + res.MeniscusYPx;
                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, absMeniscusY),
                        new PointF(x0 + w, absMeniscusY)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    $"Fill {res.FillHeightMm:0.1}mm"));

                if (pass)
                {
                    summary = $"NIR fill level OK ({res.FillHeightMm:0.1}mm, target {_cfg.TargetFillMm:0.1}mm)";
                    details.Add($"✓ Meniscus detected at {res.FillHeightMm:0.2} mm (contrast {res.MeniscusContrast:0.1})");
                    if (res.FoamThicknessMm > 0)
                        details.Add($"✓ Foam layer thickness: {res.FoamThicknessMm:0.2} mm");
                }
                else
                {
                    string failType = res.IsUnderfill ? "Underfill" : res.IsOverfill ? "Overfill" : "Meniscus not detected";
                    summary = $"NIR fill level NG: {failType} ({res.FillHeightMm:0.1}mm, target {_cfg.TargetFillMm:0.1}mm)";
                    explainKey = res.IsUnderfill ? "explain.infrared.underfill" : res.IsOverfill ? "explain.infrared.overfill" : "explain.infrared.no_meniscus";
                    details.Add($"✗ Fill level out of tolerance: {res.FillHeightMm:0.2} mm (Target: {_cfg.TargetFillMm:0.1}mm ± {_cfg.TolUnderfillMm:0.1}mm)");
                }
                break;
            }

            case InfraredInspectionMode.MetamericInk:
            {
                var res = InfraredEngine.VerifyMetameric(
                    roiPixels,
                    roiPixels,
                    w,
                    h,
                    _cfg.DyeNdmiThresh,
                    _cfg.CarbonNdmiMax,
                    (int)_cfg.MinSecurityAreaPx,
                    generateOutputs: false);

                pass = res.IsAuthentic;
                values["verified_pixels"] = res.VerifiedInkPixels;
                values["transparent_pixels"] = res.TransparentDyePixels;
                values["mean_ndmi"] = res.MeanSecurityNdmi;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Authentic Ink" : "Counterfeit Ink"));

                if (pass)
                {
                    summary = $"Metameric security ink authentic ({res.VerifiedInkPixels} verified carbon pixels)";
                    details.Add($"✓ Security ink verified: {res.VerifiedInkPixels} px (mean NDMI {res.MeanSecurityNdmi:0.2})");
                    details.Add($"✓ Decorative dye transparency confirmed: {res.TransparentDyePixels} px");
                }
                else
                {
                    summary = $"Security ink NG: Counterfeit suspected ({res.VerifiedInkPixels} verified px < {_cfg.MinSecurityAreaPx:0.0})";
                    explainKey = "explain.infrared.counterfeit_ink";
                    details.Add($"✗ Failed metameric absorption criteria (verified: {res.VerifiedInkPixels} px, required: {_cfg.MinSecurityAreaPx:0.0} px)");
                }
                break;
            }

            case InfraredInspectionMode.MoistureBarrier:
            {
                var res = InfraredEngine.DetectMoistureSeam(
                    roiPixels,
                    w,
                    h,
                    0,
                    0,
                    w,
                    h,
                    _cfg.AbsorptionThresh,
                    _cfg.MinNominalBarrierPx,
                    _cfg.MinSafeBarrierPx,
                    generateMask: false);

                pass = res.IsSealOk;
                values["min_barrier_px"] = res.MinRemainingBarrierPx;
                values["moisture_pixels"] = res.MoisturePixelCount;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Moisture Barrier OK" : "Barrier Breach"));

                if (pass)
                {
                    summary = $"SWIR moisture barrier seal OK (Remaining barrier: {res.MinRemainingBarrierPx:0.1}px)";
                    details.Add($"✓ Seal intact with {res.MinRemainingBarrierPx:0.1} px minimum barrier");
                    details.Add($"✓ Complete breach: No");
                }
                else
                {
                    summary = res.HasCompleteBreach ? "SWIR moisture barrier NG: COMPLETE BREACH" : "SWIR moisture barrier NG: Thin barrier";
                    explainKey = res.HasCompleteBreach ? "explain.infrared.barrier_breach" : "explain.infrared.barrier_thin";
                    details.Add($"✗ Moisture breach detected: {res.MoisturePixelCount} absorbed pixels");
                    details.Add($"✗ Minimum remaining barrier: {res.MinRemainingBarrierPx:0.1} px (Safe limit: {_cfg.MinSafeBarrierPx:0.1}px)");
                }
                break;
            }

            case InfraredInspectionMode.CrackDetection:
            {
                var res = InfraredEngine.ExtractCracks(
                    roiPixels,
                    w,
                    h,
                    _cfg.CrackResponseThresh,
                    _cfg.MinCrackLengthPx,
                    generateMask: false);

                pass = !res.IsCracked;
                values["crack_pixels"] = res.CrackPixelCount;
                values["max_contrast"] = res.MaxCrackContrast;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "No Cracks" : "Crack Detected"));

                if (pass)
                {
                    summary = "Infrared crack extraction OK (0 micro-cracks detected)";
                    details.Add($"✓ Solar EL / wafer surface continuous: 0 crack pixels");
                }
                else
                {
                    summary = $"Infrared defect NG: Micro-crack detected ({res.CrackPixelCount} crack pixels)";
                    explainKey = "explain.infrared.crack_detected";
                    details.Add($"✗ Micro-crack fracture detected: {res.CrackPixelCount} px (max contrast {res.MaxCrackContrast:0.1})");
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

    public void Dispose()
    {
    }
}
