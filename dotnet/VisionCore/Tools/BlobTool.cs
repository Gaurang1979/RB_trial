using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VisionCore;

// P/Invoke declarations matching engine/include/vs_blob.h.
internal static class BlobNative
{
    private const string Lib = "vs_blob";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize;
        public int RoiX, RoiY, RoiW, RoiH;
        public int ThresholdMode;
        public int Polarity;
        public int Threshold;
        public int ThresholdHigh;
        public int LocalWindow;
        public int LocalOffset;
        public int MinContrast;
        public int Connectivity;
        public int FillHoles;
        public int ExcludeEdge;
        public int Morphology;
        public float MinArea, MaxArea;
        public float MinCircularity, MaxCircularity;
        public float MinElongation, MaxElongation;
        public int SortBy;
        public int SortDescending;
        public int KeepFirst;
        public int TimeBudgetMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public float Area;
        public float Cx, Cy;
        public float Perimeter;
        public float Circularity;
        public float Compactness;
        public float MajorAxis, MinorAxis;
        public float Elongation;
        public float AngleDeg;
        public int BoxX, BoxY, BoxW, BoxH;
        public float RboxCx, RboxCy, RboxW, RboxH, RboxAngleDeg;
        public int Holes;
        public float MeanGrey;
        public int TouchesEdge;
        public int RejectReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        public int Count;
        public int Rejected;
        public float TotalArea;
        public int ThresholdUsed;
        public int Flat;
        public float RegionMean;
        public int Truncated;
        public float TimeMs;
        public int EdgeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ContourParams
    {
        public int StructSize;
        public int Enabled;
        public float EdgeLevel;
        public float Smoothing;
        public float MinDefectDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Defect
    {
        public float Depth;
        public float X, Y;
        public float Width;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Contour
    {
        public int StructSize;
        public int Points;
        public float Length;
        public float Area;
        public float Convexity;
        public int Defects;
        public float MaxDefectDepth;
        public float MaxDefectX, MaxDefectY;
        public float EdgeShare;
        public int Closed;
        public int Taught;
        public float MaxDeviation;
        public float MeanDeviation;
        public float MaxDeviationX, MaxDeviationY;
        public float PoseX, PoseY, PoseAngleDeg;
    }

    [DllImport(Lib, EntryPoint = "vs_blob_default_params")] internal static extern void DefaultParams(out Params p);
    [DllImport(Lib, EntryPoint = "vs_blob_create")] internal static extern IntPtr Create(ref Params p);
    [DllImport(Lib, EntryPoint = "vs_blob_destroy")] internal static extern void Destroy(IntPtr t);
    [DllImport(Lib, EntryPoint = "vs_blob_set_params")] internal static extern int SetParams(IntPtr t, ref Params p);
    [DllImport(Lib, EntryPoint = "vs_blob_run")] internal static extern unsafe int Run(IntPtr t, byte* px, int w, int h, int stride);
    [DllImport(Lib, EntryPoint = "vs_blob_get_result")] internal static extern void GetResult(IntPtr t, out Result r);
    [DllImport(Lib, EntryPoint = "vs_blob_get_blobs")] internal static extern unsafe int GetBlobs(IntPtr t, Info* buf, int cap);
    [DllImport(Lib, EntryPoint = "vs_blob_get_rejected")] internal static extern unsafe int GetRejected(IntPtr t, Info* buf, int cap);
    [DllImport(Lib, EntryPoint = "vs_blob_default_contour_params")] internal static extern void DefaultContourParams(out ContourParams p);
    [DllImport(Lib, EntryPoint = "vs_blob_set_contour_params")] internal static extern int SetContourParams(IntPtr t, ref ContourParams p);
    [DllImport(Lib, EntryPoint = "vs_blob_get_contour")] internal static extern int GetContour(IntPtr t, int blob, ref Contour c);
    [DllImport(Lib, EntryPoint = "vs_blob_get_outline")] internal static extern unsafe int GetOutline(IntPtr t, int blob, float* xy, int cap);
    [DllImport(Lib, EntryPoint = "vs_blob_get_defects")] internal static extern unsafe int GetDefects(IntPtr t, int blob, Defect* d, int cap);
    [DllImport(Lib, EntryPoint = "vs_blob_teach_outline")] internal static extern int TeachOutline(IntPtr t, int blob);
    [DllImport(Lib, EntryPoint = "vs_blob_set_taught_outline")] internal static extern unsafe int SetTaughtOutline(IntPtr t, float* xy, int n);
    [DllImport(Lib, EntryPoint = "vs_blob_get_taught_outline")] internal static extern unsafe int GetTaughtOutline(IntPtr t, float* xy, int cap);
    [DllImport(Lib, EntryPoint = "vs_blob_last_error")] private static extern IntPtr LastErrorPtr(IntPtr t);
    internal static string LastError(IntPtr t) => Marshal.PtrToStringUTF8(LastErrorPtr(t)) ?? "";
}

public enum BlobThreshold { Auto, Fixed, Band, Local }

/// <summary>Unit of the outline limits (largest dent, largest deviation) and their values: Mm needs the camera's calibration.</summary>
public enum BlobOutlineUnit { Px, Mm }
public enum BlobPolarity { DarkOnLight, LightOnDark }
public enum BlobMorphology { None, Open3, Close3 }
public enum BlobSort { None, Area, X, Y, Perimeter, Circularity, Elongation, MeanGrey }
/// <summary>Presence rule: Any = only the count range decides; Present = at least one blob; Absent = none.</summary>
public enum BlobPresence { Any, Present, Absent }

/// <summary>One blob found: area in pixels, centroid, shape measures, boxes (tool spec 4.1); its outline when
/// the outline check is on (tool spec 4.2).</summary>
public sealed record BlobInfo(
    float Area, PointF Centroid, float Perimeter, float Circularity, float Compactness,
    float MajorAxis, float MinorAxis, float Elongation, float AngleDeg,
    RoiBox Box, PointF[] TurnedBox, int Holes, float MeanGrey, bool TouchesEdge, BlobContour? Contour = null);

/// <summary>One indent of an outline: depth from the convex hull (px), its deepest point, the hull chord's length.</summary>
public sealed record BlobDefect(float Depth, PointF At, float Width);

/// <summary>Check outline (tool spec 4.2): the blob's outline to a fraction of a pixel and its shape. Outline is
/// simplified to 0.25 px for display. Defects lists the indents at least MinDefectDepth deep; MaxDentDepth and DentAt
/// are the deepest indent whatever its depth (0 when convex). MaxDeviation (px) and Pose are null when no outline is taught.</summary>
public sealed record BlobContour(
    PointF[] Outline, float Length, float Area, float Convexity, IReadOnlyList<BlobDefect> Defects, float MaxDentDepth, PointF DentAt,
    float EdgeShare, bool Closed, float? MaxDeviation, float? MeanDeviation, PointF? DeviationAt, PointF? PoseCentre, float? PoseAngleDeg);

/// <summary>Count / spot (tool spec 4.1 and 4.4): threshold the area, find the blobs, filter and count them.</summary>
public sealed class BlobToolConfig : ToolConfig
{
    public BlobThreshold Threshold { get; set; } = BlobThreshold.Auto;
    public BlobPolarity Polarity { get; set; } = BlobPolarity.DarkOnLight;
    /// <summary>Fixed: the grey level (dark blobs are at or below it); Band: the low end.</summary>
    public int Level { get; set; } = 128;
    /// <summary>Band: the high end.</summary>
    public int LevelHigh { get; set; } = 255;
    /// <summary>Local: window side (px, odd) and how far past the local mean a blob pixel is (grey levels).</summary>
    public int LocalWindow { get; set; } = 31;
    public int LocalOffset { get; set; } = 10;
    /// <summary>Auto: an area with less grey spread than this has no blobs (Otsu would split noise).</summary>
    public int MinContrast { get; set; } = 15;
    public int Connectivity { get; set; } = 8;
    public bool FillHoles { get; set; }
    public bool ExcludeEdge { get; set; } = true;
    public BlobMorphology Morphology { get; set; } = BlobMorphology.None;

    // filters (tool spec 4.4): blobs outside them are left out, not failed
    public float MinArea { get; set; } = 10;
    /// <summary>0 = no upper limit.</summary>
    public float MaxArea { get; set; }
    public float MinCircularity { get; set; }
    public float MaxCircularity { get; set; }
    public float MinElongation { get; set; }
    public float MaxElongation { get; set; }
    public BlobSort SortBy { get; set; } = BlobSort.None;
    public bool SortDescending { get; set; }
    /// <summary>Keep only the first N after sorting; 0 = all.</summary>
    public int KeepFirst { get; set; }

    // pass rules: a blob kept by the filters that breaks one fails the part
    public BlobPresence Presence { get; set; } = BlobPresence.Any;
    public int? MinCount { get; set; }
    public int? MaxCount { get; set; }
    /// <summary>Every blob counted must have an area in this range (px); null = no limit.</summary>
    public float? BlobAreaMin { get; set; }
    public float? BlobAreaMax { get; set; }
    /// <summary>The counted blobs' total area must be in this range (px); null = no limit.</summary>
    public float? TotalAreaMin { get; set; }
    public float? TotalAreaMax { get; set; }
    /// <summary>A darker area mean than this is a dark frame (light or camera failed): NG, never "no spots".</summary>
    public float MinRegionMean { get; set; } = 8;
    /// <summary>A brighter area mean than this is a white (over-exposed) frame: NG, never "no spots".</summary>
    public float MaxRegionMean { get; set; } = 250;
    public int TimeBudgetMs { get; set; } = 50;

    // outline check (tool spec 4.2, "Check outline"): each counted blob's outline; its rules fail the part
    public bool CheckOutline { get; set; }
    /// <summary>Grey level of the edge; null = halfway between the blob and its surroundings.</summary>
    public int? EdgeLevel { get; set; }
    /// <summary>Gaussian sigma (px) on the image before the edge is placed; 0 = none.</summary>
    public float OutlineSmoothing { get; set; } = 1;
    /// <summary>Indents shallower than this (px) are not listed as dents.</summary>
    public float MinDefectDepth { get; set; } = 1;
    /// <summary>Unit of <see cref="MaxDentDepth"/> and <see cref="MaxDeviation"/> and of the outline values reported; Mm needs the camera's calibration (tool spec 4.2).</summary>
    public BlobOutlineUnit OutlineUnit { get; set; } = BlobOutlineUnit.Px;
    /// <summary>Pass rule: no dent deeper than this (in <see cref="OutlineUnit"/>); null = no limit.</summary>
    public float? MaxDentDepth { get; set; }
    /// <summary>Pass rule: area / convex hull area at least this (0..1); null = no limit.</summary>
    public float? MinConvexity { get; set; }
    /// <summary>The taught outline, x and y pairs round its centroid (from <see cref="BlobTool.TeachOutline"/>).</summary>
    public float[]? TaughtOutline { get; set; }
    /// <summary>Pass rule with a taught outline: no point further than this (in <see cref="OutlineUnit"/>) from it, either way.</summary>
    public float? MaxDeviation { get; set; }
    /// <summary>An outline with fewer of its points on a grey edge than this share is broken: not checked (NG).</summary>
    public float MinEdgeShare { get; set; } = 0.9f;

    /// <summary>An outline rule is set (dent, convexity or deviation).</summary>
    public bool HasOutlineRules => MaxDentDepth is not null || MinConvexity is not null || MaxDeviation is not null;

    public override IEnumerable<FieldRequest> Fields() => Array.Empty<FieldRequest>();

    /// <summary>The count rule after presence: (lowest, highest or null).</summary>
    public (int Min, int? Max) CountRange()
    {
        int min = MinCount ?? 0;
        int? max = MaxCount;
        if (Presence == BlobPresence.Present) min = Math.Max(min, 1);
        if (Presence == BlobPresence.Absent) max = 0;
        return (min, max);
    }
}

public sealed class BlobTool : ITool
{
    private readonly BlobToolConfig _cfg;
    private BlobNative.Params _p;
    private IntPtr _h;
    private readonly string? _setupError;

    /// <summary>mm per pixel at the part from the camera's calibration; 0 = none (outline values stay in px).</summary>
    private readonly double _mmPerPx;
    private double UnitScale => _cfg.OutlineUnit == BlobOutlineUnit.Mm ? _mmPerPx : 1;
    private string UnitName => _cfg.OutlineUnit == BlobOutlineUnit.Mm ? "mm" : "px";

    /// <param name="calibration">The camera's lens and scale at the part (Set real size); null = outline values in px only.</param>
    public BlobTool(BlobToolConfig cfg, BarcodeReading.LensCalibration? calibration = null)
    {
        _cfg = cfg;
        _mmPerPx = calibration is { MmPerPx: > 0 } l && float.IsFinite(l.MmPerPx) ? l.MmPerPx : 0;
        _setupError = SetupProblem(cfg, calibration);
        BlobNative.DefaultParams(out _p);
        _p.ThresholdMode = (int)cfg.Threshold;
        _p.Polarity = (int)cfg.Polarity;
        _p.Threshold = cfg.Level;
        _p.ThresholdHigh = cfg.LevelHigh;
        _p.LocalWindow = cfg.LocalWindow;
        _p.LocalOffset = cfg.LocalOffset;
        _p.MinContrast = cfg.MinContrast;
        _p.Connectivity = cfg.Connectivity;
        _p.FillHoles = cfg.FillHoles ? 1 : 0;
        _p.ExcludeEdge = cfg.ExcludeEdge ? 1 : 0;
        _p.Morphology = (int)cfg.Morphology;
        _p.MinArea = cfg.MinArea;
        _p.MaxArea = cfg.MaxArea;
        _p.MinCircularity = cfg.MinCircularity;
        _p.MaxCircularity = cfg.MaxCircularity;
        _p.MinElongation = cfg.MinElongation;
        _p.MaxElongation = cfg.MaxElongation;
        _p.SortBy = (int)cfg.SortBy;
        _p.SortDescending = cfg.SortDescending ? 1 : 0;
        _p.KeepFirst = cfg.KeepFirst;
        _p.TimeBudgetMs = cfg.TimeBudgetMs;
        _h = BlobNative.Create(ref _p);
        if (_h == IntPtr.Zero) throw new InvalidOperationException("vs_blob_create failed");
        BlobNative.DefaultContourParams(out var cp);
        cp.Enabled = cfg.CheckOutline ? 1 : 0;
        cp.EdgeLevel = cfg.EdgeLevel ?? -1;
        cp.Smoothing = cfg.OutlineSmoothing;
        cp.MinDefectDepth = cfg.MinDefectDepth;
        if (BlobNative.SetContourParams(_h, ref cp) != 0) throw new InvalidOperationException("vs_blob_set_contour_params failed");
        if (_setupError is null) SetTaught(cfg.TaughtOutline);
    }

    private unsafe void SetTaught(float[]? xy)
    {
        xy ??= Array.Empty<float>();
        fixed (float* p = xy)
            if (BlobNative.SetTaughtOutline(_h, p, xy.Length / 2) != 0) throw new InvalidOperationException("vs_blob_set_taught_outline failed");
    }

    public ToolConfig Config => _cfg;

    /// <summary>A sentence for the engineer when the tool is set up so that it cannot work; null when it can.</summary>
    public static string? SetupProblem(BlobToolConfig c, BarcodeReading.LensCalibration? calibration = null)
    {
        if (c.OutlineUnit == BlobOutlineUnit.Mm && !(calibration is { MmPerPx: > 0 } && float.IsFinite(calibration.MmPerPx)))
            return "outline limits in mm need the camera's real size (calibrate the camera first) or use px";
        if (c.Connectivity is not (4 or 8)) return "connectivity is 4 or 8";
        if (c.Level is < 0 or > 255 || c.LevelHigh is < 0 or > 255) return "grey levels are 0 to 255";
        if (c.Threshold == BlobThreshold.Band && c.LevelHigh < c.Level) return "the band's high end is below its low end";
        if (c.Threshold == BlobThreshold.Local && c.LocalWindow < 3) return "the local window is 3 px at least";
        if (c.MinArea < 1) return "the smallest area is 1 px at least";
        if (c.MaxArea > 0 && c.MaxArea < c.MinArea) return "the largest area is below the smallest";
        if (c.MaxCircularity > 0 && c.MaxCircularity < c.MinCircularity) return "the highest circularity is below the lowest";
        if (c.MaxElongation > 0 && c.MaxElongation < c.MinElongation) return "the highest elongation is below the lowest";
        if (c.MinCount is < 0 || c.MaxCount is < 0) return "counts cannot be negative";
        var (min, max) = c.CountRange();
        if (max is { } m && m < min)
            return c.Presence == BlobPresence.Absent && min > 0 ? "no spots allowed, but a lowest count is set" : "the highest count is below the lowest";
        if (c.KeepFirst > 0 && min > c.KeepFirst) return $"at least {min} needed, but only the first {c.KeepFirst} are kept";
        // keep-first would hide extra or wrong blobs from these rules
        if (c.KeepFirst > 0 && (max is not null || c.TotalAreaMax is not null || c.BlobAreaMin is not null || c.BlobAreaMax is not null || c.HasOutlineRules))
            return "keep first N hides blobs from the highest count and area rules: keep all blobs, or drop those rules";
        if (c.MaxRegionMean <= c.MinRegionMean) return "the brightest area mean is not above the darkest";
        if (c.BlobAreaMin is { } a0 && c.BlobAreaMax is { } a1 && a1 < a0) return "the largest spot area is below the smallest";
        if (c.TotalAreaMin is { } t0 && c.TotalAreaMax is { } t1 && t1 < t0) return "the largest total area is below the smallest";
        if (c.TimeBudgetMs < 1) return "the time budget is 1 ms at least";
        if (c.HasOutlineRules && !c.CheckOutline) return "outline rules are set, but the outline check is off";
        // with no part required, a missing part (or one the filters leave out) would pass the outline rules unchecked
        if (c.HasOutlineRules && min < 1) return "outline rules need a part to check: set Present or a lowest count of 1 or more";
        if (c.EdgeLevel is < 0 or > 255) return "the edge level is 0 to 255";
        if (c.OutlineSmoothing is < 0 or > 5) return "the outline smoothing is 0 to 5 px";
        if (c.MinDefectDepth < 0 || c.MaxDentDepth is < 0) return "dent depths cannot be negative";
        if (c.MinConvexity is < 0 or > 1) return "the lowest convexity is 0 to 1";
        if (c.MinEdgeShare is < 0 or > 1) return "the lowest edge share is 0 to 1";
        if (c.TaughtOutline is { } o && (o.Length % 2 != 0 || o.Length < 6 || o.Any(v => !float.IsFinite(v))))
            return "the taught outline is not a list of 3 or more points";
        if (c.MaxDeviation is not null && c.TaughtOutline is null) return "a largest deviation is set, but no outline is taught";
        if (c.MaxDeviation is < 0) return "the largest deviation cannot be negative";
        // a taught outline nothing is compared against would pass any shape
        if (c.CheckOutline && c.TaughtOutline is not null && c.MaxDeviation is null) return "an outline is taught, but no largest deviation is set";
        return null;
    }

    public unsafe ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null)
    {
        if (_setupError is not null)
            return Fail(_setupError, "explain.blob.setup", 0, Array.Empty<Graphic>());
        var sw = Stopwatch.StartNew();
        // an area cut by the picture's edge is not checked whole: blobs on the cut would be left out unseen
        if (!RunNative(image, fixture))
            return Fail("The area to check is partly outside the picture", "explain.blob.region", sw.Elapsed.TotalMilliseconds, Array.Empty<Graphic>());
        BlobNative.GetResult(_h, out var res);
        var kept = Read(rejected: false);
        var rejected = Read(rejected: true);
        var blobs = kept.Select((b, i) => ToInfo(b, _cfg.CheckOutline && res.Truncated == 0 ? ReadContour(i) : null)).ToList();

        var graphics = new List<Graphic>();
        var values = new Dictionary<string, double>
        {
            ["count"] = res.Count,
            ["total_area"] = res.TotalArea,
            ["threshold"] = res.ThresholdUsed,
        };
        var details = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"{res.Count} found, {res.Rejected} left out, total area {res.TotalArea:0} px, threshold {(res.ThresholdUsed >= 0 ? res.ThresholdUsed.ToString(CultureInfo.InvariantCulture) : "local")}, {res.TimeMs:0.0} ms"),
        };
        double ms = sw.Elapsed.TotalMilliseconds;

        // line safety: a run that could not finish, or a dark frame, is not checked: NG, never "no spots"
        if (res.Truncated != 0)
            return Fail($"Not checked: {BlobNative.LastError(_h)}", "explain.blob.incomplete", ms, graphics, details, values);
        if (res.RegionMean < _cfg.MinRegionMean)
            return Fail(string.Create(CultureInfo.InvariantCulture, $"The picture is almost black here (mean grey {res.RegionMean:0})"),
                "explain.blob.image_dark", ms, graphics, details, values);
        if (res.RegionMean > _cfg.MaxRegionMean)
            return Fail(string.Create(CultureInfo.InvariantCulture, $"The picture is almost white here (mean grey {res.RegionMean:0})"),
                "explain.blob.image_bright", ms, graphics, details, values);

        // blobs left out only for touching the area's edge still count against the upper limits: a smear across
        // the edge, or something covering the whole area, must not pass "none allowed"
        var edge = rejected.Where(b => b.RejectReason == 1 && b.Area >= _cfg.MinArea && (_cfg.MaxArea <= 0 || b.Area <= _cfg.MaxArea)).ToList();
        int upperCount = res.Count + res.EdgeCount;
        float upperArea = res.TotalArea + edge.Sum(b => b.Area);

        // which counted blobs break the per-blob area rule, and which their outline rules
        var bad = new bool[blobs.Count];
        var outlineBad = new string?[blobs.Count];
        int firstBad = -1;
        for (int i = 0; i < blobs.Count; i++)
        {
            bad[i] = _cfg.BlobAreaMin is { } lo && blobs[i].Area < lo || _cfg.BlobAreaMax is { } hi && blobs[i].Area > hi;
            if (bad[i] && firstBad < 0) firstBad = i;
            outlineBad[i] = OutlineFault(blobs[i].Contour);
        }
        int firstOutline = Array.FindIndex(outlineBad, o => o is not null);
        var (min, max) = _cfg.CountRange();
        string? explain = null, summary = null;
        if (max is 0 && upperCount > 0)
            (explain, summary) = ("explain.blob.present", res.Count > 0 ? $"Found {upperCount}, none allowed" : $"Found {upperCount} at the area's edge, none allowed");
        else if (res.Count < min && res.Count == 0)
            (explain, summary) = ("explain.blob.none", res.Flat != 0 ? "Nothing found: no contrast in the area" : "Nothing found");
        else if (res.Count < min)
            (explain, summary) = ("explain.blob.too_few", $"Found {res.Count}, at least {min} needed");
        else if (max is { } mx && upperCount > mx)
            (explain, summary) = ("explain.blob.too_many", res.EdgeCount > 0
                ? $"Found {res.Count} and {res.EdgeCount} at the area's edge, at most {mx} allowed" : $"Found {res.Count}, at most {mx} allowed");
        else if (firstBad >= 0)
            (explain, summary) = ("explain.blob.area", string.Create(CultureInfo.InvariantCulture,
                $"Spot {firstBad + 1} has area {blobs[firstBad].Area:0} px, allowed {Range(_cfg.BlobAreaMin, _cfg.BlobAreaMax)}"));
        else if (_cfg.TotalAreaMin is { } ta0 && res.TotalArea < ta0 || _cfg.TotalAreaMax is { } ta1 && upperArea > ta1)
            (explain, summary) = ("explain.blob.total_area", string.Create(CultureInfo.InvariantCulture,
                $"Total area {res.TotalArea:0} px, allowed {Range(_cfg.TotalAreaMin, _cfg.TotalAreaMax)}"));
        else if (firstOutline >= 0)
            (explain, summary) = (outlineBad[firstOutline], OutlineSummary(firstOutline + 1, blobs[firstOutline].Contour!, outlineBad[firstOutline]!));
        else if (_cfg.HasOutlineRules && edge.Count > 0)
            (explain, summary) = ("explain.contour.not_closed", $"{edge.Count} part(s) at the area's edge: outline not checked");
        bool pass = explain is null;

        // kept blobs: green; past the highest count, or outside the area rule: red. Rejected ones: info.
        // with the outline check, the outline is drawn instead of the turned box, and the dent or deviation is marked
        for (int i = 0; i < blobs.Count; i++)
        {
            bool red = bad[i] || outlineBad[i] is not null || max is { } m && i >= m;
            var shape = blobs[i].Contour is { Outline.Length: >= 3 } oc ? oc.Outline : blobs[i].TurnedBox;
            graphics.Add(new Graphic(shape, red ? GraphicKind.Fail : GraphicKind.Ok, (i + 1).ToString(CultureInfo.InvariantCulture)));
            if (outlineBad[i] is { } why && blobs[i].Contour is { } bc && Mark(why, bc) is { } at)
                graphics.Add(new Graphic(Diamond(at, 4), GraphicKind.Fail, why == "explain.contour.dent" ? "dent" : why == "explain.contour.deviation" ? "deviation" : null));
        }
        if (_cfg.CheckOutline && blobs.Count > 0 && blobs.All(b => b.Contour is not null))
        {
            details.Add($"outline values in {UnitName}" + (_cfg.OutlineUnit == BlobOutlineUnit.Px && _mmPerPx <= 0 ? " (the camera has no real size)" : ""));
            values["min_convexity"] = blobs.Min(b => b.Contour!.Convexity);
            values["max_dent"] = blobs.Max(b => b.Contour!.MaxDentDepth) * UnitScale;
            // a blob not compared has no deviation: then no value, never a perfect 0
            if (_cfg.TaughtOutline is not null && blobs.All(b => b.Contour!.MaxDeviation is not null))
                values["max_deviation"] = blobs.Max(b => b.Contour!.MaxDeviation!.Value) * UnitScale;
        }
        bool edgeCounts = max is not null || _cfg.TotalAreaMax is not null || _cfg.HasOutlineRules;
        foreach (var b in edge.Where(_ => edgeCounts && !pass))
            graphics.Add(new Graphic(BoxCorners(b.BoxX, b.BoxY, b.BoxW, b.BoxH), GraphicKind.Fail, "edge"));
        foreach (var b in rejected.Where(b => !(edgeCounts && !pass && edge.Contains(b))).Take(100))
            graphics.Add(new Graphic(BoxCorners(b.BoxX, b.BoxY, b.BoxW, b.BoxH), GraphicKind.Info, RejectLabel(b.RejectReason)));
        values["confidence"] = Confidence(blobs);

        var result = new ToolResult(_cfg.Name, pass, pass ? $"{res.Count} found" : summary!, details, sw.Elapsed.TotalMilliseconds, graphics)
            { Explain = explain, Values = values, Blobs = blobs };
        return result;
    }

    /// <summary>Teaches the outline of counted blob <paramref name="blob"/> (sort order) on this image: the returned
    /// x and y pairs, round the outline's centroid, go into <see cref="BlobToolConfig.TaughtOutline"/>. Null when
    /// that blob is not there, the area is outside the picture, the frame is dark or white, or the outline is cut by
    /// the area's edge or not found on the grey (a wrong reference). Runs with the outline check on.</summary>
    public unsafe float[]? TeachOutline(GrayImage image, int blob = 0, Fixture? fixture = null)
    {
        BlobNative.DefaultContourParams(out var cp);
        cp.Enabled = 1;
        cp.EdgeLevel = _cfg.EdgeLevel ?? -1;
        cp.Smoothing = _cfg.OutlineSmoothing;
        cp.MinDefectDepth = _cfg.MinDefectDepth;
        BlobNative.SetContourParams(_h, ref cp);
        try
        {
            if (!RunNative(image, fixture)) return null;
            BlobNative.GetResult(_h, out var res);
            if (res.Truncated != 0 || res.RegionMean < _cfg.MinRegionMean || res.RegionMean > _cfg.MaxRegionMean) return null;
            var c = new BlobNative.Contour { StructSize = sizeof(BlobNative.Contour) };
            if (BlobNative.GetContour(_h, blob, ref c) != 0 || c.Closed == 0 || c.EdgeShare < _cfg.MinEdgeShare) return null;
            if (BlobNative.TeachOutline(_h, blob) != 0) return null;
            var xy = new float[2 * BlobNative.GetTaughtOutline(_h, null, 0)];
            fixed (float* p = xy) BlobNative.GetTaughtOutline(_h, p, xy.Length / 2);
            return xy;
        }
        finally
        {
            cp.Enabled = _cfg.CheckOutline ? 1 : 0;
            BlobNative.SetContourParams(_h, ref cp);
            SetTaught(_setupError is null ? _cfg.TaughtOutline : null);
        }
    }

    /// <summary>Runs the engine on the tool's area (moved by the fixture); false when the area is cut by the picture's edge.</summary>
    private unsafe bool RunNative(GrayImage image, Fixture? fixture)
    {
        if (_cfg.Roi is { } r1 && !Inside(r1, fixture, image.Width, image.Height)) return false;
        RoiBox? roi = _cfg.Roi is { } r0 && fixture is not null ? fixture.Map(r0, image.Width, image.Height) : _cfg.Roi;
        var p = _p;
        if (roi is { } r) { p.RoiX = r.X; p.RoiY = r.Y; p.RoiW = r.Width; p.RoiH = r.Height; }
        else { p.RoiX = p.RoiY = p.RoiW = p.RoiH = 0; }
        if (BlobNative.SetParams(_h, ref p) != 0) throw new InvalidOperationException("vs_blob_set_params failed");
        fixed (byte* px = image.Pixels)
            if (BlobNative.Run(_h, px, image.Width, image.Height, image.Stride) != 0)
                throw new InvalidOperationException(BlobNative.LastError(_h));
        return true;
    }

    private unsafe BlobContour? ReadContour(int i)
    {
        var c = new BlobNative.Contour { StructSize = sizeof(BlobNative.Contour) };
        if (BlobNative.GetContour(_h, i, ref c) != 0) return null;
        var xy = new float[2 * Math.Max(0, BlobNative.GetOutline(_h, i, null, 0))];
        fixed (float* p = xy) BlobNative.GetOutline(_h, i, p, xy.Length / 2);
        var d = new BlobNative.Defect[Math.Max(0, BlobNative.GetDefects(_h, i, null, 0))];
        fixed (BlobNative.Defect* p = d) BlobNative.GetDefects(_h, i, p, d.Length);
        var pts = new PointF[xy.Length / 2];
        for (int k = 0; k < pts.Length; k++) pts[k] = new PointF(xy[2 * k], xy[2 * k + 1]);
        bool taught = c.Taught != 0;
        return new BlobContour(Simplify(pts, 0.25f), c.Length, c.Area, c.Convexity,
            d.Select(x => new BlobDefect(x.Depth, new PointF(x.X, x.Y), x.Width)).ToList(), c.MaxDefectDepth, new PointF(c.MaxDefectX, c.MaxDefectY),
            c.EdgeShare, c.Closed != 0,
            taught ? c.MaxDeviation : null, taught ? c.MeanDeviation : null, taught ? new PointF(c.MaxDeviationX, c.MaxDeviationY) : null,
            taught ? new PointF(c.PoseX, c.PoseY) : null, taught ? c.PoseAngleDeg : null);
    }

    /// <summary>What <see cref="AutoTolerance"/> suggests, in the config's outline unit.</summary>
    public sealed record OutlineTolerance(float MaxDeviation, float MaxDentDepth, int Images, float WorstDeviation, float WorstDent);

    /// <summary>Auto-tolerance (tool spec 4.2): measures the taught outline's deviation and the dents on good images and
    /// suggests the largest deviation and dent as the worst good value times <paramref name="margin"/>, plus
    /// <paramref name="floorPx"/> (the outline is only good to a fraction of a pixel). Null, never a made-up limit,
    /// when there are under 3 images, the config is not set up, no outline is taught, or any image shows no single
    /// closed, found outline (a missing, cut, broken or doubled part is not a good image). Suggests only: the engineer
    /// accepts it, as a looser limit waits for approval (section 17). <paramref name="fixtures"/>: one per image, where
    /// the camera's Locate tool put the part on it (overrides <paramref name="fixture"/>).</summary>
    public static OutlineTolerance? AutoTolerance(BlobToolConfig cfg, IReadOnlyList<GrayImage> good, BarcodeReading.LensCalibration? calibration = null,
        float margin = 1.5f, float floorPx = 0.3f, Fixture? fixture = null, IReadOnlyList<Fixture?>? fixtures = null)
    {
        if (good.Count < 3 || !(margin >= 1) || !(floorPx >= 0) || cfg.TaughtOutline is null) return null;
        if (fixtures is not null && fixtures.Count != good.Count) return null;
        // measure only: the rules are replaced by loose ones that pass anything, so every image is measured
        var m = new BlobToolConfig();
        foreach (var pr in typeof(BlobToolConfig).GetProperties().Where(pr => pr.CanWrite && pr.GetIndexParameters().Length == 0))
            pr.SetValue(m, pr.GetValue(cfg));
        m.CheckOutline = true;
        m.MaxDeviation = float.MaxValue; m.MaxDentDepth = null; m.MinConvexity = null;
        m.Presence = BlobPresence.Present; m.MinCount = 1; m.MaxCount = 1; m.KeepFirst = 0;
        using var t = new BlobTool(m, calibration);
        if (t._setupError is not null) return null;
        float dev = 0, dent = 0;
        for (int i = 0; i < good.Count; i++)
        {
            var r = t.Run(good[i], new BatchFields(), fixtures is null ? fixture : fixtures[i]);
            if (r.Blobs is not { Count: 1 } b || b[0].Contour is not { Closed: true, MaxDeviation: not null } c || c.EdgeShare < m.MinEdgeShare) return null;
            dev = Math.Max(dev, c.MaxDeviation!.Value);
            dent = Math.Max(dent, c.MaxDentDepth);
        }
        double k = t.UnitScale;
        float F(float px) => (float)((px * margin + floorPx) * k);
        return new OutlineTolerance(F(dev), F(dent), good.Count, (float)(dev * k), (float)(dent * k));
    }

    /// <summary>The explain key of the first outline rule a blob breaks; null when it keeps them all (or none is set).
    /// An outline cut by the area's edge, or not found on the grey, is not checked: NG, never passed.</summary>
    private string? OutlineFault(BlobContour? c)
    {
        if (!_cfg.HasOutlineRules) return null;
        if (c is null || !c.Closed || c.EdgeShare < _cfg.MinEdgeShare) return "explain.contour.not_closed";
        // the engine measures in px: the limits (in the outline unit) are brought to px
        if (_cfg.MaxDeviation is { } dv && (c.MaxDeviation is not { } md || md > dv / UnitScale)) return "explain.contour.deviation";
        if (_cfg.MaxDentDepth is { } dd && c.MaxDentDepth > dd / UnitScale) return "explain.contour.dent";
        if (_cfg.MinConvexity is { } cv && c.Convexity < cv) return "explain.contour.convexity";
        return null;
    }

    private string OutlineSummary(int spot, BlobContour c, string why) => why switch
    {
        "explain.contour.not_closed" => !c.Closed
            ? $"Spot {spot}: outline cut by the area's edge, not checked"
            : string.Create(CultureInfo.InvariantCulture, $"Spot {spot}: outline found on only {c.EdgeShare * 100:0}% of its edge, not checked"),
        "explain.contour.deviation" => string.Create(CultureInfo.InvariantCulture,
            $"Spot {spot}: outline {c.MaxDeviation * UnitScale:0.0##} {UnitName} from the taught one, at most {_cfg.MaxDeviation:0.0##} allowed"),
        "explain.contour.dent" => string.Create(CultureInfo.InvariantCulture,
            $"Spot {spot}: dent of {c.MaxDentDepth * UnitScale:0.0##} {UnitName}, at most {_cfg.MaxDentDepth:0.0##} allowed"),
        _ => string.Create(CultureInfo.InvariantCulture, $"Spot {spot}: convexity {c.Convexity:0.000}, at least {_cfg.MinConvexity:0.000} needed"),
    };

    private static PointF? Mark(string why, BlobContour c) => why switch
    {
        "explain.contour.dent" => c.DentAt,
        "explain.contour.deviation" => c.DeviationAt,
        _ => null,
    };

    private static PointF[] Diamond(PointF c, float r) =>
        new[] { new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y), new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y) };

    /// <summary>Douglas-Peucker on a closed outline, for display only (tool spec 4.2).</summary>
    public static PointF[] Simplify(PointF[] p, float tol)
    {
        if (p.Length < 4) return p;
        // split at the point furthest from the first, then simplify both open halves
        int far = 0;
        float best = -1;
        for (int k = 1; k < p.Length; k++)
        {
            float d = (p[k].X - p[0].X) * (p[k].X - p[0].X) + (p[k].Y - p[0].Y) * (p[k].Y - p[0].Y);
            if (d > best) { best = d; far = k; }
        }
        var keep = new bool[p.Length + 1];
        keep[0] = keep[far] = keep[p.Length] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, far));
        stack.Push((far, p.Length));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            PointF pa = p[a], pb = p[b % p.Length];
            float dx = pb.X - pa.X, dy = pb.Y - pa.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            int at = -1;
            float worst = tol;
            for (int k = a + 1; k < b; k++)
            {
                float d = len > 0 ? MathF.Abs(dx * (p[k].Y - pa.Y) - dy * (p[k].X - pa.X)) / len
                                  : MathF.Sqrt((p[k].X - pa.X) * (p[k].X - pa.X) + (p[k].Y - pa.Y) * (p[k].Y - pa.Y));
                if (d > worst) { worst = d; at = k; }
            }
            if (at < 0) continue;
            keep[at] = true;
            stack.Push((a, at));
            stack.Push((at, b));
        }
        var outPts = new List<PointF>();
        for (int k = 0; k < p.Length; k++) if (keep[k]) outPts.Add(p[k]);
        return outPts.ToArray();
    }

    private unsafe List<BlobNative.Info> Read(bool rejected)
    {
        int n = rejected ? BlobNative.GetRejected(_h, null, 0) : BlobNative.GetBlobs(_h, null, 0);
        var arr = new BlobNative.Info[Math.Max(0, n)];
        fixed (BlobNative.Info* b = arr)
            if (rejected) BlobNative.GetRejected(_h, b, arr.Length); else BlobNative.GetBlobs(_h, b, arr.Length);
        return arr.ToList();
    }

    private ToolResult Fail(string summary, string explain, double ms, IReadOnlyList<Graphic> graphics,
                            IReadOnlyList<string>? details = null, Dictionary<string, double>? values = null) =>
        new(_cfg.Name, false, summary, details ?? Array.Empty<string>(), ms, graphics)
            { Explain = explain, Values = values ?? new Dictionary<string, double>() };

    /// <summary>Tool spec 4.1: 100 x (1 - share of counted blobs within 10% of a filter limit).</summary>
    private float Confidence(IReadOnlyList<BlobInfo> blobs)
    {
        if (blobs.Count == 0) return 100;
        static bool Near(float v, float limit) => limit > 0 && Math.Abs(v - limit) <= 0.1f * limit;
        int close = blobs.Count(b => Near(b.Area, _cfg.MinArea) || Near(b.Area, _cfg.MaxArea)
            || Near(b.Circularity, _cfg.MinCircularity) || Near(b.Circularity, _cfg.MaxCircularity)
            || Near(b.Elongation, _cfg.MinElongation) || Near(b.Elongation, _cfg.MaxElongation));
        return 100f * (1 - (float)close / blobs.Count);
    }

    private static string Range(float? lo, float? hi) => string.Create(CultureInfo.InvariantCulture,
        $"{(lo is { } a ? a.ToString("0", CultureInfo.InvariantCulture) : "0")}–{(hi is { } b ? b.ToString("0", CultureInfo.InvariantCulture) : "any")} px");

    /// <summary>The taught area, moved with the part, lies wholly inside the picture.</summary>
    private static bool Inside(RoiBox r, Fixture? fx, int w, int h)
    {
        var pts = new[] { new PointF(r.X, r.Y), new PointF(r.X + r.Width, r.Y), new PointF(r.X + r.Width, r.Y + r.Height), new PointF(r.X, r.Y + r.Height) };
        if (fx is not null) pts = pts.Select(p => fx.Map(p.X, p.Y)).ToArray();
        return r.Width >= 1 && r.Height >= 1 && pts.All(p => p.X >= -0.5f && p.Y >= -0.5f && p.X <= w + 0.5f && p.Y <= h + 0.5f);
    }

    private static string RejectLabel(int reason) => reason switch
    {
        1 => "edge",
        2 => "small",
        3 => "large",
        4 or 5 => "shape",
        6 or 7 => "length",
        8 => "extra",
        _ => "",
    };

    private static PointF[] BoxCorners(int x, int y, int w, int h) =>
        new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x, y + h) };

    private static BlobInfo ToInfo(BlobNative.Info b, BlobContour? contour)
    {
        // the turned box's corners: the box's width runs along the angle (counter-clockwise on screen)
        double a = b.RboxAngleDeg * Math.PI / 180, c = Math.Cos(a), s = -Math.Sin(a);
        float hw = b.RboxW / 2, hh = b.RboxH / 2;
        PointF Corner(float u, float v) => new((float)(b.RboxCx + u * c - v * s), (float)(b.RboxCy + u * s + v * c));
        return new BlobInfo(b.Area, new PointF(b.Cx, b.Cy), b.Perimeter, b.Circularity, b.Compactness,
            b.MajorAxis, b.MinorAxis, b.Elongation, b.AngleDeg, new RoiBox(b.BoxX, b.BoxY, b.BoxW, b.BoxH),
            new[] { Corner(-hw, -hh), Corner(hw, -hh), Corner(hw, hh), Corner(-hw, hh) }, b.Holes, b.MeanGrey, b.TouchesEdge != 0, contour);
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) BlobNative.Destroy(_h);
        _h = IntPtr.Zero;
    }
}
