using VisionCore;
using VisionStudio.Services;
using Xunit;

public sealed class ToolNoticeTests
{
    private static TextToolConfig Read(params string[] formats) => new()
    {
        Mode = TextMode.Read,
        Lines = formats.Select(f => new TextLineSpec { Entry = LineEntry.Format, Template = f }).ToList(),
    };

    [Fact]
    public void TeachWarnsOfAReadTextToolThatWouldPassAnyText()
    {
        Assert.Equal("explain.text.no_rule", ToolNotice.Of(Read()));
        Assert.Equal("explain.text.no_rule", ToolNotice.Of(Read("LOT {D:5}", "{?}")));
        Assert.Null(ToolNotice.Of(Read("LOT {D:5}")));
        Assert.Null(ToolNotice.Of(new TextToolConfig { Mode = TextMode.Check }));
        Assert.Null(ToolNotice.Of(new CodeToolConfig()));
        var report = Read();
        report.ReportOnly = true;
        Assert.Equal("explain.text.report_only", ToolNotice.Of(report));
    }

    [Fact]
    public void AReportOnlyResultIsNeverShownAsOk()
    {
        ToolResult R(bool pass, bool report = false) => new("T", pass, "", [], 0, []) { ReportOnly = report };
        Assert.Equal(("ⓘ", "info"), (ToolNotice.Mark(R(true, true)), ToolNotice.Row(R(true, true))));
        Assert.Equal(("✓", "ok"), (ToolNotice.Mark(R(true)), ToolNotice.Row(R(true))));
        Assert.Equal(("✗", "ng"), (ToolNotice.Mark(R(false)), ToolNotice.Row(R(false))));
    }

    [Fact]
    public void TheExplainKeysAreInTheEnglishTable()
    {
        string? dir = AppContext.BaseDirectory;
        string? en = null;
        for (var d = new DirectoryInfo(dir); d is not null && en is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VisionStudio", "i18n", "en.json"))) en = Path.Combine(d.FullName, "VisionStudio", "i18n", "en.json");
        Assert.NotNull(en);
        var keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(en!))!;
        foreach (var k in new[] { TextTool.NoRuleExplain, TextTool.ReportOnlyExplain, "text.report_only", "text.report_only_tag", "text.no_rule_tag" })
            Assert.True(keys.ContainsKey(k), k);
    }
}
