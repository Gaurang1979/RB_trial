using System.Text.Json;
using BarcodeReading;
using VisionCore;
using Xunit;

/// <summary>"Any type" is for teaching only (AS-12a): a job's Read code tool on it is refused, the teaching probe is not.</summary>
public class CodeAnyTypeTests
{
    private static CodeToolConfig Tool(CodeFormat f) => new() { Name = "Case", Format = f };

    [Theory]
    [InlineData(0x7FFFF)]
    [InlineData(0x3FFFF)]   // an older job's "any"
    [InlineData(0xFFFF)]
    public void AnyTypeIsRefusedInAJob(int f)
    {
        Assert.Contains("teaching only", CodeTool.SetupProblem(Tool((CodeFormat)f)));
        var e = Assert.Throws<ArgumentException>(() => new Job { Name = "J", Tools = { Tool((CodeFormat)f) } }.Validate());
        Assert.StartsWith("Case: Pick the code type", e.Message);
    }

    [Fact]
    public void AnyTypeIsAllowedWhileTeaching() => Assert.Null(CodeTool.SetupProblem(Tool(CodeFormat.Any), teaching: true));

    [Fact]
    public void ChosenTypesStillRun()
    {
        Assert.Null(CodeTool.SetupProblem(Tool(CodeFormat.QrCode)));
        Assert.Null(CodeTool.SetupProblem(Tool(CodeFormat.QrCode | CodeFormat.DataMatrix)));
        Assert.Null(CodeTool.SetupProblem(Tool(CodeFormat.AllLinear)));
        new Job { Name = "J", Tools = { Tool(CodeFormat.DataMatrix) } }.Validate();
    }

    [Fact]
    public void AnOlderJobSavedWithAnyIsRefused()
    {
        var old = JsonSerializer.Deserialize<CodeToolConfig>("{\"Name\":\"Case\",\"Format\":65535}", JobStore.Json)!;
        Assert.NotNull(CodeTool.SetupProblem(old));
    }

    [Fact]
    public void AJobToolOnAnyFailsEveryPartButTheTeachingProbeReads()
    {
        var image = new GrayImage(new byte[64 * 64], 64, 64);
        var models = new ModelPaths(Path.GetTempPath());
        using (var job = new CodeTool(Tool(CodeFormat.Any), models))
        {
            var r = job.Run(image, new BatchFields());
            Assert.False(r.Pass);
            Assert.Equal("explain.code.setup", r.Explain);
        }
        using var probe = new CodeTool(new CodeToolConfig { Name = "probe", Format = CodeFormat.Any, ExpectedCount = 0 }, models, teaching: true);
        Assert.NotEqual("explain.code.setup", probe.Run(image, new BatchFields()).Explain);
    }
}
