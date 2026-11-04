using System;
using System.Collections.Generic;
using System.Linq;

namespace BarcodeReading;

/// <summary>Why a case came up short, and what to do about it on the line.</summary>
public sealed record Advice(string Code, string Message, float? SuggestedExposureUs = null);

/// <summary>
/// Diagnostics agent: looks at recent frames of one product and explains misses in plain words
/// (under-exposed, glare, codes not found, codes found but unreadable, too slow), with a
/// suggested exposure when the labels are too dark or too bright.
/// </summary>
public sealed class LineAdvisor
{
    private readonly Queue<DecodeResult> _recent = new();

    /// <summary>Frames kept for the advice (a rolling window).</summary>
    public int Window { get; set; } = 50;
    /// <summary>Target brightness of label paper, 0..255.</summary>
    public float TargetLabelBrightness { get; set; } = 170;

    public void Add(DecodeResult r)
    {
        _recent.Enqueue(r);
        while (_recent.Count > Window) _recent.Dequeue();
    }

    public int Frames => _recent.Count;

    /// <summary>Read rate over the window: codes read / codes expected.</summary>
    public double ReadRate
    {
        get
        {
            long exp = _recent.Sum(r => (long)Math.Max(0, r.Stats.Expected));
            long got = _recent.Sum(r => (long)Math.Min(r.Stats.Codes, Math.Max(0, r.Stats.Expected)));
            return exp == 0 ? 1 : (double)got / exp;
        }
    }

    /// <summary>Advice for the current window, most important first. Empty when all is well.</summary>
    public IReadOnlyList<Advice> Advise(CameraSettings camera, int timeBudgetMs)
    {
        var list = new List<Advice>();
        if (_recent.Count == 0) return list;

        float brightness = Median(_recent.Select(r => r.Stats.LabelBrightness));
        if (brightness < TargetLabelBrightness * 0.6f)
        {
            float factor = Math.Min(4f, TargetLabelBrightness / Math.Max(1f, brightness));
            list.Add(new Advice("underexposed",
                $"Labels are dark ({brightness:F0}/255, target {TargetLabelBrightness:F0}). Raise exposure or light power.",
                camera.ExposureUs * factor));
        }
        else if (brightness > 245)
        {
            list.Add(new Advice("overexposed",
                $"Labels are saturated ({brightness:F0}/255). Lower exposure or light power, or check for glare.",
                camera.ExposureUs * 0.7f));
        }

        var short_ = _recent.Where(r => !r.Stats.Complete).ToList();
        if (short_.Count > 0)
        {
            double missedPerCase = short_.Average(r => r.Stats.Expected - r.Stats.Codes);
            double unreadRegions = short_.Average(r => r.Misses.Count);
            if (unreadRegions >= missedPerCase * 0.8)
            {
                // the engine's diagnosis of the unread regions names the likeliest cause
                var tally = new MissReasonTally();
                foreach (var r in short_) tally.Add(r);
                list.Add(new Advice("unreadable",
                    $"{short_.Count} of {_recent.Count} cases short: the codes are found but do not decode " +
                    (tally.Top() is { } top
                        ? $"(most unread regions: {MissDiagnosis.TagOf(top)}). {MissDiagnosis.FixOf(top)}"
                        : "(print quality, focus, glare on the code, or modules too small).")));
            }
            else
                list.Add(new Advice("not-found",
                    $"{short_.Count} of {_recent.Count} cases short: some codes are not found at all " +
                    "(covered, outside the field of view or region of interest, or very low contrast)."));
        }

        // codes at the edge of the crop: cases stand somewhere else than at calibration, and the
        // next code over may already be cut off
        if (camera.Roi is { } roi)
        {
            int atEdge = _recent.Count(r => r.Codes.Any(c => NearEdge(c, roi)));
            if (atEdge * 5 >= _recent.Count)
                list.Add(new Advice("crop-edge",
                    $"In {atEdge} of {_recent.Count} cases codes touch the edge of the crop: cases stand elsewhere than at " +
                    "calibration. Auto calibrate again, or set the case guides."));
        }

        float time = Median(_recent.Select(r => r.Stats.TotalMs));
        if (time > timeBudgetMs)
            list.Add(new Advice("slow", $"Median decode time {time:F0} ms is over the {timeBudgetMs} ms budget. " +
                                        "Use the AI locator, set a region of interest, or a faster PC."));
        return list;
    }

    // a code within half its size of the crop's edge
    private static bool NearEdge(DecodedCode c, RoiSettings roi)
    {
        float m = c.SidePx / 2;
        return c.Corners.Any(p => p.X < roi.X + m || p.Y < roi.Y + m || p.X > roi.X + roi.Width - m || p.Y > roi.Y + roi.Height - m);
    }

    private static float Median(IEnumerable<float> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? 0 : v[v.Length / 2];
    }
}
