using System.Text.Json;
using VisionCore;
using Xunit;

// Check text (tool spec 7.3): expected text from the PLC or another tool, PrintQuality, Allow shift; 7.5 dot
// size, join strength and the Inkjet print type in the settings
public class ExpectedTextSourceTests
{
    private static readonly PointF[] Here = { new(10, 10), new(90, 10), new(90, 40), new(10, 40) };
    private static readonly PointF[] Nowhere = { new(0, 0), new(0, 0), new(0, 0), new(0, 0) };

    private static ToolResult CodeResult(string tool, bool pass, params string[] texts) =>
        new(tool, pass, "", Array.Empty<string>(), 1, Array.Empty<Graphic>())
        {
            Codes = texts.Select(t => new CodeRead(BarcodeReading.CodeFormat.DataMatrix, t, Here, null)).ToList(),
        };

    private static TextLineSpec Plc(string field) => new() { Source = ExpectedSource.Plc, SourceName = field };
    private static TextLineSpec FromTool(string tool) => new() { Source = ExpectedSource.Tool, SourceName = tool };

    [Fact]
    public void ThePlcValueIsTheExpectedTextAsPrinted()
    {
        var ctx = new RunContext(new PlcValues(" LOT A12?45 ", 7));
        Assert.Equal(@"LOT A12\?45", TextTemplate.Pattern(Plc("PartId"), 0, "T", new BatchFields(), ctx));
        Assert.Equal("7", TextTemplate.Pattern(Plc("productid"), 0, "T", new BatchFields(), ctx));
    }

    [Fact]
    public void AMissingOrEmptyPlcValueNeverPasses()
    {
        var batch = new BatchFields();
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(Plc("PartId"), 0, "T", batch, null));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(Plc("PartId"), 0, "T", batch, new RunContext()));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(Plc("PartId"), 0, "T", batch, new RunContext(new PlcValues("", 0))));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(Plc("PartId"), 0, "T", batch, new RunContext(new PlcValues("   ", 0))));
        Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(Plc("ProductId"), 0, "T", batch, new RunContext(new PlcValues("X", 0))));
    }

    [Fact]
    public void TheTextOfAToolThatRanBefore()
    {
        var ctx = new RunContext(null, new[] { CodeResult("Code", true, "0104012345678901") });
        Assert.Equal("0104012345678901", TextTemplate.Pattern(FromTool("code"), 0, "T", new BatchFields(), ctx));
        // the same code read twice is one text
        ctx = new RunContext(null, new[] { CodeResult("Code", true, "ABC", "ABC") });
        Assert.Equal("ABC", TextTemplate.Pattern(FromTool("Code"), 0, "T", new BatchFields(), ctx));
    }

    [Theory]
    [InlineData(false, new[] { "ABC" }, "did not pass")]
    [InlineData(true, new string[0], "gave no text")]
    [InlineData(true, new[] { "ABC", "ABD" }, "gave 2 texts")]
    public void AToolTextInDoubtFailsThePart(bool pass, string[] texts, string why)
    {
        var ctx = new RunContext(null, new[] { CodeResult("Code", pass, texts) });
        var e = Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(FromTool("Code"), 0, "T", new BatchFields(), ctx));
        Assert.Contains(why, e.Message);
        e = Assert.Throws<ExpectedTextMissingException>(() => TextTemplate.Pattern(FromTool("Other"), 0, "T", new BatchFields(), ctx));
        Assert.Contains("must run before this tool", e.Message);
    }

    [Fact]
    public void PlcAndToolLinesNeedNoBatchField()
    {
        var cfg = new TextToolConfig { Name = "T", Lines = { Plc("PartId"), FromTool("Code"), new() { Template = "MRP {MRP}" } } };
        Assert.Equal(new[] { "MRP" }, cfg.Fields().Select(f => f.Key));
    }

    [Fact]
    public void SourcesAreCheckedAtSetup()
    {
        TextToolConfig Cfg(params TextLineSpec[] lines)
        {
            var c = new TextToolConfig { Name = "Lot" };
            c.Lines.AddRange(lines);
            return c;
        }
        Assert.Null(TextTool.SetupProblem(Cfg(Plc("PartId"), FromTool("Code"))));
        Assert.Contains("name the PLC field", TextTool.SetupProblem(Cfg(Plc(""))));
        Assert.Contains("name the tool", TextTool.SetupProblem(Cfg(FromTool(" "))));
        Assert.Contains("the PLC sends no Batch", TextTool.SetupProblem(Cfg(Plc("Batch"))));
        Assert.Contains("its own text", TextTool.SetupProblem(Cfg(FromTool("lot"))));
        var read = Cfg(Plc("PartId"));
        read.Mode = TextMode.Read;
        Assert.Contains("only in Check text mode", TextTool.SetupProblem(read));
        var bad = Cfg();
        bad.JoinStrength = 5;
        Assert.NotNull(TextTool.SetupProblem(bad));
        bad.JoinStrength = 0;
        bad.AllowShiftPx = -1;
        Assert.NotNull(TextTool.SetupProblem(bad));
    }

    [Fact]
    public void PrintQualityIsTheLowestCharacterOverEveryExpectedLine()
    {
        OcrLine L(float q, bool found = true) =>
            new("X", "X", true, q >= 60, q, Array.Empty<OcrChar>(), found ? Here : Nowhere, 0, 0, 30);
        Assert.Equal(71, TextTool.PrintQualityOf(new[] { L(92), L(71) }, 2));
        Assert.Equal(0, TextTool.PrintQualityOf(new[] { L(92) }, 2));        // a line not found
        Assert.Equal(0, TextTool.PrintQualityOf(new[] { L(92), L(0, false) }, 2));
        Assert.Equal(0, TextTool.PrintQualityOf(Array.Empty<OcrLine>(), 0));
        // a free read beside the expected lines does not count
        var free = new OcrLine("junk", "", false, false, 5, Array.Empty<OcrChar>(), Here, 0, 0, 30);
        Assert.Equal(92, TextTool.PrintQualityOf(new[] { L(92), free }, 1));
    }

    [Fact]
    public void MarginIsTheLowestOverEveryExpectedLine()
    {
        OcrLine L(float m, bool found = true) =>
            new("X", "X", true, true, 90, Array.Empty<OcrChar>(), found ? Here : Nowhere, 0, 0, 30) { Margin = m };
        Assert.Equal(1.5f, TextTool.MarginOf(new[] { L(6.2f), L(1.5f) }, 2));
        Assert.Equal(-99, TextTool.MarginOf(new[] { L(6.2f) }, 2));          // a line not found
        Assert.Equal(-99, TextTool.MarginOf(Array.Empty<OcrLine>(), 0));
        Assert.Equal(-99, TextTool.MarginOf(new[] { L(6.2f), L(-99, false) }, 2));
        // a free read or a line read under a format does not count
        var free = new OcrLine("junk", "", false, false, 5, Array.Empty<OcrChar>(), Here, 0, 0, 30) { Margin = -5 };
        var fmt = new OcrLine("LOT 1", "", true, true, 90, Array.Empty<OcrChar>(), Here, 0, 0, 30) { Format = 0, Margin = 0 };
        Assert.Equal(6.2f, TextTool.MarginOf(new[] { L(6.2f), free, fmt }, 1));
    }

    [Fact]
    public void AShiftedLineIsExplained()
    {
        var ok = new OcrChar("A", "A", 95, CharStatus.Ok, Here);
        OcrLine L(ShiftState s) => new("A", "A", true, false, 95, new[] { ok }, Here, 0, 0, 30) { Shift = s, ShiftPx = 12.4f };
        Assert.Equal("explain.ocv.shift", TextTool.ExplainOf(L(ShiftState.Beyond)));
        Assert.Contains("12 px from where the good parts had it", TextTool.Describe(L(ShiftState.Beyond)));
        Assert.Equal("explain.ocv.shift_unlearned", TextTool.ExplainOf(L(ShiftState.Unlearned)));
        Assert.Contains("Learn a good part", TextTool.Describe(L(ShiftState.Unlearned)));
        var dots = new OcrChar("1", "1", 30, CharStatus.Unsure, Here) { Quality = new CharQuality(95, 60, 40, 20, 100, 30, 100, PrintReason.Broken, true) };
        var broken = new OcrLine("1", "1", true, false, 30, new[] { dots }, Here, 0, 0, 30);
        Assert.Equal("explain.ocr.dots_missing", TextTool.ExplainOf(broken, PrintType.Inkjet));
    }

    [Fact]
    public void ALineCutShortIsExplained()
    {
        var ok = new OcrChar("A", "A", 95, CharStatus.Ok, Here);
        var cut = new OcrLine("A", "A", true, false, 59, new[] { ok }, Here, 0, 0, 30) { CutShort = true };
        Assert.Equal("explain.ocr.cut_short", TextTool.ExplainOf(cut));
        Assert.Contains("may go on past \"A\"", TextTool.Describe(cut));
        // what it misses may be only the cut: the cut is the reason given
        var gone = new OcrChar("B", "", 0, CharStatus.Missing, Here);
        Assert.Equal("explain.ocr.cut_short", TextTool.ExplainOf(cut with { Chars = new[] { ok, gone } }));
        Assert.Equal("explain.ocv.missing", TextTool.ExplainOf(cut with { Chars = new[] { ok, gone }, CutShort = false }));
        // not found at all: missing, never cut short
        Assert.Equal("explain.ocv.missing", TextTool.ExplainOf(cut with { Corners = Nowhere }));
    }

    [Fact]
    public void NewSettingsTravelWithTheJob()
    {
        var job = new Job
        {
            Name = "Lot",
            Cameras = { new CameraConfig { Name = "Top" } },
            Tools =
            {
                new TextToolConfig
                {
                    Name = "Lot", Print = PrintType.Inkjet, AllowShiftPx = 8, DotSizePx = 4.5f, JoinStrength = 1.5f,
                    Lines = { Plc("PartId"), FromTool("Code") },
                },
            },
        };
        var back = Assert.IsType<TextToolConfig>(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!.Tools[0]);
        Assert.Equal((PrintType.Inkjet, 8f, 4.5f, 1.5f), (back.Print, back.AllowShiftPx, back.DotSizePx, back.JoinStrength));
        Assert.Equal((ExpectedSource.Plc, "PartId"), (back.Lines[0].Source, back.Lines[0].SourceName));
        Assert.Equal((ExpectedSource.Tool, "Code"), (back.Lines[1].Source, back.Lines[1].SourceName));
        // a job saved before these settings: expected text from the job, nothing else changes
        var old = new TextToolConfig();
        Assert.Equal((ExpectedSource.Job, 0f, 0f, 0f), (new TextLineSpec().Source, old.AllowShiftPx, old.DotSizePx, old.JoinStrength));
    }

    // needs the engine built (engine/build); skipped when the native library is not there
    private static bool EngineBuilt => File.Exists(Path.Combine(AppContext.BaseDirectory, "libvs_ocr.dylib"))
                                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "vs_ocr.dll"));

    [Fact]
    public void TheSettingsStructMatchesTheEngine()
    {
        if (!EngineBuilt) return;
        var s = new OcrSettings { Print = PrintType.Inkjet, AllowShiftPx = 6, DotSizePx = 4, JoinStrength = 1.2f };
        using var e = new OcrEngine(s);
        e.Apply(s);   // throws when the struct size differs from vs_ocr_params
    }

    [Fact]
    public void AToolWithoutItsPlcValueFailsBeforeReading()
    {
        if (!EngineBuilt) return;
        var models = FindModels();
        if (models is null) return;
        var cfg = new TextToolConfig { Name = "Lot", CharHeightPx = 30, Lines = { Plc("PartId") } };
        using var tool = new TextTool(cfg, new ModelPaths(models));
        var image = new GrayImage(Enumerable.Repeat((byte)200, 320 * 120).ToArray(), 320, 120);
        var r = tool.Run(image, new BatchFields());
        Assert.False(r.Pass);
        Assert.Equal("explain.ocv.no_expected", r.Explain);
        Assert.Equal(0f, r.PrintQuality);
        r = tool.Run(image, new BatchFields(), null, new RunContext(new PlcValues("", 0)));
        Assert.Equal("explain.ocv.no_expected", r.Explain);
        // with the value and nothing printed: NOT OK as well
        r = tool.Run(image, new BatchFields(), null, new RunContext(new PlcValues("LOT A12345", 0)));
        Assert.False(r.Pass);
        Assert.Equal(0f, r.PrintQuality);
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
