using Google.Protobuf;
using VisionSystem.Runtime;
using Xunit;
using V1 = VisionStudio.Remote.V1;

/// <summary>What a tool measured, carried in the contract's ToolResult for the line and TryJob alike: report only
/// (shown as information, never as OK) and a graded Read code's lowest grade (the margin chart).</summary>
public sealed class ToolMeasuresTests
{
    static V1.ToolResult Mapped(VisionCore.ToolResult t)
    {
        var tool = new V1.ToolResult { Name = t.ToolName, Pass = t.Pass };
        LineRuntime.AddMeasures(tool, t);
        return tool;
    }

    static VisionCore.ToolResult Result(bool pass = true) => new("T", pass, "", [], 0, []);

    [Fact]
    public void A_report_only_result_is_carried_as_report_only()
    {
        var tool = Mapped(Result() with { ReportOnly = true });
        Assert.True(tool.ReportOnly);
        Assert.True(tool.Pass);
        Assert.False(tool.HasCodeGrade);
        Assert.False(tool.HasPrintQuality);
        Assert.False(Mapped(Result()).ReportOnly);
    }

    [Fact]
    public void A_graded_code_carries_its_lowest_grade_and_an_ungraded_one_none()
    {
        var graded = Mapped(Result() with { Values = new Dictionary<string, double> { ["grade"] = 2, ["grade.modulation"] = 3 } });
        Assert.True(graded.HasCodeGrade);
        Assert.Equal(2f, graded.CodeGrade);
        // codes that could not be graded give no grade, never a grade of F
        var ungraded = Mapped(Result(pass: false) with { Values = new Dictionary<string, double> { ["grade.ungraded"] = 1 } });
        Assert.False(ungraded.HasCodeGrade);
        // one code graded A and one not: the tool failed, so no grade (never an A for the picture)
        var partly = Mapped(Result(pass: false) with { Values = new Dictionary<string, double> { ["grade"] = 4, ["grade.ungraded"] = 1 } });
        Assert.False(partly.HasCodeGrade);
    }

    [Fact]
    public void Check_text_keeps_its_print_quality()
    {
        var tool = Mapped(Result() with { PrintQuality = 72 });
        Assert.Equal(72f, tool.PrintQuality);
        Assert.False(tool.ReportOnly);
    }

    [Fact]
    public void An_older_runtimes_result_reads_as_deciding_and_ungraded()
    {
        // fields 9 and 10 absent on the wire, as from a runtime before this contract
        var old = V1.ToolResult.Parser.ParseFrom(new V1.ToolResult { Name = "T", Pass = true }.ToByteArray());
        Assert.False(old.ReportOnly);
        Assert.False(old.HasCodeGrade);
    }
}
