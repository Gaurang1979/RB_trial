using System.Text.RegularExpressions;

namespace VisionCore;

/// <summary>
/// Teaching a text tool from what the recogniser reads (Auto find on the Teach page, and the whole-image
/// <see cref="AutoFind"/>): the letter height that reads best, the block of lines, the area and the lines' formats.
/// </summary>
public static class TextTeach
{
    /// <summary>Letter heights tried when the tool has none set (teaching runs once, so time is no issue).</summary>
    public static readonly int[] Heights = { 14, 20, 28, 38, 52, 70, 95 };

    /// <summary>
    /// Reads the tool's area (the whole image when it has none) at several letter heights and keeps the
    /// height that reads the most characters with confidence; a set height is tried first and kept when it
    /// reads well. Only lines of 3 characters or more with quality 60 or more. Throws InvalidOperationException
    /// when a taught font is refused by the engine.
    /// </summary>
    public static List<OcrLine> Read(GrayImage image, TextToolConfig text, ModelPaths models, int timeBudgetMs = 1000, int maxLines = 8)
    {
        List<OcrLine> best = new();
        double bestScore = 0;
        foreach (var h in HeightsFor(text))
        {
            var good = ReadAt(image, text, models, h, timeBudgetMs, maxLines);
            double score = Score(good);
            if (score > bestScore) { bestScore = score; best = good; }
        }
        return best;
    }

    /// <summary>
    /// Every line read at any of the heights, for a whole image with print of several sizes: where lines
    /// read at two heights overlap, the better-read one is kept.
    /// </summary>
    public static List<OcrLine> ReadEvery(GrayImage image, TextToolConfig text, ModelPaths models, int timeBudgetMs = 1000, int maxLines = 32)
    {
        var all = HeightsFor(text).SelectMany(h => ReadAt(image, text, models, h, timeBudgetMs, maxLines))
            .OrderByDescending(l => Score(new[] { l })).ToList();
        var kept = new List<OcrLine>();
        foreach (var l in all)
            if (!kept.Any(k => Overlap(Box(k.Corners), Box(l.Corners)) > 0.5)) kept.Add(l);
        return kept;
    }

    /// <summary>The lines that sit with the best-read one (letter heights within 35%): a block, not stray label print.</summary>
    public static List<OcrLine> MainBlock(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return new();
        var main = lines.OrderByDescending(l => l.Quality * l.Text.Length).First();
        float ch = main.CharHeightPx;
        return lines.Where(l => Math.Abs(l.CharHeightPx - ch) < 0.35 * ch).ToList();
    }

    /// <summary>
    /// Sizes the tool to the lines (letter height, area with room to move) and fills its lines: Read text gets a
    /// format per line and the date rules from the dates read; Check text gets the text with a printed time
    /// near the picture's time as {TIME}. <paramref name="lines"/> must not be empty.
    /// </summary>
    public static void Fill(TextToolConfig text, IReadOnlyList<OcrLine> lines, GrayImage image)
    {
        // the job's letter height is the capital height: about 0.8 of the measured line (capitals + descenders),
        // whichever search height found the block
        text.CharHeightPx = Math.Max(8, (int)Math.Round(0.8 * lines.Select(l => l.CharHeightPx).OrderBy(h => h).ElementAt(lines.Count / 2)));
        // room for the product to move on the line: 15% of the block width + 2 letters at each side, 1.5 letters above and below
        var pts = lines.SelectMany(l => l.Corners).ToList();
        float bx0 = pts.Min(p => p.X), bx1 = pts.Max(p => p.X), by0 = pts.Min(p => p.Y), by1 = pts.Max(p => p.Y);
        double mx = 0.15 * (bx1 - bx0) + 2 * text.CharHeightPx, my = 1.5 * text.CharHeightPx;
        int rx = (int)Math.Max(0, bx0 - mx), ry = (int)Math.Max(0, by0 - my);
        text.Roi = new RoiBox(rx, ry, (int)Math.Min(image.Width - rx, bx1 - bx0 + 2 * mx), (int)Math.Min(image.Height - ry, by1 - by0 + 2 * my));
        var when = image.TakenAt ?? DateTime.Now;
        if (text.Mode == TextMode.Read)
        {
            // Read text: a format per line from what was read; the expiry rule from the taught dates
            // one set of date names for the block: two lines never both get MFD
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var monthLangs = KnownMonthLanguages(text);
            text.Lines = lines.Select(l => new TextLineSpec { Entry = LineEntry.Format, Template = TextFormat.Propose(l.Text, used, monthLangs) }).ToList();
            var formats = text.Lines.Select(l => { try { return TextFormat.Parse(l.Template, monthLangs); } catch (FormatException) { return null; } }).ToList();
            var dates = lines.Zip(formats).Where(p => p.Second is not null).Select(p => p.Second!.Check(p.First.Text))
                .Where(r => r.Ok).SelectMany(r => r.Dates).GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            if (dates.TryGetValue(text.Dates.MfdField, out var mfd) && dates.TryGetValue(text.Dates.ExpField, out var exp))
            {
                int months = (exp.Date.Year * 12 + exp.Date.Month) - (mfd.Date.Year * 12 + mfd.Date.Month);
                if (months > 0) text.Dates.ExpiryMonths = months;
            }
            // a production date printed on the picture's day: the printer's clock is checked from now on
            if (dates.TryGetValue(text.Dates.MfdField, out var made) && made.HasDay &&
                Math.Abs(made.Date.DayNumber - DateOnly.FromDateTime(when).DayNumber) <= 1)
                text.Dates.MfdWithinDays = 2;
        }
        else
        {
            text.Lines = lines.Select(l => new TextLineSpec { Entry = LineEntry.Template, Template = Suggest(l.Text, when) }).ToList();
        }
    }

    /// <summary>The tool's languages that have month names: an unknown code from a job file is left out here
    /// (the setup check reports it) so formats are still proposed.</summary>
    public static List<string> KnownMonthLanguages(TextToolConfig text) =>
        text.MonthLanguages.Where(TextFormat.MonthNames.ContainsKey).ToList();

    /// <summary>A printed time within 15 minutes of the image time becomes a {TIME} field.</summary>
    public static string Suggest(string read, DateTime when)
    {
        var s = Regex.Replace(read, @"\b([01]\d|2[0-3]):([0-5]\d)\b", m =>
        {
            var t = when.Date.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
            return Math.Abs((t - when).TotalMinutes) <= 15 ? "{TIME:HH:mm+-3}" : m.Value;
        });
        return s.Trim();
    }

    private static IEnumerable<int> HeightsFor(TextToolConfig text)
    {
        if (text.CharHeightPx > 0) yield return text.CharHeightPx;
        foreach (var h in Heights) if (h != text.CharHeightPx) yield return h;
    }

    private static List<OcrLine> ReadAt(GrayImage image, TextToolConfig text, ModelPaths models, int height, int timeBudgetMs, int maxLines)
    {
        bool font = text.Reader == OcrReader.Font;
        // teaching only proposes lines for the operator to confirm: no second reader, and a line is judged by its
        // characters as before; the tool that runs on the line judges it with both readers and the line confidence
        using var eng = new OcrEngine(new OcrSettings { Roi = text.Roi, Print = text.Print, CharHeightPx = height, TimeBudgetMs = timeBudgetMs, MaxLines = maxLines, Reader = text.Reader, Agreement = ReadAgreement.Off });
        if (font) eng.SetFont(text.Font);
        else eng.LoadModel(models.OcrNet, models.OcrCharset);
        eng.SetExpected(Array.Empty<string>());
        var r = eng.Run(image.Pixels, image.Width, image.Height, image.Stride);
        return r.Lines.Select(l => l.CutShort || l.Chars.Count == 0 ? l : l with { Quality = l.Chars.Min(c => c.Score) })
            .Where(l => l.Text.Replace(" ", "").Length >= 3 && l.Quality >= 60).ToList();
    }

    private static double Score(IEnumerable<OcrLine> lines) => lines.Sum(l => l.Text.Replace(" ", "").Length * l.Quality / 100.0);

    internal static (float X0, float Y0, float X1, float Y1) Box(IEnumerable<PointF> pts)
    {
        var p = pts as IReadOnlyCollection<PointF> ?? pts.ToList();
        return (p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    // the shared area over the smaller box's area
    private static double Overlap((float X0, float Y0, float X1, float Y1) a, (float X0, float Y0, float X1, float Y1) b)
    {
        double w = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0), h = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
        if (w <= 0 || h <= 0) return 0;
        double small = Math.Min((a.X1 - a.X0) * (a.Y1 - a.Y0), (b.X1 - b.X0) * (b.Y1 - b.Y0));
        return small <= 0 ? 0 : w * h / small;
    }
}
