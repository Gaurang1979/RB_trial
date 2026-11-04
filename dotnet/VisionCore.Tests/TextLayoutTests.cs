using System.Text.Json;
using VisionCore;
using Xunit;

// Text-line layout prior and tight dot spacing (tool study 7.4.6, 7.5.1) in the Read / Check text tool
public class TextLayoutTests
{
    private static readonly PointF[] Here = { new(10, 10), new(90, 10), new(90, 40), new(10, 40) };

    // needs the engine built (engine/build); skipped when the native library is not there
    private static bool EngineBuilt => File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib"))
                                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"));

    [Theory]
    [InlineData("")]
    [InlineData("2 2 2-4")]
    [InlineData("6 | 4 2 |2-4")]
    [InlineData(" 64 ")]
    public void ALayoutThatParsesIsAccepted(string layout) => Assert.Null(TextTool.LayoutProblem(layout));

    [Theory]
    [InlineData("2 x", "is not a group size")]
    [InlineData("4-2", "lower count first")]
    [InlineData("65", "1 to 64")]
    [InlineData("0", "1 to 64")]
    [InlineData("2 || 3", "has no groups")]
    public void ALayoutThatDoesNotParseIsRefused(string layout, string why) =>
        Assert.Contains(why, TextTool.LayoutProblem(layout));

    [Fact]
    public void ABadLayoutIsASetupProblem()
    {
        var cfg = new TextToolConfig { Name = "Lot", Mode = TextMode.Check, Layout = "3 x", Lines = { new TextLineSpec { Template = "LOT {BATCH}" } } };
        Assert.Contains("is not a group size", TextTool.SetupProblem(cfg));
        cfg.Layout = "3 6";
        Assert.Null(TextTool.SetupProblem(cfg));
    }

    [Fact]
    public void LayoutAndTightSpacingAreKeptWithTheJob()
    {
        var job = new Job
        {
            Name = "Lot",
            Cameras = { new CameraConfig { Name = "Top" } },
            Tools = { new TextToolConfig { Name = "Lot", Print = PrintType.DotMatrix, Layout = "3 6 | 3 5", TightSpacing = true } },
        };
        var back = Assert.IsType<TextToolConfig>(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!.Tools[0]);
        Assert.Equal(("3 6 | 3 5", true), (back.Layout, back.TightSpacing));
        Assert.True(back.ToSettings().TightSpacing);
        // a job saved before these settings: no layout, normal spacing
        var old = new TextToolConfig();
        Assert.Equal(("", false), (old.Layout, old.TightSpacing));
    }

    private static OcrLine Line(string text, bool pass, int layoutLine, params OcrChar[] chars) =>
        new(text, "{?:3}", true, pass, pass ? 90 : 40, chars, Here, 0, 0, 30) { LayoutLine = layoutLine };

    [Fact]
    public void AFailedLayoutLineIsExplained()
    {
        var ok = new OcrChar("A", "A", 95, CharStatus.Ok, Here);
        var weak = new OcrChar("B", "B", 41, CharStatus.Unsure, Here);
        Assert.Null(TextTool.LayoutLineProblem(new[] { Line("AB", true, 0, ok, ok) }));
        // a line not read under a layout line is not this check's
        Assert.Null(TextTool.LayoutLineProblem(new[] { Line("AB", false, -1, ok, weak) }));
        var unclear = TextTool.LayoutLineProblem(new[] { Line("AB", false, 0, ok, weak) });
        Assert.Equal("explain.ocr.unsure", unclear?.Explain);
        Assert.Contains("Character 2", unclear?.Problem);
        var extra = TextTool.LayoutLineProblem(new[] { Line("ABX", false, 1, ok, ok, new OcrChar("X", "X", 80, CharStatus.Extra, Here)) });
        Assert.Equal("explain.ocr.layout", extra?.Explain);
        var cut = TextTool.LayoutLineProblem(new[] { Line("AB", false, 0, ok, ok) with { CutShort = true } });
        Assert.Equal("explain.ocr.cut_short", cut?.Explain);
    }

    [Fact]
    public void TheEngineKeepsTheLayoutAndTheSpacing()
    {
        if (!EngineBuilt) return;
        var s = new OcrSettings { Print = PrintType.DotMatrix, TightSpacing = true };
        using var e = new OcrEngine(s);
        e.Apply(s);
        e.SetLayout(" 2 2  2-4|6 ");
        Assert.Equal("2 2 2-4 | 6", e.GetLayout());
        Assert.Throws<ArgumentException>(() => e.SetLayout("2 x"));
        Assert.Equal("2 2 2-4 | 6", e.GetLayout());   // a refused layout leaves the one set before
        e.SetLayout(null);
        Assert.Equal("", e.GetLayout());
        // nothing run yet: nothing to learn the layout from
        Assert.Throws<InvalidOperationException>(() => e.LearnLayout());
    }

    [Fact]
    public void ABlankPartFailsItsLayoutWithTheReason()
    {
        if (!EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        // (a format too: a layout counts characters but not which ones, so alone it does not decide OK/NG)
        var cfg = new TextToolConfig
        {
            Name = "Lot", Mode = TextMode.Read, CharHeightPx = 30, Layout = "3 6",
            Lines = { new TextLineSpec { Entry = LineEntry.Format, Template = "{X:3} {X:6}" } },
        };
        using var tool = new TextTool(cfg, new ModelPaths(models));
        var image = new GrayImage(Enumerable.Repeat((byte)200, 320 * 120).ToArray(), 320, 120);
        var r = tool.Run(image, new BatchFields());
        Assert.False(r.Pass);
        Assert.Equal("explain.ocr.layout", r.Explain);
        Assert.Contains("layout", r.Summary);
        // a part that fails teaches no layout, and the one set is kept
        var learned = tool.LearnLayout(image, new BatchFields());
        Assert.False(learned.Learned);
        Assert.Equal("3 6", cfg.Layout);
    }

    private static string? FindModels()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var m = Path.Combine(d.FullName, "ml", "models");
            if (File.Exists(Path.Combine(m, "ocrnet.onnx"))) return m;
        }
        return null;
    }
}
