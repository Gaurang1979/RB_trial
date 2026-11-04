using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VisionCore;

public enum WaveletInspectionMode
{
    ScratchDetection = 0,
    PinholeDefect = 1,
    TextureFiltering = 2
}

/// <summary>
/// Wavelet Homomorphic Scratch &amp; Micro-Defect Inspection Tool (Tool 8.6 / Tool 10).
/// Suppresses periodic machining textures, knurling grids, and brushed metal grain, isolating fine hairline scratches and pinholes.
/// </summary>
public sealed class WaveletToolConfig : ToolConfig
{
    public WaveletInspectionMode Mode { get; set; } = WaveletInspectionMode.ScratchDetection;
    public TextureOrientation TextureMode { get; set; } = TextureOrientation.Auto;

    public int Levels { get; set; } = 2;
    public float SuppressionStrength { get; set; } = 0.85f;
    public float ScratchGain { get; set; } = 3.0f;
    public bool HomomorphicEnable { get; set; } = true;
    public float DefectContrastThresh { get; set; } = 35.0f;
    public int MinDefectAreaPx { get; set; } = 5;
    public int MaxAllowedDefectPx { get; set; } = 0;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class WaveletTool : ITool
{
    private readonly WaveletToolConfig _cfg;

    public WaveletTool(WaveletToolConfig cfg)
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

        if (image == null || image.Pixels == null || image.Width < 8 || image.Height < 8)
        {
            return new ToolResult(_cfg.Name, false, "Wavelet inspection failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.wavelet.scratch_detected"
            };
        }

        // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
        if (ToolArea.Resolve(_cfg.Roi, fixture, image.Width, image.Height, 8) is not { } area)
            return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds, 8);
        int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

        byte[] roiPixels = ToolArea.Copy(image, area);

        using var engine = new WaveletEngine(
            levels: _cfg.Levels,
            textureMode: _cfg.TextureMode,
            suppressionStrength: _cfg.SuppressionStrength,
            scratchGain: _cfg.ScratchGain,
            homomorphicEnable: _cfg.HomomorphicEnable,
            defectContrastThresh: _cfg.DefectContrastThresh,
            minDefectAreaPx: _cfg.MinDefectAreaPx);

        var res = engine.Process(roiPixels, w, h, produceResidual: true, produceMask: true);

        values["defect_pixels"] = res.DefectPixelCount;
        values["max_contrast"] = res.MaxScratchContrast;
        values["energy_suppressed"] = res.TextureEnergySuppressed;
        values["detected_orientation"] = (int)res.DetectedOrientation;

        bool pass = res.DefectPixelCount <= _cfg.MaxAllowedDefectPx;
        string summary = "";
        string? explainKey = null;

        switch (_cfg.Mode)
        {
            default:
                // a mode this software does not know checks nothing: NG (AUD-03)
                pass = false;
                summary = $"Unknown mode {_cfg.Mode}: nothing was checked";
                explainKey = "explain.tool.error";
                break;

            case WaveletInspectionMode.ScratchDetection:
            {
                if (pass)
                {
                    summary = $"Surface scratch inspection OK (0 scratch defects, {res.TextureEnergySuppressed:0.0}% texture suppressed)";
                    details.Add($"✓ Machining texture suppressed: {res.TextureEnergySuppressed:0.1}% periodic energy eliminated");
                    details.Add($"✓ No scratches exceeding contrast {_cfg.DefectContrastThresh:0.0} found");
                }
                else
                {
                    summary = $"Hairline scratch detected: {res.DefectPixelCount} defect px (Max contrast: {res.MaxScratchContrast:0.1})";
                    explainKey = "explain.wavelet.scratch_detected";
                    details.Add($"✗ Surface scratch detected: {res.DefectPixelCount} pixels exceed threshold");
                    details.Add($"✗ Max scratch contrast: {res.MaxScratchContrast:0.1} (Limit: {_cfg.DefectContrastThresh:0.1})");
                    details.Add($"✗ Background texture orientation: {res.DetectedOrientation}");
                }
                break;
            }

            case WaveletInspectionMode.PinholeDefect:
            {
                if (pass)
                {
                    summary = $"Foil barrier inspection OK (0 pinhole micro-voids)";
                    details.Add($"✓ Knurling pattern suppressed, seal barrier continuous");
                }
                else
                {
                    summary = $"Pinhole breach detected: {res.DefectPixelCount} defect px in packaging foil";
                    explainKey = "explain.wavelet.pinhole_detected";
                    details.Add($"✗ Pinhole or perforation detected in packaging seal");
                    details.Add($"✗ Micro-defect pixel count: {res.DefectPixelCount} (Threshold: {_cfg.MinDefectAreaPx} px)");
                }
                break;
            }

            case WaveletInspectionMode.TextureFiltering:
            {
                pass = res.TextureEnergySuppressed >= 40.0f;
                if (pass)
                {
                    summary = $"Texture suppression filtering OK ({res.TextureEnergySuppressed:0.1}% periodic noise removed)";
                    details.Add($"✓ Homomorphic wavelet filter attenuated background grid");
                }
                else
                {
                    summary = $"Insufficient texture suppression ({res.TextureEnergySuppressed:0.1}% < 40%)";
                    explainKey = "explain.wavelet.excessive_noise";
                    details.Add($"✗ Surface noise not sufficiently periodic for automated suppression");
                }
                break;
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
            pass ? "Surface Nominal" : (_cfg.Mode == WaveletInspectionMode.PinholeDefect ? "Pinhole Flaw" : "Scratch Flaw")));

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
