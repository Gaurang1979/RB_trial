using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace VisionCore;

/// <summary>Check defects (tool spec 8.2): golden template and variation model surface inspection.</summary>
public sealed class InspectToolConfig : ToolConfig
{
    public float Sensitivity { get; set; } = 1.0f;
    public float BaseOffset { get; set; } = 12.0f;
    public float EdgeTolerancePx { get; set; } = 1.5f;
    public float EdgeMarginWeight { get; set; } = 0.15f;
    public int MinDefectAreaPx { get; set; } = 15;
    public bool NormalizeLighting { get; set; } = true;
    public string? ModelPath { get; set; }

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();
}

public sealed class InspectTool : ITool
{
    private readonly InspectToolConfig _cfg;
    private VariationModel? _model;
    private readonly string? _loadError;
    private readonly object _lock = new();

    public InspectTool(InspectToolConfig cfg)
    {
        _cfg = cfg;
        if (!string.IsNullOrWhiteSpace(cfg.ModelPath))
        {
            // a model that does not load leaves the tool NG with the reason, never a silent pass (AUD-08)
            if (!File.Exists(cfg.ModelPath)) _loadError = $"the model file {cfg.ModelPath} is missing";
            else
                try { _model = VariationModel.Load(cfg.ModelPath); }
                catch (Exception e) when (e is InvalidOperationException or IOException or DllNotFoundException or EntryPointNotFoundException) { _loadError = e.Message; }
        }
    }

    public ToolConfig Config => _cfg;

    public void SetModel(VariationModel model)
    {
        lock (_lock)
        {
            _model?.Dispose();
            _model = model;
        }
    }

    public ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        var sw = Stopwatch.StartNew();
        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>();
        var details = new List<string>();

        lock (_lock)
        {
            if (_model is null)
            {
                return new ToolResult(
                    _cfg.Name,
                    false,
                    _loadError is null ? "Variation model not taught" : "Variation model not loaded: " + _loadError,
                    new[] { _loadError is null ? "No golden template model has been trained for this tool." : "✗ " + _loadError },
                    sw.Elapsed.TotalMilliseconds,
                    graphics)
                {
                    Explain = "explain.inspect.no_model"
                };
            }

            // the area follows the part (AUD-04); the tool works on a copy, never on the camera image (AUD-02)
            if (ToolArea.ResolveTaughtSize(_cfg.Roi, fixture, image.Width, image.Height) is not { } area)
                return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds);
            int x0 = area.X, y0 = area.Y, w = area.Width, h = area.Height;

            if (w != _model.Width || h != _model.Height)
            {
                return new ToolResult(
                    _cfg.Name,
                    false,
                    $"Dimension mismatch: ROI is {w}x{h}, model is {_model.Width}x{_model.Height}",
                    new[] { "Image size or ROI does not match trained model geometry." },
                    sw.Elapsed.TotalMilliseconds,
                    graphics)
                {
                    Explain = "explain.inspect.dimension_mismatch"
                };
            }

            byte[] roiPixels = ToolArea.Copy(image, area);

            var runResult = _model.Run(roiPixels, w, h, w);
            double ms = sw.Elapsed.TotalMilliseconds;

            values["defects"] = runResult.DefectCount;
            values["total_area"] = runResult.TotalDefectArea;
            values["max_contrast"] = runResult.MaxContrast;
            values["confidence"] = runResult.Confidence;

            bool pass = runResult.Verdict == (int)InspectNative.InspectVerdict.Pass;

            if (pass)
            {
                details.Add($"✓ No defects ({runResult.DefectCount} found, confidence {runResult.Confidence:0}%)");
                if (_cfg.Roi is not null)
                    graphics.Add(new Graphic(ToolArea.Corners(area), GraphicKind.Ok));

                return new ToolResult(_cfg.Name, true, "OK", details, ms, graphics)
                {
                    Values = values
                };
            }
            else
            {
                var worst = runResult.WorstDefect;
                int absX = x0 + worst.BBoxX;
                int absY = y0 + worst.BBoxY;

                graphics.Add(new Graphic(
                    new[]
                    {
                        new PointF(absX, absY),
                        new PointF(absX + worst.BBoxW, absY),
                        new PointF(absX + worst.BBoxW, absY + worst.BBoxH),
                        new PointF(absX, absY + worst.BBoxH)
                    },
                    GraphicKind.Fail,
                    $"Defect {worst.AreaPx}px"));

                string summary = $"Defect of {worst.AreaPx} px detected at ({absX},{absY})";
                details.Add($"✗ {runResult.DefectCount} defects found, largest {worst.AreaPx} px ({worst.BBoxW}x{worst.BBoxH})");

                return new ToolResult(_cfg.Name, false, summary, details, ms, graphics)
                {
                    Explain = "explain.inspect.defect",
                    Values = values
                };
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
