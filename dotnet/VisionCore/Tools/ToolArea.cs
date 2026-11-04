namespace VisionCore;

/// <summary>
/// The area a tool looks at in one image (requirements 17.10 AUD-02, AUD-04): the taught area moved with the part
/// by the camera's Find the part tool, as Blob, Measure and Code do, and a copy of its pixels, so no tool ever
/// changes the camera's image. An area the part moved out of the picture is not checked: the tool fails (NG).
/// </summary>
public static class ToolArea
{
    public const string OutsideExplain = "explain.area.outside";
    public const string NotTaughtExplain = "explain.reference.not_taught";

    /// <summary>The taught area in this image: moved with the part (the upright box round its turned corners), or
    /// the whole picture when the tool has no area. Null when the moved area is not wholly inside the picture, or
    /// is smaller than <paramref name="minSize"/> px either way.</summary>
    public static RoiBox? Resolve(RoiBox? roi, Fixture? fixture, int width, int height, int minSize = 1)
    {
        if (width < minSize || height < minSize) return null;
        if (roi is null) return new RoiBox(0, 0, width, height);
        if (roi.Width < minSize || roi.Height < minSize) return null;
        var pts = Corners(roi);
        if (fixture is not null) pts = pts.Select(p => fixture.Map(p.X, p.Y)).ToArray();
        if (!pts.All(p => p.X >= -0.5f && p.Y >= -0.5f && p.X <= width + 0.5f && p.Y <= height + 0.5f)) return null;
        var box = fixture is null ? roi : fixture.Map(roi, width, height);
        return box.Width >= minSize && box.Height >= minSize ? box : null;
    }

    /// <summary>For a tool compared with a taught reference of the area's size (Inspect, Pouch Alignment, phase):
    /// the taught size, centred on where the taught area's centre is now. Null when that lies outside the picture.</summary>
    public static RoiBox? ResolveTaughtSize(RoiBox? roi, Fixture? fixture, int width, int height)
    {
        if (roi is null) return new RoiBox(0, 0, width, height);
        if (roi.Width < 1 || roi.Height < 1) return null;
        float cx = roi.X + roi.Width / 2f, cy = roi.Y + roi.Height / 2f;
        if (fixture is not null) (cx, cy) = fixture.Map(cx, cy);
        int x0 = (int)MathF.Round(cx - roi.Width / 2f), y0 = (int)MathF.Round(cy - roi.Height / 2f);
        if (x0 < 0 || y0 < 0 || x0 + roi.Width > width || y0 + roi.Height > height) return null;
        return new RoiBox(x0, y0, roi.Width, roi.Height);
    }

    /// <summary>A new array with the area's pixels, row by row (stride = the area's width).</summary>
    public static byte[] Copy(GrayImage image, RoiBox a)
    {
        var px = new byte[a.Width * a.Height];
        for (int y = 0; y < a.Height; y++)
            Buffer.BlockCopy(image.Pixels, (a.Y + y) * image.Stride + a.X, px, y * a.Width, a.Width);
        return px;
    }

    public static PointF[] Corners(RoiBox a) =>
        new[] { new PointF(a.X, a.Y), new PointF(a.X + a.Width, a.Y), new PointF(a.X + a.Width, a.Y + a.Height), new PointF(a.X, a.Y + a.Height) };

    /// <summary>NG for an area the part moved out of the picture (or too small for the tool).</summary>
    public static ToolResult Outside(ToolConfig tool, double ms, int minSize = 1) =>
        new(tool.Name, false, "The area to check is outside the picture, so it was not checked",
            new[] { minSize > 1 ? $"✗ The area, moved with the part, must lie wholly in the picture and be at least {minSize}×{minSize} px" : "✗ The area, moved with the part, must lie wholly in the picture" },
            ms, Array.Empty<Graphic>())
        { Explain = OutsideExplain };

    /// <summary>NG for a tool with no taught reference, or one whose size does not match its area (AUD-01).</summary>
    public static ToolResult NotTaught(ToolConfig tool, string why, double ms) =>
        new(tool.Name, false, "Not taught: " + why, new[] { "✗ Teach the reference in Teach and save the job" }, ms, Array.Empty<Graphic>())
        { Explain = NotTaughtExplain };

    /// <summary>
    /// True when the tool's result decides the part's OK/NG (AUD-03). Report-only Read text, image corrections
    /// (inpaint, rectify, photometric virtual light) and advisory tools (auto-tune, drift and the robustness check with
    /// their fail option off) do not: a camera needs at least one tool that checks the part. Their failures still
    /// fail the part.
    /// </summary>
    public static bool Decides(ToolConfig tool) => tool switch
    {
        TextToolConfig { Mode: TextMode.Read, ReportOnly: true } => false,
        InpaintToolConfig or RectifyToolConfig or AutotuneToolConfig => false,
        PhotometricToolConfig { Mode: PhotometricMode.VirtualLighting } => false,
        DriftToolConfig { FailOnDrift: false } => false,
        MetamorphicToolConfig { FailOnNonRobust: false } => false,
        _ => true,
    };
}

/// <summary>
/// A tool compared with a taught reference picture of its area (requirements 17.10 AUD-01): Pouch Alignment and
/// phase correlation. The reference is cut from the teach image in Teach and saved with the job, as the Find the
/// part pattern is (jobs/assets/&lt;job&gt;/&lt;tool&gt;.v&lt;version&gt;.bmp); a part on the line never becomes it.
/// </summary>
public interface ITaughtReference
{
    string Name { get; }
    bool Enabled { get; }
    RoiBox? Roi { get; }
    /// <summary>The reference picture file, next to the job (jobs/assets/&lt;job&gt;/...); null = not taught.</summary>
    string? ReferenceFile { get; set; }
    /// <summary>The reference pixels (8-bit grey, row by row) while teaching and after loading.</summary>
    byte[]? Reference { get; set; }
    int ReferenceWidth { get; set; }
    int ReferenceHeight { get; set; }
}

public static class TaughtReference
{
    /// <summary>Cuts the reference from the teach image at the tool's area (the whole picture when it has none).</summary>
    public static void Teach(ITaughtReference tool, GrayImage image, int minSize)
    {
        var a = ToolArea.Resolve(tool.Roi, null, image.Width, image.Height, minSize)
            ?? throw new InvalidOperationException($"Mark an area of at least {minSize}×{minSize} px inside the picture first");
        tool.Reference = ToolArea.Copy(image, a);
        tool.ReferenceWidth = a.Width;
        tool.ReferenceHeight = a.Height;
    }

    /// <summary>Why the tool cannot run (no reference, or one not the size of its area); null when it can.</summary>
    public static string? Problem(ITaughtReference tool)
    {
        if (tool.Reference is not { } px || tool.ReferenceWidth <= 0 || tool.ReferenceHeight <= 0 || px.Length < tool.ReferenceWidth * tool.ReferenceHeight)
            return "no reference taught. Teach the reference from a good part";
        if (tool.Roi is { } r && (r.Width != tool.ReferenceWidth || r.Height != tool.ReferenceHeight))
            return $"the reference taught is {tool.ReferenceWidth}×{tool.ReferenceHeight} px but the area is {r.Width}×{r.Height} px. Teach the reference again";
        return null;
    }
}
