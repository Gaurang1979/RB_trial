using BarcodeReading;
using VisionCore;
using Xunit;

/// <summary>Read code: the EAN/UPC add-on check (required, expected text).</summary>
public class CodeAddOnTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 10, 0, 0);
    private static readonly Dictionary<string, string> NoValues = new();
    private static readonly (CodeFormat, string)[] One = { (CodeFormat.Ean13, "4006381333931") };

    private static CodeToolConfig Tool(bool required = true, string expected = "", CodeFormat f = CodeFormat.Ean13) =>
        new() { Name = "EAN", Format = f, AddOnRequired = required, ExpectedAddOn = expected };

    private static CodeTool.Linear1d Read(params string[] addOns) =>
        new(addOns.Select(_ => (bool?)true).ToList(), 0, 0) { AddOns = addOns };

    [Fact]
    public void OffByDefaultAndAnAddOnIsNotNeeded()
    {
        var cfg = new CodeToolConfig { Name = "EAN" };
        Assert.False(cfg.ChecksAddOn);
        Assert.True(CodeTool.Judge(One, cfg, null, NoValues, Now, linear: Read("")).Pass);
    }

    [Fact]
    public void RequiredAddOnPresentPasses()
    {
        var v = CodeTool.Judge(One, Tool(), null, NoValues, Now, linear: Read("12345"));
        Assert.True(v.Pass, v.Summary);
        Assert.Contains("  Add-on: 12345", v.Details);
    }

    [Fact]
    public void RequiredAddOnMissingFails()
    {
        // missing, unreadable and reads that disagreed all come as "" from the engine
        var v = CodeTool.Judge(One, Tool(), null, NoValues, Now, linear: Read(""));
        Assert.False(v.Pass);
        Assert.Equal("explain.code1d.addon_missing", v.Explain);
        Assert.False(v.CodeOk[0]);
    }

    [Fact]
    public void AddOnsNotReportedCountAsMissing()
    {
        // judged without the engine's add-ons (in doubt): NG
        var v = CodeTool.Judge(One, Tool(), null, NoValues, Now);
        Assert.False(v.Pass);
        Assert.Equal("explain.code1d.addon_missing", v.Explain);
    }

    [Fact]
    public void WrongAddOnFails()
    {
        var v = CodeTool.Judge(One, Tool(expected: "05"), null, NoValues, Now, linear: Read("06"), expectedAddOn: "05");
        Assert.False(v.Pass);
        Assert.Equal("explain.code1d.addon_wrong", v.Explain);
        Assert.Equal("Add-on reads \"06\", expected \"05\"", v.Summary);
    }

    [Fact]
    public void ExpectedAddOnMakesItRequired()
    {
        var cfg = Tool(required: false, expected: "05");
        Assert.True(cfg.ChecksAddOn);
        Assert.Equal("explain.code1d.addon_missing", CodeTool.Judge(One, cfg, null, NoValues, Now, linear: Read(""), expectedAddOn: "05").Explain);
        Assert.True(CodeTool.Judge(One, cfg, null, NoValues, Now, linear: Read("05"), expectedAddOn: "05").Pass);
    }

    [Fact]
    public void WrongMainTextIsNamedBeforeTheAddOn()
    {
        var v = CodeTool.Judge(One, Tool(expected: "05"), "4006381333948", NoValues, Now, linear: Read("06"), expectedAddOn: "05");
        Assert.Equal("explain.code.wrong", v.Explain);
        Assert.Contains("  NG Add-on reads \"06\", expected \"05\"", v.Details);
    }

    [Fact]
    public void EachCodeNeedsItsOwnAddOn()
    {
        var two = new[] { (CodeFormat.Ean13, "4006381333931"), (CodeFormat.Ean13, "9780201379624") };
        var cfg = Tool();
        cfg.ExpectedCount = 2;
        var v = CodeTool.Judge(two, cfg, null, NoValues, Now, linear: Read("12345", ""));
        Assert.False(v.Pass);
        Assert.Equal(new[] { true, false }, v.CodeOk);
    }

    [Fact]
    public void QuietZoneIsCheckedAfterTheAddOn()
    {
        var linear = new CodeTool.Linear1d(new bool?[] { false }, 0, 0) { AddOns = new[] { "" } };
        var v = CodeTool.Judge(One, Tool(), null, NoValues, Now, linear: linear);
        Assert.Equal("explain.code1d.addon_missing", v.Explain);
    }

    [Theory]
    [InlineData(CodeFormat.UpcA)]
    [InlineData(CodeFormat.UpcE)]
    [InlineData(CodeFormat.Ean8)]
    [InlineData(CodeFormat.Ean13 | CodeFormat.UpcA)]
    public void EanUpcTypesMayCheckTheAddOn(CodeFormat f) => Assert.Null(CodeTool.SetupProblem(Tool(f: f)));

    [Theory]
    [InlineData(CodeFormat.Gs1_128)]
    [InlineData(CodeFormat.QrCode)]
    [InlineData(CodeFormat.Ean13 | CodeFormat.Gs1_128)]
    public void OtherTypesCannotCheckTheAddOn(CodeFormat f) => Assert.Contains("Add-on check", CodeTool.SetupProblem(Tool(f: f)));

    [Theory]
    [InlineData("05", true)]
    [InlineData("12345", true)]
    [InlineData("{ISSUE}", true)]
    [InlineData("123", false)]
    [InlineData("AB", false)]
    public void ExpectedAddOnMustBeTwoOrFiveDigits(string expected, bool ok) =>
        Assert.Equal(ok, CodeTool.SetupProblem(Tool(expected: expected)) is null);

    [Fact]
    public void ExpectedAddOnAsksForItsBatchField() =>
        Assert.Contains(Tool(expected: "{ISSUE}").Fields(), f => f.Key == "ISSUE");

    [Fact]
    public void SettingsSurviveTheJobFile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Tool(expected: "05"), JobStore.Json);
        Assert.DoesNotContain("ChecksAddOn", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<CodeToolConfig>(json, JobStore.Json)!;
        Assert.True(back.AddOnRequired);
        Assert.Equal("05", back.ExpectedAddOn);
        // a job saved before the setting existed does not check the add-on
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<CodeToolConfig>("{\"Name\":\"EAN\"}", JobStore.Json)!.ChecksAddOn);
    }
}
