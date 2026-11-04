using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BarcodeReading;
using VisionCore;
using VisionCore.Reports;
using Xunit;
using PointF = VisionCore.PointF;

public class GradeReportTests
{
    private static GrayImage Image()
    {
        var px = new byte[400 * 200];
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 400; x++)
                px[y * 400 + x] = (byte)(x is >= 100 and < 300 && y is >= 60 and < 140 && (x / 6) % 2 == 0 ? 30 : 220);
        return new GrayImage(px, 400, 200) { Name = "20261029_101500000.bmp" };
    }

    private static CodeRead Linear(bool calibrated = false) => new(CodeFormat.Code39, "VS-39AV",
        new[] { new PointF(100, 60), new PointF(300, 60), new PointF(300, 140), new PointF(100, 140) },
        new CodeQuality(78, 0.62f, 3f, 0.9f, 0f, 3)
        {
            Calibrated = calibrated,
            Linear = new LinearGrade(55, 0.04f, 0.97f, 12, 87, 10, 10, 4, 4, 4),
            Grades = new[] { ("Symbol contrast", 4), ("Modulation", 3), ("Edge contrast", 4), ("Defects", 4), ("Decodability", 4), ("Decode", 4) },
        });

    private static readonly GradeReportInfo Info = new() { Job = "Shipper", Tool = "ITF", Camera = "Top", Operator = "asha", When = new DateTime(2026, 10, 29, 10, 15, 0) };

    // every object the xref names starts where it says; the page contents inflate to text
    private static string CheckAndExtract(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", s);
        Assert.EndsWith("%%EOF\n", s);
        long xref = long.Parse(Regex.Match(s, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", s[(int)xref..]);
        var offsets = Regex.Matches(s[(int)xref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        for (int i = 0; i < offsets.Count; i++) Assert.StartsWith($"{i + 1} 0 obj", s[offsets[i]..]);
        var text = new StringBuilder();
        foreach (Match m in Regex.Matches(s, @"<< /Filter /FlateDecode /Length (\d+) >>\nstream\n"))
        {
            int start = m.Index + m.Length, len = int.Parse(m.Groups[1].Value);
            using var z = new ZLibStream(new MemoryStream(pdf, start, len), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.Latin1);
            text.Append(r.ReadToEnd());
        }
        return text.ToString();
    }

    [Fact]
    public void ReportHoldsTheGradesAndSaysProcessControlWithoutCalibration()
    {
        var pdf = GradeReport.Build(Image(), Linear(), Info);
        var content = CheckAndExtract(pdf);
        Assert.Contains("(Verification Report)", content);
        Assert.Contains("Process control grading after ISO/IEC 15416: not ISO verification", content);
        Assert.Contains("(VS-39AV)", content);
        Assert.Contains("(Modulation)", content);
        Assert.Contains("(0.62)", content);                     // its value, full template
        Assert.Contains("(10 of 10 scans)", content);
        Assert.Contains("(B)", content);                        // 1D: the letter only (the engine rounds the scan mean)
        Assert.Contains("(N/A \\(no scan mean yet\\))", content);
        Assert.Contains("/Im1 Do", content);                    // the code's image
        Assert.Contains("(asha)", content);
        Assert.Contains("(2026-10-29 10:15:00)", content);          // report time
        Assert.Contains("(Image time)", content);
        if (Environment.GetEnvironmentVariable("VS_REPORT_SAMPLE") is { Length: > 0 } path) File.WriteAllBytes(path, pdf);
    }

    [Fact]
    public void CalibratedSummaryWithoutImage()
    {
        var info = new GradeReportInfo { Job = "J", Tool = "T", Full = false, IncludeImage = false, Lighting = "45 degrees, 660 nm" };
        var content = CheckAndExtract(GradeReport.Build(Image(), Linear(calibrated: true), info));
        Assert.Contains("Verifier-style grading after ISO/IEC 15416", content);
        Assert.DoesNotContain("(0.62)", content);               // grades only
        Assert.DoesNotContain("Do Q", content);
        Assert.Contains("(45 degrees, 660 nm)", content);
    }

    [Fact]
    public void TwoDimensionalCodesNameTheirStandard()
    {
        var dm = new CodeRead(CodeFormat.DataMatrix, "0109506000134352\u001D21X", new[] { new PointF(150, 70), new PointF(250, 70), new PointF(250, 130), new PointF(150, 130) },
            new CodeQuality(60, 0.5f, 4f, 0.9f, 0f, 2) { Grades = new[] { ("Symbol contrast", 3), ("Modulation", 2) } });
        var content = CheckAndExtract(GradeReport.Build(Image(), dm, Info));
        Assert.Contains("ISO/IEC 15415", content);
        Assert.Contains("0109506000134352<GS>21X", content);
    }

    [Fact]
    public void Pdf417IsReportedAfterIso15415WithItsCodewordYield()
    {
        // PDF417 comes in the 1D fields (Linear) but is a multi-row 2D symbol: ISO/IEC 15415, no 1D scan analysis
        var pdf417 = new CodeRead(CodeFormat.Pdf417, "VS-PDF417", new[] { new PointF(100, 60), new PointF(300, 60), new PointF(300, 140), new PointF(100, 140) },
            new CodeQuality(78, 0.62f, 3f, 0.9f, 0f, 2)
            {
                Linear = new LinearGrade(55, 0.04f, 0.97f, 12, 87, 8, 7, 4, 4, 4),
                Grades = new[] { ("Symbol contrast", 4), ("Modulation", 3), ("Codeword yield", 2), ("Unused error correction", 4), ("Quiet zone", 4) },
                Values = new Dictionary<string, float> { ["Codeword yield"] = 0.6f, ["Unused error correction"] = 0.9f },
            });
        var content = CheckAndExtract(GradeReport.Build(Image(), pdf417, Info));
        Assert.Contains("Process control grading after ISO/IEC 15415", content);
        Assert.DoesNotContain("15416", content);
        Assert.Contains("(Codeword yield)", content);
        Assert.Contains("(0.60)", content);
        Assert.Contains("(8 \\(7 with start and stop\\))", content);
        Assert.Contains("Codewords along the rows", content);     // the diagnosis of a C codeword yield
        Assert.DoesNotContain("(Rmin)", content);                 // no 1D per-scan rows
    }

    [Fact]
    public void AztecHasACellMapFromTheEngineGridWithoutAQuietZoneRing()
    {
        // a 15 x 15 Aztec of 8 px modules (a checkerboard is enough for the cell map)
        var px = new byte[200 * 200];
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 200; x++)
                px[y * 200 + x] = (byte)(x is >= 40 and < 160 && y is >= 40 and < 160 && ((x - 40) / 8 + (y - 40) / 8) % 2 == 0 ? 30 : 220);
        var img = new GrayImage(px, 200, 200);
        var corners = new[] { new PointF(40, 40), new PointF(160, 40), new PointF(160, 160), new PointF(40, 160) };
        Assert.Equal((15, 15), SymbolAnalysis.GridSize(CodeFormat.Aztec, corners, 8f));
        Assert.Equal((37, 37), SymbolAnalysis.GridSize(CodeFormat.Aztec, new[] { new PointF(0, 0), new PointF(370, 0), new PointF(370, 370), new PointF(0, 370) }, 10f)); // full-range, 5 layers
        Assert.Equal((0, 0), SymbolAnalysis.GridSize(CodeFormat.Aztec, new[] { new PointF(0, 0), new PointF(170, 0), new PointF(170, 170), new PointF(0, 170) }, 10f)); // 17: no such size
        var aztec = new CodeRead(CodeFormat.Aztec, "AZ", corners,
            new CodeQuality(80, 0.9f, 8f, 0.9f, 0f, 4) { Grades = new[] { ("Modulation", 4) }, GridRows = 15, GridCols = 15 });
        var content = CheckAndExtract(GradeReport.Build(img, aztec, Info));
        Assert.Contains("(15x15)", content);
        Assert.Contains("Modulation values", content);
        Assert.DoesNotContain("(-1)", content);                   // no quiet zone ring row or column
        Assert.Contains("(14)", content);
    }

    [Fact]
    public void ApertureAndOverallAsTheEngineMeasures()
    {
        var dm = new CodeRead(CodeFormat.DataMatrix, "X", new[] { new PointF(150, 70), new PointF(250, 70), new PointF(250, 130), new PointF(150, 130) },
            new CodeQuality(60, 0.5f, 6f, 0.9f, 0f, 2) { Grades = new[] { ("Modulation", 2) } });
        var c2 = CheckAndExtract(GradeReport.Build(Image(), dm, Info));
        Assert.Contains("(X/3 = 2.0 px)", c2);          // uncalibrated 2D samples a third of a module
        Assert.Contains("(Overall: the lowest parameter grade.)", c2);
        Assert.Contains("(0.8 X = 4.8 px)", CheckAndExtract(GradeReport.Build(Image(), dm with { Quality = dm.Quality! with { Calibrated = true } }, Info)));
        var c1 = CheckAndExtract(GradeReport.Build(Image(), Linear(), Info));
        Assert.Contains("(Overall: the mean of the scan grades, rounded to a grade by the engine;", c1);
        Assert.Contains("Rmin and the quiet zone are graded on each scan", c1);
    }

    [Fact]
    public void LongDataStaysBesideTheGradeBoxAndIsCapped()
    {
        var text = string.Concat(Enumerable.Repeat("WWWWWWWWWW", 300)); // 3000 wide characters
        var code = Linear() with { Text = text };
        var content = CheckAndExtract(GradeReport.Build(Image(), code, Info));
        Assert.Contains("more characters", content);
        foreach (var line in GradeReport.WrapToWidth(text, 300, 9).Take(3))
            Assert.True(MiniPdf.TextWidth(line, 9) <= 300);
    }

    [Fact]
    public void FormalGradeOnlyWhenCalibratedWithAScale()
    {
        var q = new CodeQuality(78, 0.62f, 3f, 0.9f, 0f, 3);
        var info = new GradeReportInfo { WavelengthNm = 660, AngleDeg = 45 };
        Assert.Null(GradeReport.FormalGrade(q, info, false));
        Assert.Null(GradeReport.FormalGrade(q with { Calibrated = true }, info, false));             // no mm scale: no aperture in mil
        Assert.Equal("3.0/10/660/45", GradeReport.FormalGrade(q with { Calibrated = true, XDimMm = 0.33f }, info, false)); // 0.8 x 0.33 mm = 10.4 mil
        Assert.Null(GradeReport.FormalGrade(q with { Calibrated = true, XDimMm = 0.33f }, new GradeReportInfo(), false)); // setup not recorded
        Assert.Null(GradeReport.FormalGrade(q with { Calibrated = true, XDimMm = 0.33f }, info, true));                   // 1D: no scan mean yet
    }

    [Fact]
    public void VerifierLayoutHasSetupPassFailGs1AndImages()
    {
        var dm = new CodeRead(CodeFormat.DataMatrix, "0109506000134352\u001D21X", new[] { new PointF(100, 60), new PointF(280, 60), new PointF(280, 140), new PointF(100, 140) },
            new CodeQuality(60, 0.5f, 10f, 0.9f, 0f, 2) { Grades = new[] { ("Symbol contrast", 3), ("Modulation", 2), ("Fixed pattern damage", 4) } });
        var info = new GradeReportInfo { Job = "J", Tool = "T", Station = "LINE-PC-1", MinGrade = 3, WavelengthNm = 660, AngleDeg = 45 };
        var content = CheckAndExtract(GradeReport.Build(Image(), dm, info));
        Assert.Contains("(LINE-PC-1)", content);
        Assert.Contains("(Grade acceptance: FAIL)", content);
        Assert.Contains("(660 nm)", content);
        Assert.Contains("(N/A \\(uncalibrated\\))", content);
        Assert.Contains("(GS1 data check: PASS)", content);
        Assert.Contains("FNC1 position not checked", content);
        Assert.Contains("(GTIN)", content);
        // with the engine's symbology identifier the FNC1 position is checked: ]d2 passes, ]d1 (no FNC1 first) fails
        var withId = CheckAndExtract(GradeReport.Build(Image(), dm with { SymbologyId = "]d2" }, info));
        Assert.Contains("(GS1 data check: PASS)", withId);
        Assert.Contains("FNC1 first \\(]d2\\)", withId);
        Assert.DoesNotContain("FNC1 position not checked", withId);
        var noFnc1 = CheckAndExtract(GradeReport.Build(Image(), dm with { SymbologyId = "]d1" }, info));
        Assert.Contains("(GS1 data check: FAIL)", noFnc1);
        var expiry = CheckAndExtract(GradeReport.Build(Image(), dm with { Text = "010950600013435217281231" }, info));
        Assert.Contains("(281231  \\(31 Dec 2028\\))", expiry);
        Assert.Contains("Left 'L' side \\(LLS\\)", content);
        Assert.Contains("(Modulation values", content);
        Assert.Contains("(Histogram of module centres \\(grey levels, the report's estimate\\))", content);
        Assert.Contains("(Page 2)", content);
        Assert.Contains("(Matrix size)", content);
    }

    [Fact]
    public void LinearReportHasTheScanProfile()
    {
        var content = CheckAndExtract(GradeReport.Build(Image(), Linear(), Info));
        Assert.Contains("(Scan reflectance profile", content);
        Assert.Contains("(Histogram of symbol region \\(grey levels\\))", content);
        Assert.DoesNotContain("Modulation values", content);
    }

    [Fact]
    public void CellsAndGridSize()
    {
        // a 10 x 10 checkerboard of 10 px cells, dark 30, light 220
        var px = new byte[100 * 100];
        for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++) px[y * 100 + x] = (byte)(((x / 10) + (y / 10)) % 2 == 0 ? 30 : 220);
        var img = new GrayImage(px, 100, 100);
        var c = new[] { new PointF(0, 0), new PointF(100, 0), new PointF(100, 100), new PointF(0, 100) };
        Assert.Equal((10, 10), SymbolAnalysis.GridSize(CodeFormat.DataMatrix, c, 10f));
        Assert.Equal((21, 21), SymbolAnalysis.GridSize(CodeFormat.QrCode, c, 100f / 21));
        var cells = SymbolAnalysis.Cells(img, c, 10, 10, 2f);
        Assert.True(cells.Modulation.All(m => m > 0.95f) && cells.Grade.All(g => g == 4));
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, new[] { 0.6f, 0.45f, 0.35f, 0.25f, 0.1f }.Select(SymbolAnalysis.ModulationGrade));
        var (rgb, w, h) = SymbolAnalysis.GradeMap(cells, 4);
        Assert.Equal(40 * 40 * 3, rgb.Length);
        Assert.Equal(new[] { 1, 1 }, SymbolAnalysis.Histogram(new[] { 0f, 100f }, 2));
    }

    [Fact]
    public void DataMatrixFixedPatternSegments()
    {
        // a 12 x 12 grid of 10 px cells inside a 2-cell light quiet zone: L finder dark, clock tracks
        // alternating, the data region a checkerboard
        const int n = 12, cpx = 10, q = 2, size = (n + 2 * q) * cpx;
        bool Dark(int r, int k) => k == 0 || r == n - 1 || (r == 0 ? k % 2 == 0 : k == n - 1 ? (n - 1 - r) % 2 == 0 : (r + k) % 2 == 0);
        GrayImage Draw(Func<int, int, bool> dark)
        {
            var px = new byte[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int r = y / cpx - q, k = x / cpx - q;
                    px[y * size + x] = (byte)(r >= 0 && r < n && k >= 0 && k < n && dark(r, k) ? 30 : 220);
                }
            return new GrayImage(px, size, size);
        }
        var corners = new[] { new PointF(q * cpx, q * cpx), new PointF((q + n) * cpx, q * cpx), new PointF((q + n) * cpx, (q + n) * cpx), new PointF(q * cpx, (q + n) * cpx) };
        var good = Draw(Dark);
        var cells = SymbolAnalysis.Cells(good, corners, n, n, 2f);
        var segs = SymbolAnalysis.DataMatrixSegments(good, corners, cells, 2f);
        Assert.Equal(new[] { "LLS", "BLS", "LQZ", "BQZ", "TQZ", "RQZ", "TCT", "RCT" }, segs.Select(sg => sg.Code));
        Assert.All(segs, sg => Assert.Equal(0, sg.Wrong));
        // one finder cell printed light: the left 'L' side shows it
        var broken = Draw((r, k) => !(r == 5 && k == 0) && Dark(r, k));
        var bc = SymbolAnalysis.Cells(broken, corners, n, n, 2f);
        var lls = SymbolAnalysis.DataMatrixSegments(broken, corners, bc, 2f)[0];
        Assert.Equal((1, 0f), (lls.Wrong, lls.LowestModulation));
        Assert.Equal((5, 0), lls.WrongCells.Single());
        Assert.Equal((10, 10, 5, 7), SymbolAnalysis.Ecc200(12, 12) is { } e ? (e.DataRows, e.DataCols, e.DataCw, e.EcCw) : default);
        Assert.Equal((20, 20, 30, 20), SymbolAnalysis.Ecc200(22, 22) is { } f ? (f.DataRows, f.DataCols, f.DataCw, f.EcCw) : default);
        Assert.Equal((28, 28, 62, 36), SymbolAnalysis.Ecc200(32, 32) is { } g ? (g.DataRows, g.DataCols, g.DataCw, g.EcCw) : default);
    }

    [Fact]
    public void TheToolsGs1RulesDecideTheGs1Check()
    {
        var dm = new CodeRead(CodeFormat.DataMatrix, "0109506000134352\u001D21X", new[] { new PointF(100, 60), new PointF(300, 60), new PointF(300, 140), new PointF(100, 140) },
            new CodeQuality(60, 0.5f, 10f, 0.9f, 0f, 2) { Grades = new[] { ("Modulation", 2) } });
        var info = new GradeReportInfo { Gs1Rules = new Gs1Rules { Required = { "01", "21", "10" } }, Gs1Values = new Dictionary<string, string> { ["21"] = "Y" } };
        var content = CheckAndExtract(GradeReport.Build(Image(), dm, info));
        Assert.Contains("(GS1 data check: FAIL)", content);
        Assert.Contains("Problem: Code has no batch \\(10\\)", content);
        // a plain Code 128 that only looks like AI 10 is not called GS1
        var c128 = new CodeRead(CodeFormat.Gs1_128, "10ABC123", dm.Corners, new CodeQuality(70, 0.6f, 3f, 0.9f, 0f, 3) { Linear = new LinearGrade(50, 0.05f, 0.9f, 10, 85, 10, 10, 4, 4, 4) });
        Assert.DoesNotContain("GS1 data check", CheckAndExtract(GradeReport.Build(Image(), c128, new GradeReportInfo())));
    }

    [Fact]
    public void AMatrixSizeTheSymbologyDoesNotHaveIsNotEstimated()
    {
        var c = new[] { new PointF(0, 0), new PointF(230, 0), new PointF(230, 230), new PointF(0, 230) };
        Assert.Equal((0, 0), SymbolAnalysis.GridSize(CodeFormat.DataMatrix, c, 10f));  // 23 x 23: no such size
        Assert.Equal((8, 64), SymbolAnalysis.GridSize(CodeFormat.DataMatrix, new[] { new PointF(0, 0), new PointF(640, 0), new PointF(640, 80), new PointF(0, 80) }, 10f)); // DMRE
        // perspective: the homography lands on the corners and the far side's cells are narrower
        var q = new[] { new PointF(0, 0), new PointF(100, 10), new PointF(100, 90), new PointF(0, 100) };
        var (x0, y0) = SymbolAnalysis.Map(q, 1, 1);
        Assert.Equal((100f, 90f), (MathF.Round(x0, 3), MathF.Round(y0, 3)));
    }

    [Fact]
    public void GuidanceApertureAndLighting()
    {
        Assert.NotNull(GradeReport.Advice("Modulation", false));
        Assert.Contains("scan lines", GradeReport.Advice("Decode", true));
        Assert.Null(GradeReport.Advice("Something else", false));
        Assert.Equal(new int?[] { null, 3, 5, 10, 20 }, new[] { 3.0, 6.0, 10.0, 20.0, 30.0 }.Select(GradeReport.RecommendedAperture1D));
        Assert.Equal("45Q", new GradeReportInfo { AngleDeg = 45, LightSides = 4 }.LightingCode);
        Assert.Equal("30T", new GradeReportInfo { AngleDeg = 30, LightSides = 2 }.LightingCode);
        Assert.Null(new GradeReportInfo().LightingCode);
        var q = new CodeQuality(78, 0.62f, 3f, 0.9f, 0f, 3) { Calibrated = true, XDimMm = 0.254f };
        Assert.Equal("3.0/08/660/45Q", GradeReport.FormalGrade(q, new GradeReportInfo { WavelengthNm = 660, AngleDeg = 45, LightSides = 4 }, false));
        var words = GradeReport.WrapWords("one two three four five six seven", 40, 9).ToList();
        Assert.True(words.Count > 1 && words.All(l => !l.StartsWith(' ') && !l.EndsWith(' ')));
        Assert.Equal("one two three four five six seven", string.Join(" ", words));
    }

    [Fact]
    public void DiagnosisAndOverlayForAPoorCode()
    {
        var dm = new CodeRead(CodeFormat.DataMatrix, "X", new[] { new PointF(100, 60), new PointF(280, 60), new PointF(280, 140), new PointF(100, 140) },
            new CodeQuality(60, 0.2f, 10f, 0.9f, 0f, 1) { Grades = new[] { ("Symbol contrast", 4), ("Modulation", 1) } });
        var content = CheckAndExtract(GradeReport.Build(Image(), dm, new GradeReportInfo()));
        Assert.Contains("(Diagnosis: what to check)", content);
        Assert.Contains("(Modulation \\(D\\))", content);
        Assert.Contains("Image and modulation overlay", content);
        Assert.Contains("0 = wrong side of", content);
    }

    [Fact]
    public void UnitsAndSections()
    {
        var q = new CodeQuality(78, 0.62f, 10f, 0.9f, 0f, 3) { Calibrated = true, XDimMm = 0.254f, Grades = new[] { ("Modulation", 3) } };
        var dm = new CodeRead(CodeFormat.DataMatrix, "X", new[] { new PointF(100, 60), new PointF(300, 60), new PointF(300, 140), new PointF(100, 140) }, q);
        var mm = CheckAndExtract(GradeReport.Build(Image(), dm, new GradeReportInfo { Units = ReportUnits.Mm, Company = "Sundaram", Batch = "B42", Note = "line 2" }));
        Assert.Contains("(0.8 X = 0.203 mm)", mm);
        Assert.Contains("(Sundaram)", mm);
        Assert.Contains("(B42)", mm);
        var mil = CheckAndExtract(GradeReport.Build(Image(), dm, new GradeReportInfo { ModulationTable = false, Histograms = false, DataDetail = false, GeneralCharacteristics = false }));
        Assert.Contains("(0.8 X = 8.0 mil)", mil);
        Assert.DoesNotContain("Modulation values", mil);
        Assert.DoesNotContain("Histogram", mil);
        Assert.DoesNotContain("ASCII values", mil);
    }

    [Fact]
    public void AnUngradedCodeHasNoReport() =>
        Assert.Throws<ArgumentException>(() => GradeReport.Build(Image(), Linear() with { Quality = null }, Info));

    [Fact]
    public void CropAndText()
    {
        var (px, w, h) = GradeReport.Crop(Image(), Linear().Corners);
        Assert.True(w > 200 && h > 80 && w <= 400 && h <= 200 && px.Length == w * h);
        var (_, sw, sh) = GradeReport.Crop(Image(), Linear().Corners, maxSide: 100);
        Assert.True(Math.Max(sw, sh) <= 100);
        Assert.Equal("A<GS>B<0A>", GradeReport.Printable("A\u001DB\n"));
        // C1 controls and text the report font cannot show are named, never printed as other characters
        Assert.Equal("<80>é<U+0939><U+20B9>", GradeReport.Printable("\u0080é\u0939\u20B9"));
    }

    [Fact]
    public void PdfTextIsEscapedAndMeasured()
    {
        var pdf = new MiniPdf();
        pdf.AddPage();
        pdf.Text(10, 10, "a(b)c\\d ₹ é");
        var content = CheckAndExtract(pdf.ToBytes());
        Assert.Contains("(a\\(b\\)c\\\\d ? é)", content);       // ₹ is not in WinAnsi: "?"
        Assert.Equal(MiniPdf.TextWidth("W", 10) * 2, MiniPdf.TextWidth("WW", 10), 3);
        Assert.True(MiniPdf.TextWidth("i", 10) < MiniPdf.TextWidth("M", 10));
    }
}

public class ReportFilesTests
{
    [Fact]
    public void NamesAreSafeAndOnlySafeNamesAreServed()
    {
        var n = ReportFiles.Name("Harpic 200ml / line 2", "ITF ..\\x", new DateTime(2026, 10, 29, 10, 15, 0, 123));
        Assert.Equal("Harpic-200ml-line-2_ITF-x_20261029_101500_123.pdf", n);
        Assert.True(ReportFiles.IsSafe(n));
        foreach (var bad in new[] { "../jobs.pdf", "a/b.pdf", "a\\b.pdf", "x.exe", "..pdf", "", "a..b.pdf" })
            Assert.False(ReportFiles.IsSafe(bad), bad);
    }
}

public class DataMatrixSampleReport
{
    /// <summary>
    /// A sample report from a real grading: VS_DM_SAMPLE names a folder with sample.bmp (a Data Matrix image)
    /// and quality.json (the engine's ISO 15415 measurements of it); writes report.pdf there. Without the
    /// variable there is nothing to do.
    /// </summary>
    [Fact]
    public void WriteSample()
    {
        if (Environment.GetEnvironmentVariable("VS_DM_SAMPLE") is not { Length: > 0 } dir) return;
        var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "quality.json"))).RootElement;
        float F(string k) => j.GetProperty(k).GetSingle();
        var g = j.GetProperty("g").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        string[] names = { "Symbol contrast", "Modulation", "Reflectance margin", "Fixed pattern damage", "Axial non-uniformity",
                           "Grid non-uniformity", "Unused error correction", "Quiet zone" };
        var quality = new CodeQuality(F("sc"), F("mod"), F("px"), F("light"), F("sat"), j.GetProperty("overall").GetInt32())
        {
            Grades = names.Zip(g).Where(p => p.Second >= 0).Select(p => (p.First, p.Second)).ToList(),
            Values = new Dictionary<string, float>
            {
                ["Reflectance margin"] = F("rm"), ["Fixed pattern damage"] = F("fpd"), ["Axial non-uniformity"] = F("an"),
                ["Grid non-uniformity"] = F("gn"), ["Unused error correction"] = F("uec"), ["Print growth"] = F("pg"),
            },
        };
        var xs = j.GetProperty("x").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var ys = j.GetProperty("y").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var corners = xs.Zip(ys, (x, y) => new PointF(x, y)).ToArray();
        var img = GrayImage.Load(Path.Combine(dir, "sample.bmp"));
        var code = new CodeRead(CodeFormat.DataMatrix, "0109506000134352172812311042AB\u001D21SER0001", corners, quality);
        var pdf = GradeReport.Build(img, code, new GradeReportInfo
        {
            Job = "Sample GS1 Data Matrix", Tool = "Read code", Camera = "Top", Operator = "VisionCore test",
            When = new DateTime(2026, 10, 29, 21, 47, 0), Station = "Sample PC", MinGrade = 3, WavelengthNm = 660, AngleDeg = 45, LightSides = 4,
        });
        File.WriteAllBytes(Path.Combine(dir, "report.pdf"), pdf);
    }
}
