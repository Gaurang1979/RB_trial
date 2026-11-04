using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

public enum PhotometricMode
{
    CurvatureRelief = 0,
    EmbossPresence = 1,
    AlbedoInspection = 2,
    VirtualLighting = 3
}

public enum PhotometricLayout
{
    Quadrant2x2 = 0,
    VerticalStripes4x1 = 1,
    HorizontalStripes1x4 = 2
}

/// <summary>
/// Computational Photometric Stereo inspection tool (Tool 0 / LumiTrax / SurfaceFX alternative).
/// Decouples 2D printed ink (albedo) from 3D surface relief (shape curvature) using directional lighting.
/// </summary>
public sealed class PhotometricToolConfig : ToolConfig
{
    public PhotometricMode Mode { get; set; } = PhotometricMode.CurvatureRelief;
    public PhotometricLayout Layout { get; set; } = PhotometricLayout.Quadrant2x2;

    public float ShapeContrastGain { get; set; } = 140.0f;
    public float AlbedoGamma { get; set; } = 1.0f;

    public int ReliefThreshold { get; set; } = 25;
    public int MaxAllowedDefectPx { get; set; } = 50;
    public int MinFeaturePx { get; set; } = 100;
    public int MaxFeaturePx { get; set; } = 50000;

    public float VirtualTiltDeg { get; set; } = 45.0f;
    public float VirtualSlantDeg { get; set; } = 45.0f;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class PhotometricTool : ITool
{
    private readonly PhotometricToolConfig _cfg;

    public PhotometricTool(PhotometricToolConfig cfg)
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
            return new ToolResult(_cfg.Name, false, "Photometric inspection failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.photometric.surface_defect"
            };
        }

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 4) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 4);
        int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

        // Extract sub-images based on layout
        int subW, subH;
        byte[][] inputs;

        try
        {
            inputs = ExtractInputs(new GrayImage(ToolArea.Copy(image, area), w, h), 0, 0, w, h, _cfg.Layout, out subW, out subH);
        }
        catch (Exception ex)
        {
            details.Add($"✗ Layout extraction error: {ex.Message}");
            return new ToolResult(_cfg.Name, false, $"Photometric extraction failed: {ex.Message}", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.photometric.surface_defect"
            };
        }

        using var engine = new PhotometricEngine(_cfg.ShapeContrastGain, _cfg.AlbedoGamma);

        // a mode this software does not know checks nothing: NG (AUD-03)
        bool pass = false;
        string summary = $"Unknown mode {_cfg.Mode}: nothing was checked";
        string? explainKey = "explain.tool.error";

        if (_cfg.Mode == PhotometricMode.VirtualLighting)
        {
            var (albedo, shape, normals) = engine.ComputeWithNormals(inputs, subW, subH);
            byte[] shaded = PhotometricEngine.SynthesizeLighting(albedo, normals, subW, subH, _cfg.VirtualTiltDeg, _cfg.VirtualSlantDeg);

            long sumShaded = 0;
            for (int i = 0; i < shaded.Length; i++) sumShaded += shaded[i];
            double meanShaded = shaded.Length > 0 ? (double)sumShaded / shaded.Length : 0.0;

            values["virtual_tilt"] = _cfg.VirtualTiltDeg;
            values["virtual_slant"] = _cfg.VirtualSlantDeg;
            values["mean_shaded"] = meanShaded;

            pass = true;
            explainKey = null;
            summary = $"Virtual lighting synthesis OK (Tilt {_cfg.VirtualTiltDeg:0}°, Slant {_cfg.VirtualSlantDeg:0}°)";
            details.Add($"✓ Directional lighting synthesized successfully from normal vectors");
            details.Add($"✓ Mean synthesized surface luminance: {meanShaded:0.1}");
        }
        else
        {
            var (albedo, shape) = engine.Compute(inputs, subW, subH);

            switch (_cfg.Mode)
            {
                case PhotometricMode.CurvatureRelief:
                {
                    explainKey = null;
                    int defectPixels = 0;
                    int maxDelta = 0;

                    for (int i = 0; i < shape.Length; i++)
                    {
                        int delta = Math.Abs(shape[i] - 128);
                        if (delta > maxDelta) maxDelta = delta;
                        if (delta >= _cfg.ReliefThreshold) defectPixels++;
                    }

                    double defectPct = shape.Length > 0 ? (defectPixels * 100.0) / shape.Length : 0.0;
                    values["defect_pixels"] = defectPixels;
                    values["defect_pct"] = defectPct;
                    values["max_relief_delta"] = maxDelta;

                    pass = defectPixels <= _cfg.MaxAllowedDefectPx;

                    if (pass)
                    {
                        summary = $"Surface curvature relief OK ({defectPixels} anomalous pixels)";
                        details.Add($"✓ Surface 3D curvature uniform: {defectPixels} defect pixels <= {_cfg.MaxAllowedDefectPx} limit");
                        details.Add($"✓ Max surface relief delta: {maxDelta} (Threshold: {_cfg.ReliefThreshold})");
                    }
                    else
                    {
                        summary = $"3D surface defect detected: {defectPixels} pixels > {_cfg.MaxAllowedDefectPx} limit";
                        explainKey = "explain.photometric.surface_defect";
                        details.Add($"✗ Anomalous surface relief detected ({defectPixels} pixels, {defectPct:0.1}% of ROI)");
                        details.Add($"✗ Max relief deviation {maxDelta} exceeds threshold {_cfg.ReliefThreshold}");
                    }
                    break;
                }

                case PhotometricMode.EmbossPresence:
                {
                    explainKey = null;
                    int featurePixels = 0;
                    long reliefSum = 0;

                    for (int i = 0; i < shape.Length; i++)
                    {
                        int delta = Math.Abs(shape[i] - 128);
                        if (delta >= _cfg.ReliefThreshold)
                        {
                            featurePixels++;
                            reliefSum += delta;
                        }
                    }

                    double meanRelief = featurePixels > 0 ? (double)reliefSum / featurePixels : 0.0;
                    values["feature_pixels"] = featurePixels;
                    values["mean_relief"] = meanRelief;

                    if (featurePixels < _cfg.MinFeaturePx)
                    {
                        pass = false;
                        summary = $"Embossed mark missing or insufficient: {featurePixels} px < {_cfg.MinFeaturePx} min";
                        explainKey = "explain.photometric.emboss_missing";
                        details.Add($"✗ Raised stamped mark not found ({featurePixels} px detected, expected >= {_cfg.MinFeaturePx})");
                    }
                    else if (featurePixels > _cfg.MaxFeaturePx)
                    {
                        pass = false;
                        summary = $"Embossed mark deformed or excessive: {featurePixels} px > {_cfg.MaxFeaturePx} max";
                        explainKey = "explain.photometric.emboss_deformed";
                        details.Add($"✗ Embossed stamp area exceeds limit ({featurePixels} px > {_cfg.MaxFeaturePx})");
                    }
                    else
                    {
                        pass = true;
                        summary = $"Embossed mark verified OK ({featurePixels} feature pixels)";
                        details.Add($"✓ Embossed stamp present: {featurePixels} px (Target: [{_cfg.MinFeaturePx}..{_cfg.MaxFeaturePx}])");
                        details.Add($"✓ Mean relief height contrast: {meanRelief:0.1}");
                    }
                    break;
                }

                case PhotometricMode.AlbedoInspection:
                {
                    explainKey = null;
                    long sum = 0;
                    byte minV = 255, maxV = 0;
                    for (int i = 0; i < albedo.Length; i++)
                    {
                        byte b = albedo[i];
                        sum += b;
                        if (b < minV) minV = b;
                        if (b > maxV) maxV = b;
                    }

                    double meanAlbedo = albedo.Length > 0 ? (double)sum / albedo.Length : 0.0;
                    int contrast = maxV - minV;
                    values["mean_albedo"] = meanAlbedo;
                    values["contrast"] = contrast;

                    pass = contrast >= 30; // Reliable print contrast
                    if (pass)
                    {
                        summary = $"Albedo print decoupling OK (Contrast {contrast}, Mean {meanAlbedo:0.1})";
                        details.Add($"✓ 3D shading decoupled: Pure 2D print contrast = {contrast}");
                    }
                    else
                    {
                        summary = $"Insufficient albedo print contrast: {contrast} < 30";
                        explainKey = "explain.photometric.low_contrast";
                        details.Add($"✗ Printed mark albedo contrast too low after 3D shading removal");
                    }
                    break;
                }
            }
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
            pass ? "Photometric OK" : "Photometric NG"));

        double ms = sw.Elapsed.TotalMilliseconds;
        return new ToolResult(_cfg.Name, pass, summary, details, ms, graphics)
        {
            Explain = explainKey,
            Values = values
        };
    }

    private static byte[][] ExtractInputs(GrayImage image, int x0, int y0, int w, int h, PhotometricLayout layout, out int subW, out int subH)
    {
        byte[][] inputs = new byte[4][];

        switch (layout)
        {
            case PhotometricLayout.Quadrant2x2:
            {
                subW = w / 2;
                subH = h / 2;
                if (subW < 2 || subH < 2) throw new InvalidOperationException($"ROI size ({w}x{h}) too small for Quadrant 2x2 layout.");

                for (int k = 0; k < 4; k++) inputs[k] = new byte[subW * subH];

                int x1 = x0 + subW;
                int y1 = y0 + subH;

                for (int y = 0; y < subH; y++)
                {
                    int rowSrc0 = (y0 + y) * image.Stride;
                    int rowSrc1 = (y1 + y) * image.Stride;
                    int rowDst = y * subW;

                    // Light 0: Top (Top-Left quadrant)
                    Buffer.BlockCopy(image.Pixels, rowSrc0 + x0, inputs[0], rowDst, subW);
                    // Light 1: Right (Top-Right quadrant)
                    Buffer.BlockCopy(image.Pixels, rowSrc0 + x1, inputs[1], rowDst, subW);
                    // Light 2: Bottom (Bottom-Left quadrant)
                    Buffer.BlockCopy(image.Pixels, rowSrc1 + x0, inputs[2], rowDst, subW);
                    // Light 3: Left (Bottom-Right quadrant)
                    Buffer.BlockCopy(image.Pixels, rowSrc1 + x1, inputs[3], rowDst, subW);
                }
                break;
            }

            case PhotometricLayout.VerticalStripes4x1:
            {
                subW = w;
                subH = h / 4;
                if (subH < 2) throw new InvalidOperationException($"ROI height ({h}) too small for Vertical Stripes 4x1.");

                for (int k = 0; k < 4; k++) inputs[k] = new byte[subW * subH];

                for (int k = 0; k < 4; k++)
                {
                    int startY = y0 + k * subH;
                    for (int y = 0; y < subH; y++)
                    {
                        int rowSrc = (startY + y) * image.Stride + x0;
                        Buffer.BlockCopy(image.Pixels, rowSrc, inputs[k], y * subW, subW);
                    }
                }
                break;
            }

            case PhotometricLayout.HorizontalStripes1x4:
            {
                subW = w / 4;
                subH = h;
                if (subW < 2) throw new InvalidOperationException($"ROI width ({w}) too small for Horizontal Stripes 1x4.");

                for (int k = 0; k < 4; k++) inputs[k] = new byte[subW * subH];

                for (int k = 0; k < 4; k++)
                {
                    int startX = x0 + k * subW;
                    for (int y = 0; y < subH; y++)
                    {
                        int rowSrc = (y0 + y) * image.Stride + startX;
                        Buffer.BlockCopy(image.Pixels, rowSrc, inputs[k], y * subW, subW);
                    }
                }
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }

        return inputs;
    }

    public void Dispose()
    {
    }
}
