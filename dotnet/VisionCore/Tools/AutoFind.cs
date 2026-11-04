using System.Text.RegularExpressions;
using BarcodeReading;

namespace VisionCore;

public enum SuggestionKind { Code, Text }

/// <summary>
/// One tool Auto find proposes (requirement AS-01): the tool, already set up from what was read, where it
/// lies, a short sentence and what was read (one entry per code or text line) with each line's field type.
/// </summary>
public sealed record Suggestion(SuggestionKind Kind, ToolConfig Tool, PointF[] Outline, string Summary,
                                IReadOnlyList<string> Read, IReadOnlyList<string> Fields);

/// <summary>
/// Auto find on the whole image (requirements AS-01): every code with its type and every block of text with
/// each line's field type, with nothing drawn. These are suggestions only: nothing is added to a job until the
/// user accepts one (line safety, AS-12).
/// </summary>
public static class AutoFind
{
    /// <summary>
    /// One suggestion per code type (a Read code tool locked to that type and count, <see cref="CodeTeach.Lock"/>)
    /// and one per block of text lines (a Read text tool with a format per line, <see cref="TextTeach.Fill"/>);
    /// text only when the OCR model is there. Codes first, then text from top to bottom. With <paramref name="area"/>
    /// (the window the user dragged) only what lies in it is looked for and offered: a code or line counts as in it
    /// when its centre is.
    /// </summary>
    public static List<Suggestion> Suggest(GrayImage image, ModelPaths models, int timeBudgetMs = 3000, RoiBox? area = null)
    {
        var found = new List<Suggestion>();
        area = Clip(area, image.Width, image.Height);
        // every type and every code (count 0: the reader does not stop at the first)
        var probe = new CodeToolConfig { Name = "probe", Format = CodeFormat.Any, ExpectedCount = 0, TimeBudgetMs = timeBudgetMs, Roi = area };
        IReadOnlyList<CodeRead> codes;
        using (var tool = new CodeTool(probe, models, teaching: true))
            codes = tool.Run(image, new BatchFields()).Codes ?? Array.Empty<CodeRead>();
        codes = codes.Where(c => Inside(c.Corners, area)).ToList();
        foreach (var g in codes.GroupBy(c => c.Format).OrderByDescending(g => g.Count()))
        {
            var mine = g.ToList();
            var cfg = new CodeToolConfig { Name = CodeFormats.Name(g.Key), Format = CodeFormat.Any };
            CodeTeach.Lock(cfg, mine.Select(c => (c.Format, c.Text)).ToList());
            var pts = mine.SelectMany(c => c.Corners).ToList();
            cfg.Roi = Pad(pts, 0.25, image.Width, image.Height);
            found.Add(new Suggestion(SuggestionKind.Code, cfg, Outline(pts),
                $"{CodeFormats.Name(g.Key)} x {mine.Count}", mine.Select(c => c.Text).ToList(), mine.Select(_ => "code").ToList()));
        }

        if (!File.Exists(models.OcrNet)) return found;
        var text = new TextToolConfig { Mode = TextMode.Read, Roi = area };
        var lines = TextTeach.ReadEvery(image, text, models).Where(l => Inside(l.Corners, area)).ToList();
        // the digits printed under a barcode are part of the code, not a text field
        var codeBoxes = codes.Select(c => TextTeach.Box(c.Corners)).Select(b =>
        {
            float m = 0.25f * Math.Max(b.X1 - b.X0, b.Y1 - b.Y0);
            return (X0: b.X0 - m, Y0: b.Y0 - m, X1: b.X1 + m, Y1: b.Y1 + m);
        }).ToList();
        lines = lines.Where(l =>
        {
            var b = TextTeach.Box(l.Corners);
            float cx = (b.X0 + b.X1) / 2, cy = (b.Y0 + b.Y1) / 2;
            return !codeBoxes.Any(c => cx >= c.X0 && cx <= c.X1 && cy >= c.Y0 && cy <= c.Y1);
        }).ToList();
        int n = 0;
        foreach (var block in Blocks(lines))
        {
            var cfg = new TextToolConfig { Name = $"Text {++n}", Mode = TextMode.Read };
            TextTeach.Fill(cfg, block, image);
            var fields = block.Zip(cfg.Lines).Select(p => LineField(p.First.Text, p.Second.Template)).ToList();
            found.Add(new Suggestion(SuggestionKind.Text, cfg, Outline(block.SelectMany(l => l.Corners)),
                $"Text, {block.Count} line{(block.Count == 1 ? "" : "s")}: {string.Join(", ", fields.Distinct())}",
                block.Select(l => l.Text).ToList(), fields));
        }
        return found;
    }

    /// <summary>
    /// Lines grouped into blocks, each block top to bottom and the blocks top to bottom: two lines are in one
    /// block when their letter heights are within 35%, the gap between them is under 1.5 letter heights up or
    /// down, and they overlap side to side or lie under 2 letter heights apart.
    /// </summary>
    public static List<List<OcrLine>> Blocks(IReadOnlyList<OcrLine> lines)
    {
        int count = lines.Count;
        var parent = Enumerable.Range(0, count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var boxes = lines.Select(l => TextTeach.Box(l.Corners)).ToList();
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                float hi = lines[i].CharHeightPx, hj = lines[j].CharHeightPx, h = Math.Max(hi, hj);
                if (Math.Abs(hi - hj) >= 0.35 * h) continue;
                var a = boxes[i]; var b = boxes[j];
                float gapY = Math.Max(a.Y0, b.Y0) - Math.Min(a.Y1, b.Y1);
                float gapX = Math.Max(a.X0, b.X0) - Math.Min(a.X1, b.X1);
                if (gapY < 1.5 * h && gapX < 2 * h) parent[Root(i)] = Root(j);
            }
        return Enumerable.Range(0, count).GroupBy(Root)
            .Select(g => g.OrderBy(i => boxes[i].Y0).Select(i => lines[i]).ToList())
            .OrderBy(b => b.Min(l => TextTeach.Box(l.Corners).Y0)).ToList();
    }

    // a label on its own anywhere, or at the line's start run into the number (the reader often drops
    // the dot and space: "B.NO. AB123" read as "B.NOAB123")
    private static readonly Regex Lot = new(@"(?<![\p{L}\d])(LOT|BATCH|B\.?\s?NO|BN|B/N)(?![\p{L}\d])|^\W*(LOT|BATCH|B\.?\s?NO\.?|B/N)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Price = new(@"(?<![\p{L}\d])(MRP|RS|INR|PRICE)(?![\p{L}\d])|₹", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Time = new(@"(?<!\d)([01]?\d|2[0-3]):[0-5]\d(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The field type of a text line from what was read and its proposed format (<see cref="TextFormat.Propose"/>):
    /// production date, expiry date, date, time, lot / batch, price, number / code or text.
    /// </summary>
    public static string LineField(string read, string template)
    {
        if (template.Contains("{MFD", StringComparison.OrdinalIgnoreCase)) return "production date";
        if (template.Contains("{EXP", StringComparison.OrdinalIgnoreCase) || template.Contains("{LIFE}", StringComparison.Ordinal)) return "expiry date";
        if (template.Contains("{DATE", StringComparison.OrdinalIgnoreCase)) return "date";
        if (template.Contains("{TIME", StringComparison.OrdinalIgnoreCase)) return "time";
        if (template.Contains("{MRP", StringComparison.Ordinal) || template.Contains("{PRICE}", StringComparison.Ordinal)) return "price";
        if (Lot.IsMatch(read)) return "lot / batch";
        if (Price.IsMatch(read)) return "price";
        if (Time.IsMatch(read)) return "time";
        if (template.Contains("{D:") || template.Contains("{X:")) return "number / code";
        return "text";
    }

    /// <summary>The points' box with a margin of <paramref name="frac"/> of its longer side, kept inside the image.</summary>
    public static RoiBox Pad(IReadOnlyList<PointF> pts, double frac, int width, int height)
    {
        float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        double m = frac * Math.Max(x1 - x0, y1 - y0);
        int X = (int)Math.Max(0, x0 - m), Y = (int)Math.Max(0, y0 - m);
        int W = (int)Math.Min(width - X, x1 - x0 + 2 * m), H = (int)Math.Min(height - Y, y1 - y0 + 2 * m);
        return new RoiBox(X, Y, W, H);
    }

    // the dragged window kept inside the image; null (the whole image) when nothing of it is left
    private static RoiBox? Clip(RoiBox? a, int width, int height)
    {
        if (a is null) return null;
        int x0 = Math.Clamp(a.X, 0, width), y0 = Math.Clamp(a.Y, 0, height);
        int x1 = Math.Clamp(a.X + a.Width, 0, width), y1 = Math.Clamp(a.Y + a.Height, 0, height);
        return x1 - x0 < 8 || y1 - y0 < 8 ? null : new RoiBox(x0, y0, x1 - x0, y1 - y0);
    }

    private static bool Inside(IReadOnlyList<PointF> pts, RoiBox? a)
    {
        if (a is null || pts.Count == 0) return true;
        float cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        return cx >= a.X && cx <= a.X + a.Width && cy >= a.Y && cy <= a.Y + a.Height;
    }

    private static PointF[] Outline(IEnumerable<PointF> pts)
    {
        var b = TextTeach.Box(pts);
        return new[] { new PointF(b.X0, b.Y0), new PointF(b.X1, b.Y0), new PointF(b.X1, b.Y1), new PointF(b.X0, b.Y1) };
    }
}
