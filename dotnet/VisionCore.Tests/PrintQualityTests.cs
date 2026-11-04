using System.Text.Json;
using VisionCore;
using Xunit;

public class PrintQualityTests
{
    private static readonly PointF[] Here = { new(10, 10), new(90, 10), new(90, 40), new(10, 40) };
    private static readonly PointF[] Nowhere = { new(0, 0), new(0, 0), new(0, 0), new(0, 0) };

    private static OcrChar Ok(string c) => new(c, c, 95, CharStatus.Ok, Here)
        { Quality = new CharQuality(95, 60, 100, 20, 100, 100, 100, PrintReason.Ok, true) };

    private static OcrChar Poor(string c, PrintReason why, float score = 30) => new(c, c, score, CharStatus.Unsure, Here)
        { Quality = new CharQuality(95, 60, 100, 20, 100, 100, 100, why, true) };

    private static OcrLine Line(string expected, string text, params OcrChar[] chars) =>
        new(text, expected, true, false, chars.Min(c => c.Score), chars, Here, 0, 0, 30);

    [Fact]
    public void EachFaultHasItsOwnExplainKey()
    {
        Assert.Equal("explain.ocv.missing", TextTool.ExplainOf(Line("A1", "A", Ok("A"), new OcrChar("1", "", 0, CharStatus.Missing, Nowhere))));
        Assert.Equal("explain.ocv.missing", TextTool.ExplainOf(new OcrLine("", "A1", true, false, 0, Array.Empty<OcrChar>(), Nowhere, 0, 0, 0)));
        Assert.Equal("explain.ocv.wrong", TextTool.ExplainOf(Line("A1", "A7", Ok("A"), new OcrChar("1", "7", 5, CharStatus.Wrong, Here))));
        Assert.Equal("explain.ocv.extra", TextTool.ExplainOf(Line("A1", "A17", Ok("A"), Ok("1"), new OcrChar("", "7", 90, CharStatus.Extra, Here))));
        Assert.Equal("explain.ocv.broken", TextTool.ExplainOf(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Faint))));
        Assert.Equal("explain.ocv.broken", TextTool.ExplainOf(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Broken))));
        Assert.Equal("explain.ocr.dots_missing", TextTool.ExplainOf(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Broken)), PrintType.DotMatrix));
    }

    [Fact]
    public void TheOperatorIsToldWhatIsWrongWithTheCharacter()
    {
        Assert.Contains("\"1\" in \"A1\" is printed faint", TextTool.Describe(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Faint))));
        Assert.Contains("has dots missing", TextTool.Describe(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Broken)), PrintType.DotMatrix));
        Assert.Contains("is broken", TextTool.Describe(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Broken))));
        Assert.Contains("is smeared", TextTool.Describe(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Smeared))));
        Assert.Contains("is out of place", TextTool.Describe(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Position))));
        Assert.Contains("printed poorly", TextTool.Describe(Line("A1", "A1", Ok("A"), Poor("1", PrintReason.Unclear))));
        Assert.Contains("Extra print \"7\"", TextTool.Describe(Line("A1", "A17", Ok("A"), Ok("1"), new OcrChar("", "7", 90, CharStatus.Extra, Here))));
        // a character without a quality (an older engine) still gets a sentence
        Assert.Contains("printed poorly", TextTool.Describe(Line("A1", "A1", Ok("A"), new OcrChar("1", "1", 30, CharStatus.Unsure, Here))));
    }

    [Fact]
    public void ADamagedReferenceIsASetupError()
    {
        var tool = new TextToolConfig { Name = "T", Mode = TextMode.Check, PrintReference = "not a reference", Lines = { new TextLineSpec { Template = "A1" } } };
        Assert.Contains("print quality is damaged", TextTool.SetupProblem(tool));
        tool.PrintReference = "VSPRINT1 10 4\nI 49 10 1000 100100\n";
        Assert.Null(TextTool.SetupProblem(tool));
        tool.PrintReference = "";
        Assert.Null(TextTool.SetupProblem(tool));
    }

    [Fact]
    public void TheReferenceTravelsWithTheJob()
    {
        const string reference = "VSPRINT1 12 4\nI 49 12 1200 120200\nC 0 12 720 43300\n";
        var job = new Job
        {
            Name = "Dates",
            Cameras = { new CameraConfig { Name = "Top" } },
            Tools = { new TextToolConfig { Name = "Lot", Mode = TextMode.Check, PrintReference = reference } },
        };
        var back = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!;
        Assert.Equal(reference, Assert.IsType<TextToolConfig>(back.Tools[0]).PrintReference);
        // a job saved before print quality existed checks with the recogniser only
        Assert.Equal("", new TextToolConfig().PrintReference);
    }
}
