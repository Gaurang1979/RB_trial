using VisionCore;
using Xunit;

/// <summary>
/// A Read text tool that decides OK/NG needs a format on every line: free reading passes a misread line (full
/// system test 2026-10-30: 22 of 103 wrong laser lines scored at or above strictness 60). Without one it is
/// refused at save, or set to report only, when its result never decides the part.
/// </summary>
public class TextDecidesTests
{
    private static TextToolConfig Read(params string[] formats) => new()
    {
        Name = "Lot", Mode = TextMode.Read,
        Lines = formats.Select(f => new TextLineSpec { Entry = LineEntry.Format, Template = f }).ToList(),
    };

    private static Job JobWith(params ToolConfig[] tools)
    {
        var j = new Job { Name = "Case", Cameras = new() { new CameraConfig { Name = "Top" } } };
        j.Tools.AddRange(tools);
        return j;
    }

    [Theory]
    [InlineData("{?}", true)]
    [InlineData("{?:5}", true)]
    [InlineData(" {?:4-8}  {?} ", true)]
    [InlineData("{X}", true)]
    [InlineData("{X} {?:3}", true)]
    [InlineData("{X:6}", false)]           // a length and a charset
    [InlineData("{X:4-8}", false)]
    [InlineData("{D}", false)]
    [InlineData("{A:2}{?:3}", false)]
    [InlineData("LOT {?}", false)]         // printed text
    [InlineData("{EXP:MM/YY}", false)]
    [InlineData("{MRP}", false)]
    [InlineData(@"{re:[A-Z]\d+}", false)]  // the engineer's own rule
    public void AFormatOfOnlyAnyCharactersAcceptsAnyText(string format, bool open) =>
        Assert.Equal(open, TextTool.OpenFormat(format));

    [Fact]
    public void FreeReadingIsASetupProblemUnlessReportOnly()
    {
        var free = Read();
        Assert.Equal(TextTool.NoRule, TextTool.SetupProblem(free));
        free.ReportOnly = true;
        Assert.Null(TextTool.SetupProblem(free));

        var open = Read("LOT {D:5}", "{?:8}");
        Assert.Contains("Line 2: the format {?:8} accepts any text", TextTool.SetupProblem(open));
        open.ReportOnly = true;
        Assert.Null(TextTool.SetupProblem(open));

        Assert.Null(TextTool.SetupProblem(Read("LOT {D:5}", "{X:6}")));
    }

    [Fact]
    public void ReportOnlyIsForReadTextOnly()
    {
        var check = new TextToolConfig { Name = "Lot", Mode = TextMode.Check, ReportOnly = true, Lines = { new TextLineSpec { Template = "LOT {BATCH}" } } };
        Assert.Contains("Report only works in Read text mode", TextTool.SetupProblem(check));
        Assert.True(TextTool.Decides(new TextToolConfig { Mode = TextMode.Check }));
        Assert.True(TextTool.Decides(Read("LOT {D:5}")));
        Assert.False(TextTool.Decides(new TextToolConfig { Mode = TextMode.Read, ReportOnly = true }));
        Assert.True(TextTool.Decides(new CodeToolConfig()));
    }

    // OCV-01: a Check text tool with no line checks nothing, so it would pass any part whose print reads
    [Fact]
    public void CheckTextWithNoLineIsASetupProblem()
    {
        var empty = new TextToolConfig { Name = "Lot", Mode = TextMode.Check };
        Assert.Equal(TextTool.NoLines, TextTool.SetupProblem(empty));

        var withLine = new TextToolConfig { Name = "Lot", Mode = TextMode.Check, Lines = { new TextLineSpec { Template = "LOT 123" } } };
        Assert.Null(TextTool.SetupProblem(withLine));

        var e = Assert.Throws<ArgumentException>(() => JobWith(empty).Validate());
        Assert.StartsWith("Lot: add the lines to check", e.Message);
    }

    [Fact]
    public void AJobWithFreeReadingIsRefusedAtSave()
    {
        var e = Assert.Throws<ArgumentException>(() => JobWith(Read()).Validate());
        Assert.StartsWith("Lot: Read text with no format passes a misread line", e.Message);
    }

    [Fact]
    public void ACameraOfOnlyReportOnlyToolsIsRefused()
    {
        var report = Read();
        report.ReportOnly = true;
        var e = Assert.Throws<ArgumentException>(() => JobWith(report).Validate());
        Assert.Equal("Top: every tool on it is report only, so nothing decides OK/NG. Add a tool that checks the part", e.Message);
        // another tool on the camera decides
        JobWith(report, new CodeToolConfig { Name = "Code" }).Validate();
        // a disabled deciding tool does not count
        Assert.Throws<ArgumentException>(() => JobWith(report, new CodeToolConfig { Name = "Code", Enabled = false }).Validate());
    }

    [Fact]
    public void CheckTextCannotTakeItsTextFromAReportOnlyTool()
    {
        var report = Read();
        report.ReportOnly = true;
        var check = new TextToolConfig
        {
            Name = "Again", Mode = TextMode.Check,
            Lines = { new TextLineSpec { Source = ExpectedSource.Tool, SourceName = "lot" } },
        };
        var e = Assert.Throws<ArgumentException>(() => JobWith(report, check).Validate());
        Assert.Equal("Again: line 1 takes its text from Lot, which is report only. Take it from a tool that decides OK/NG", e.Message);
        report.ReportOnly = false;
        report.Lines.Add(new TextLineSpec { Entry = LineEntry.Format, Template = "LOT {D:5}" });
        JobWith(report, check).Validate();
    }

    [Fact]
    public void AReportOnlyResultNeverDecidesThePart()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib")) && !File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"))) return;
        string? models = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && models is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ml", "models", "ocrnet.onnx"))) models = Path.Combine(d.FullName, "ml", "models");
        if (models is null) return;
        var image = new GrayImage(Enumerable.Repeat((byte)200, 320 * 120).ToArray(), 320, 120);
        var report = Read("LOT {D:5}");
        report.ReportOnly = true;
        using (var tool = new TextTool(report, new ModelPaths(models)))
        {
            var r = tool.Run(image, new BatchFields());
            Assert.True(r.Pass);
            Assert.True(r.ReportOnly);
            Assert.Equal(TextTool.ReportOnlyExplain, r.Explain);
            Assert.StartsWith("Report only: no text found", r.Summary);
        }
        // a job saved before the check (not validated) with only report-only tools: NG, never OK with nothing checked
        using (var runner = new JobRunner(JobWith(report), new ModelPaths(models)))
            Assert.False(runner.Run(0, image, new BatchFields()).Pass);
        // a free-reading tool from such a job fails every part with the reason
        using (var tool = new TextTool(Read(), new ModelPaths(models)))
        {
            var r = tool.Run(image, new BatchFields());
            Assert.False(r.Pass);
            Assert.Equal("explain.format.setup", r.Explain);
        }
    }
}
