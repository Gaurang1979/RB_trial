using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

/// <summary>
/// Specular highlight glare inpainting tool (Tool 0 / Base Enhancement / AG-07).
/// Restores barcode tracks, OCR text, and feature edges obliterated by plastic wrap or glossy specular glare.
/// </summary>
public sealed class InpaintToolConfig : ToolConfig
{
    public byte GlareThreshold { get; set; } = 250;
    public int HaloRadius { get; set; } = 2;
    public int InpaintRadius { get; set; } = 3;
    public InpaintNative.InpaintMethod Method { get; set; } = InpaintNative.InpaintMethod.Telea;
    public float MaxAllowedGlarePct { get; set; } = 35.0f;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class InpaintTool : ITool
{
    private readonly InpaintToolConfig _cfg;

    public InpaintTool(InpaintToolConfig cfg)
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

        if (image == null || image.Pixels == null || image.Width < 4 || image.Height < 4)
        {
            return new ToolResult(_cfg.Name, false, "Glare inpaint failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.inpaint.excessive_glare"
            };
        }

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 4) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 4);
        int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

        InpaintNative.DefaultParams(out var p);
        p.GlareThreshold = _cfg.GlareThreshold;
        p.HaloRadius = _cfg.HaloRadius;
        p.InpaintRadius = _cfg.InpaintRadius;
        p.Method = (int)_cfg.Method;
        p.MaxGlarePct = _cfg.MaxAllowedGlarePct;

        byte[] restoredRoi = new byte[w * h];
        int rc = InpaintEngine.InpaintRoi(
            ToolArea.Copy(image, area), w, h, w,
            0, 0, w, h,
            restoredRoi, w,
            out int glareCount,
            p);

        int totalRoiPixels = w * h;
        double glarePct = totalRoiPixels > 0 ? (glareCount * 100.0) / totalRoiPixels : 0.0;
        values["glare_pixels"] = glareCount;
        values["glare_pct"] = glarePct;

        bool pass = (rc == 0) && (glarePct <= _cfg.MaxAllowedGlarePct);
        string summary = "";
        string? explainKey = null;

        if (pass)
        {
            summary = $"Specular glare inpainting OK ({glareCount} glare px, {glarePct:0.1}% area)";
            details.Add($"✓ Inpainted {glareCount} specular saturated pixels using {_cfg.Method}");
            details.Add($"✓ Glare extent {glarePct:0.1}% within {_cfg.MaxAllowedGlarePct:0.1}% threshold");
        }
        else
        {
            summary = $"Excessive specular glare: {glarePct:0.1}% area > {_cfg.MaxAllowedGlarePct:0.1}% limit";
            explainKey = "explain.inpaint.excessive_glare";
            details.Add($"✗ Blinding specular glare detected ({glareCount} pixels, {glarePct:0.1}% of ROI)");
            details.Add($"✗ Inpainting aborted to prevent hallucinating ungrounded data");
        }

        graphics.Add(new Graphic(
            new[]
            {
                new PointF(x0, y0),
                new PointF(x0 + w, y0),
                new PointF(x0 + w, y0 + h),
                new PointF(x0, y0 + h)
            },
            pass ? GraphicKind.Ok : GraphicKind.Fail,
            pass ? "Glare Inpainted" : "Excessive Glare"));

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
