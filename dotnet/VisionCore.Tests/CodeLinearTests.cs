using BarcodeReading;
using VisionCore;
using Xunit;

/// <summary>Code Reader spec 5.2: the quiet-zone check and the explanations of 1D reads the rules dropped.</summary>
public class CodeLinearTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 10, 0, 0);
    private static readonly Dictionary<string, string> NoValues = new();
    private static CodeToolConfig Tool(CodeFormat f = CodeFormat.Code39) => new() { Name = "1D", Format = f };
    private static CodeTool.Linear1d Measured(params bool?[] quietZones) => new(quietZones, 0, 0);

    [Fact]
    public void QuietZoneCheckIsOnByDefault() => Assert.True(new CodeToolConfig().QuietZoneCheck);

    [Fact]
    public void ClearQuietZonePasses()
    {
        var v = CodeTool.Judge(new[] { (CodeFormat.Code39, "VS-39AV") }, Tool(), null, NoValues, Now, null, linear: Measured(true));
        Assert.True(v.Pass, v.Summary);
    }

    [Fact]
    public void MarkInQuietZoneFails()
    {
        var v = CodeTool.Judge(new[] { (CodeFormat.Code39, "VS-39AV") }, Tool(), null, NoValues, Now, null, linear: Measured(false));
        Assert.False(v.Pass);
        Assert.Equal("explain.code1d.quiet_zone", v.Explain);
        Assert.False(v.CodeOk[0]);
    }

    [Fact]
    public void QuietZoneNotMeasuredFails()
    {
        // a zone in doubt is NG (line safety)
        var v = CodeTool.Judge(new[] { (CodeFormat.Ean13, "4006381333931") }, Tool(CodeFormat.Ean13), null, NoValues, Now, null, linear: Measured(new bool?[] { null }));
        Assert.False(v.Pass);
        Assert.Equal("explain.code1d.quiet_zone", v.Explain);
    }

    [Fact]
    public void QuietZoneCheckOffOrNotApplicable()
    {
        var off = Tool();
        off.QuietZoneCheck = false;
        Assert.True(CodeTool.Judge(new[] { (CodeFormat.Code39, "VS-39AV") }, off, null, NoValues, Now, null, linear: Measured(false)).Pass);
        // DataBar needs no quiet zone; 2D codes have their own measures
        Assert.True(CodeTool.Judge(new[] { (CodeFormat.DataBar, "0109506000134352") }, Tool(CodeFormat.DataBar), null, NoValues, Now, null, linear: Measured(new bool?[] { null })).Pass);
        Assert.True(CodeTool.Judge(new[] { (CodeFormat.QrCode, "X") }, Tool(CodeFormat.QrCode), null, NoValues, Now, null, linear: Measured(new bool?[] { null })).Pass);
    }

    [Fact]
    public void WrongTextIsNamedBeforeQuietZone()
    {
        var v = CodeTool.Judge(new[] { (CodeFormat.Code39, "VS-39AV") }, Tool(), "OTHER", NoValues, Now, null, linear: Measured(false));
        Assert.Equal("explain.code.wrong", v.Explain);
    }

    [Fact]
    public void ShortReadNamesTheDroppedRule()
    {
        var none = Array.Empty<(CodeFormat, string)>();
        var check = CodeTool.Judge(none, Tool(), null, NoValues, Now, null, linear: new CodeTool.Linear1d(Array.Empty<bool?>(), 2, 1));
        Assert.False(check.Pass);
        Assert.Equal("explain.code1d.check_digit", check.Explain);
        var partial = CodeTool.Judge(none, Tool(), null, NoValues, Now, null, linear: new CodeTool.Linear1d(Array.Empty<bool?>(), 3, 0));
        Assert.Equal("explain.code1d.too_short", partial.Explain);
        Assert.Equal("explain.code.none", CodeTool.Judge(none, Tool(), null, NoValues, Now, null, linear: Measured()).Explain);

        var cfg = Tool();
        cfg.ExpectedCount = 2;
        var one = CodeTool.Judge(new[] { (CodeFormat.Code39, "VS-39AV") }, cfg, null, NoValues, Now, null,
            linear: new CodeTool.Linear1d(new bool?[] { true }, 1, 0));
        Assert.False(one.Pass);
        Assert.Equal("explain.code1d.too_short", one.Explain);
    }

    [Fact]
    public void QuietZoneCheckSurvivesTheJobFile()
    {
        var cfg = Tool();
        cfg.QuietZoneCheck = false;
        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<CodeToolConfig>(json)!.QuietZoneCheck);
        // a job saved before the check existed gets it on
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<CodeToolConfig>("{\"Name\":\"1D\"}")!.QuietZoneCheck);
    }
}
