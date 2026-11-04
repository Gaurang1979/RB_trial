using System.Globalization;
using System.Text.RegularExpressions;
using VisionCore;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// "Teach" in the job designer (UI-UX-SPEC.md 4.8): sets one tool up from a good part's picture,
/// as the Teach page of the browser Vision Studio does (Teach.razor AutoFindCode, AutoFindText).
/// Read code: every type is looked for and the tool is locked to what was read (CodeTeach.Lock:
/// type, count, the length of a 1D code, GS1 rules), with its area around the codes. Read text and
/// Check text (the AI reader): the text is read at several letter heights, the block found sizes
/// the letter height and the area, and the lines are filled in (formats for Read text, the printed
/// text for Check text). Returns a sentence for the engineer, or throws InvalidOperationException
/// with one when nothing could be taught.
/// </summary>
public static class ToolTeach
{
    /// <param name="job">The draft the tool is in: a Blob tool reads its camera's calibration and Locate tool there.</param>
    /// <param name="spot">A Blob tool: the spot whose outline is taught, by its number as drawn (1 = the first); other tools ignore it.</param>
    public static string Teach(ToolConfig tool, GrayImage image, ModelPaths models, Job? job = null, int spot = 1) => tool switch
    {
        CodeToolConfig code => Code(code, image, models),
        TextToolConfig text => Text(text, image, models),
        BlobToolConfig blob => Blob(blob, image, job, spot),
        _ => throw new InvalidOperationException($"{tool.Name}: this tool is not taught from a picture here."),
    };

    /// <summary>Where a deviation limit starts when the outline is taught and none is set (px); Auto tolerance then sets it
    /// from good parts (requirements 17.12 BLB-OUT-04).</summary>
    public const float StartDeviationPx = 2;

    /// <summary>Blob (BLB-OUT-04, BLB-OUT-07): the outline check on, and the outline of counted spot <paramref name="spot"/>
    /// (1 = the first, as numbered on the picture) taught. A deviation limit not yet set starts at <see cref="StartDeviationPx"/>
    /// and a part is required, so the taught tool never passes any shape (the setup check refuses a taught outline with no
    /// limit, and outline rules with no part). A spot that is not there is refused, never another one taught.</summary>
    static string Blob(BlobToolConfig blob, GrayImage image, Job? job, int spot)
    {
        if (spot < 1) throw new InvalidOperationException($"There is no spot {spot}: spots are numbered from 1.");
        var calibration = job?.Cameras.ElementAtOrDefault(blob.Camera)?.Calibration;
        var mm = blob.OutlineUnit == BlobOutlineUnit.Mm;
        if (mm && !(calibration is { MmPerPx: > 0 } && float.IsFinite(calibration.MmPerPx)))
            throw new InvalidOperationException("Outline limits in mm need the camera's calibration. Calibrate the camera, or use px.");
        var probe = MeasureCopy(blob);
        if (BlobTool.SetupProblem(probe, calibration) is { } problem) throw new InvalidOperationException($"{blob.Name}: {problem}.");
        var fixture = Locate(job, blob.Camera, image);
        using var tool = new BlobTool(probe, calibration);
        if (spot > 1 && tool.Run(image, new BatchFields(), fixture).Blobs is { } found && found.Count < spot)
            throw new InvalidOperationException($"There is no spot {spot} on this picture: {found.Count} found. Try the picture again and click a numbered spot.");
        var xy = tool.TeachOutline(image, spot - 1, fixture)
            ?? throw new InvalidOperationException("No whole outline was found here. Put a good part under the camera, draw the area round it, or set the threshold so the part is one spot.");
        blob.CheckOutline = true;
        blob.TaughtOutline = xy;
        blob.MaxDeviation ??= mm ? StartDeviationPx * calibration!.MmPerPx : StartDeviationPx;
        if (blob.CountRange().Min < 1) blob.Presence = BlobPresence.Present;
        var unit = mm ? "mm" : "px";
        return string.Create(CultureInfo.InvariantCulture,
            $"Outline taught from spot {spot} ({xy.Length / 2} points); largest deviation {blob.MaxDeviation:0.###} {unit}. Use Auto tolerance on good pictures to set the limits.");
    }

    /// <summary>A Blob tool's settings with the outline check on and no outline rules, so the outline of any spot is measured
    /// (as <see cref="BlobTool.AutoTolerance"/> measures): for teaching, and to say why a suggestion cannot be made.</summary>
    public static BlobToolConfig MeasureCopy(BlobToolConfig blob)
    {
        var m = new BlobToolConfig();
        foreach (var p in typeof(BlobToolConfig).GetProperties().Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
            p.SetValue(m, p.GetValue(blob));
        m.CheckOutline = true;
        m.TaughtOutline = null;
        m.MaxDeviation = null; m.MaxDentDepth = null; m.MinConvexity = null;
        m.KeepFirst = 0;
        return m;
    }

    /// <summary>Where the camera's Locate tool finds the part on the picture, as the line runs it (JobRunner: the first
    /// enabled Locate tool of the camera); null with no Locate tool. Throws when it does not find the part.</summary>
    public static Fixture? Locate(Job? job, int camera, GrayImage image)
    {
        if (job?.Tools.OfType<LocateToolConfig>().FirstOrDefault(t => t.Enabled && t.Camera == camera) is not { } cfg) return null;
        using var locate = new LocateTool(cfg);
        var (_, fixture) = locate.Find(image);
        return fixture ?? throw new InvalidOperationException($"{cfg.Name} did not find the part on this picture.");
    }

    static string Code(CodeToolConfig code, GrayImage image, ModelPaths models)
    {
        // every type and every code in the area (count 0: the reader does not stop at the first)
        var probe = new CodeToolConfig { Name = code.Name, Format = BarcodeReading.CodeFormat.Any, ExpectedCount = 0, Roi = code.Roi, TimeBudgetMs = 1500 };
        using var tool = new CodeTool(probe, models, teaching: true);
        var r = tool.Run(image, new BatchFields());
        var mine = CodeTeach.Lock(code, tool.LastCodes);
        if (mine.Count == 0) throw new InvalidOperationException("No code was read here. Put a good part under the camera, or draw the area around the code.");
        var pts = r.Graphics.Where(g => mine.Any(c => c.Text == g.Label)).SelectMany(g => g.Points).ToList();
        if (pts.Count > 0) code.Roi = Pad(pts, 0.25, image);
        return mine.Count == 1
            ? $"Found {BarcodeReading.CodeFormats.Name(code.Format)}: {mine[0].Text}"
            : $"Found {mine.Count} codes of type {BarcodeReading.CodeFormats.Name(code.Format)}";
    }

    static string Text(TextToolConfig text, GrayImage image, ModelPaths models)
    {
        if (text.Reader == OcrReader.Font) throw new InvalidOperationException("A taught font is taught on the Teach page of the browser Vision Studio for now.");
        if (!File.Exists(models.OcrNet)) throw new InvalidOperationException("The text reading model is missing on this PC.");
        // several letter heights (teaching runs once, so time is no issue); the one that reads the most
        // characters with confidence wins; a height already set is tried first
        var heights = new List<int>();
        if (text.CharHeightPx > 0) heights.Add(text.CharHeightPx);
        heights.AddRange(new[] { 14, 20, 28, 38, 52, 70, 95 }.Where(h => h != text.CharHeightPx));
        List<OcrLine> lines = [];
        double best = 0;
        foreach (var h in heights)
        {
            using var eng = new OcrEngine(new OcrSettings { Roi = text.Roi, Print = text.Print, Polarity = text.Polarity, CharHeightPx = h, TimeBudgetMs = 1000, Reader = text.Reader });
            eng.LoadModel(models.OcrNet, models.OcrCharset);
            eng.SetExpected([]);
            var r = eng.Run(image.Pixels, image.Width, image.Height, image.Stride);
            var good = r.Lines.Where(l => l.Text.Replace(" ", "").Length >= 3 && l.Quality >= 60).ToList();
            var score = good.Sum(l => l.Text.Replace(" ", "").Length * l.Quality / 100.0);
            if (score > best) { best = score; lines = good; }
        }
        if (lines.Count == 0) throw new InvalidOperationException("No text was read here. Draw the area around the print, or set the print type and colour.");
        // the lines that sit together with the best-read one (a block), not stray label print
        var main = lines.OrderByDescending(l => l.Quality * l.Text.Length).First();
        var ch = main.CharHeightPx;
        lines = lines.Where(l => Math.Abs(l.CharHeightPx - ch) < 0.35 * ch).OrderBy(l => l.Corners.Average(p => p.Y)).ToList();
        // the job's letter height is the capital height: about 0.8 of the measured line
        text.CharHeightPx = Math.Max(8, (int)Math.Round(0.8 * lines.Select(l => l.CharHeightPx).OrderBy(h => h).ElementAt(lines.Count / 2)));
        // room for the part to move: 15 % of the block width + 2 letters at each side, 1.5 letters above and below
        var pts = lines.SelectMany(l => l.Corners).ToList();
        float bx0 = pts.Min(p => p.X), bx1 = pts.Max(p => p.X), by0 = pts.Min(p => p.Y), by1 = pts.Max(p => p.Y);
        double mx = 0.15 * (bx1 - bx0) + 2 * text.CharHeightPx, my = 1.5 * text.CharHeightPx;
        int rx = (int)Math.Max(0, bx0 - mx), ry = (int)Math.Max(0, by0 - my);
        text.Roi = new RoiBox(rx, ry, (int)Math.Min(image.Width - rx, bx1 - bx0 + 2 * mx), (int)Math.Min(image.Height - ry, by1 - by0 + 2 * my));
        var when = image.TakenAt ?? DateTime.Now;
        if (text.Mode == TextMode.Read)
        {
            // a format per line; one set of date names for the block (two lines never both MFD)
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var months = MonthLanguages.Known(text);
            text.Lines = lines.Select(l => new TextLineSpec { Entry = LineEntry.Format, Template = TextFormat.Propose(l.Text, used, months) }).ToList();
            var formats = text.Lines.Select(l => { try { return TextFormat.Parse(l.Template, months); } catch (FormatException) { return null; } }).ToList();
            var dates = lines.Zip(formats).Where(p => p.Second is not null).Select(p => p.Second!.Check(p.First.Text))
                .Where(r => r.Ok).SelectMany(r => r.Dates).GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            if (dates.TryGetValue(text.Dates.MfdField, out var mfd) && dates.TryGetValue(text.Dates.ExpField, out var exp))
            {
                var span = exp.Date.Year * 12 + exp.Date.Month - (mfd.Date.Year * 12 + mfd.Date.Month);
                if (span > 0) text.Dates.ExpiryMonths = span;
            }
        }
        else
        {
            // Check text: the printed text as each line's expected text; the engineer then makes the
            // batch's parts fields ("B.NO {BATCH}") or takes them from the PLC
            text.Lines = lines.Select(l => new TextLineSpec { Entry = LineEntry.Template, Template = Suggest(l.Text, when) }).ToList();
        }
        return $"Found {lines.Count} line(s) of text, letters {text.CharHeightPx} px high: " + string.Join(" / ", lines.Select(l => l.Text));
    }

    /// <summary>The read text as a template: braces and "?" as printed, and a printed time within 15
    /// minutes of the picture's time as a {TIME} field.</summary>
    static string Suggest(string read, DateTime when)
    {
        var s = Regex.Replace(read.Trim(), @"[{}?\\]", m => "\\" + m.Value);
        return Regex.Replace(s, @"\b([01]\d|2[0-3]):([0-5]\d)\b", m =>
        {
            var t = when.Date.AddHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).AddMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            return Math.Abs((t - when).TotalMinutes) <= 15 ? "{TIME:HH:mm+-3}" : m.Value;
        });
    }

    static RoiBox Pad(List<PointF> pts, double frac, GrayImage image)
    {
        float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        var m = frac * Math.Max(x1 - x0, y1 - y0);
        int X = (int)Math.Max(0, x0 - m), Y = (int)Math.Max(0, y0 - m);
        return new RoiBox(X, Y, (int)Math.Min(image.Width - X, x1 - x0 + 2 * m), (int)Math.Min(image.Height - Y, y1 - y0 + 2 * m));
    }
}
