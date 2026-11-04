using System.Text.Json;
using VisionCore;
using Xunit;

/// <summary>Check text with a rendered system font (tool spec 7.3): no teaching from sample print.</summary>
public class RenderedFontTests
{
    // a font every development and line PC has; the tests that draw are skipped on a PC with none of these
    private static readonly string? Family = new[] { "Arial", "Helvetica", "DejaVu Sans", "Liberation Sans" }
        .FirstOrDefault(f => FontRender.Families().Contains(f, StringComparer.OrdinalIgnoreCase));

    // needs the engine built (engine/build); skipped when the native library is not there
    private static bool EngineBuilt => File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib"))
                                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"));

    private static TextToolConfig Check(string template, string family = "", int size = 30, string? chars = null) => new()
    {
        Name = "Lot", Mode = TextMode.Check, Reader = OcrReader.Font, Print = PrintType.Solid,
        TimeBudgetMs = 5000,   // the default 100 ms budget is a line-speed limit; a loaded test machine must not trip it
        Rendered = new RenderedFont { Family = family, SizePx = size, Characters = chars ?? RenderedFont.DefaultCharacters },
        Lines = { new TextLineSpec { Entry = LineEntry.Template, Template = template } },
    };

    private static Job JobWith(ToolConfig tool) => new() { Name = "Case", Cameras = { new CameraConfig { Name = "Top" } }, Tools = { tool } };

    [Fact]
    public void AFontThatCannotPrintTheTextIsRefusedAtSave()
    {
        if (Family is null) return;
        Assert.Contains("Pick the font", TextTool.SetupProblem(Check("LOT {LOT}")));
        Assert.Contains("is not installed", TextTool.SetupProblem(Check("LOT {LOT}", "No Such Font 4711")));
        Assert.Contains("capital letter", TextTool.SetupProblem(Check("LOT {LOT}", Family, size: 4)));
        // the fixed text of a line and the characters of the fields both need a glyph in the font
        Assert.Contains("cannot print ", TextTool.SetupProblem(Check("LOT  {LOT}", Family)));
        Assert.Contains("cannot print ", TextTool.SetupProblem(Check("LOT {LOT}", Family, chars: "0123456789")));
        var e = Assert.Throws<ArgumentException>(() => JobWith(Check("LOT ", Family)).Validate());
        Assert.StartsWith("Lot: The font", e.Message);
        // the AI reader ignores the system font settings (kept for switching back)
        var ai = Check("LOT \uE000", Family);
        ai.Reader = OcrReader.Ai;
        Assert.DoesNotContain("font", TextTool.SetupProblem(ai) ?? "");
        // drawn shapes have no dots: dot and inkjet print are taught from print
        var dots = Check("LOT {LOT}", Family);
        dots.Print = PrintType.DotMatrix;
        Assert.Contains("solid print", TextTool.SetupProblem(dots));
        // nothing taught is needed: the fixed text is drawn by itself
        Assert.Null(TextTool.SetupProblem(Check("MRP Rs. {MRP}", Family)));
    }

    [Fact]
    public void AFormatNeedsItsCharactersInTheRenderedFont()
    {
        if (Family is null) return;
        var cfg = Check("", Family, chars: "0123456789");
        cfg.Mode = TextMode.Read;
        cfg.Lines[0] = new TextLineSpec { Entry = LineEntry.Format, Template = "LOT {D:5}" };
        Assert.Contains("The rendered font cannot read L O T", TextTool.SetupProblem(cfg));
        cfg.Rendered!.Characters = "LOT0123456789";
        Assert.Null(TextTool.SetupProblem(cfg));
    }

    [Fact]
    public void TheRenderedFontTravelsWithTheJob()
    {
        var job = JobWith(Check("LOT {LOT}", "Arial", 24));
        var tool = (TextToolConfig)job.Tools[0];
        tool.Rendered!.Bold = true;
        tool.Font = "VSFONT1\n65 16 00\n";
        tool.Rendered.RenderedAs = FontRender.Key(tool);
        var back = Assert.IsType<TextToolConfig>(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!.Tools[0]);
        Assert.Equal(("Arial", true, 24, RenderedFont.DefaultCharacters), (back.Rendered!.Family, back.Rendered.Bold, back.Rendered.SizePx, back.Rendered.Characters));
        Assert.Equal((tool.Font, tool.Rendered.RenderedAs), (back.Font, back.Rendered.RenderedAs));
        // the size drawn is the letter height the reader looks for
        Assert.Equal(24, back.ToSettings().CharHeightPx);
        // a font kept with the job reads on a PC without it installed; a changed one must be drawn again there
        back.Rendered.Family = "No Such Font 4711";
        Assert.Contains("is not installed", TextTool.SetupProblem(back));
        back.Rendered.RenderedAs = FontRender.Key(back);
        Assert.Null(TextTool.SetupProblem(back));
        // a job saved before rendered fonts: taught from print as before
        Assert.Null(new TextToolConfig().Rendered);
    }

    [Fact]
    public void TheFixedTextAndFieldCharactersAreDrawnOnce()
    {
        var cfg = Check("MRP Rs. {MRP} {DATE:MM/YY}", "Arial", chars: "0123456789.");
        Assert.Equal("0123456789.MRPs", string.Concat(FontRender.Needed(cfg)));
        Assert.Contains((int)' ', FontRender.Charset(cfg));
    }

    [Fact]
    public void PrintInTheRenderedFontIsCheckedWithoutTeaching()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("LOT {LOT}", Family);
        JobWith(cfg).Validate();   // draws and teaches the font
        Assert.Equal(FontRender.Needed(cfg).Count, TextTool.FontGlyphs(cfg.Font));
        Assert.Equal(FontRender.Key(cfg), cfg.Rendered!.RenderedAs);
        using var tool = new TextTool(cfg, new ModelPaths(Path.GetTempPath()));
        var batch = new BatchFields { Values = { ["LOT"] = "A123" } };

        var good = tool.Run(Print("LOT A123"), batch);
        Assert.True(good.Pass, good.Summary);
        Assert.True(good.PrintQuality > cfg.MinQuality);
        // line safety: a wrong, missing or extra character is NOT OK
        var wrong = tool.Run(Print("LOT A128"), batch);
        Assert.False(wrong.Pass);
        Assert.Equal("explain.ocv.wrong", wrong.Explain);
        Assert.False(tool.Run(Print("LOT A12"), batch).Pass);
        Assert.False(tool.Run(Print("LOT A1234"), batch).Pass);
        // a field value with a character the font was not drawn with is never passed
        var lower = tool.Run(Print("LOT a123"), new BatchFields { Values = { ["LOT"] = "a123" } });
        Assert.False(lower.Pass);
    }

    // OCV-01: a Check text tool whose lines were all removed (or one tapped onto the picture before any was added)
    // checks nothing; on the line it fails every part, never OK on clean print
    [Fact]
    public void CheckTextWithNoLineFailsEveryPart()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("LOT {LOT}", Family);
        JobWith(cfg).Validate();   // draws and teaches the font
        cfg.Lines.Clear();
        using var tool = new TextTool(cfg, new ModelPaths(Path.GetTempPath()));
        var r = tool.Run(Print("LOT A123"), new BatchFields { Values = { ["LOT"] = "A123" } });
        Assert.False(r.Pass);
        Assert.Equal(TextTool.NoLines, r.Summary);
        Assert.Equal("explain.format.setup", r.Explain);
    }

    [Fact]
    public void TextCutByTheAreaSaysSo()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("LOT {LOT}", Family);
        JobWith(cfg).Validate();
        var batch = new BatchFields { Values = { ["LOT"] = "A12" } };
        var image = Print("LOT A123");
        using (var whole = new TextTool(cfg, new ModelPaths(Path.GetTempPath())))
        {
            var r = whole.Run(image, new BatchFields { Values = { ["LOT"] = "A123" } });
            Assert.True(r.Pass, r.Summary);
            Assert.False(r.Lines![0].CutShort);
        }
        // the area ends in the middle of the last 3: LOT A12 is there, but the text goes on past it
        int right = 0;
        for (int x = 0; x < image.Width; x++)
            for (int y = 0; y < image.Height; y++)
                if (image.Pixels[y * image.Width + x] < 128) right = x;
        cfg.Roi = new RoiBox(10, 5, right - 7 - 10, image.Height - 10);
        using var tool = new TextTool(cfg, new ModelPaths(Path.GetTempPath()));
        var cut = tool.Run(image, batch);
        Assert.False(cut.Pass, $"right {right} w {image.Width}x{image.Height}: " + string.Join(" / ", cut.Lines!.Select(l => $"{l.Text} cut={l.CutShort} q={l.Quality:0} x={l.Corners[0].X:0}..{l.Corners[1].X:0}")));
        Assert.Contains(cut.Lines!, l => l.CutShort);
        Assert.Equal("explain.ocr.cut_short", cut.Explain);
        Assert.Contains("may go on past", cut.Summary);
    }

    [Fact]
    public void SmallLettersAreTaughtBesideCapitals()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("Lot {LOT}", Family);   // o and t are drawn between capitals, at their own height
        JobWith(cfg).Validate();
        using var tool = new TextTool(cfg, new ModelPaths(Path.GetTempPath()));
        var batch = new BatchFields { Values = { ["LOT"] = "A123" } };
        var r = tool.Run(Print("Lot A123"), batch);
        Assert.True(r.Pass, r.Summary);
        Assert.False(tool.Run(Print("Lat A123"), batch).Pass);
    }

    [Fact]
    public void TheLineNeverDrawsAFontNotCheckedAtSave()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("LOT {LOT}", Family, chars: "0123456789");
        JobWith(cfg).Validate();
        cfg.Rendered!.SizePx = 40;   // changed by hand after the save
        using var tool = new TextTool(cfg, new ModelPaths(Path.GetTempPath()));
        var r = tool.Run(Print("LOT 123"), new BatchFields { Values = { ["LOT"] = "123" } });
        Assert.False(r.Pass);
        Assert.Equal("explain.format.setup", r.Explain);
    }

    [Fact]
    public void LightPrintOnADarkPartReadsWithTheSameFont()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("EXP {EXP}", Family, chars: "0123456789/");
        cfg.Polarity = TextPolarity.LightOnDark;
        JobWith(cfg).Validate();
        using var tool = new TextTool(cfg, new ModelPaths(Path.GetTempPath()));
        var batch = new BatchFields { Values = { ["EXP"] = "09/28" } };
        var dark = Print("EXP 09/28");
        var light = new GrayImage(dark.Pixels.Select(p => (byte)(255 - p)).ToArray(), dark.Width, dark.Height);
        var r = tool.Run(light, batch);
        Assert.True(r.Pass, r.Summary);
        Assert.False(tool.Run(light, new BatchFields { Values = { ["EXP"] = "08/28" } }).Pass);
    }

    [Fact]
    public void ChangingTheFontDrawsItAgain()
    {
        if (Family is null || !EngineBuilt) return;
        var cfg = Check("LOT {LOT}", Family, chars: "0123456789");
        Assert.Null(FontRender.Prepare(cfg));
        var first = cfg.Font;
        Assert.Null(FontRender.Prepare(cfg));
        Assert.Same(first, cfg.Font);   // unchanged: kept as is
        cfg.Rendered!.Characters = "0123456789AB";
        Assert.Null(FontRender.Prepare(cfg));
        Assert.Equal(15, TextTool.FontGlyphs(cfg.Font));
    }

    // print as a printer sets it: the font's own spacing, on a larger label
    private static GrayImage Print(string text)
    {
        var line = FontRender.Draw(Family!, false, 30, text, gapEm: 0);
        int w = line.Width + 80, h = line.Height + 60;
        var px = Enumerable.Repeat((byte)230, w * h).ToArray();
        for (int y = 0; y < line.Height; y++)
            for (int x = 0; x < line.Width; x++)
                px[(y + 30) * w + x + 40] = (byte)Math.Max(20, line.Pixels[y * line.Width + x] * 230 / 255);
        return new GrayImage(px, w, h);
    }
}
