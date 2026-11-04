using BarcodeReading;
using VisionCore;
using Xunit;

public class CodeGradeTests
{
    private static readonly DateTime Now = new(2026, 10, 29);
    private static readonly Dictionary<string, string> NoValues = new();
    private static readonly (CodeFormat, string)[] One = { (CodeFormat.Code39, "VS-39AV") };

    private static CodeQuality Q(int overall, params (string, int)[] grades) =>
        new(70, 0.6f, 3f, 0.9f, 0f, overall) { Grades = grades };

    [Fact]
    public void AGradeBelowTheMinimumFailsAndNamesTheWorstParameter()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39, MinGrade = 3 };
        var v = CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(2, ("Symbol contrast", 4), ("Modulation", 2), ("Decodability", 3)) });
        Assert.False(v.Pass);
        Assert.Equal("explain.verify.grade_low", v.Explain);
        Assert.Equal("Code grade is C, minimum B. Worst: modulation (C)", v.Summary);
        Assert.Contains(v.Details, d => d.Contains("Grade C (process control): Symbol contrast A, Modulation C, Decodability B"));
    }

    [Fact]
    public void AtOrAboveTheMinimumPasses()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39, MinGrade = 3 };
        Assert.True(CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(3, ("Modulation", 3)) }).Pass);
        Assert.True(CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(4) with { Calibrated = true } }).Pass);
    }

    [Fact]
    public void ACodeThatCouldNotBeGradedFails()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39, MinGrade = 1 };
        var dataBar = new[] { (CodeFormat.DataBar, "0109501101530010") };
        Assert.True(CodeTool.Judge(dataBar, cfg, null, NoValues, Now, new[] { Q(3) }).Pass);
        var v = CodeTool.Judge(dataBar, cfg, null, NoValues, Now, new CodeQuality?[] { null });
        Assert.False(v.Pass);
        Assert.Equal("explain.verify.not_graded", v.Explain);
        Assert.False(CodeTool.Judge(One, cfg, null, NoValues, Now).Pass); // no grades given at all
    }

    [Fact]
    public void GradingOffIgnoresQuality()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39 };
        Assert.True(CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(0) }).Pass);
    }

    [Fact]
    public void LettersAndSetup()
    {
        Assert.Equal("ABCDF-", string.Concat(new[] { 4, 3, 2, 1, 0, -1 }.Select(CodeQuality.Letter)));
        Assert.NotNull(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Code39, MinGrade = 7 }));
        Assert.NotNull(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Code39, MinGrade = 0 })); // "F or better" checks nothing
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Code39, MinGrade = 2 }));
        // every type the engine reads is graded (Micro QR, PDF417 and Aztec on their grids or rows; Pharmacode
        // and DotCode since PR #117): a lowest grade is taken; a code that cannot be graded then fails the part
        foreach (var f in new[] { CodeFormat.MicroQr, CodeFormat.Pdf417, CodeFormat.Aztec, CodeFormat.DotCode })
            Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = f, MinGrade = 2 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Pdf417 }));
        // GS1 DataBar is graded, single-row and stacked (a stacked one that cannot be graded fails the part)
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.DataBar, MinGrade = 2 }));
    }

    [Fact]
    public void RMqrTakesALowestGrade()
    {
        // rMQR is graded by ISO/IEC 15415 with its own fixed patterns (Req 17.1 GRD-02): a lowest grade and
        // recorded grades are taken; a code that cannot be graded then fails the part, as for every type
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.RMqr, MinGrade = 2 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.RMqr | CodeFormat.QrCode, RecordGrades = true }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.RMqr }));
    }

    [Fact]
    public void AnUngradedDataBarFailsWithALowestGrade()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.DataBar, MinGrade = 2 };
        var dataBar = new[] { (CodeFormat.DataBar, "0109501101530010") };
        Assert.True(CodeTool.Judge(dataBar, cfg, null, NoValues, Now, new[] { Q(3) }).Pass);
        var v = CodeTool.Judge(dataBar, cfg, null, NoValues, Now, new CodeQuality?[] { null });
        Assert.False(v.Pass);
        Assert.Equal("explain.verify.not_graded", v.Explain);
    }

    [Fact]
    public void AWrongLabelIsNamedBeforeAPoorPrint()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39, MinGrade = 3 };
        var v = CodeTool.Judge(One, cfg, "OTHER", NoValues, Now, new[] { Q(1, ("Modulation", 1)) });
        Assert.False(v.Pass);
        Assert.Equal("explain.code.wrong", v.Explain);
    }

    [Fact]
    public void WorstNamesRminAndTheQuietZoneGradedPerScan()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.Code39, MinGrade = 3 };
        // the engine reports Rmin and the quiet zones as their scans' mean grades: the low one is named
        var qz = CodeTool.Judge(One, cfg, null, NoValues, Now,
            new[] { Q(1, ("Symbol contrast", 4), ("Modulation", 4), ("Decode", 4), ("Rmin", 4), ("Quiet zone", 1)) });
        Assert.EndsWith("Worst: quiet zone (D)", qz.Summary);
        var rmin = CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(2, ("Symbol contrast", 4), ("Rmin", 2), ("Quiet zone", 4)) });
        Assert.EndsWith("Worst: rmin (C)", rmin.Summary);
        // scans that failed on different parameters: no mean is as low as the overall grade
        var v = CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(1, ("Symbol contrast", 4), ("Modulation", 4), ("Decode", 4)) });
        Assert.EndsWith("Worst: different parameters on different scans", v.Summary);
        var d = CodeTool.Judge(One, cfg, null, NoValues, Now, new[] { Q(1, ("Symbol contrast", 4), ("Decode", 0)) });
        Assert.EndsWith("Worst: decode (F)", d.Summary);
    }
}
