using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

public enum PolarInspectionMode
{
    GlareRemoval = 0,
    StressBirefringence = 1,
    SpecularSaliency = 2
}

/// <summary>Polarimetric reflection decoupling and stress birefringence inspection (Tool 0 / Base Enhancement).</summary>
public sealed class PolarToolConfig : ToolConfig
{
    public PolarInspectionMode Mode { get; set; } = PolarInspectionMode.GlareRemoval;

    public float GlareSuppressAlpha { get; set; } = 0.95f;
    public float SpecularGain { get; set; } = 1.5f;
    public float DolpDefectThresh { get; set; } = 0.40f;
    public float StressGradientThresh { get; set; } = 50.0f;
    public float MaxAllowedGlarePct { get; set; } = 5.0f;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class PolarTool : ITool
{
    private readonly PolarToolConfig _cfg;

    public PolarTool(PolarToolConfig cfg)
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
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 2) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 2);
        int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

        byte[] roiPixels = ToolArea.Copy(image, area);

        // Decompose raw 2x2 Sony Polarsens mosaic
        var polResult = PolarEngine.ProcessMosaic(
            roiPixels,
            w,
            h,
            glareSuppressAlpha: _cfg.GlareSuppressAlpha,
            specularGain: _cfg.SpecularGain,
            dolpDefectThresh: _cfg.DolpDefectThresh,
            stressGradientThresh: _cfg.StressGradientThresh,
            outputDiffuse: false,
            outputSpecular: false,
            outputDolp: false,
            outputAolp: false);

        values["glare_pixels"] = polResult.GlarePixelCount;
        values["mean_dolp"] = polResult.MeanDolp;
        values["max_dolp"] = polResult.MaxDolp;
        values["max_stress_gradient"] = polResult.MaxStressGradient;

        int totalPixels = (w / 2) * (h / 2);
        double glarePct = totalPixels > 0 ? (polResult.GlarePixelCount * 100.0) / totalPixels : 0.0;
        values["glare_pct"] = glarePct;

        // a mode this software does not know checks nothing: NG (AUD-03)
        bool pass = false;
        string summary = $"Unknown mode {_cfg.Mode}: nothing was checked";
        string? explainKey = "explain.tool.error";

        switch (_cfg.Mode)
        {
            case PolarInspectionMode.GlareRemoval:
            {
                explainKey = null;
                pass = glarePct <= _cfg.MaxAllowedGlarePct;
                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Glare Suppressed" : "Excessive Glare"));

                if (pass)
                {
                    summary = $"Polarimetric glare suppression OK ({glarePct:0.1}% glare area)";
                    details.Add($"✓ Diffuse image recovered: {polResult.GlarePixelCount} glare pixels suppressed");
                    details.Add($"✓ Mean linear polarization (DoLP): {polResult.MeanDolp:0.2}");
                }
                else
                {
                    summary = $"Excessive specular reflection: {glarePct:0.1}% area > {_cfg.MaxAllowedGlarePct:0.1}% limit";
                    explainKey = "explain.polar.glare_excessive";
                    details.Add($"✗ Blinding specular glare detected ({polResult.GlarePixelCount} pixels, {glarePct:0.1}% of ROI)");
                    details.Add($"✗ Max DoLP: {polResult.MaxDolp:0.2}");
                }
                break;
            }

            case PolarInspectionMode.StressBirefringence:
            {
                explainKey = null;
                pass = !polResult.IsStressDefective;
                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Stress Nominal" : "Stress Flaw"));

                if (pass)
                {
                    summary = $"Photoelastic stress inspection OK (Max gradient {polResult.MaxStressGradient:0.1}°)";
                    details.Add($"✓ Molded material stress field uniform (Max gradient: {polResult.MaxStressGradient:0.1}°)");
                }
                else
                {
                    summary = $"Internal stress flaw detected: Gradient {polResult.MaxStressGradient:0.1}° exceeds {_cfg.StressGradientThresh:0.1}°";
                    explainKey = "explain.polar.stress_flaw";
                    details.Add($"✗ Severe internal photoelastic stress birefringence ({polResult.MaxStressGradient:0.1}° gradient)");
                }
                break;
            }

            case PolarInspectionMode.SpecularSaliency:
            {
                explainKey = null;
                pass = polResult.MeanDolp <= _cfg.DolpDefectThresh;
                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(x0, y0),
                        new PointF(x0 + w, y0),
                        new PointF(x0 + w, y0 + h),
                        new PointF(x0, y0 + h)
                    },
                    pass ? GraphicKind.Ok : GraphicKind.Fail,
                    pass ? "Surface Non-Specular" : "Specular Inclusion"));

                if (pass)
                {
                    summary = $"Specular saliency inspection OK (Mean DoLP {polResult.MeanDolp:0.2})";
                    details.Add($"✓ Surface polarization within threshold");
                }
                else
                {
                    summary = $"Specular inclusion / oily contamination detected (Mean DoLP {polResult.MeanDolp:0.2} > {_cfg.DolpDefectThresh:0.2})";
                    explainKey = "explain.polar.specular_inclusion";
                    details.Add($"✗ Polarized specular reflectance exceeds threshold");
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
