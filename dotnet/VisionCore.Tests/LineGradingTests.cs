using BarcodeReading;
using VisionCore;
using Xunit;

public class LineGradingTests
{
    private static readonly DateTime Now = new(2026, 10, 29);
    private static readonly (CodeFormat, string)[] One = { (CodeFormat.Code39, "VS-39AV") };

    private static CodeQuality Q(int overall, params (string, int)[] grades) => new(70, 0.6f, 3f, 0.9f, 0f, overall) { Grades = grades };

    [Fact]
    public void EveryNthPartIsGraded()
    {
        Assert.Equal(new[] { true, false, false, true, false, false, true }, Enumerable.Range(0, 7).Select(i => CodeTool.IsSample(i, 3)));
        Assert.All(Enumerable.Range(0, 5), i => Assert.True(CodeTool.IsSample(i, 1)));
        Assert.True(CodeTool.IsSample(0, 0)); // a bad setting grades every part rather than none
    }

    [Fact]
    public void PartsBetweenSamplesAreNotJudgedOnGrade()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39, MinGrade = 3, GradeEvery = 10 };
        // not a sample: no grade, no NG for it
        Assert.True(CodeTool.Judge(One, cfg, null, new Dictionary<string, string>(), Now, new CodeQuality?[] { null }, graded: false).Pass);
        // a sample below the minimum: NG, as on every part
        Assert.False(CodeTool.Judge(One, cfg, null, new Dictionary<string, string>(), Now, new[] { Q(2, ("Modulation", 2)) }, graded: true).Pass);
        // a sample that could not be graded: NG
        Assert.False(CodeTool.Judge(One, cfg, null, new Dictionary<string, string>(), Now, new CodeQuality?[] { null }, graded: true).Pass);
        // the text is still checked on every part
        Assert.False(CodeTool.Judge(One, cfg, "OTHER", new Dictionary<string, string>(), Now, new CodeQuality?[] { null }, graded: false).Pass);
    }

    [Fact]
    public void GradeValuesTakeTheWorstCode()
    {
        var v = CodeTool.GradeValues(new CodeQuality?[]
        {
            Q(3, ("Symbol contrast", 4), ("Modulation", 3)) with { SymbolContrast = 80, Modulation = 0.7f },
            Q(2, ("Symbol contrast", 3), ("Modulation", 2)) with { SymbolContrast = 65, Modulation = 0.5f },
            null,
        });
        Assert.Equal(2, v["grade"]);
        Assert.Equal(3, v["grade.symbol_contrast"]);
        Assert.Equal(2, v["grade.modulation"]);
        Assert.Equal(65, v["symbol_contrast_pct"]);
        Assert.Equal(0.5, v["modulation"], 3);
        Assert.Equal(1, v["grade.ungraded"]);
        Assert.Empty(CodeTool.GradeValues(Array.Empty<CodeQuality?>()));
    }

    [Fact]
    public void GradeValuesCarryTheToolsLowestGradeWhenSet()
    {
        var q = new CodeQuality?[] { Q(3, ("Modulation", 3)) };
        Assert.False(CodeTool.GradeValues(q).ContainsKey("grade_min"));
        Assert.False(CodeTool.GradeValues(q, -1).ContainsKey("grade_min"));
        Assert.Equal(2, CodeTool.GradeValues(q, 2)["grade_min"]);
        // not graded at all: still sent, so the plant server judges "ungraded" against the line's limit
        Assert.Equal(3, CodeTool.GradeValues(new CodeQuality?[] { null }, 3)["grade_min"]);
        // the job result names it per tool, as the plant server reads it
        var t = new ToolResult("Lot code", true, "", Array.Empty<string>(), 1, Array.Empty<Graphic>()) { Values = CodeTool.GradeValues(q, 2) };
        Assert.Equal(2, new JobResult(true, new[] { t }, 1, null).Values["Lot code.grade_min"]);
    }

    [Fact]
    public void JobResultValuesArePrefixedByTool()
    {
        var a = new ToolResult("Lot code", true, "", Array.Empty<string>(), 1, Array.Empty<Graphic>()) { Values = new Dictionary<string, double> { ["grade"] = 3 } };
        var b = new ToolResult("Case", true, "", Array.Empty<string>(), 1, Array.Empty<Graphic>());
        var r = new JobResult(true, new[] { a, b }, 2, null);
        Assert.Equal(new Dictionary<string, double> { ["Lot code.grade"] = 3 }, r.Values);
    }

    // A drawn Code 39 "*AB12*": narrow 4 px, wide 10 px, quiet zone 80 px, dark 30 on light 220.
    internal static GrayImage Code39()
    {
        var table = new Dictionary<char, string>
        {
            ['*'] = "nwnnwnwnn", ['A'] = "wnnnnwnnw", ['B'] = "nnwnnwnnw", ['1'] = "wnnwnnnnw", ['2'] = "nnwwnnnnw",
        };
        var runs = new List<(int Width, bool Dark)>();
        foreach (var ch in "*AB12*")
        {
            for (int k = 0; k < 9; k++) runs.Add((table[ch][k] == 'w' ? 10 : 4, k % 2 == 0));
            runs.Add((4, false));   // gap between characters
        }
        int w = 80 + runs.Sum(r => r.Width) + 80, h = 160;
        var px = Enumerable.Repeat((byte)220, w * h).ToArray();
        int x = 80;
        foreach (var (width, dark) in runs)
        {
            if (dark)
                for (int y = 20; y < h - 20; y++)
                    for (int i = x; i < x + width; i++) px[y * w + i] = 30;
            x += width;
        }
        return new GrayImage(px, w, h);
    }

    [Fact]
    public void ToolGradesOnlyTheSampledParts()
    {
        // Sampling is the point, not speed: a generous budget so a busy machine still reads the clean
        // drawn code (the first read also pays the engine's start-up). A fast read returns at once.
        var cfg = new CodeToolConfig { Name = "Lot", Format = CodeFormat.Code39, RecordGrades = true, GradeEvery = 2, Expected = "AB12", TimeBudgetMs = 10_000 };
        using var tool = new CodeTool(cfg, new ModelPaths(Path.GetTempPath()));
        var img = Code39();
        var runs = Enumerable.Range(0, 3).Select(_ => tool.Run(img, new BatchFields())).ToList();
        Assert.All(runs, r => Assert.True(r.Pass, r.Summary));
        // parts 1 and 3 are samples: graded, grades kept for the plant trend; part 2 is not graded
        Assert.Contains("grade", runs[0].Values.Keys);
        Assert.NotNull(runs[0].Codes![0].Quality);
        Assert.Empty(runs[1].Values);
        Assert.Null(runs[1].Codes![0].Quality);
        Assert.Contains("grade", runs[2].Values.Keys);
    }

    [Fact]
    public void GradingSettings()
    {
        Assert.False(new CodeToolConfig().Grading);
        Assert.True(new CodeToolConfig { RecordGrades = true }.Grading);
        Assert.True(new CodeToolConfig { MinGrade = 2 }.Grading);
        Assert.NotNull(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Code39, GradeEvery = 0 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Code39, RecordGrades = true, GradeEvery = 50 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pdf417, RecordGrades = true })); // graded by rows
        var json = System.Text.Json.JsonSerializer.Serialize(new CodeToolConfig { RecordGrades = true, GradeEvery = 20 }, JobStore.Json);
        Assert.DoesNotContain("\"grading\"", json, StringComparison.OrdinalIgnoreCase);
        var back = System.Text.Json.JsonSerializer.Deserialize<CodeToolConfig>(json, JobStore.Json)!;
        Assert.Equal((true, 20), (back.RecordGrades, back.GradeEvery));
    }
}
