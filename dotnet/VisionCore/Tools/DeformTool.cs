using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;

namespace VisionCore;

/// <summary>
/// Non-rigid deformable mesh matcher and unwarper (Tool 1 / Flexible Packaging / AG-05).
/// Registers wrinkled foil pouches, flow-wraps, and blister surfaces to planar reference templates,
/// rectifying surface distortion in-place and detecting film tears, ruptures, and excessive crumpling.
/// </summary>
public sealed class DeformToolConfig : ToolConfig, ITaughtReference
{
    public int MeshCols { get; set; } = 9;
    public int MeshRows { get; set; } = 9;
    public int PyramidLevels { get; set; } = 3;
    public int MaxIterationsPerLevel { get; set; } = 15;
    public float SmoothnessWeight { get; set; } = 0.35f;
    public float RuptureStrainThreshold { get; set; } = 0.35f;
    public float CrumpleBendingThreshold { get; set; } = 40.0f;
    public float MinAlignmentScore { get; set; } = 0.70f;
    /// <summary>Kept so older job files load. The camera image is never changed (AUD-02): the unwarped picture
    /// is not written back.</summary>
    public bool UnwarpInPlace { get; set; } = true;
    /// <summary>Kept so older job files load; not used. The reference is taught (<see cref="ReferenceFile"/>).</summary>
    public string? TemplatePath { get; set; }
    public string? ReferenceFile { get; set; }
    [JsonIgnore] public byte[]? Reference { get; set; }
    [JsonIgnore] public int ReferenceWidth { get; set; }
    [JsonIgnore] public int ReferenceHeight { get; set; }

    public const int MinSize = 4;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();

    /// <summary>Cuts the reference from the teach image at the area.</summary>
    public void Teach(GrayImage image) => TaughtReference.Teach(this, image, MinSize);
}

public sealed class DeformTool : ITool
{
    private readonly DeformToolConfig _cfg;

    public DeformTool(DeformToolConfig cfg)
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
            return new ToolResult(_cfg.Name, false, "Deformable matching failed: Image buffer invalid or too small", details, sw.Elapsed.TotalMilliseconds, graphics)
            {
                Explain = "explain.deform.invalid_image"
            };
        }

        // only a taught reference, never this part (AUD-01)
        if (TaughtReference.Problem(_cfg) is { } notTaught)
            return ToolArea.NotTaught(_cfg, notTaught, sw.Elapsed.TotalMilliseconds);
        var area = ToolArea.ResolveTaughtSize(_cfg.Roi, fixture, image.Width, image.Height);
        if (area is null || area.Width != _cfg.ReferenceWidth || area.Height != _cfg.ReferenceHeight)
            return area is null ? ToolArea.Outside(_cfg, sw.Elapsed.TotalMilliseconds)
                : ToolArea.NotTaught(_cfg, $"the reference taught is {_cfg.ReferenceWidth}×{_cfg.ReferenceHeight} px but the picture is {area.Width}×{area.Height} px", sw.Elapsed.TotalMilliseconds);
        int rw = area.Width, rh = area.Height;
        var roiPx = ToolArea.Copy(image, area);   // the camera image is never changed (AUD-02)

        {
            DeformResult res;
            try
            {
                res = DeformEngine.MatchAndUnwarp(
                    roiPx, rw, rh,
                    _cfg.Reference!, _cfg.ReferenceWidth, _cfg.ReferenceHeight,
                    meshCols: _cfg.MeshCols,
                    meshRows: _cfg.MeshRows,
                    pyramidLevels: _cfg.PyramidLevels,
                    maxIterationsPerLevel: _cfg.MaxIterationsPerLevel,
                    smoothnessWeight: _cfg.SmoothnessWeight,
                    ruptureStrainThreshold: _cfg.RuptureStrainThreshold,
                    crumpleBendingThreshold: _cfg.CrumpleBendingThreshold,
                    minAlignmentScore: _cfg.MinAlignmentScore,
                    outputUnwarped: false);
            }
            catch (Exception ex)
            {
                return new ToolResult(_cfg.Name, false, $"Deformation solver error: {ex.Message}", details, sw.Elapsed.TotalMilliseconds, graphics)
                {
                    Explain = "explain.deform.alignment_failed"
                };
            }

            values["alignment_score"] = res.Stats.FinalAlignmentScore;
            values["mean_displacement_px"] = res.Stats.MeanDisplacementPx;
            values["max_displacement_px"] = res.Stats.MaxDisplacementPx;
            values["area_stretch"] = res.Stats.MaxAreaStretch;
            values["bending_energy"] = res.Stats.TotalBendingEnergy;

            bool pass = res.Stats.IsMatchOk &&
                        !res.Stats.IsFilmRuptured &&
                        !res.Stats.IsExcessiveCrumple &&
                        res.Stats.FinalAlignmentScore >= _cfg.MinAlignmentScore;

            string summary;
            string? explainKey = null;

            if (pass)
            {
                summary = $"Deformable mesh registered (Score={res.Stats.FinalAlignmentScore:0.2}, Shift={res.Stats.MeanDisplacementPx:0.1} px)";
                details.Add($"✓ Flexible packaging aligned: score {res.Stats.FinalAlignmentScore:0.2} >= {_cfg.MinAlignmentScore:0.2}");
                details.Add($"✓ Non-rigid warp compensated: mean {res.Stats.MeanDisplacementPx:0.1} px, max {res.Stats.MaxDisplacementPx:0.1} px");
                details.Add($"✓ Film integrity intact: stretch {res.Stats.MaxAreaStretch:0.2}, bending {res.Stats.TotalBendingEnergy:0.1}");
            }
            else
            {
                if (res.Stats.IsFilmRuptured)
                {
                    explainKey = "explain.deform.film_ruptured";
                    summary = $"Packaging film rupture / tear detected (Area stretch {res.Stats.MaxAreaStretch:0.2} > {_cfg.RuptureStrainThreshold:0.2})";
                    details.Add($"✗ Packaging film rupture detected: local area stretch {res.Stats.MaxAreaStretch:0.2} exceeds limit");
                }
                else if (res.Stats.IsExcessiveCrumple)
                {
                    explainKey = "explain.deform.excessive_crumple";
                    summary = $"Excessive packaging crumple (Bending energy {res.Stats.TotalBendingEnergy:0.1} > {_cfg.CrumpleBendingThreshold:0.1})";
                    details.Add($"✗ Severe pouch crumple or fold: bending energy {res.Stats.TotalBendingEnergy:0.1} exceeds limit");
                }
                else
                {
                    explainKey = "explain.deform.alignment_failed";
                    summary = $"Deformable match score low ({res.Stats.FinalAlignmentScore:0.2} < {_cfg.MinAlignmentScore:0.2})";
                    details.Add($"✗ Could not align wrinkled pouch to canonical template: score {res.Stats.FinalAlignmentScore:0.2} < {_cfg.MinAlignmentScore:0.2}");
                }
            }

            graphics.Add(new Graphic(
                ToolArea.Corners(area),
                pass ? GraphicKind.Ok : GraphicKind.Fail,
                pass ? "Pouch Rectified" : "Deformation NG"));

            double ms = sw.Elapsed.TotalMilliseconds;
            return new ToolResult(_cfg.Name, pass, summary, details, ms, graphics)
            {
                Explain = explainKey,
                Values = values
            };
        }
    }

    public void Dispose()
    {
    }
}
