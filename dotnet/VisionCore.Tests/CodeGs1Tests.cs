using System.Text.Json;
using BarcodeReading;
using VisionCore;
using Xunit;

public class CodeGs1Tests
{
    private const char GS = '\u001D';
    private static readonly DateTime Now = new(2026, 10, 29, 10, 0, 0);
    private static readonly string Good = $"0109506000134352172912311042AB{GS}21SER1";
    private static readonly Dictionary<string, string> NoValues = new();

    private static CodeToolConfig Tool() => new()
    {
        Name = "DM", Format = CodeFormat.DataMatrix,
        Gs1 = new Gs1Rules { Required = { "01", "17", "10", "21" } },
    };

    [Fact]
    public void GoodCodePasses()
    {
        var v = CodeTool.Judge(new[] { (CodeFormat.DataMatrix, Good) }, Tool(), null, NoValues, Now);
        Assert.True(v.Pass, v.Summary);
        Assert.Contains(v.Details, d => d.Contains("(10) batch: 42AB"));
    }

    [Fact]
    public void ExpectedAiValuesFromTheBatch()
    {
        var cfg = Tool();
        var values = new Dictionary<string, string> { ["01"] = "09506000134352", ["10"] = "42AC" };
        var v = CodeTool.Judge(new[] { (CodeFormat.DataMatrix, Good) }, cfg, null, values, Now);
        Assert.False(v.Pass);
        Assert.Equal("explain.gs1.wrong_value", v.Explain);
        Assert.Contains("batch (10) is 42AB, expected 42AC", v.Summary);
    }

    [Fact]
    public void EveryCodeIsCheckedAndTheBadOneIsMarked()
    {
        var cfg = Tool();
        cfg.ExpectedCount = 2;
        var bad = $"0109506000134353172912311042AB{GS}21SER2"; // GTIN check digit wrong
        var v = CodeTool.Judge(new[] { (CodeFormat.DataMatrix, Good), (CodeFormat.DataMatrix, bad) }, cfg, null, NoValues, Now);
        Assert.False(v.Pass);
        Assert.Equal(new[] { true, false }, v.CodeOk);
        Assert.Equal("explain.gs1.check_digit", v.Explain);
    }

    [Fact]
    public void WithoutGs1RulesOnlyTheTextCounts()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Gs1_128 };
        Assert.True(CodeTool.Judge(new[] { (CodeFormat.Gs1_128, "not gs1 at all") }, cfg, null, NoValues, Now).Pass);
        Assert.False(CodeTool.Judge(Array.Empty<(CodeFormat, string)>(), cfg, null, NoValues, Now).Pass);
        Assert.Equal("explain.code.wrong", CodeTool.Judge(new[] { (CodeFormat.Gs1_128, "A") }, cfg, "B", NoValues, Now).Explain);
    }

    [Fact]
    public void TeachingSwitchesTheCheckOnWithTheAisRead()
    {
        var code = new CodeToolConfig();
        CodeTeach.Lock(code, new[] { (CodeFormat.DataMatrix, Good) });
        Assert.NotNull(code.Gs1);
        Assert.Equal(new[] { "01", "17", "10", "21" }, code.Gs1!.Required);
        var plain = new CodeToolConfig();
        CodeTeach.Lock(plain, new[] { (CodeFormat.QrCode, "https://example.com/menu") });
        Assert.Null(plain.Gs1); // not GS1 data: no check
        // numbers that only look like AIs: an EAN-13 from a German prefix, an ITF-14, a Code 128 without a GS1 key
        foreach (var (f, t) in new[] { (CodeFormat.Ean13, "4006381333931"), (CodeFormat.Itf, "10012345678902"), (CodeFormat.Gs1_128, "10AB1234") })
        {
            var c = new CodeToolConfig();
            CodeTeach.Lock(c, new[] { (f, t) });
            Assert.Null(c.Gs1);
        }
        // a measure (3103 kg, 3 decimals) changes AI with the next pack's decimals: not made required
        var meat = new CodeToolConfig();
        CodeTeach.Lock(meat, new[] { (CodeFormat.DataBar, "01095060001343523103000750") });
        Assert.Equal(new[] { "01" }, meat.Gs1!.Required);
    }

    [Fact]
    public void AiListsAndValuesAreParsed()
    {
        Assert.Equal(new[] { "01", "10", "17" }, CodeTeach.ParseAis("(01), 10 17 10"));
        Assert.Throws<FormatException>(() => CodeTeach.ParseAis("01 55"));
        var d = CodeTeach.ParseAiValues("01={GTIN}; (10) = {BATCH}");
        Assert.Equal("{BATCH}", d["10"]);
        Assert.Equal("01={GTIN}; 10={BATCH}", CodeTeach.FormatAiValues(d));
        Assert.Throws<FormatException>(() => CodeTeach.ParseAiValues("10"));
    }

    [Fact]
    public void SetupProblemsAndBatchFields()
    {
        var cfg = Tool();
        cfg.Gs1Values["10"] = "{BATCH}";
        cfg.Gs1Values["01"] = "{GTIN}";
        Assert.Null(CodeTool.SetupProblem(cfg));
        Assert.Equal(new[] { "BATCH", "GTIN" }, cfg.Fields().Select(f => f.Key).OrderBy(k => k));
        cfg.Gs1Values["10"] = " ";
        Assert.NotNull(CodeTool.SetupProblem(cfg));
        cfg.Gs1Values.Remove("10");
        cfg.Gs1!.Required.Add("55");
        Assert.NotNull(CodeTool.SetupProblem(cfg));
        // a GS1 check with nothing required checks too little: the engineer names the AIs
        Assert.NotNull(CodeTool.SetupProblem(new CodeToolConfig { Name = "C", Format = CodeFormat.DataMatrix, Gs1 = new Gs1Rules() }));
    }

    [Fact]
    public void JobSavesTheRulesAndRefusesAnUnprotectedItf()
    {
        var job = new Job { Name = "Case", Cameras = { new CameraConfig { Name = "Top" } }, Tools = { Tool() } };
        var back = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!;
        Assert.Equal(new[] { "01", "17", "10", "21" }, Assert.IsType<CodeToolConfig>(back.Tools[0]).Gs1!.Required);
        back.Validate();
        var itf = new Job { Name = "Shipper", Cameras = { new CameraConfig { Name = "Top" } }, Tools = { new CodeToolConfig { Name = "ITF", Format = CodeFormat.Itf } } };
        Assert.Contains("ITF needs", Assert.Throws<ArgumentException>(() => itf.Validate()).Message);
    }
}
