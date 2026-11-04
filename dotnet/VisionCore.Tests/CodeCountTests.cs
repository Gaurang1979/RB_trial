using BarcodeReading;
using VisionCore;
using Xunit;

/// <summary>Code Reader count: fewer or more codes than expected fails (more = a foreign or extra pack in the case).</summary>
public class CodeCountTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 10, 0, 0);
    private static readonly Dictionary<string, string> NoValues = new();
    private static CodeToolConfig Tool(int count) => new() { Name = "Case", Format = CodeFormat.QrCode, ExpectedCount = count };
    private static (CodeFormat, string)[] Read(int n) => Enumerable.Range(1, n).Select(i => (CodeFormat.QrCode, $"SN{i:D4}")).ToArray();

    [Fact]
    public void ExactCountPasses() => Assert.True(CodeTool.Judge(Read(12), Tool(12), null, NoValues, Now).Pass);

    [Fact]
    public void MoreCodesThanExpectedFails()
    {
        var v = CodeTool.Judge(Read(13), Tool(12), null, NoValues, Now);
        Assert.False(v.Pass);
        Assert.Equal("explain.code.extra", v.Explain);
        Assert.StartsWith("13 codes read, 12 expected", v.Summary);
    }

    [Fact]
    public void ASecondCodeOnAOneCodeToolFails()
    {
        var v = CodeTool.Judge(Read(2), new CodeToolConfig { Name = "C", Format = CodeFormat.QrCode }, null, NoValues, Now);
        Assert.Equal((false, "explain.code.extra"), (v.Pass, v.Explain));
    }

    [Fact]
    public void ExpectedCountZeroSetsNoUpperLimit() => Assert.True(CodeTool.Judge(Read(30), Tool(0), null, NoValues, Now).Pass);

    [Fact]
    public void AForeignCodeWithTheWrongTextNamesTheText()
    {
        var codes = new[] { (CodeFormat.QrCode, "GOOD"), (CodeFormat.QrCode, "OTHER") };
        var v = CodeTool.Judge(codes, Tool(1), "GOOD", NoValues, Now);
        Assert.Equal((false, "explain.code.wrong"), (v.Pass, v.Explain));
    }

    [Fact]
    public void ACopyOverWhatTheLabelCarriesFailsAsAnExtraPack()
    {
        var v = CodeTool.Judge(Read(12), Tool(12), null, NoValues, Now, copiesOver: 1);
        Assert.Equal((false, "explain.code.extra_copy"), (v.Pass, v.Explain));
        Assert.Contains("copy of a code", v.Summary);
        Assert.True(CodeTool.Judge(Read(12), Tool(0), null, NoValues, Now, copiesOver: 1).Pass); // count unknown
    }

    [Fact]
    public void ShortStillFailsAsShort() => Assert.Equal("explain.code.short", CodeTool.Judge(Read(11), Tool(12), null, NoValues, Now).Explain);
}
