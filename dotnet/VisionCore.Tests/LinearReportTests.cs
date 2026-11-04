using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BarcodeReading;
using VisionCore;
using VisionCore.Reports;
using Xunit;
using PointF = VisionCore.PointF;

public class LinearReportTests
{
    private const int X = 3, Left = 60, Top = 40, BarH = 90;

    // EAN-13 modules (1 = bar): guards, six left digits by the first digit's parity, six right digits
    private static int[] Ean13(string digits)
    {
        string[] lCodes = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
        string[] parity = { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };
        string R(int d) => new(lCodes[d].Select(c => c == '0' ? '1' : '0').ToArray());
        string G(int d) => new(R(d).Reverse().ToArray());
        var sb = new StringBuilder("101");
        for (int i = 1; i <= 6; i++)
        {
            int d = digits[i] - '0';
            sb.Append(parity[digits[0] - '0'][i - 1] == 'L' ? lCodes[d] : G(d));
        }
        sb.Append("01010");
        for (int i = 7; i <= 12; i++) sb.Append(R(digits[i] - '0'));
        sb.Append("101");
        return sb.ToString().Select(c => c - '0').ToArray();
    }

    // the symbol drawn at X px per module; growth px added to each bar (taken from the spaces); an optional dark
    // mark `markX` modules before the first bar; an optional grey spot in the quiet zone over the top rows (a defect)
    private static GrayImage Image(int growth = 0, int markX = 0, int spotRows = 0)
    {
        var m = Ean13("4006381333931");
        int w = Left * 2 + m.Length * X, h = Top * 2 + BarH;
        var px = Enumerable.Repeat((byte)220, w * h).ToArray();
        for (int k = 0; k < m.Length; k++)
        {
            if (m[k] == 0) continue;
            int x0 = Left + k * X - growth / 2, x1 = Left + (k + 1) * X + (growth + 1) / 2;
            for (int y = Top; y < Top + BarH; y++)
                for (int x = x0; x < x1; x++) px[y * w + x] = 30;
        }
        if (markX > 0)
            for (int y = Top; y < Top + BarH; y++)
                for (int x = Left - markX * X - 2 * X; x < Left - markX * X; x++) px[y * w + x] = 30;
        if (spotRows > 0)
            for (int y = Top; y < Top + spotRows; y++)
                for (int x = Left - 5 * X; x < Left - 4 * X; x++) px[y * w + x] = 140;
        return new GrayImage(px, w, h) { Name = "ean.bmp" };
    }

    // the reader's corners: a thin line through the middle of the bars, in reading order
    private static PointF[] Corners()
    {
        float x0 = Left, x1 = Left + 95 * X, ym = Top + BarH / 2f;
        return new[] { new PointF(x0, ym - 2), new PointF(x1, ym - 2), new PointF(x1, ym + 2), new PointF(x0, ym + 2) };
    }

    private static CodeRead Code(bool calibrated = false, float xMm = 0, int overall = 4) => new(CodeFormat.Ean13, "4006381333931", Corners(),
        new CodeQuality(76, 0.8f, X, 0.9f, 0f, overall)
        {
            Calibrated = calibrated, XDimMm = xMm,
            Linear = new LinearGrade(60, 0.02f, 0.9f, 12, 86, 10, 10, 4, 4, 4),
            Grades = new[] { ("Symbol contrast", 4), ("Modulation", 4), ("Edge contrast", 4), ("Defects", 4), ("Decodability", 4), ("Decode", 4) },
        });

    private static EngineScan Scan(int grade, bool decoded = true) =>
        new(12, 86, 74, 60, grade == 4 ? 0.81f : 0.55f, 0.03f, 0.9f, decoded, true, 4, 4, grade);

    private static string Text(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        var text = new StringBuilder();
        foreach (Match m in Regex.Matches(s, @"<< /Filter /FlateDecode /Length (\d+) >>\nstream\n"))
        {
            using var z = new ZLibStream(new MemoryStream(pdf, m.Index + m.Length, int.Parse(m.Groups[1].Value)), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.Latin1);
            text.Append(r.ReadToEnd());
        }
        return text.ToString();
    }

    [Fact]
    public void TenScansOfACleanSymbol()
    {
        var lin = LinearAnalysis.Analyse(Image(), Corners(), X, CodeFormat.Ean13)!;
        Assert.Equal(10, lin.Scans.Count);
        Assert.All(lin.Scans, s =>
        {
            Assert.Equal(60, s.Edges);                       // 30 bars
            Assert.Equal(4, s.GradeMeasured);
            Assert.True(s.QuietLeftX >= 11 && s.QuietRightX >= 7);
            Assert.True(s.ECmin > 50);
        });
        Assert.InRange(lin.BarHeightPx, BarH - 4, BarH + 4);   // found from the image, not the thin corners
        Assert.InRange(lin.WidthPx, 95 * X - 2, 95 * X + 2);   // first to last bar
        Assert.InRange(lin.GrowthX, -0.08f, 0.08f);
    }

    [Fact]
    public void BarWidthGrowthAndAShortQuietZone()
    {
        var grown = LinearAnalysis.Analyse(Image(growth: 1), Corners(), X, CodeFormat.Ean13)!;
        Assert.InRange(grown.GrowthX, 0.2f, 0.45f);            // 1 px on a 3 px module
        var marked = LinearAnalysis.Analyse(Image(markX: 8), Corners(), X, CodeFormat.Ean13)!;
        Assert.All(marked.Scans, s => { Assert.InRange(s.QuietLeftX, 7.5f, 8.5f); Assert.False(s.QuietOk); Assert.Equal(0, s.GradeQuiet); });
    }

    [Fact]
    public void ASpotAtTheTopShowsOnTheTopScansOnly()
    {
        var lin = LinearAnalysis.Analyse(Image(spotRows: 20), Corners(), X, CodeFormat.Ean13)!;
        Assert.True(lin.Scans[0].GradeDefects < 3);            // 10 % from the top runs through the spot
        Assert.Equal(4, lin.Scans[9].GradeMeasured);
        Assert.Contains("near the top of the bars", GradeReport.ScanPattern(new[] { 0, 1 }, 10));
        Assert.Contains("near the bottom", GradeReport.ScanPattern(new[] { 8, 9 }, 10));
        Assert.Contains("every scan", GradeReport.ScanPattern(Enumerable.Range(0, 10).ToList(), 10));
        Assert.Equal(3, GradeReport.MeanGrade(new[] { 4, 4, 3, 2 }));
        Assert.Equal(-1, GradeReport.MeanGrade(Array.Empty<int>()));
    }

    [Fact]
    public void CheckDigits()
    {
        Assert.Equal(true, LinearAnalysis.CheckDigit(CodeFormat.Ean13, "4006381333931")!.Value.Ok);
        Assert.Equal(false, LinearAnalysis.CheckDigit(CodeFormat.Ean13, "4006381333932")!.Value.Ok);
        Assert.Equal("012345000065", LinearAnalysis.UpcEToA("01234565"));
        Assert.Equal(true, LinearAnalysis.CheckDigit(CodeFormat.UpcE, "01234565")!.Value.Ok);
        Assert.Equal(true, LinearAnalysis.CheckDigit(CodeFormat.Itf, "15400141288763")!.Value.Ok);
        Assert.Contains("mod 43", LinearAnalysis.CheckDigit(CodeFormat.Code39, "VS-39AV")!.Value.Text);
        Assert.Contains("decoder", LinearAnalysis.CheckDigit(CodeFormat.Gs1_128, "0012345")!.Value.Text);
        Assert.Null(LinearAnalysis.CheckDigit(CodeFormat.Codabar, "A123B"));
        Assert.Equal((11f, 7f), LinearAnalysis.MinQuietZone(CodeFormat.Ean13));
        Assert.Equal((10f, 10f), LinearAnalysis.MinQuietZone(CodeFormat.Gs1_128));
    }

    [Fact]
    public void ReportFromTheImageAlone()
    {
        var content = Text(GradeReport.Build(Image(), Code(), new GradeReportInfo()));
        Assert.Contains("Scan lines \\(ISO/IEC 15416\\): the report's estimate from the image", content);
        Assert.Contains("(at most A)", content);
        Assert.Contains("(Scan 10 \\(90 %\\))", content);
        Assert.Contains("(Scan reflectance profiles", content);
        Assert.Contains("(Rmin)", content);
        Assert.Contains("(Quiet zone)", content);
        Assert.Contains("(Bar width growth)", content);
        Assert.Contains("(Quiet zone left \\(least\\))", content);
        Assert.Contains("(Check digit)", content);
        Assert.Contains("(1 \\(GS1 mod 10: 1\\) correct)", content);
        Assert.Contains("(N/A \\(no scan mean yet\\))", content);
    }

    [Fact]
    public void ReportWithTheEnginesScans()
    {
        var scans = new[] { 4, 4, 4, 3, 3, 3, 4, 4, 3, 3 }.Select(g => Scan(g)).ToList();
        var info = new GradeReportInfo { EngineScans = scans, WavelengthNm = 660, AngleDeg = 45, LightSides = 4 };
        var pdf = GradeReport.Build(Image(), Code(calibrated: true, xMm: 0.33f), info);
        var content = Text(pdf);
        Assert.Contains("the engine's grading; quiet zones in X from the image's scan at the same height", content);
        Assert.Contains("(3.5/10/660/45Q)", content);           // the scan mean, 0.8 x 0.33 mm = 10 mil
        Assert.Contains("(A \\(3.5\\))", content);
        Assert.Contains("(10/10)", content);                     // decoded scans in the mean row
        Assert.DoesNotContain("(at most", content);
        Assert.Contains("(Modulation by scan)", content);        // weak on some scans: where along the bars
        if (Environment.GetEnvironmentVariable("VS_REPORT_1D_SAMPLE") is { Length: > 0 } path) File.WriteAllBytes(path, pdf);

        var failed = scans.Select((s, i) => i < 2 ? s with { Decoded = false, Grade = 0 } : s).ToList();
        var bad = Text(GradeReport.Build(Image(), Code(overall: 3), new GradeReportInfo { EngineScans = failed }));
        Assert.Contains("(NO)", bad);
        Assert.Contains("(Decode by scan)", bad);
        Assert.Contains("near the top of the bars", bad);
    }

    // the part of an image from (x0, y0), w x h: the symbol cut at the image's edge
    private static GrayImage Cut(GrayImage img, int x0, int y0, int w, int h)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++) Array.Copy(img.Pixels, (y0 + y) * img.Stride + x0, px, y * w, w);
        return new GrayImage(px, w, h);
    }

    [Fact]
    public void TheImagesEdgeEndsTheBarsAndTheQuietZone()
    {
        var full = Image();
        // bars from the image's top row: the height stops there, not 2 code lengths on
        var top = Cut(full, 0, Top, full.Width, full.Height - Top);
        var shifted = Corners().Select(p => new PointF(p.X, p.Y - Top)).ToArray();
        var lin = LinearAnalysis.Analyse(top, shifted, X, CodeFormat.Ean13)!;
        Assert.InRange(lin.BarHeightPx, BarH - 6, BarH + 4);
        // 2 X of light after the last bar, then the image ends: the right quiet zone is short, not clear
        var right = Cut(full, 0, 0, Left + 95 * X + 2 * X, full.Height);
        var cut = LinearAnalysis.Analyse(right, Corners(), X, CodeFormat.Ean13)!;
        Assert.All(cut.Scans, s => { Assert.InRange(s.QuietRightX, 1f, 3f); Assert.False(s.QuietOk); });
    }

    [Fact]
    public void GrowthOfATwoWidthCode()
    {
        // Code 39 at ratio 2.5 with +0.2 X growth: narrow bar 1.2, wide bar 2.7, narrow space 0.8, wide space 2.3 (X = 10 samples)
        var widths = new[] { 12, 8, 27, 8, 12, 23, 12, 8, 27, 8, 12, 8, 12, 23, 27 };
        var r = new List<float>(Enumerable.Repeat(90f, 100));
        float first = r.Count;
        for (int k = 0; k < widths.Length; k++) r.AddRange(Enumerable.Repeat(k % 2 == 0 ? 10f : 90f, widths[k]));
        float last = r.Count;
        r.AddRange(Enumerable.Repeat(90f, 100));
        var scan = new LinearAnalysis.Scan(0, r.ToArray(), 10, first - 0.5f, last - 0.5f, 10, 90, 80, 1, 0, 10, 10, true, widths.Length + 1);
        Assert.InRange(LinearAnalysis.GrowthOf(scan, 10, twoWidth: true), 0.17f, 0.23f);
    }

    [Fact]
    public void EngineScansAreUsedOnlyWhenTheyAreTheWholeGrading()
    {
        // three scans for a ten-scan grading: not used
        var few = Text(GradeReport.Build(Image(), Code(), new GradeReportInfo { EngineScans = new[] { Scan(4), Scan(4), Scan(4) } }));
        Assert.Contains("does not match its grading", few);
        Assert.Contains("(at most A)", few);
        // scans 3 and 4 could not measure decodability: F there, 0 in the mean, and named by their own numbers
        var scans = Enumerable.Range(0, 10).Select(i => i is 2 or 3 ? Scan(0) with { Decodability = -1 } : Scan(4)).ToList();
        var content = Text(GradeReport.Build(Image(), Code(overall: 3), new GradeReportInfo { EngineScans = scans }));
        Assert.DoesNotContain("does not match", content);
        Assert.Contains("(0.72)", content);                      // 8 x 0.9 / 10
        Assert.Contains("(Decodability by scan)", content);
        Assert.Contains("scans 3, 4 of 10", content);
        // no symbol found in the image (corners shorter than 4 X): the engine's scans are still shown
        var dot = Code(overall: 3) with { Corners = new[] { new PointF(100, 80), new PointF(104, 80), new PointF(104, 82), new PointF(100, 82) } };
        var noImage = Text(GradeReport.Build(Image(), dot, new GradeReportInfo { EngineScans = scans }));
        Assert.Contains("the engine's grading", noImage);
    }

    [Fact]
    public void TheCodesOwnScanLinesByDefault()
    {
        var lines = Enumerable.Range(0, 10).Select(_ => new LinearScan(12, 86, 74, 60, 0.81f, 0.03f, 0.9f, true, true, 4, 4, 4)).ToList();
        var code = Code() with { Quality = Code().Quality! with { ScanLines = lines } };
        var content = Text(GradeReport.Build(Image(), code, new GradeReportInfo()));
        Assert.Contains("the engine's grading", content);
        Assert.Contains("(A \\(4.0\\))", content);
    }

    // a stacked GS1 DataBar as the engine grades it: ten scans a row, top row first, the symbol the lowest row's grade
    private static CodeRead StackedDataBar(int overall, int scans = 20) => new(CodeFormat.DataBar, "0100012345678905", Corners(),
        new CodeQuality(76, 0.8f, X, 0.9f, 0f, overall)
        {
            Linear = new LinearGrade(60, 0.02f, 0.9f, 12, 86, scans, scans, 4, 4, 4),
            Grades = new[] { ("Symbol contrast", 4), ("Modulation", 3), ("Edge contrast", 4), ("Defects", 4), ("Decodability", 4), ("Decode", 4) },
        });

    [Fact]
    public void StackedDataBarRowByRow()
    {
        // row 1 all A, row 2 mean 3.4 = B: the symbol is B (the mean of all 20, 3.7, would be A)
        var grades = Enumerable.Repeat(4, 10).Concat(new[] { 4, 4, 3, 3, 3, 3, 4, 4, 3, 3 }).ToList();
        var scans = grades.Select(g => Scan(g)).ToList();
        var pdf = GradeReport.Build(Image(), StackedDataBar(overall: 3), new GradeReportInfo { EngineScans = scans });
        var content = Text(pdf);
        Assert.DoesNotContain("does not match", content);
        Assert.Contains("(Scan lines by row", content);
        Assert.Contains("(Row 1 of 2)", content);
        Assert.Contains("(Row 2 of 2)", content);
        Assert.Contains("(B \\(3.4\\))", content);                // overall: the lowest row's mean
        Assert.Contains("row 2, the lowest, grades the symbol: B", content);
        Assert.Contains("20 \\(2 rows of 10, 20 decoded\\)", content);
        Assert.Contains("(10/10)", content);                     // each row's decoded scans
        Assert.Contains("(Modulation by scan)", content);
        Assert.Contains("row 2 of 2 scans 3, 4, 5, 6, 9, 10", content);
        Assert.DoesNotContain("(Scan reflectance profiles", content); // the image's ten scans would cross the rows
        Assert.DoesNotContain("(Scan 11)", content);             // scans numbered within their row
        if (Environment.GetEnvironmentVariable("VS_REPORT_STACKED_SAMPLE") is { Length: > 0 } path) File.WriteAllBytes(path, pdf);

        // the mean of all scans is not the engine's grading of a stacked symbol: an overall A does not match
        var wrong = Text(GradeReport.Build(Image(), StackedDataBar(overall: 4), new GradeReportInfo { EngineScans = scans }));
        Assert.Contains("does not match its grading", wrong);
        // nor a list that is not whole rows of ten
        var odd = Text(GradeReport.Build(Image(), StackedDataBar(overall: 3, scans: 15), new GradeReportInfo { EngineScans = scans.Take(15).ToList() }));
        Assert.Contains("does not match its grading", odd);
    }

    [Fact]
    public void StackedSymbolGradeAndPatternByRow()
    {
        var grades = Enumerable.Repeat(4, 10).Concat(Enumerable.Repeat(2, 10)).Concat(Enumerable.Repeat(4, 10)).ToList();
        Assert.Equal(2, GradeReport.SymbolGrade(grades, stacked: true));
        Assert.Equal(3, GradeReport.SymbolGrade(grades, stacked: false));   // 3.33: the mean of all
        Assert.Equal(-1, GradeReport.SymbolGrade(new[] { -1, -1 }, stacked: true));
        // a whole row weak: along that row; scattered scans: named by row and their number in it
        Assert.Contains("every scan of row 2 of 3 only", GradeReport.ScanPattern(Enumerable.Range(10, 10).ToList(), 30, 10));
        Assert.Contains("row 1 of 2 scans 2, 3; row 2 of 2 scan 5", GradeReport.ScanPattern(new[] { 1, 2, 14 }, 20, 10));
        Assert.Contains("every scan:", GradeReport.ScanPattern(Enumerable.Range(0, 20).ToList(), 20, 10));
    }
}
