using System.Text.Json;
using VisionCore;
using Xunit;

public class TaughtFontTests
{
    private const string Font = "VSFONT1\n48 16 00\n48 17 00\n65 18 00\n";

    [Fact]
    public void FontReaderWithoutAFontIsASetupError()
    {
        var tool = new TextToolConfig { Name = "T", Mode = TextMode.Read, Reader = OcrReader.Font, ReportOnly = true };
        Assert.Contains("No font taught", TextTool.SetupProblem(tool));
        tool.Font = Font;
        Assert.Null(TextTool.SetupProblem(tool));
        Assert.Null(TextTool.SetupProblem(new TextToolConfig { Name = "T", Mode = TextMode.Read, ReportOnly = true })); // AI needs no font
    }

    [Fact]
    public void GlyphsAreCountedOncePerCharacter()
    {
        Assert.Equal(2, TextTool.FontGlyphs(Font));
        Assert.Equal(0, TextTool.FontGlyphs(""));
    }

    [Fact]
    public void ReaderAndFontTravelWithTheJob()
    {
        var job = new Job
        {
            Name = "Dates",
            Cameras = { new CameraConfig { Name = "Top" } },
            Tools = { new TextToolConfig { Name = "Lot", Mode = TextMode.Read, Reader = OcrReader.Font, Font = Font } },
        };
        var back = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!;
        var t = Assert.IsType<TextToolConfig>(back.Tools[0]);
        Assert.Equal((OcrReader.Font, Font), (t.Reader, t.Font));
        Assert.Equal(OcrReader.Font, t.ToSettings().Reader);
        // a job saved before the taught font existed reads with the AI
        Assert.Equal(OcrReader.Ai, new TextToolConfig().Reader);
    }
}
