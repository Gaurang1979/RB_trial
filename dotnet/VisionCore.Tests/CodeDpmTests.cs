using BarcodeReading;
using VisionCore;
using Xunit;

// Read code tool graded after ISO/IEC TR 29158 (direct part marks, tool specification 6.3)
public class CodeDpmTests
{
    private static readonly DateTime Now = new(2026, 10, 30);
    private static readonly Dictionary<string, string> NoValues = new();
    private static readonly (CodeFormat, string)[] One = { (CodeFormat.DataMatrix, "PN 4711") };

    private static DpmGrade G(int overall, bool calibrated = false) =>
        new(overall, 80, DpmLighting.Low30Four, calibrated)
        {
            WavelengthNm = 660, CellContrast = 0.4f, CellModulation = 0.6f, CellPx = 10,
            Grades = new[] { ("Decode", 4), ("Cell contrast", 4), ("Cell modulation", overall), ("Fixed pattern damage", 4) },
        };

    private static DecodedCode Code(CodeQuality? quality, DpmGrade? dpm) =>
        new("PN 4711", CodeFormat.DataMatrix, default, new BarcodeReading.PointF[4], 1) { Quality = quality, Dpm = dpm };

    [Fact]
    public void TheNotationCarriesApertureWavelengthAndLighting()
    {
        Assert.Equal("3/80/660/30Q", G(3).Notation);
        Assert.Equal("4/80/-/D", (G(4) with { WavelengthNm = 0, Lighting = DpmLighting.Dome }).Notation);
        Assert.Equal("90", DpmGrade.LightingCode(DpmLighting.Diffuse90));
        Assert.Equal("30T", DpmGrade.LightingCode(DpmLighting.Low30Two));
        Assert.Equal("30S", DpmGrade.LightingCode(DpmLighting.Low30One));
    }

    [Fact]
    public void ThePartMarkGradeIsJudgedUnderIso29158()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.DataMatrix, MinGrade = 3, GradeStandard = GradeStandard.Iso29158 };
        var print = new CodeQuality(80, 0.7f, 10, 1, 0, 4);
        // the 15415 grade is A, the part mark grade C: the tool judges the part mark grade
        var grades = CodeTool.Grades(new[] { Code(print, G(2)) }, cfg).ToList();
        var v = CodeTool.Judge(One, cfg, null, NoValues, Now, grades);
        Assert.False(v.Pass);
        Assert.Equal("explain.verify.grade_low", v.Explain);
        Assert.Equal("Code grade is C, minimum B. Worst: cell modulation (C)", v.Summary);
        Assert.True(CodeTool.Judge(One, cfg, null, NoValues, Now, CodeTool.Grades(new[] { Code(null, G(3, true)) }, cfg).ToList()).Pass);
        // under 15415 the same code is judged on its print grade
        var iso15415 = new CodeToolConfig { Name = "C", Format = CodeFormat.DataMatrix, MinGrade = 3 };
        Assert.True(CodeTool.Judge(One, iso15415, null, NoValues, Now, CodeTool.Grades(new[] { Code(print, G(2)) }, iso15415).ToList()).Pass);
    }

    [Fact]
    public void APartMarkThatCouldNotBeGradedFails()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.DataMatrix, MinGrade = 1, GradeStandard = GradeStandard.Iso29158 };
        var v = CodeTool.Judge(One, cfg, null, NoValues, Now, CodeTool.Grades(new[] { Code(new CodeQuality(80, 0.7f, 10, 1, 0, 4), null) }, cfg).ToList());
        Assert.False(v.Pass);
        Assert.Equal("explain.verify.not_graded", v.Explain);
    }

    [Fact]
    public void Iso29158IsForDataMatrixAndQrOnly()
    {
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.DataMatrix, MinGrade = 2, GradeStandard = GradeStandard.Iso29158 }));
        Assert.Null(CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.QrCode, MinGrade = 2, GradeStandard = GradeStandard.Iso29158, DpmAperturePct = 50 }));
        Assert.Contains("Data Matrix and QR only",
            CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.Ean13, MinGrade = 2, GradeStandard = GradeStandard.Iso29158 }));
        Assert.Contains("aperture",
            CodeTool.SetupProblem(new CodeToolConfig { Format = CodeFormat.DataMatrix, MinGrade = 2, GradeStandard = GradeStandard.Iso29158, DpmAperturePct = 60 }));
    }

    [Fact]
    public void ThePartMarkGradeReadsAsAQuality()
    {
        var q = G(3, true).ToQuality();
        Assert.Equal(3, q.OverallGrade);
        Assert.True(q.Calibrated);
        Assert.Equal(40f, q.SymbolContrast, 3);
        Assert.Equal(0.4f, q.Values["Cell contrast"], 3);
        Assert.Contains(("Cell modulation", 3), q.Grades);
    }
}
