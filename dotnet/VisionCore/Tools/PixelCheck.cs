using BarcodeReading;

namespace VisionCore;

/// <summary>What a tool's smallest detail is measured in (requirements AS-14).</summary>
public enum PixelMeasure
{
    /// <summary>2D codes: pixels per module.</summary>
    Module,
    /// <summary>1D codes (and PDF417, Pharmacode): pixels per narrow bar (the X-dimension).</summary>
    NarrowBar,
    /// <summary>Read text and Check text: the height of a capital letter in pixels.</summary>
    LetterHeight,
}

/// <summary>"Accept as is" on a tool with too few pixels: who accepted it, when, and the size found then
/// (a later picture with a clearly smaller size asks again). The runtime writes By and At when the job is saved.</summary>
public sealed record PixelAcceptance(string By, DateTimeOffset At, float FoundPx);

/// <summary>One code or text line found on the picture and its size; Corners in full-image pixels.</summary>
public sealed record PixelItem(string Name, PixelMeasure Measure, float SizePx, float NeedPx, PointF[] Corners)
{
    public bool Below => SizePx < NeedPx;
}

/// <summary>
/// A tool's pixel check on one picture (requirements AS-14, UI-UX-SPEC 4.19 "Pixel check"): the worst item
/// found against what the tool needs, the factor missing and the three remedies. Advice only: it never
/// changes a tool's result, and a part below the need is still decided by its tool.
/// </summary>
public sealed record PixelReport(PixelMeasure Measure, float NeedPx, float UnsurePx, float FoundPx, IReadOnlyList<PixelItem> Items)
{
    /// <summary>The camera's lens focal length in mm (<see cref="CameraConfig.LensFocalMm"/>); null = not known.</summary>
    public float? LensMm { get; init; }
    /// <summary>The engineer's "Accept as is", when it still holds for this picture.</summary>
    public PixelAcceptance? Accepted { get; init; }

    public bool Enough => FoundPx >= NeedPx;
    /// <summary>Below about this size the read itself is unsure (2D: 1.8 px per module, INDUSTRY-METHODS.md).</summary>
    public bool Unsure => FoundPx < UnsurePx;
    /// <summary>Shown as "too few pixels" in the job tree: below the need and not accepted.</summary>
    public bool TooFew => !Enough && Accepted is null;
    /// <summary>How many times more pixels across the detail are needed, rounded up to 0.1; 1 when enough.</summary>
    public float Factor => PixelCheck.Factor(NeedPx, FoundPx);
    /// <summary>A camera with this many times the pixels (the factor squared, rounded up to 0.1).</summary>
    public float PixelsTimes => PixelCheck.RoundUp(Factor * Factor);
    /// <summary>The focal length that gives the factor with the camera where it is, rounded up to 1 mm; null without the lens.</summary>
    public float? LensAdvisedMm => LensMm is { } f && f > 0 ? MathF.Ceiling(f * Factor - 0.001f) : null;

    /// <summary>The size as the label shows it, e.g. "2.1 px/module", "14 px high".</summary>
    public static string SizeText(PixelMeasure m, float px) => m switch
    {
        PixelMeasure.Module => Fmt(px) + " px/module",
        PixelMeasure.NarrowBar => Fmt(px) + " px/bar",
        _ => px.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " px high",
    };

    /// <summary>The engineer's advice in English, empty when enough: "Needs 1.2× more pixels: camera 1.2× nearer,
    /// a 30 mm lens instead of 25 mm, or a camera with 1.5× the pixels".</summary>
    public string Advice()
    {
        if (Enough) return "";
        var k = Fmt(Factor);
        var lens = LensAdvisedMm is { } to ? $"a {to:0} mm lens instead of {LensMm:0.#} mm" : $"a lens with {k}× the focal length";
        var text = $"Needs {k}× more pixels: camera {k}× nearer, {lens}, or a camera with {Fmt(PixelsTimes)}× the pixels";
        return Unsure ? text + ". At this size the read is unsure" : text;
    }

    static string Fmt(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Each Read code and text tool's need in pixels, and the check of what it found against it (requirements AS-14).
/// Starting values: 2D 2.5 px per module (below about 1.8 the read is unsure, INDUSTRY-METHODS.md);
/// 1D 2.0 px per narrow bar (unsure below 1.5); text 20 px capital height (unsure below 12; the rendered font
/// takes 10 px at the least, TOOL-SPECIFICATIONS.md 7).
/// </summary>
public static class PixelCheck
{
    public const float ModuleNeedPx = 2.5f, ModuleUnsurePx = 1.8f;
    public const float BarNeedPx = 2.0f, BarUnsurePx = 1.5f;
    public const float LetterNeedPx = 20f, LetterUnsurePx = 12f;
    /// <summary>An acceptance holds while the size found is at least this share of the size accepted (measuring noise).</summary>
    public const float AcceptedShare = 0.95f;

    /// <summary>What a code of this type is measured in.</summary>
    public static PixelMeasure MeasureOf(CodeFormat f) =>
        CodeFormats.IsLinear(f) || CodeFormats.IsPharmacode(f) || f == CodeFormat.Pdf417 ? PixelMeasure.NarrowBar : PixelMeasure.Module;

    public static (float Need, float Unsure) NeedOf(PixelMeasure m) => m switch
    {
        PixelMeasure.Module => (ModuleNeedPx, ModuleUnsurePx),
        PixelMeasure.NarrowBar => (BarNeedPx, BarUnsurePx),
        _ => (LetterNeedPx, LetterUnsurePx),
    };

    /// <summary>need / found rounded up to 0.1 (so the remedy is never short), 1 when found meets the need.</summary>
    public static float Factor(float needPx, float foundPx) =>
        foundPx <= 0 ? float.PositiveInfinity : foundPx >= needPx ? 1f : RoundUp(needPx / foundPx);

    internal static float RoundUp(float v) => MathF.Ceiling(v * 10 - 0.001f) / 10;

    /// <summary>
    /// The pixel check of a Read code or text tool on what it found; null for other tools and when nothing found
    /// was measured (a code tool measures sizes only with the engine's quality measure on, see
    /// <see cref="JobRunner.MeasurePixels"/>). The worst item (smallest share of its need) is the tool's.
    /// </summary>
    public static PixelReport? Check(ToolConfig tool, ToolResult result, CameraConfig? camera = null)
    {
        var items = new List<PixelItem>();
        if (tool is CodeToolConfig)
            foreach (var c in result.Codes ?? [])
            {
                if (c.Quality is not { ModuleSizePx: > 0 } q) continue;
                var m = MeasureOf(c.Format);
                items.Add(new PixelItem(CodeFormats.Name(c.Format), m, q.ModuleSizePx, NeedOf(m).Need, c.Corners));
            }
        else if (tool is TextToolConfig)
            foreach (var l in result.Lines ?? [])
                if (l.CharHeightPx > 0)
                    items.Add(new PixelItem("text", PixelMeasure.LetterHeight, l.CharHeightPx, LetterNeedPx, l.Corners));
        if (items.Count == 0) return null;
        var worst = items.MinBy(i => i.SizePx / i.NeedPx)!;
        var (need, unsure) = NeedOf(worst.Measure);
        var accepted = tool.PixelsAccepted is { } a && Holds(a, worst.SizePx) ? a : null;
        return new PixelReport(worst.Measure, need, unsure, worst.SizePx, items) { LensMm = camera?.LensFocalMm, Accepted = accepted };
    }

    /// <summary>An acceptance covers the size it was given for, and anything not clearly smaller.</summary>
    public static bool Holds(PixelAcceptance a, float foundPx) => foundPx >= a.FoundPx * AcceptedShare;

    /// <summary>
    /// On save: each tool's acceptance that is new (or for another size) since the saved version is recorded as the
    /// saving user's, now; one unchanged keeps the saved name and time, so the client cannot rewrite who accepted.
    /// </summary>
    public static void Stamp(Job job, Job? saved, string user, DateTimeOffset now)
    {
        foreach (var t in job.Tools)
        {
            if (t.PixelsAccepted is not { } a) continue;
            var before = saved?.Tools.FirstOrDefault(s => s.Name == t.Name)?.PixelsAccepted;
            t.PixelsAccepted = before is not null && before.FoundPx == a.FoundPx
                ? before
                : a with { By = user.Length > 0 ? user : a.By, At = now };
        }
    }
}
