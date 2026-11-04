using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// A tool dragged from the toolbox onto the picture (UI-UX-SPEC.md 5 "Drop on image", requirement AS-05).
/// The tool is made with its area centred on the drop point, then taught there with the runtime's Teach
/// (the same call as the Teach button): a Read code area snaps to the code nearest the drop point, a Read
/// text or Check text area to the block of text there. The taught tool is tried on the picture at once.
/// Where nothing is found the area stays where it was dropped and the tool shows "!" with a hint; the
/// tool's own try then fails, so nothing unsure passes (AS-12). Only the draft changes, until Save.
/// </summary>
public sealed partial class DesignerViewModel
{
    /// <summary>A dropped text tool is taught again in the area it was given while that area reaches past
    /// where it looked (a line cut at the edge); this many teaches at most.</summary>
    public const int TextTeaches = 3;

    /// <summary>Tools dropped where nothing was found (by camera and name), with the area they were left: the tree row
    /// shows "!" and Save waits while the tool keeps that area (until an area is drawn or the tool is taught).</summary>
    private readonly Dictionary<(int Camera, string Name), (int X, int Y, int Width, int Height)> _notSnapped = [];

    private bool NotSnapped(ToolDraft tool) => _notSnapped.TryGetValue((tool.Camera, tool.Name), out var left) && tool.Roi == left;

    /// <summary>A dropped tool's area snapped onto what it found (pixels of the picture shown): the view flashes it once.</summary>
    public event Action<Point2, Point2>? AreaSnapped;

    private enum Taught { Found, Nothing, Stopped }

    /// <summary>The area a tool dropped at this point would start with, both in pixels of the picture shown (JPEG,
    /// or camera once the full picture is shown: <see cref="ShownScale"/>); the view's dashed preview while
    /// dragging; null with no picture shown.</summary>
    public (Point2 From, Point2 To)? DropPreview(ToolKind kind, Point2 at)
    {
        if (_last is not { Scale: > 0 } shown) return null;
        var s = ShownScale;
        var (w, h) = FullSize(shown);
        var a = DefaultArea(kind, at.X / s, at.Y / s, w, h);
        return (new Point2(a.X * s, a.Y * s), new Point2((a.X + a.Width) * s, (a.Y + a.Height) * s));
    }

    /// <summary>The area a dropped tool starts with, centred on the drop point (full-image pixels) and kept inside
    /// the picture: a square a quarter of the picture's short side for a code or a Blob tool, a third of its width by a sixth of
    /// its height for text (a line or a few).</summary>
    public static (int X, int Y, int Width, int Height) DefaultArea(ToolKind kind, double x, double y, int width, int height)
    {
        var side = Math.Min(width, height) / 4.0;
        var (w, h) = kind is ToolKind.ReadCode or ToolKind.Blob ? (side, side) : (width / 3.0, height / 6.0);
        return Centred(x, y, w, h, width, height);
    }

    /// <summary>Where a dropped tool looks for its code or text: its starting area twice as wide and high about
    /// the same centre, kept inside the picture.</summary>
    public static (int X, int Y, int Width, int Height) SearchArea((int X, int Y, int Width, int Height) a, int width, int height) =>
        Centred(a.X + a.Width / 2.0, a.Y + a.Height / 2.0, 2.0 * a.Width, 2.0 * a.Height, width, height);

    private static (int X, int Y, int Width, int Height) Centred(double x, double y, double w, double h, int width, int height)
    {
        w = Math.Min(w, width);
        h = Math.Min(h, height);
        var x0 = Math.Clamp(x - w / 2, 0, width - w);
        var y0 = Math.Clamp(y - h / 2, 0, height - h);
        return ((int)x0, (int)y0, (int)w, (int)h);
    }

    private static (int Width, int Height) FullSize(TryOutcome r) => r.ImageWidth > 0 && r.ImageHeight > 0
        ? (r.ImageWidth, r.ImageHeight) : ((int)(r.Width / r.Scale), (int)(r.Height / r.Scale));

    /// <summary>A toolbox tool dropped on the picture at a point in pixels of the picture shown; one undo step
    /// with its snapping, so Ctrl+Z takes the dropped tool back whole (AS-18).</summary>
    public Task DropToolAsync(ToolKind kind, Point2 at) => OneStepAsync(() => DropAsync(kind, at));

    private async Task DropAsync(ToolKind kind, Point2 at)
    {
        if (Busy || Job is null || CameraIndex < 0) return;
        if (_last is not { Scale: > 0 } shown || PictureIndex < 0) { Say("design.drop.try_first", Tone.Warn); return; }
        var (width, height) = FullSize(shown);
        double x = at.X / ShownScale, y = at.Y / ShownScale;
        at = new Point2(x * shown.Scale, y * shown.Scale);   // the snap compares with the try's graphics: JPEG pixels
        if (x < 0 || y < 0 || x >= width || y >= height) return;   // beside the picture: not a drop
        var area = DefaultArea(kind, x, y, width, height);
        AddTool(kind);
        var tool = SelectedTool!;
        var name = tool.Name;
        tool.Roi = area;
        if (kind == ToolKind.Blob)
        {
            // a Blob tool has nothing to snap to: it counts what is in the area it was dropped with (BLB-OUT-03)
            Changed();
            DrawGraphics();
            await RunAsync(null);
            return;
        }
        Taught found;
        string? why;
        Busy = true;
        try
        {
            (found, why) = kind == ToolKind.ReadCode
                ? await SnapCodeAsync(name, at, area, SearchArea(area, width, height))
                : await SnapTextAsync(name, area, SearchArea(area, width, height));
            if (found != Taught.Found && ToolNamed(name) is { } left) left.Roi = area;   // where it was dropped
        }
        finally { Busy = false; }
        if (found == Taught.Nothing && ToolNamed(name) is { } unsnapped) _notSnapped[(unsnapped.Camera, unsnapped.Name)] = area;
        Changed();
        DrawGraphics();
        if (found == Taught.Found)
        {
            // a later teach that was refused keeps the first one's area, with the refusal said: no flash then
            if (MessageTone != Tone.Ng && ToolNamed(name)?.Roi is { } a && _last is { Scale: > 0 })
            {
                var s = ShownScale;
                AreaSnapped?.Invoke(new Point2(a.X * s, a.Y * s), new Point2((a.X + a.Width) * s, (a.Y + a.Height) * s));
            }
            return;
        }
        if (found == Taught.Stopped) return;   // not connected or refused: said already, nothing tried
        await RunAsync(null);   // the tool tried where it was dropped
        // the try's own refusal first (a Read text without lines makes the job invalid), then why Teach found nothing
        var refused = MessageTone == Tone.Ng ? Message : null;
        var hint = _strings[kind == ToolKind.ReadCode ? "design.drop.left_code" : "design.drop.left_text"];
        SayText(string.Join(" ", new[] { refused, why, hint }.Where(t => !string.IsNullOrWhiteSpace(t))), refused is null ? Tone.Warn : Tone.Ng);
    }

    /// <summary>Save and Save as wait while a dropped tool still shows "!": its area is a guess, and a Check text tool
    /// without lines would pass any clean print (AS-12).</summary>
    private bool DroppedToolUnsure()
    {
        if (Job?.Tools.FirstOrDefault(NotSnapped) is not { } tool) return false;
        SayText(_strings.Format("design.drop.save_first", tool.Name), Tone.Warn);
        return true;
    }

    /// <summary>Taught in the starting area, else in the search area; when that holds several codes, taught again on the
    /// one nearest the drop point alone (its own type, count and rules, nothing kept from the codes round it).</summary>
    private async Task<(Taught, string?)> SnapCodeAsync(string name, Point2 at, (int X, int Y, int Width, int Height) area,
        (int X, int Y, int Width, int Height) search)
    {
        var (found, r, why) = await TeachInAsync(name, area);
        if (found == Taught.Nothing) (found, r, why) = await TeachInAsync(name, search);
        if (found != Taught.Found || ToolNamed(name) is not { ExpectedCount: > 1, Roi: { } roi } tool || NearestCode(r!, roi, at) is not { } one)
            return (found, why);
        var before = Job!.ToJson();
        tool.Gs1Check = false;   // the GS1 rules common to all the codes are weaker than this code's own
        tool.Expected = "";
        var (again, _, _) = await TeachInAsync(name, one);
        if (again != Taught.Found) Restore(before, name, r!, say: again == Taught.Nothing);   // the codes found first stay
        return (Taught.Found, null);
    }

    /// <summary>Taught in the starting area, else in the search area; then again in the area taught while that reaches
    /// past where it looked (a line cut at the edge).</summary>
    private async Task<(Taught, string?)> SnapTextAsync(string name, (int X, int Y, int Width, int Height) area,
        (int X, int Y, int Width, int Height) search)
    {
        var looked = area;
        var (found, r, why) = await TeachInAsync(name, area);
        if (found == Taught.Nothing) (found, r, why) = await TeachInAsync(name, looked = search);
        if (found != Taught.Found) return (found, why);
        for (var i = 1; i < TextTeaches && ToolNamed(name)?.Roi is { } roi && !Inside(roi, looked); i++)
        {
            var before = Job!.ToJson();
            looked = roi;
            var (again, next, _) = await TeachInAsync(name, looked);
            if (again != Taught.Found) { Restore(before, name, r!, say: again == Taught.Nothing); break; }
            r = next;
        }
        return (Taught.Found, null);
    }

    /// <summary>The named tool taught in an area on the picture shown: the taught draft replaces the draft and its try
    /// is shown. Nothing: nothing to teach was found there (the draft is as it was). Stopped: not connected or refused.</summary>
    private async Task<(Taught, TryOutcome?, string?)> TeachInAsync(string name, (int X, int Y, int Width, int Height) area)
    {
        if (ToolNamed(name) is not { } tool) return (Taught.Stopped, null, null);
        tool.Roi = area;
        var request = new TryRequest(Job!.ToJson(), CameraIndex, Stored: Pictures[PictureIndex], TeachTool: name, Batch: ParseBatch(TryBatch),
            PlcText: ParsePlcText(TryBatch));
        var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.TryJobAsync(request, ct), TryLimit);
        if (!reached) { Say("run.reason.not_connected", Tone.Ng); return (Taught.Stopped, null, null); }
        if (r is not { Ok: true })
        {
            // Teach found nothing to teach here; its reason also names a missing reading model
            if (r?.Refused?.ReasonKey == "runtime.refused.teach") return (Taught.Nothing, null, r.Refused.ReasonText);
            Refused(r?.Refused);
            return (Taught.Stopped, null, null);
        }
        if (r.TaughtJson.Length > 0) ApplyTaught(r.TaughtJson, name);
        Show(r);
        SayText(r.TaughtText, Tone.Ok);
        return (Taught.Found, r, null);
    }

    /// <summary>Back to a draft taught before, with its try shown; its sentence said again unless a refusal is showing.</summary>
    private void Restore(string json, string name, TryOutcome tried, bool say)
    {
        ApplyTaught(json, name);
        Show(tried);
        if (say) SayText(tried.TaughtText, Tone.Ok);
    }

    private ToolDraft? ToolNamed(string name) => CameraTools.FirstOrDefault(t => t.Name == name);

    /// <summary>The code read inside the area (full-image pixels) nearest the drop point (JPEG pixels), as an area
    /// round it with a quarter of its size spare at each side, as Teach leaves round a code; null when none is placed.</summary>
    private static (int X, int Y, int Width, int Height)? NearestCode(TryOutcome r, (int X, int Y, int Width, int Height) roi, Point2 at)
    {
        var s = r.Scale;
        var texts = (r.Codes ?? []).Select(c => c.Text).ToHashSet();
        var best = (r.Graphics ?? [])
            .Where(g => g.Kind == GraphicKind.Polygon && g.Points.Count > 0 && g.Label is { } l && texts.Contains(l))
            .Select(g => (X0: g.Points.Min(p => p.X), Y0: g.Points.Min(p => p.Y), X1: g.Points.Max(p => p.X), Y1: g.Points.Max(p => p.Y)))
            .Where(b => Inside(((b.X0 + b.X1) / 2 / s, (b.Y0 + b.Y1) / 2 / s), roi))
            .OrderBy(b => Gap(b, at)).ThenBy(b => Math.Abs((b.X0 + b.X1) / 2 - at.X) + Math.Abs((b.Y0 + b.Y1) / 2 - at.Y))
            .Select(b => ((double X0, double Y0, double X1, double Y1)?)b).FirstOrDefault();
        if (best is not { } b) return null;
        var (w, h) = FullSize(r);
        double x0 = b.X0 / s, y0 = b.Y0 / s, x1 = b.X1 / s, y1 = b.Y1 / s;
        var m = 0.25 * Math.Max(x1 - x0, y1 - y0);
        int ax = (int)Math.Max(0, x0 - m), ay = (int)Math.Max(0, y0 - m);
        return (ax, ay, (int)Math.Min(w - ax, x1 - x0 + 2 * m), (int)Math.Min(h - ay, y1 - y0 + 2 * m));
    }

    /// <summary>How far a point is from a box (0 inside it).</summary>
    private static double Gap((double X0, double Y0, double X1, double Y1) b, Point2 p)
    {
        double dx = Math.Max(0, Math.Max(b.X0 - p.X, p.X - b.X1)), dy = Math.Max(0, Math.Max(b.Y0 - p.Y, p.Y - b.Y1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool Inside((double X, double Y) p, (int X, int Y, int Width, int Height) a) =>
        p.X >= a.X && p.Y >= a.Y && p.X <= a.X + a.Width && p.Y <= a.Y + a.Height;

    private static bool Inside((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b) =>
        a.X >= b.X && a.Y >= b.Y && a.X + a.Width <= b.X + b.Width && a.Y + a.Height <= b.Y + b.Height;
}
