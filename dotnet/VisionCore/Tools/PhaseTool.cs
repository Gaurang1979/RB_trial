using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;

namespace VisionCore;

/// <summary>
/// Log-Polar Fourier sub-pixel phase correlator tool (Tool 1 / High-Precision Alignment / AG-04).
/// Sub-0.01 px translation and 0.02° rotation registration invariant to extreme contrast fade,
/// low SNR, specular reflections, and non-linear lighting fluctuations.
/// </summary>
public sealed class PhaseToolConfig : ToolConfig, ITaughtReference
{
    public bool EstimateRotation { get; set; } = true;
    public bool EstimateScale { get; set; } = false;
    public float MinCorrelationScore { get; set; } = 0.15f;
    public float MinPsr { get; set; } = 7.0f;
    public bool ApplyHannWindow { get; set; } = true;
    /// <summary>Kept so older job files load; not used. The reference is taught (<see cref="ReferenceFile"/>).</summary>
    public string? TemplatePath { get; set; }
    public string? ReferenceFile { get; set; }
    [JsonIgnore] public byte[]? Reference { get; set; }
    [JsonIgnore] public int ReferenceWidth { get; set; }
    [JsonIgnore] public int ReferenceHeight { get; set; }

    public const int MinSize = 8;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();

    /// <summary>Cuts the reference from the teach image at the area.</summary>
    public void Teach(GrayImage image) => TaughtReference.Teach(this, image, MinSize);
}

public sealed class PhaseTool : ITool
{
    private readonly PhaseToolConfig _cfg;
    private PhaseEngine? _engine;
    private readonly object _lock = new();

    public PhaseTool(PhaseToolConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        // only the taught reference, never a part on the line (AUD-01); without one every run is NG
        if (TaughtReference.Problem(cfg) is null)
        {
            _engine = new PhaseEngine(
                estimateRotation: _cfg.EstimateRotation,
                estimateScale: _cfg.EstimateScale,
                minCorrelationScore: _cfg.MinCorrelationScore,
                minPsr: _cfg.MinPsr,
                applyHannWindow: _cfg.ApplyHannWindow);
            _engine.Teach(cfg.Reference!, cfg.ReferenceWidth, cfg.ReferenceHeight);
        }
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
            return new ToolResult(_cfg.Name, false, "Phase correlation failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.phase.invalid_image"
            };
        }

        lock (_lock)
        {
            if (_engine is null)
                return ToolArea.NotTaught(_cfg, TaughtReference.Problem(_cfg) ?? "no reference taught", sw.Elapsed.TotalMilliseconds);
            var area = ToolArea.ResolveTaughtSize(_cfg.Roi, fixture, image.Width, image.Height);
            if (area is null) return ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds);
            if (area.Width != _cfg.ReferenceWidth || area.Height != _cfg.ReferenceHeight)
                return ToolArea.NotTaught(_cfg, $"the reference taught is {_cfg.ReferenceWidth}×{_cfg.ReferenceHeight} px but the picture is {area.Width}×{area.Height} px", sw.Elapsed.TotalMilliseconds);
            int rw = area.Width, rh = area.Height;
            var roiPx = ToolArea.Copy(image, area);

            PhaseMatchResult res;
            try
            {
                res = _engine.Match(roiPx, rw, rh);
            }
            catch (Exception ex)
            {
                return new ToolResult(_cfg.Name, false, $"Phase correlator error: {ex.Message}", details, sw.Elapsed.TotalMilliseconds, graphics)
                {
                    Explain = "explain.phase.not_found"
                };
            }

            values["shift_x"] = res.ShiftX;
            values["shift_y"] = res.ShiftY;
            values["rotation_deg"] = res.RotationDeg;
            values["scale"] = res.Scale;
            values["correlation_score"] = res.CorrelationScore;
            values["psr"] = res.Psr;

            bool pass = res.IsFound &&
                        res.CorrelationScore >= _cfg.MinCorrelationScore &&
                        res.Psr >= _cfg.MinPsr;

            string summary;
            string? explainKey = null;

            if (pass)
            {
                summary = $"Phase aligned: Shift=({res.ShiftX:0.2}, {res.ShiftY:0.2}) px, Angle={res.RotationDeg:0.2}°, Score={res.CorrelationScore:0.2}";
                details.Add($"✓ Sub-pixel registration locked: offset ({res.ShiftX:0.2}, {res.ShiftY:0.2}) px at {res.RotationDeg:0.2}° rotation");
                details.Add($"✓ Phase correlation strength: peak {res.CorrelationScore:0.2} >= {_cfg.MinCorrelationScore:0.2}, PSR {res.Psr:0.1} >= {_cfg.MinPsr:0.1}");
            }
            else
            {
                explainKey = "explain.phase.not_found";
                summary = $"Phase correlation peak low (Score: {res.CorrelationScore:0.2} < {_cfg.MinCorrelationScore:0.2} or PSR: {res.Psr:0.1} < {_cfg.MinPsr:0.1})";
                details.Add($"✗ Part position could not be locked: peak score {res.CorrelationScore:0.2} (threshold {_cfg.MinCorrelationScore:0.2})");
                details.Add($"✗ Peak-to-sidelobe ratio {res.Psr:0.1} below confidence limit ({_cfg.MinPsr:0.1})");
            }

            graphics.Add(new Graphic(
                ToolArea.Corners(area),
                pass ? GraphicKind.Ok : GraphicKind.Fail,
                pass ? "Phase Locked" : "Phase NG"));

            double ms = sw.Elapsed.TotalMilliseconds;
            return new ToolResult(_cfg.Name, pass, summary, details, ms, graphics)
            {
                Explain = explainKey,
                Values = values,
                MatchScore = res.CorrelationScore
            };
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
