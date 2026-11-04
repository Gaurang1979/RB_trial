using System.Globalization;
using System.Text;
using BarcodeReading;

namespace VisionCore.Reports;

/// <summary>Units distances are reported in (as a verifier's report settings).</summary>
public enum ReportUnits { Mil, Mm, Um }

/// <summary>Who, where and how for a grade report, and which of its sections to include.</summary>
public sealed class GradeReportInfo
{
    public string Job { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Camera { get; init; } = "";
    public string Operator { get; init; } = "";
    public DateTime When { get; init; } = DateTime.Now;
    /// <summary>The PC (station) that graded; the software and its version.</summary>
    public string Station { get; init; } = Environment.MachineName;
    public string Software { get; init; } = "Vision System " + (typeof(GradeReport).Assembly.GetName().Version?.ToString(3) ?? "");
    /// <summary>User information printed in the header: company, batch (null = taken from the code's GS1 batch, AI 10), a note.</summary>
    public string Company { get; init; } = "";
    public string? Batch { get; init; }
    public string Note { get; init; } = "";
    /// <summary>Lighting as set up for grading (e.g. "45° four-side ring"); null = not recorded.</summary>
    public string? Lighting { get; init; }
    /// <summary>Light wavelength in nm (660 for ISO grading) and lighting angle in degrees (45, 30, 90); null = not recorded.</summary>
    public int? WavelengthNm { get; init; }
    public int? AngleDeg { get; init; }
    /// <summary>Sides lit at that angle: 4, 2 or 1, written Q, T or S after the angle (45Q); null = not recorded.</summary>
    public int? LightSides { get; init; }
    /// <summary>The angle with its sides letter, e.g. "45Q"; null when the angle is not recorded.</summary>
    public string? LightingCode => AngleDeg is { } a ? a.ToString(CultureInfo.InvariantCulture) + LightSides switch { 4 => "Q", 2 => "T", 1 => "S", _ => "" } : null;
    /// <summary>When the reflectance calibration (test card) was last made; null = none recorded.</summary>
    public DateTime? CalibrationDate { get; init; }
    /// <summary>The job's lowest print grade (4 = A .. 1 = D); -1 = none set (no acceptance line, no per-parameter PASS).</summary>
    public int MinGrade { get; init; } = -1;
    /// <summary>The tool's GS1 rules and expected AI values (filled from the batch), as the part was judged; null = the
    /// syntax and date checks only.</summary>
    public BarcodeReading.Gs1Rules? Gs1Rules { get; init; }
    public IReadOnlyDictionary<string, string>? Gs1Values { get; init; }
    /// <summary>1D codes: the engine's scan lines in order; null = the code's own (CodeQuality.ScanLines). Without any,
    /// the per-scan table shows the report's estimate from the image, without decodability and decode. Used only
    /// when there is one per scan and their mean is the overall grade (stacked GS1 DataBar: ten per row, top row
    /// first, the lowest row's mean the overall grade).</summary>
    public IReadOnlyList<EngineScan>? EngineScans { get; init; }
    public ReportUnits Units { get; init; } = ReportUnits.Mil;
    /// <summary>Full: every parameter's value as well as its grade. Summary: grades only.</summary>
    public bool Full { get; init; } = true;
    // report sections, as a verifier's report settings
    public bool IncludeImage { get; init; } = true;
    public bool GeneralCharacteristics { get; init; } = true;
    public bool DataDetail { get; init; } = true;
    public bool AsciiValues { get; init; } = true;
    public bool ModulationTable { get; init; } = true;
    public bool Histograms { get; init; } = true;
}

/// <summary>
/// The grade report of one code (tool specification 6.5), laid out as a verifier's report (after the Cognex
/// DataMan 475V Verification Report): the header (station, software, last calibrated, company, operator,
/// batch, note, job, image and times); the verification grade row (overall, aperture, wavelength, lighting,
/// formal grade); acceptance and data format results; the quality parameters, numbered, each with grade,
/// value and PASS against the job's lowest grade, the Data Matrix fixed-pattern segments among them; the
/// general characteristics (matrix and data region size, codewords and error correction, polarity, X and
/// pixels per module, contrast uniformity and where, MRD); data detail (data, GS1 elements, ASCII values);
/// the image; the modulation values table (each cell's modulation coloured by grade, light and dark, a cell
/// of the fixed pattern on the wrong side shown as 0); the histograms of the symbol region and the cell
/// centres with the threshold and grade bands. For 1D (after ISO/IEC 15416 verifier reports) the ten scan
/// lines: a table of each scan's Rmin, Rmax, SC, ECmin, MOD, defects, decodability, decode, quiet zones in X
/// and grade with the symbol means, the scan reflectance profiles each with its grade, the X-dimension, bar
/// width growth, symbol size, quiet zones against the type's minimum and the check digit; a stacked GS1 DataBar
/// row by row (ISO/IEC 24724), each row's ten scans with the row's grade and the lowest row as the symbol's. The grades are the
/// engine's; the figures and segment checks are measured from the same image and labelled so.
/// Without a reflectance calibration the report says the grade is process control, not ISO verification,
/// and gives no formal grade (as a verifier does uncalibrated).
/// </summary>
public static class GradeReport
{
    private const float L = 50, R = MiniPdf.PageWidth - 50, Bottom = MiniPdf.PageHeight - 64;

    public static byte[] Build(GrayImage image, CodeRead code, GradeReportInfo info)
    {
        var q = code.Quality ?? throw new ArgumentException("The code was not graded");
        // PDF417 comes in the 1D fields but is graded after ISO/IEC 15415's multi-row method: laid out as a 2D symbol
        bool pdf417 = q.Linear is not null && code.Format == CodeFormat.Pdf417;
        bool linear = q.Linear is not null && !pdf417;
        // stacked GS1 DataBar: graded row by row, ten scans a row, the symbol taking the lowest row's grade (ISO/IEC 24724)
        bool stacked = linear && code.Format == CodeFormat.DataBar && q.Linear!.Scans > ScansPerRow;
        string standard = linear ? "ISO/IEC 15416" : "ISO/IEC 15415";
        // GS1: with the code's symbology identifier in front, a symbol without FNC1 first fails ("]d1", "]Q1");
        // without one (not known) the report checks the element syntax and the tool's GS1 rules, and says so.
        // A GS1 Digital Link URL is carried in a plain symbol: its identifier is left off
        bool fnc1Known = code.SymbologyId.Length > 0 && !code.Text.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        var gs1 = Gs1Check.Check(fnc1Known ? code.SymbologyId + code.Text : code.Text, info.Gs1Rules ?? new Gs1Rules { ExpiryNotPast = false }, image.TakenAt ?? info.When);
        var gs1Problems = gs1.Problems.ToList();
        if (info.Gs1Values is { } want && gs1.Valid)
            foreach (var (ai, v) in want)
                if (gs1.Get(ai) is not { } got) gs1Problems.Add(new Gs1Problem($"Code has no {Gs1Check.NameOf(ai)} ({ai})", "explain.gs1.missing_ai"));
                else if (got != v) gs1Problems.Add(new Gs1Problem($"The {Gs1Check.NameOf(ai)} ({ai}) is {got}, expected {v}", "explain.gs1.wrong_value"));
        // GS1 data is recognised by its data: a symbol without FNC1 first holding it is shown as GS1 data that fails
        var data = fnc1Known ? Gs1Check.Check(code.Text, info.Gs1Rules ?? new Gs1Rules { ExpiryNotPast = false }, image.TakenAt ?? info.When) : gs1;
        bool isGs1 = info.Gs1Rules is not null || data.Elements.Any(e => e.Ai is "00" or "01" or "02" or "414" or "8006");
        bool iso = linear || q.Calibrated;
        double apShare = iso ? 0.8 : 1.0 / 3.0;
        float radius = (float)(apShare * q.ModuleSizePx / 2);
        // the engine's grid when it gives one (QR, Micro QR, Data Matrix, Aztec), else the nearest size from the corners
        var (rows, cols) = linear || pdf417 ? (0, 0)
            : q.GridRows > 0 && q.GridCols > 0 ? (q.GridRows, q.GridCols) : SymbolAnalysis.GridSize(code.Format, code.Corners, q.ModuleSizePx);
        var cells = rows > 0 ? SymbolAnalysis.Cells(image, code.Corners, rows, cols, radius) : null;
        var segments = cells is not null && code.Format == CodeFormat.DataMatrix
            ? SymbolAnalysis.DataMatrixSegments(image, code.Corners, cells, radius) : new List<SymbolAnalysis.Segment>();
        // 1D: the ten scans measured from the image, and the engine's own when handed over (not for a stacked
        // symbol: ten scans over its whole height would cross its rows and separator patterns)
        var lin = linear && !stacked ? LinearAnalysis.Analyse(image, code.Corners, q.ModuleSizePx, code.Format) : null;
        // the engine's scans only when they are its whole grading: one per scan line, their mean (stacked: the
        // lowest row's) its overall grade
        var esIn = !linear ? null : info.EngineScans is { Count: > 0 } engineScans ? engineScans
                 : q.ScanLines.Count > 0 ? q.ScanLines.Select(x => new EngineScan(x.RMin, x.RMax, x.SymbolContrast, x.EdgeContrastMin, x.Modulation,
                       x.Defects, x.Decodability, x.Decoded, x.QuietZoneOk, x.GradeRmin, x.GradeQuietZone, x.Grade)).ToList() : null;
        var es = esIn is not null && q.Linear is { } lg && esIn.Count == lg.Scans && (!stacked || esIn.Count % ScansPerRow == 0)
                 && SymbolGrade(esIn.Select(x => x.Grade).ToList(), stacked) == q.OverallGrade
            ? esIn : null;
        // the symbol's scan mean: of all its scans, or the lowest row's
        double? scanMean = es is null ? null : ScanRows(es, stacked)[LowestRow(es.Select(x => x.Grade).ToList(), stacked)].Average(x => (double)x.Grade);

        var pdf = new MiniPdf();
        var doc = new Doc(pdf, linear);
        doc.NewPage();
        float y = 60;

        // ---- header
        pdf.Text(L, y, "Verification Report", 18, bold: true);
        pdf.Text(R - MiniPdf.TextWidth(info.Station, 9), y - 6, info.Station, 9);
        pdf.Text(R - MiniPdf.TextWidth(info.Software, 8), y + 5, info.Software, 8);
        y += 18;
        string lastCal = q.Calibrated
            ? "Last calibrated: " + (info.CalibrationDate is { } cd ? cd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "date not recorded")
            : "Last calibrated: N/A (no reflectance calibration)";
        pdf.Text(L, y, (q.Calibrated ? $"Verifier-style grading after {standard}" : $"Process control grading after {standard}: not ISO verification") + "   " + lastCal, 8);
        y += 8;
        pdf.Line(L, y, R, y, 0.8f);
        y += 16;
        string batch = info.Batch ?? gs1.Get("10") ?? "";
        var hl = new List<(string, string)> { ("Company", info.Company), ("Operator", info.Operator), ("Batch", batch), ("Note", info.Note) };
        var hr = new List<(string, string)>
        {
            ("Job", info.Job), ("Tool / camera", $"{info.Tool} / {info.Camera}"), ("Image", image.Name ?? "-"),
            ("Image time", image.TakenAt is { } taken ? taken.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-"),
            ("Report time", info.When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        };
        y = TwoColumns(pdf, y, hl.Select(x => (x.Item1, x.Item2.Length == 0 ? "-" : x.Item2)).ToList(), hr);
        y += 6;

        // ---- verification grade row: Overall | Aperture | Wavelength | Lighting | Formal
        string apText = q.XDimMm > 0 ? Dist(apShare * q.XDimMm, info.Units) : F($"{apShare * q.ModuleSizePx:0.0} px");
        var grade = new[]
        {
            // 1D: the mean of the engine's scan grades when handed over; otherwise the engine's rounding, the letter only
            ("Overall", linear ? scanMean is { } sm ? F($"{CodeQuality.Letter(q.OverallGrade)} ({sm:0.0})") : CodeQuality.Letter(q.OverallGrade)
                               : F($"{CodeQuality.Letter(q.OverallGrade)} ({q.OverallGrade:0.0})")),
            ("Aperture", (iso ? "0.8 X = " : "X/3 = ") + apText),
            ("Wavelength", info.WavelengthNm is { } nm ? $"{nm} nm" : "not recorded"),
            ("Lighting", info.LightingCode ?? info.Lighting ?? "not recorded"),
            ("Formal", FormalGrade(q, info, linear, scanMean) ?? (linear && scanMean is null ? "N/A (no scan mean yet)" : !q.Calibrated ? "N/A (uncalibrated)" : "N/A (setup not recorded)")),
        };
        y = GradeRow(pdf, y, grade, q.OverallGrade);
        pdf.Text(L, y, stacked ? "Overall: the lowest row's grade (ISO/IEC 24724), a row's grade the mean of its ten scan grades; a scan's grade is its lowest parameter."
                              : linear ? "Overall: the mean of the scan grades, rounded to a grade by the engine; a scan's grade is its lowest parameter."
                              : pdf417 ? "Overall: the lowest of the row scans' mean grade (Rmin, contrast, edge contrast, start and stop) and the symbol's parameters."
                              : "Overall: the lowest parameter grade.", 7);
        if (esIn is not null && es is null)
        {
            y += 9;
            pdf.Text(L, y, "The engine's scan list does not match its grading (number of scans or their mean): not used; the scan table is the report's estimate.", 7, bold: true);
        }
        y += 14;

        // ---- acceptance and format results
        if (info.MinGrade >= 1)
            y = ResultBar(pdf, y, "Grade acceptance", $"lowest print grade {CodeQuality.Letter(info.MinGrade)} (the print grade only)", q.OverallGrade >= info.MinGrade);
        if (isGs1)
            y = ResultBar(pdf, y, "GS1 data check", fnc1Known ? $"FNC1 first ({code.SymbologyId}), element syntax and the tool's GS1 rules"
                                                                  : "element syntax and the tool's GS1 rules (FNC1 position not checked)", gs1Problems.Count == 0);
        y += 6;

        // ---- quality parameters
        y = Section(pdf, y, $"{standard} quality parameters");
        pdf.Text(L, y, "#", 8, bold: true);
        pdf.Text(L + 18, y, "Parameter", 8, bold: true);
        pdf.Text(L + 215, y, "Grade", 8, bold: true);
        if (info.Full) pdf.Text(L + 260, y, "Value", 8, bold: true);
        if (info.MinGrade >= 1) pdf.Text(R - 34, y, "Result", 8, bold: true);
        y += 4;
        pdf.Line(L, y, R, y, 0.3f, 0.5f);
        y += 11;
        int n = 0;
        string Num() => (++n).ToString(CultureInfo.InvariantCulture);
        // light and dark: the engine's for 1D; for 2D the report's estimate from the cells' grey levels
        var rlrd = linear ? F($"Rl {q.Linear!.RMax:0} % / Rd {q.Linear.RMin:0} %")
                 : cells is not null ? F($"cells: light {cells.Threshold + cells.Contrast / 2:0} / dark {cells.Threshold - cells.Contrast / 2:0} grey %") : "";
        foreach (var (name, g) in q.Grades)
        {
            string value = ValueOf(name, q);
            if (name == "Symbol contrast" && rlrd.Length > 0) value += "   (" + rlrd + ")";
            y = doc.Param(y, Num(), name, g, value, info);
            if (name == "Fixed pattern damage")
                foreach (var sg in segments)
                    y = doc.Param(y, Num(), $"   {sg.Name} ({sg.Code})", -1,
                        $"{sg.Wrong} of {sg.Cells} cells wrong, lowest modulation {sg.LowestModulation:0.00}", info,
                        sg.Wrong == 0 ? null : false);
        }
        if (!linear) y = doc.Param(y, Num(), "Decode", 4, "decoded", info);
        // 1D: Rmin and the quiet zone are graded per scan; listed here from the engine's scans, else the image's
        if (linear && (es is not null || lin is not null))
        {
            string src = es is null ? " (from the image)" : "";
            int n10 = es?.Count ?? lin!.Scans.Count;
            if (!q.Grades.Any(g => g.Parameter == "Rmin"))
            {
                var gr = es?.Select(x => x.GradeRmin).ToList() ?? lin!.Scans.Select(x => x.GradeRmin).ToList();
                y = doc.Param(y, Num(), "Rmin", SymbolGrade(gr, stacked), $"Rmin <= 0.5 Rmax in {gr.Count(g => g == 4)} of {n10} scans{src}", info);
            }
            if (!q.Grades.Any(g => g.Parameter == "Quiet zone"))
            {
                var gq = es?.Select(x => x.GradeQuietZone).ToList() ?? lin!.Scans.Select(x => x.GradeQuiet).ToList();
                var (mL, mR) = LinearAnalysis.MinQuietZone(code.Format);
                y = doc.Param(y, Num(), "Quiet zone", SymbolGrade(gq, stacked), F($"clear (min {mL:0} X / {mR:0} X) in {gq.Count(g => g == 4)} of {n10} scans{src}"), info);
            }
        }
        if (pdf417)
        {
            pdf.Text(L + 18, y, "Contrast and edge contrast: means over the row scans; modulation, defects and decodability graded per codeword against the error correction.", 6.5f);
            y += 10;
        }
        if (segments.Count > 0)
        {
            pdf.Text(L + 18, y, "Fixed pattern segments: the report's estimate, each cell checked against its known pattern from grey levels (not the engine's grading); transition ratios and average grade: not measured yet.", 6f);
            y += 10;
        }
        if (linear)
        {
            // stacked: the lowest row's means, a longer note wrapped
            foreach (var line in WrapWords((stacked ? "Values: the means over the lowest row's 10 scans; Rmin and the quiet zone: the lowest row mean." : "Values: the means over the 10 scans.")
                                           + " Edge contrast, Rmin and the quiet zone are graded on each scan and counted in the overall grade.", R - L - 18, 6.5f))
            {
                pdf.Text(L + 18, y, line, 6.5f);
                y += 10;
            }
        }
        y += 6;

        // ---- diagnosis: what a parameter below B usually means and what to check (the report's own guidance)
        var weak = q.Grades.Where(g => g.Grade >= 0 && g.Grade < 3).ToList();
        if (segments.Any(sg => sg.Wrong > 0) && !weak.Any(g => g.Parameter == "Fixed pattern damage")) weak.Add(("Fixed pattern damage", -1));
        if (q.Values.TryGetValue("Print growth", out var pg) && Math.Abs(pg) > 0.15f) weak.Add(("Print growth", -1));
        var perScan = new List<(string Name, int Grade)>();
        if (lin is not null || es is not null)
        {
            // the 1D parameters graded per scan, weakest letters from the scan table (the engine's when handed over)
            foreach (var (name, grades) in ScanGrades(lin, es, code.Format, stacked))
            {
                int g = SymbolGrade(grades, stacked);
                if (grades.Any(x => x is >= 0 and < 3)) perScan.Add((name, g));
                if (g is >= 0 and < 3 && !weak.Any(w => w.Parameter == name)) weak.Add((name, g));
            }
            if (lin is not null && Math.Abs(lin.GrowthX) > 0.15f) weak.Add(("Print growth", -1));
        }
        if (weak.Count > 0)
        {
            y = doc.Ensure(y, 40);
            y = Section(pdf, y, "Diagnosis: what to check");
            foreach (var (name, g) in weak)
            {
                if (Advice(name, linear) is not { } advice) continue;
                y = doc.Ensure(y, 26);
                pdf.Text(L, y, name + (g >= 0 ? $" ({CodeQuality.Letter(g)})" : ""), 8, bold: true);
                foreach (var line in WrapWords(advice, R - L - 120, 8))
                {
                    pdf.Text(L + 120, y, line, 8);
                    y += 10;
                }
                y += 3;
            }
            y += 6;
        }
        if (perScan.Count > 0)
        {
            // where along the bars' height: on a few scans a local cause, on all of them one across the symbol
            y = doc.Ensure(y, 30);
            if (weak.Count == 0) y = Section(pdf, y, "Diagnosis: what to check");
            foreach (var (name, _) in perScan)
            {
                var grades = ScanGrades(lin, es, code.Format, stacked).First(x => x.Name == name).Grades;
                var low = Enumerable.Range(0, grades.Count).Where(i => grades[i] is >= 0 and < 3).ToList();
                string text = ScanPattern(low, grades.Count, stacked ? ScansPerRow : 0);
                y = doc.Ensure(y, 22);
                pdf.Text(L, y, name + " by scan", 8, bold: true);
                foreach (var line in WrapWords(text, R - L - 120, 8)) { pdf.Text(L + 120, y, line, 8); y += 10; }
                y += 3;
            }
            y += 6;
        }

        // ---- general characteristics
        if (info.GeneralCharacteristics)
        {
            var gc = new List<(string, string)>
            {
                ("Symbology", CodeFormats.Name(code.Format) + (isGs1 ? " (GS1 data)" : "")),
                ("Standard", standard + (isGs1 ? ", GS1 General Specifications" : "")),
            };
            if (pdf417) gc.Add(("Rows scanned", $"{q.Linear!.Scans} ({q.Linear.DecodedScans} with start and stop)"));
            else if (!linear && cells is null) gc.Add(("Matrix size", "not estimated"));
            if (cells is not null)
            {
                var ecc = code.Format == CodeFormat.DataMatrix ? SymbolAnalysis.Ecc200(rows, cols) : null;
                gc.Add(("Matrix size", ecc is { } e ? $"{rows}x{cols} (Data: {e.DataRows}x{e.DataCols})" : $"{rows}x{cols}"));
                gc.Add(("Encoded characters", code.Text.Length.ToString(CultureInfo.InvariantCulture)));
                if (ecc is { } e2)
                {
                    gc.Add(("Total codewords", (e2.DataCw + e2.EcCw).ToString(CultureInfo.InvariantCulture)));
                    gc.Add(("Data codewords", e2.DataCw.ToString(CultureInfo.InvariantCulture)));
                    gc.Add(("Error correction budget", e2.EcCw.ToString(CultureInfo.InvariantCulture)));
                    int used = (int)Math.Round((1 - (q.Values.TryGetValue("Unused error correction", out var uec) ? uec : 1)) * 100);
                    gc.Add(("Error capacity used", $"{used} %"));
                    gc.Add(("Error correction type", "ECC 200"));
                }
                int dark = cells.Reflectance.Count(v => v < cells.Threshold);
                bool finderDark = segments.Count == 0 || segments[0].Wrong < segments[0].Cells / 2;
                gc.Add(("Image", finderDark ? "Black on white" : "White on black"));
                int at = Array.IndexOf(cells.Modulation, cells.MinModulation);
                gc.Add(("Contrast uniformity", F($"{cells.MinModulation * 100:0} at module ({at / cols},{at % cols})")));
                float lt = cells.Threshold + cells.Contrast / 2, dk = cells.Threshold - cells.Contrast / 2;
                gc.Add(("MRD (grey)", F($"{lt - dk:0} % ({lt:0} % - {dk:0} %)")));
                if (q.Values.TryGetValue("Print growth", out var growth))
                    gc.Add(("Print growth (both axes)", F($"{growth * 100:+0;-0;0} % of X") + (q.XDimMm > 0 ? ", " + Dist(growth * q.XDimMm, info.Units) : "")));
            }
            gc.Add(("Nominal X dimension", q.XDimMm > 0 ? Dist(q.XDimMm, info.Units) : "no mm scale set"));
            gc.Add(("Pixels per module", F($"{q.ModuleSizePx:0.00}")));
            if (linear)
            {
                if (code.Format is CodeFormat.Ean13 or CodeFormat.Ean8 or CodeFormat.UpcA or CodeFormat.UpcE && q.XDimMm > 0)
                    gc.Add(("Magnification", F($"{q.XDimMm / 0.330 * 100:0} %")));
                // the inspection band: 10 % to 90 % of the bar height measured from the image (the corners may be a thin line)
                float zone = lin is not null ? 0.8f * lin.BarHeightPx : ZoneHeightPx(code.Corners);
                gc.Add(("Inspection zone height", q.XDimMm > 0 && q.ModuleSizePx > 0 ? Dist(zone * q.XDimMm / q.ModuleSizePx, info.Units) : F($"{zone:0} px")));
                gc.Add(("Scans", stacked ? $"{q.Linear!.Scans} ({q.Linear.Scans / ScansPerRow} rows of {ScansPerRow}, {q.Linear.DecodedScans} decoded)"
                                         : $"{q.Linear!.Scans} ({q.Linear.DecodedScans} decoded)"));
                if (lin is not null)
                {
                    string D(float px) => q.XDimMm > 0 && q.ModuleSizePx > 0 ? Dist(px * q.XDimMm / q.ModuleSizePx, info.Units) : F($"{px:0} px");
                    gc.Add(("X-dimension (measured)", F($"{lin.XPx:0.00} px") + (q.XDimMm > 0 ? ", " + Dist(q.XDimMm, info.Units) : "")));
                    gc.Add(("Bar width growth", F($"{lin.GrowthX:+0.00;-0.00;0.00} X ({lin.GrowthX * 100:+0;-0;0} %)") + (q.XDimMm > 0 ? ", " + Dist(lin.GrowthX * q.XDimMm, info.Units) : "")));
                    if (lin.WidthPx > 0) gc.Add(("Symbol width (bars)", D(lin.WidthPx) + F($" = {lin.WidthPx / lin.XPx:0.0} X")));
                    gc.Add(("Bar height", D(lin.BarHeightPx) + F($" = {lin.BarHeightPx / lin.XPx:0} X")));
                    var (mL, mR) = LinearAnalysis.MinQuietZone(code.Format);
                    var ok = lin.Scans.Where(s => s.Edges >= 4).ToList();
                    if (ok.Count > 0)
                    {
                        float ql = ok.Min(s => s.QuietLeftX), qr = ok.Min(s => s.QuietRightX);
                        // the scan runs 12 X past the code: more than that is shown as at least
                        string Qz(float v, float min) => (v >= 11.5f ? F($">= {v:0.0} X") : F($"{v:0.0} X")) + F($" (min {min:0} X) ") + (v >= min ? "PASS" : "FAIL");
                        gc.Add(("Quiet zone left (least)", Qz(ql, mL)));
                        gc.Add(("Quiet zone right (least)", Qz(qr, mR)));
                    }
                }
                if (q.XDimMm > 0 && RecommendedAperture1D(q.XDimMm / 0.0254) is { } rec)
                    gc.Add(("Recommended aperture", F($"{rec} mil for X {q.XDimMm / 0.0254:0.0} mil (application standard first)")));
            }
            gc.Add(("Lighting uniformity", F($"{q.LightingUniformity:0.00}")));
            y = doc.Ensure(y, 30 + 12 * ((gc.Count + 1) / 2));
            y = Section(pdf, y, "General characteristics" + (cells is not null ? " (cell figures: the report's estimate from grey levels)" : ""));
            int half = (gc.Count + 1) / 2;
            y = TwoColumns(pdf, y, gc.Take(half).ToList(), gc.Skip(half).ToList(), 110);
            y += 8;
        }

        // ---- data detail
        if (info.DataDetail)
        {
            y = doc.Ensure(y, 60);
            y = Section(pdf, y, "Data detail");
            pdf.Text(L, y, "Data", 9, bold: true);
            var lines = WrapToWidth(Printable(code.Text), R - L - 70, 9).ToList();
            foreach (var line in lines.Take(6)) { pdf.Text(L + 70, y, line, 9); y += 12; }
            if (lines.Count > 6) { pdf.Text(L + 70, y, $"... ({lines.Skip(6).Sum(l => l.Length)} more characters)", 9); y += 12; }
            if (linear && LinearAnalysis.CheckDigit(code.Format, code.Text) is { } check)
            {
                pdf.Text(L, y, "Check digit", 9, bold: true);
                pdf.Text(L + 70, y, Clip(check.Text, R - L - 70, 9), 9, bold: check.Ok == false);
                y += 12;
            }
            if (isGs1)
            {
                y += 4;
                // problems first, so a failed check always shows why
                foreach (var problem in gs1Problems.Take(6))
                {
                    y = doc.Ensure(y, 12);
                    pdf.Text(L + 70, y, Clip("Problem: " + problem.Message, R - L - 70, 8), 8, bold: true);
                    y += 11;
                }
                if (gs1Problems.Count > 6) { pdf.Text(L + 70, y, $"... {gs1Problems.Count - 6} more problems", 8); y += 11; }
                const int maxElements = 20;
                foreach (var (ai, value) in gs1.Elements.Take(maxElements))
                {
                    y = doc.Ensure(y, 14);
                    string shown = ai is "11" or "12" or "13" or "15" or "16" or "17" && Gs1Check.Date(value, true, info.When) is { } d
                        ? $"{value}  ({d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)})" : Printable(value);
                    pdf.Text(L + 70, y, $"({ai})", 9);
                    pdf.Text(L + 110, y, Gs1Check.NameOf(ai), 9);
                    pdf.Text(L + 240, y, Clip(shown, R - L - 240, 9), 9);
                    y += 12;
                }
                if (gs1.Elements.Count > maxElements) { pdf.Text(L + 70, y, $"... {gs1.Elements.Count - maxElements} more elements", 8); y += 11; }
            }
            if (info.AsciiValues)
            {
                y += 4;
                pdf.Text(L, y, "ASCII values", 9, bold: true);
                var ascii = string.Join(" ", code.Text.Take(400).Select(ch => ((int)ch).ToString("000", CultureInfo.InvariantCulture)));
                foreach (var line in WrapToWidth(ascii, R - L - 70, 8).Take(8)) { y = doc.Ensure(y, 12); pdf.Text(L + 70, y, line, 8); y += 11; }
            }
            y += 8;
        }

        // ---- image, and for 2D the modulation overlay: the decode grid with the aperture at each cell, the
        // cells below grade B marked yellow (C, D) or red (F, or a fixed-pattern cell on the wrong side)
        if (info.IncludeImage)
        {
            var (px, w, h, _, _, _) = CropAt(image, code.Corners);
            if (w > 0)
            {
                float leftW = cells is not null ? (R - L) * 0.4f : R - L;
                float sc = Math.Min(leftW / w, 200f / h);
                float blockH = h * sc;
                // the overlay: a tight crop (a few modules round the symbol), as large as the space allows
                var (opx, ow, oh, cx0, cy0, step) = cells is not null ? CropAt(image, code.Corners, 900, 0.08f) : (px, 0, 0, 0, 0, 1);
                float rightW = R - L - leftW - 16, osc = ow > 0 ? Math.Min(rightW / ow, 250f / oh) : 0;
                blockH = Math.Max(blockH, oh * osc);
                y = doc.Ensure(y, blockH + 30);
                y = Section(pdf, y, cells is not null ? "Image and modulation overlay (the report's estimate)" : "Image");
                pdf.Image(px, w, h, L, y - 6, w * sc, h * sc);
                if (cells is not null && ow > 0)
                {
                    float ox = L + leftW + 16, oy = y - 6;
                    pdf.Image(opx, ow, oh, ox, oy, ow * osc, oh * osc);
                    (float X, float Y) P(float u, float v)
                    {
                        var (ix, iy) = SymbolAnalysis.Map(code.Corners, u, v);
                        return (ox + (ix - cx0) / step * osc, oy + (iy - cy0) / step * osc);
                    }
                    for (int k = 0; k <= cols; k++) pdf.Polyline(new[] { P((float)k / cols, 0), P((float)k / cols, 1) }, 0.25f, 0.2f, 0.8f, 0.3f);
                    for (int r = 0; r <= rows; r++) pdf.Polyline(new[] { P(0, (float)r / rows), P(1, (float)r / rows) }, 0.25f, 0.2f, 0.8f, 0.3f);
                    var wrongCells = new HashSet<(int, int)>(segments.SelectMany(sg => sg.WrongCells));
                    float ar = Math.Max(0.6f, radius / step * osc);
                    for (int r = 0; r < rows; r++)
                        for (int k = 0; k < cols; k++)
                        {
                            var (x, yy) = P((k + 0.5f) / cols, (r + 0.5f) / rows);
                            int g = wrongCells.Contains((r, k)) ? 0 : cells.Grade[r * cols + k];
                            if (g >= 3) pdf.Circle(x, yy, ar, 0.2f, 0.8f, 0.3f, false, 0.3f);
                            else
                            {
                                var (cr, cg, cb) = g == 0 ? (0.9f, 0.1f, 0.1f) : (1f, 0.8f, 0.1f);
                                pdf.Circle(x, yy, Math.Max(ar, 1.8f), cr, cg, cb, true);
                            }
                        }
                    pdf.Text(ox, oy + oh * osc + 9, "Grid and aperture per cell; yellow: cell grade C or D, red: F or on the wrong side.", 6.5f);
                }
                y += blockH + 16;
            }
        }

        // ---- modulation values table
        if (info.ModulationTable && cells is not null)
        {
            float cell = Math.Min(11f, (R - L - 90) / (cols + 2));
            float tableH = cell * rows + 30;
            y = doc.Ensure(y, tableH + 20);
            y = Section(pdf, y, "Modulation values (x 100): the report's estimate from grey levels, not the engine's grading");
            y = ModulationTable(pdf, y, cells, segments, cell, (r, k) => SymbolAnalysis.CellAt(image, code.Corners, rows, cols, r, k, radius),
                ring: code.Format != CodeFormat.Aztec); // Aztec needs no quiet zone (ISO/IEC 24778)
            y += 10;
        }

        // ---- histograms and profile
        float[] prof = lin is not null ? lin.Scans[4].Profile
                     : linear && !stacked ? SymbolAnalysis.Profile(image, code.Corners, q.ModuleSizePx, (float)(0.8 * q.ModuleSizePx)) : Array.Empty<float>();
        if (lin is not null || es is not null) y = ScanTable(doc, pdf, y, lin, es, code.Format, stacked);
        if (lin is not null)
        {
            y = ScanProfiles(doc, pdf, y, lin, es, code.Format);
        }
        else if (linear && prof.Length > 1)
        {
            y = doc.Ensure(y, 130);
            // the threshold of the profile itself (grey levels: the engine's reflectance scale may differ when calibrated)
            Graph(pdf, ref y, "Scan reflectance profile (middle scan line, 0.8 X aperture, grey levels: the report's estimate)", prof, prof.Min(), prof.Max());
        }
        if (info.Histograms)
        {
            y = doc.Ensure(y, 100);
            // 1D: the corners a reader gives are a scan line, so the region's pixels are the profile's
            var region = linear && !stacked ? prof : SymbolAnalysis.PixelsInside(image, code.Corners).ToArray();
            Histogram(pdf, ref y, "Histogram of symbol region (grey levels)", SymbolAnalysis.Histogram(region), cells?.Threshold, cells?.Contrast, false);
            if (cells is not null)
            {
                y = doc.Ensure(y, 110);
                Histogram(pdf, ref y, "Histogram of module centres (grey levels, the report's estimate)", SymbolAnalysis.Histogram(cells.Reflectance), cells.Threshold, cells.Contrast, true);
            }
        }
        doc.Finish();
        return pdf.ToBytes();
    }

    // pages with a footer each; Ensure starts a new page when the next block does not fit
    private sealed class Doc
    {
        private readonly MiniPdf _pdf;
        private readonly bool _linear;
        private int _page;
        public Doc(MiniPdf pdf, bool linear) { _pdf = pdf; _linear = linear; }

        public void NewPage()
        {
            if (_page > 0) Footer();
            _pdf.AddPage();
            _page++;
        }

        public float Ensure(float y, float need)
        {
            if (y + need <= Bottom) return y;
            NewPage();
            return 64;
        }

        public void Finish() => Footer();

        private void Footer()
        {
            _pdf.Line(L, MiniPdf.PageHeight - 50, R, MiniPdf.PageHeight - 50, 0.3f, 0.5f);
            _pdf.Text(L, MiniPdf.PageHeight - 36, "Grades A = 4 ... F = 0 by the grading engine; figures marked \"from the image\" are measured from the graded image.", 7);
            _pdf.Text(R - 30, MiniPdf.PageHeight - 36, $"Page {_page}", 7);
        }

        public float Param(float y, string num, string name, int grade, string value, GradeReportInfo info, bool? passOverride = null)
        {
            y = Ensure(y, 13);
            _pdf.Text(L, y, num, 8);
            _pdf.Text(L + 18, y, name, 8);
            if (grade >= 0)
            {
                var (r, g, b) = SymbolAnalysis.GradeColour(grade);
                _pdf.BoxRgb(L + 205, y - 8, 30, 10, r, g, b);
                _pdf.Text(L + 216, y, CodeQuality.Letter(grade), 8, bold: true);
            }
            if (info.Full) _pdf.Text(L + 260, y, Clip(value, R - 40 - (L + 260), 8), 8);
            if (info.MinGrade >= 1 && (grade >= 0 || passOverride is not null))
            {
                bool pass = passOverride ?? grade >= info.MinGrade;
                _pdf.Text(R - 34, y, pass ? "PASS" : "FAIL", 8, bold: !pass);
            }
            return y + 12;
        }
    }

    // one row of the 1D scan table: the engine's values when handed over (the quiet zones in X always from the image);
    // Row: the symbol row it lies in (stacked DataBar, from 0 at the top; else 0), N its number in that row from 1
    private sealed record ScanRow(int Row, int N, float HeightPct, float RMin, float RMax, float Sc, float Ec, float Mod, float Def,
        float? Decodability, bool? Decoded, float? QzL, float? QzR, int Grade, bool AtMost, IReadOnlyList<(string Name, int Grade)> Grades);

    private static readonly string[] ScanParameters = { "Rmin", "Symbol contrast", "Edge contrast", "Modulation", "Defects", "Decodability", "Decode", "Quiet zone" };

    /// <summary>Scans per row of a stacked GS1 DataBar, as the engine grades it.</summary>
    private const int ScansPerRow = 10;

    // a stacked symbol's scans in rows of ten, top row first; any other 1D symbol's as one row
    private static List<List<T>> ScanRows<T>(IReadOnlyList<T> scans, bool stacked) =>
        stacked ? scans.Chunk(ScansPerRow).Select(c => c.ToList()).ToList() : new List<List<T>> { scans.ToList() };

    /// <summary>The symbol's grade from its scans' grades (-1: not measured on that scan, left out): the mean of the
    /// scans, rounded as the engine rounds it; a stacked GS1 DataBar's the lowest row's mean (rows of ten). -1 for none.</summary>
    public static int SymbolGrade(IReadOnlyList<int> grades, bool stacked) =>
        ScanRows(grades, stacked).Select(r => MeanGrade(r.Where(g => g >= 0).ToList())).Where(g => g >= 0).DefaultIfEmpty(-1).Min();

    // the row that grades a stacked symbol: the first with the lowest mean grade, as the engine picks it
    private static int LowestRow(IReadOnlyList<int> grades, bool stacked)
    {
        var rows = ScanRows(grades, stacked).Select(r => MeanGrade(r)).ToList();
        int worst = 0;
        for (int k = 1; k < rows.Count; k++) if (rows[k] < rows[worst]) worst = k;
        return worst;
    }

    private static List<ScanRow> Rows(LinearAnalysis.Symbol? lin, IReadOnlyList<EngineScan>? es, CodeFormat format, bool stacked = false)
    {
        var rows = new List<ScanRow>();
        int n = es?.Count ?? lin?.Scans.Count ?? 0;
        int per = stacked ? ScansPerRow : Math.Max(1, n);
        for (int i = 0; i < n; i++)
        {
            var s = lin is not null && i < lin.Scans.Count ? lin.Scans[i] : null;
            bool measured = s is { Edges: >= 4 };
            // from the top of the bars (stacked: of its row)
            float pct = 10 + 80f * (i % per) / Math.Max(1, per - 1);
            if (es is not null)
            {
                var e = es[i];
                var g = new List<(string, int)>
                {
                    ("Rmin", e.GradeRmin), ("Symbol contrast", LinearAnalysis.GradeHigh(e.SymbolContrast, 70, 55, 40, 20)),
                    ("Edge contrast", e.EdgeContrastMin >= 15 ? 4 : 0), ("Modulation", LinearAnalysis.GradeHigh(e.Modulation, 0.70f, 0.60f, 0.50f, 0.40f)),
                    ("Defects", LinearAnalysis.GradeLow(e.Defects, 0.15f, 0.20f, 0.25f, 0.30f)),
                    // as the engine: a scan whose elements do not fit its type's decodability grades F (DataBar has none)
                    ("Decodability", e.Decodability >= 0 ? LinearAnalysis.GradeHigh(e.Decodability, 0.62f, 0.50f, 0.37f, 0.25f)
                                     : format == CodeFormat.DataBar ? -1 : 0),
                    ("Decode", e.Decoded ? 4 : 0), ("Quiet zone", e.GradeQuietZone),
                };
                rows.Add(new ScanRow(i / per, i % per + 1, pct, e.RMin, e.RMax, e.SymbolContrast, e.EdgeContrastMin, e.Modulation, e.Defects,
                    e.Decodability >= 0 ? e.Decodability : null, e.Decoded, measured ? s!.QuietLeftX : null, measured ? s!.QuietRightX : null,
                    e.Grade, false, g));
            }
            else if (s is not null)
            {
                var g = new List<(string, int)>
                {
                    ("Rmin", s.GradeRmin), ("Symbol contrast", s.GradeSc), ("Edge contrast", s.GradeEc), ("Modulation", s.GradeMod),
                    ("Defects", s.GradeDefects), ("Quiet zone", s.GradeQuiet),
                };
                rows.Add(new ScanRow(i / per, i % per + 1, pct, s.RMin, s.RMax, s.SymbolContrast, s.ECmin, s.Modulation, s.Defects, null, null,
                    measured ? s.QuietLeftX : null, measured ? s.QuietRightX : null, s.GradeMeasured, true, g));
            }
        }
        return rows;
    }

    // each per-scan parameter with its grade on every scan, in scan order (-1: not measured on that scan);
    // parameters measured on no scan left out
    private static List<(string Name, List<int> Grades)> ScanGrades(LinearAnalysis.Symbol? lin, IReadOnlyList<EngineScan>? es, CodeFormat format, bool stacked)
    {
        var rows = Rows(lin, es, format, stacked);
        return ScanParameters
            .Select(p => (p, rows.Select(r => r.Grades.FirstOrDefault(g => g.Name == p, (Name: p, Grade: -1)).Grade).ToList()))
            .Where(x => x.Item2.Any(g => g >= 0)).ToList();
    }

    /// <summary>The mean of grades rounded to a grade as the engine rounds the scan mean; -1 for none.</summary>
    public static int MeanGrade(IReadOnlyCollection<int> grades)
    {
        if (grades.Count == 0) return -1;
        double avg = grades.Average();
        return avg >= 3.5 ? 4 : avg >= 2.5 ? 3 : avg >= 1.5 ? 2 : avg >= 0.5 ? 1 : 0;
    }

    /// <summary>Where a parameter is weak along the bars' height, and what that points to (scans numbered from 1, top first;
    /// perRow > 0: a stacked symbol's scans in rows of that many, named by row).</summary>
    public static string ScanPattern(IReadOnlyList<int> lowScans, int scans, int perRow = 0)
    {
        if (lowScans.Count == 0) return "";
        if (lowScans.Count == scans)
            return "Below B on every scan: the cause runs across the whole symbol (ink, print growth, contrast or the lighting), not a local spot.";
        if (perRow > 0 && scans > perRow)
        {
            var byRow = lowScans.GroupBy(i => i / perRow).ToList();
            int nRows = (scans + perRow - 1) / perRow;
            string In(int row) => F($"row {row + 1} of {nRows}");
            if (byRow.Count == 1 && byRow[0].Count() == Math.Min(perRow, scans - byRow[0].Key * perRow))
                return $"Below B on every scan of {In(byRow[0].Key)} only: the cause runs along that row (that row printed, " +
                       "lit or creased unevenly, or a print head band across it), not the whole symbol.";
            string rowList = string.Join("; ", byRow.Select(g => $"{In(g.Key)} scan{(g.Count() > 1 ? "s" : "")} " +
                string.Join(", ", g.Select(i => (i % perRow + 1).ToString(CultureInfo.InvariantCulture)))));
            return $"Below B on {rowList} (scans numbered from the top of each row): a local cause, such as a spot, void, scratch, " +
                   "crease, a blocked nozzle or a worn print head element. The scan table shows that row's scans.";
        }
        string list = string.Join(", ", lowScans.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
        string where = lowScans.All(i => i < scans / 3) ? "near the top of the bars"
                     : lowScans.All(i => i >= scans - scans / 3) ? "near the bottom of the bars" : "spread over the bars' height";
        return $"Below B on scan{(lowScans.Count > 1 ? "s" : "")} {list} of {scans}, {where}: a local cause, such as a spot, void, scratch, " +
               "crease, a blocked nozzle or a worn print head element. The profile of that scan shows where along the code.";
    }

    // the scan table: each scan's parameters, its grade, and the symbol means with their grades; a stacked
    // DataBar row by row, each row's scans with the row's means and grade, then the lowest row as the symbol's
    private static float ScanTable(Doc doc, MiniPdf pdf, float y, LinearAnalysis.Symbol? lin, IReadOnlyList<EngineScan>? es, CodeFormat format, bool stacked = false)
    {
        var rows = Rows(lin, es, format, stacked);
        if (rows.Count == 0) return y;
        var groups = rows.GroupBy(r => r.Row).Select(g => g.ToList()).ToList();
        y = doc.Ensure(y, 60 + 11 * groups[0].Count);
        y = Section(pdf, y, stacked
            ? F($"Scan lines by row (ISO/IEC 15416, rows per ISO/IEC 24724): the engine's grading, {groups.Count} rows of {ScansPerRow} scans, top row first")
            : es is null
            ? "Scan lines (ISO/IEC 15416): the report's estimate from the image; decodability and decode need the engine's scans"
            : "Scan lines (ISO/IEC 15416): the engine's grading; quiet zones in X from the image's scan at the same height");
        var (mL, mR) = LinearAnalysis.MinQuietZone(format);
        string[] head = { "Scan", "Height", "Rmin %", "Rmax %", "SC %", "ECmin %", "MOD", "Defects", "Decodab.", "Decode", F($"QZ L (X)"), F($"QZ R (X)"), "Grade" };
        float[] w = { 26, 34, 38, 38, 36, 40, 36, 38, 40, 36, 42, 42, 49 };
        float[] x = new float[w.Length];
        for (int i = 1; i < w.Length; i++) x[i] = x[i - 1] + w[i - 1];
        float Head(float yy)
        {
            for (int i = 0; i < head.Length; i++) pdf.Text(L + x[i] + 2, yy, head[i], 7, bold: true);
            yy += 4;
            pdf.Line(L, yy, R, yy, 0.3f, 0.5f);
            return yy + 10;
        }
        void Cell(int col, float yy, string text, int grade, bool bold = false)
        {
            if (grade >= 0)
            {
                var (r, g, b) = CellColour(grade, false);
                pdf.BoxRgb(L + x[col], yy - 8, w[col] - 1, 10.5f, r, g, b);
            }
            pdf.Text(L + x[col] + 2, yy, text, 7, bold);
        }
        int G(ScanRow r, string p) => r.Grades.FirstOrDefault(g => g.Name == p, (Name: p, Grade: -1)).Grade;
        void Lines(List<ScanRow> rs)
        {
            foreach (var r in rs)
            {
                y = doc.Ensure(y, 24);
                Cell(0, y, r.N.ToString(CultureInfo.InvariantCulture), -1);
                Cell(1, y, F($"{r.HeightPct:0} %"), -1);
                Cell(2, y, F($"{r.RMin:0}"), G(r, "Rmin"));
                Cell(3, y, F($"{r.RMax:0}"), -1);
                Cell(4, y, F($"{r.Sc:0}"), G(r, "Symbol contrast"));
                Cell(5, y, F($"{r.Ec:0}"), G(r, "Edge contrast"));
                Cell(6, y, F($"{r.Mod:0.00}"), G(r, "Modulation"));
                Cell(7, y, F($"{r.Def:0.00}"), G(r, "Defects"));
                Cell(8, y, r.Decodability is { } d ? F($"{d:0.00}") : "-", G(r, "Decodability"));
                Cell(9, y, r.Decoded is { } ok ? ok ? "yes" : "NO" : "-", G(r, "Decode"), r.Decoded == false);
                Cell(10, y, r.QzL is { } ql ? F($"{ql:0.0}") : "-", r.QzL is { } a ? a >= mL ? 4 : 0 : -1);
                Cell(11, y, r.QzR is { } qr ? F($"{qr:0.0}") : "-", r.QzR is { } b2 ? b2 >= mR ? 4 : 0 : -1);
                var (gr, gg, gb) = SymbolAnalysis.GradeColour(r.Grade);
                pdf.BoxRgb(L + x[12], y - 8, w[12] - 1, 10.5f, gr, gg, gb);
                pdf.Text(L + x[12] + 2, y, (r.AtMost ? "at most " : "") + CodeQuality.Letter(r.Grade), 7, bold: true);
                y += 11;
            }
        }
        // the means of a set of scans, each graded on its mean (Rmin, decode and the quiet zone: the mean of the scan grades)
        void Means(string label, List<ScanRow> rs)
        {
            y = doc.Ensure(y, 16);
            pdf.Line(L, y - 7, R, y - 7, 0.5f, 0.3f);
            y += 2;
            float M(Func<ScanRow, float> f) => rs.Average(f);
            int MG(string p) => MeanGrade(rs.Select(r => G(r, p)).Where(g => g >= 0).ToList());
            Cell(0, y, label, -1, true);
            Cell(2, y, F($"{M(r => r.RMin):0}"), MG("Rmin"), true);
            Cell(3, y, F($"{M(r => r.RMax):0}"), -1, true);
            Cell(4, y, F($"{M(r => r.Sc):0}"), LinearAnalysis.GradeHigh(M(r => r.Sc), 70, 55, 40, 20), true);
            Cell(5, y, F($"{M(r => r.Ec):0}"), M(r => r.Ec) >= 15 ? 4 : 0, true);
            Cell(6, y, F($"{M(r => r.Mod):0.00}"), LinearAnalysis.GradeHigh(M(r => r.Mod), 0.70f, 0.60f, 0.50f, 0.40f), true);
            Cell(7, y, F($"{M(r => r.Def):0.00}"), LinearAnalysis.GradeLow(M(r => r.Def), 0.15f, 0.20f, 0.25f, 0.30f), true);
            // as the engine's mean: a scan that could not measure decodability counts 0 (DataBar has none)
            var dec = es is not null && format != CodeFormat.DataBar ? rs.Select(r => r.Decodability ?? 0f).ToList()
                    : rs.Where(r => r.Decodability is not null).Select(r => r.Decodability!.Value).ToList();
            if (dec.Count > 0) Cell(8, y, F($"{dec.Average():0.00}"), LinearAnalysis.GradeHigh(dec.Average(), 0.62f, 0.50f, 0.37f, 0.25f), true);
            if (rs.Any(r => r.Decoded is not null)) Cell(9, y, F($"{rs.Count(r => r.Decoded == true)}/{rs.Count}"), MG("Decode"), true);
            var qls = rs.Where(r => r.QzL is not null).ToList();
            if (qls.Count > 0)
            {
                Cell(10, y, F($"{qls.Min(r => r.QzL!.Value):0.0}"), qls.Min(r => r.QzL!.Value) >= mL ? 4 : 0, true);
                Cell(11, y, F($"{qls.Min(r => r.QzR!.Value):0.0}"), qls.Min(r => r.QzR!.Value) >= mR ? 4 : 0, true);
            }
            double mean = rs.Average(r => (double)r.Grade);
            int mg = MeanGrade(rs.Select(r => r.Grade).ToList());
            var (mr, mgc, mb) = SymbolAnalysis.GradeColour(mg);
            pdf.BoxRgb(L + x[12], y - 8, w[12] - 1, 10.5f, mr, mgc, mb);
            pdf.Text(L + x[12] + 2, y, (rs[0].AtMost ? "<= " : "") + F($"{mean:0.0} ") + CodeQuality.Letter(mg), 7, bold: true);
            y += 12;
        }
        if (!stacked)
        {
            y = Head(y);
            Lines(rows);
            Means("Mean", rows);
        }
        else
        {
            // each row with its own heading, its scans and its means; a row is kept on one page
            foreach (var g in groups)
            {
                y = doc.Ensure(y, 44 + 11 * g.Count);
                pdf.Text(L, y, F($"Row {g[0].Row + 1} of {groups.Count}"), 7.5f, bold: true);
                y = Head(y + 11);
                Lines(g);
                Means(F($"Row {g[0].Row + 1}"), g);
                y += 4;
            }
            // the symbol: the lowest row's grade (the first such row, as the engine)
            var rowGrades = groups.Select(g => MeanGrade(g.Select(r => r.Grade).ToList())).ToList();
            int worst = LowestRow(rows.Select(r => r.Grade).ToList(), true);
            string all = string.Join(", ", rowGrades.Select((g, k) => F($"row {k + 1} {CodeQuality.Letter(g)}")));
            var overall = WrapWords(F($"Overall: row {worst + 1}, the lowest, grades the symbol: {CodeQuality.Letter(rowGrades[worst])} ({groups[worst].Average(r => (double)r.Grade):0.0}). Rows: {all}."), R - L, 7.5f).ToList();
            y = doc.Ensure(y, 10 * overall.Count + 6);
            foreach (var line in overall) { pdf.Text(L, y, line, 7.5f, bold: true); y += 10; }
            y += 4;
        }
        var notes = WrapWords((stacked ? F($"Height: the scan's place from the top of its row; a scan of a stacked row decodes with the other rows' best scans. Quiet zones: graded by the engine on each scan (QZ OK in its grade), minimum {mL:0} X left and {mR:0} X right. ")
                                       : F($"Height: the scan's place from the top of the bars. Quiet zones: least of the scans in the mean row, minimum {mL:0} X left and {mR:0} X right for {CodeFormats.Name(format)}. ")) +
                     "Grades per scan: Rmin <= 0.5 Rmax A, else F; SC A >= 70, B >= 55, C >= 40, D >= 20 %; ECmin >= 15 % A, else F; MOD A >= 0.70, B >= 0.60, C >= 0.50, D >= 0.40; " +
                     "defects A <= 0.15, B <= 0.20, C <= 0.25, D <= 0.30; decodability A >= 0.62, B >= 0.50, C >= 0.37, D >= 0.25. A scan's grade is its lowest parameter; " +
                     (stacked ? "a row's grade the mean of its scan grades, the symbol's the lowest row's (ISO/IEC 24724)." : "the symbol's the mean of the scan grades.") +
                     (es is null ? " Without the engine's scans the grade leaves out decodability and decode, so it is at most the letter shown." : ""),
                     R - L, 6.5f).ToList();
        y = doc.Ensure(y, notes.Count * 8 + 4);
        foreach (var line in notes)
        {
            pdf.Text(L, y, line, 6.5f);
            y += 8;
        }
        return y + 8;
    }

    // the ten scan reflectance profiles, two by two, each with its grade, the threshold and its quiet zones
    private static float ScanProfiles(Doc doc, MiniPdf pdf, float y, LinearAnalysis.Symbol lin, IReadOnlyList<EngineScan>? es, CodeFormat format)
    {
        var rows = Rows(lin, es, format);
        var (mL, mR) = LinearAnalysis.MinQuietZone(format);
        const float h = 50, rowH = h + 24, gap = 12;
        float cw = (R - L - gap) / 2;
        y = doc.Ensure(y, 30 + rowH);
        y = Section(pdf, y, "Scan reflectance profiles (0.8 X aperture, grey levels from the image; top of the bars first)");
        for (int i = 0; i < lin.Scans.Count; i++)
        {
            if (i % 2 == 0)
            {
                if (i > 0) y += rowH;
                float before = y;
                y = doc.Ensure(y, rowH);
                if (y < before) y += 4;
            }
            var s = lin.Scans[i];
            var row = rows.FirstOrDefault(r => r.N == i + 1);
            float x0 = L + (i % 2) * (cw + gap), top = y + 4;
            pdf.Text(x0, y, F($"Scan {i + 1} ({10 + 80f * i / 9:0} %)"), 7.5f, bold: true);
            if (row is not null)
            {
                var (gr, gg, gb) = SymbolAnalysis.GradeColour(row.Grade);
                pdf.BoxRgb(x0 + cw - 44, y - 8, 44, 10.5f, gr, gg, gb);
                pdf.Text(x0 + cw - 41, y, (row.AtMost ? "at most " : "grade ") + CodeQuality.Letter(row.Grade), 7, bold: true);
                var worst = row.Grades.Where(g => g.Grade >= 0).MinBy(g => g.Grade);
                if (worst.Grade < 4) pdf.Text(x0 + 70, y, Clip($"lowest: {worst.Name} ({CodeQuality.Letter(worst.Grade)})", cw - 120, 6.5f), 6.5f);
            }
            pdf.Box(x0, top, cw, h, 0.96f);
            if (s.Edges >= 4)
            {
                // the symbol with its quiet zones and 3 X more each side
                int a = Math.Max(0, (int)(s.First - (mL + 3) * s.SamplesPerX)), b = Math.Min(s.Profile.Length, (int)Math.Ceiling(s.Last + (mR + 3) * s.SamplesPerX));
                float span = Math.Max(1, b - a);
                float X(float sample) => x0 + cw * (sample - a) / span;
                float Y(float v) => top + h - h * Math.Clamp(v / 100f, 0, 1);
                // the quiet zones the type needs, along the bottom: green when clear, red when short
                var (okR, okG, okB) = SymbolAnalysis.GradeColour(4);
                var (bdR, bdG, bdB) = SymbolAnalysis.GradeColour(0);
                float qa = Math.Max(a, s.First - mL * s.SamplesPerX), qb = Math.Min(b, s.Last + mR * s.SamplesPerX);
                bool lOk = s.QuietLeftX >= mL, rOk = s.QuietRightX >= mR;
                pdf.BoxRgb(X(qa), top + h - 3, X(s.First) - X(qa), 3, lOk ? okR : bdR, lOk ? okG : bdG, lOk ? okB : bdB);
                pdf.BoxRgb(X(s.Last), top + h - 3, X(qb) - X(s.Last), 3, rOk ? okR : bdR, rOk ? okG : bdG, rOk ? okB : bdB);
                // the profile: per point of width its lowest and highest sample, so no narrow element is lost
                var pts = new List<(float, float)>();
                int per = Math.Max(1, (int)(span / (cw * 2)));
                for (int k = a; k < b; k += per)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int j = k; j < Math.Min(b, k + per); j++) { lo = Math.Min(lo, s.Profile[j]); hi = Math.Max(hi, s.Profile[j]); }
                    bool rising = k + per < b && s.Profile[Math.Min(b - 1, k + per)] > s.Profile[k];
                    pts.Add((X(k), Y(rising ? lo : hi)));
                    if (per > 1) pts.Add((X(k + per * 0.5f), Y(rising ? hi : lo)));
                }
                pdf.Polyline(new[] { (x0, Y(s.RMax)), (x0 + cw, Y(s.RMax)) }, 0.3f, 0.6f, 0.6f, 0.6f);
                pdf.Polyline(new[] { (x0, Y(s.RMin)), (x0 + cw, Y(s.RMin)) }, 0.3f, 0.6f, 0.6f, 0.6f);
                pdf.Polyline(pts, 0.5f, 0.1f, 0.2f, 0.6f);
                pdf.Polyline(new[] { (x0, Y(s.Threshold)), (x0 + cw, Y(s.Threshold)) }, 0.4f, 1f, 0.55f, 0.1f);
                pdf.Text(x0, top + h + 8, F($"Rmin {s.RMin:0}  Rmax {s.RMax:0}  GT {s.Threshold:0}  ECmin {s.ECmin:0}  MOD {s.Modulation:0.00}  QZ {s.QuietLeftX:0.0} / {s.QuietRightX:0.0} X"), 6f);
            }
            else pdf.Text(x0 + 4, top + h / 2, "no symbol found on this scan line", 7);
        }
        y += rowH;
        pdf.Text(L, y, "Blue: profile; orange: global threshold GT; grey: Rmin and Rmax; bar along the bottom: the type's minimum quiet zones (green clear, red short).", 6.5f);
        return y + 16;
    }

    private static float Section(MiniPdf pdf, float y, string title)
    {
        pdf.Box(L, y - 10, R - L, 14, 0.15f);
        pdf.TextWhite(L + 4, y, title, 9);
        return y + 16;
    }

    private static float TwoColumns(MiniPdf pdf, float y, List<(string, string)> left, List<(string, string)> right, float keyW = 70)
    {
        float y0 = y, half = (R - L) / 2;
        foreach (var (k, v) in left) { pdf.Text(L, y, k, 8, bold: true); pdf.Text(L + keyW, y, Clip(v, half - keyW - 6, 8), 8); y += 12; }
        float yr = y0;
        foreach (var (k, v) in right) { pdf.Text(L + half, yr, k, 8, bold: true); pdf.Text(L + half + keyW, yr, Clip(v, half - keyW, 8), 8); yr += 12; }
        return Math.Max(y, yr);
    }

    private static float GradeRow(MiniPdf pdf, float y, (string Key, string Value)[] cols, int overall)
    {
        float w = (R - L) / cols.Length;
        pdf.Box(L, y - 10, R - L, 14, 0.15f);
        for (int i = 0; i < cols.Length; i++) pdf.TextWhite(L + i * w + 4, y, cols[i].Key, 8);
        y += 16;
        var (r, g, b) = SymbolAnalysis.GradeColour(overall);
        pdf.BoxRgb(L, y - 11, w, 16, r, g, b);
        for (int i = 0; i < cols.Length; i++) pdf.Text(L + i * w + 4, y, Clip(cols[i].Value, w - 6, 8), 8, bold: i == 0 || i == cols.Length - 1);
        pdf.Line(L, y + 6, R, y + 6, 0.3f, 0.5f);
        return y + 16;
    }

    private static float ResultBar(MiniPdf pdf, float y, string what, string criterion, bool pass)
    {
        var (r, g, b) = SymbolAnalysis.GradeColour(pass ? 4 : 0);
        pdf.BoxRgb(L, y - 10, R - L, 15, r, g, b);
        pdf.TextWhite(L + 4, y, $"{what}: {(pass ? "PASS" : "FAIL")}", 9, bold: true);
        pdf.TextWhite(L + 230, y, criterion, 8);
        return y + 19;
    }

    // the modulation table: each cell's value, coloured by grade (light cells pale, dark cells deep, as the
    // verifier's legend); a fixed-pattern cell on the wrong side shows 0 (dark red: should be dark; pink: should be light)
    private static float ModulationTable(MiniPdf pdf, float y, SymbolAnalysis.CellMap m, List<SymbolAnalysis.Segment> segs, float cell,
                                         Func<int, int, float> outside, bool ring = true)
    {
        // rows and columns -1 and n are the quiet zone ring (expected light), as the verifier's table shows them
        var wrong = new HashSet<(int, int)>(segs.SelectMany(sg => sg.WrongCells));
        float x0 = L + 18, y0 = y + 2;
        float fs = Math.Max(3.5f, cell * 0.42f);
        int every = m.Cols > 30 ? 5 : 1;
        for (int k = -1; k <= m.Cols; k++)
            if ((k == -1 || k == m.Cols) && !ring) continue;
            else if (k == -1 || k == m.Cols || k % every == 0) pdf.Text(x0 + (k + 1) * cell + 1, y0, k.ToString(CultureInfo.InvariantCulture), Math.Min(6, fs));
        y0 += 4;
        for (int r = -1; r <= m.Rows; r++)
        {
            int row = r + 1;
            if ((r == -1 || r == m.Rows) && !ring) continue;
            if (r == -1 || r == m.Rows || r % every == 0) pdf.Text(L, y0 + row * cell + cell * 0.7f, r.ToString(CultureInfo.InvariantCulture), Math.Min(6, fs));
            for (int k = -1; k <= m.Cols; k++)
            {
                bool inRing = r < 0 || k < 0 || r >= m.Rows || k >= m.Cols;
                if (inRing && !ring) continue;
                float v = inRing ? outside(r, k) : m.Reflectance[r * m.Cols + k];
                float mod = m.Contrast > 0 ? Math.Clamp(2 * Math.Abs(v - m.Threshold) / m.Contrast, 0f, 1f) : 0f;
                bool dark = v < m.Threshold;
                bool bad = wrong.Contains((r, k)) || inRing && dark; // the ring should be light
                int g = bad ? 0 : SymbolAnalysis.ModulationGrade(mod);
                var (cr, cg, cb) = CellColour(g, bad ? !dark : dark);
                float cx = x0 + (k + 1) * cell, cy = y0 + row * cell;
                pdf.BoxRgb(cx, cy, cell - 0.6f, cell - 0.6f, cr, cg, cb);
                if (cell >= 7)
                {
                    string t = bad ? "0" : Math.Min(99, (int)Math.Round(mod * 100)).ToString(CultureInfo.InvariantCulture);
                    float tx = cx + (cell - MiniPdf.TextWidth(t, fs)) / 2, ty = cy + cell * 0.68f;
                    if (dark != bad) pdf.TextWhite(tx, ty, t, fs); else pdf.Text(tx, ty, t, fs);
                }
            }
        }
        // legend: A 50-100 ... F 0-19, light and dark
        float lx = x0 + (m.Cols + 2) * cell + 14, ly = y0 + 8;
        pdf.Text(lx + 46, ly - 2, "Light", 6);
        pdf.Text(lx + 66, ly - 2, "Dark", 6);
        foreach (var (g, label) in new[] { (4, "A 50-100"), (3, "B 40-49"), (2, "C 30-39"), (1, "D 20-29"), (0, "F 0-19") })
        {
            ly += 12;
            pdf.Text(lx, ly, label, 6.5f);
            var (r1, g1, b1) = CellColour(g, false);
            var (r2, g2, b2) = CellColour(g, true);
            pdf.BoxRgb(lx + 46, ly - 8, 16, 10, r1, g1, b1);
            pdf.BoxRgb(lx + 66, ly - 8, 16, 10, r2, g2, b2);
        }
        pdf.Text(lx, ly + 16, "0 = wrong side of", 6);
        pdf.Text(lx, ly + 23, "the threshold", 6);
        return y0 + (m.Rows + 2) * cell + 6;
    }

    /// <summary>The usual causes of a low grade and what to check, by parameter (the report's own guidance).</summary>
    public static string? Advice(string parameter, bool linear) => parameter switch
    {
        "Symbol contrast" => "Light and dark are too close. Use darker ink or a lighter, less glossy label; check the lighting angle and exposure.",
        "Modulation" or "Reflectance margin" => "Some cells or bars sit close to the threshold. Usually print growth or loss (ink amount, heat, laser power or speed), uneven print, or show-through of the material. The modulation table shows where.",
        "Fixed pattern damage" => "The finder L, clock tracks or quiet zone are damaged or touched. Look for missing dots or a worn needle, laser or print head, and keep the quiet zone clear of print and edges.",
        "Axial non-uniformity" => "Cells are stretched along one axis. Check the line speed against the printing or marking speed and the artwork scale.",
        "Grid non-uniformity" => "Cells are off their ideal positions. Look for vibration or slipping, a curved or tilted surface, uneven print speed, or poorly rounded artwork pixels.",
        "Unused error correction" => "Error correction was needed to read the code. Find the cells read on the wrong side (0 in the modulation table) and remove their cause.",
        "Edge contrast" => "A bar and space pair is too close in reflectance. Darker bars, lighter spaces or a wider X-dimension help; use the aperture the application standard asks for.",
        "Defects" => "Spots in spaces or voids in bars. Clean the print head, check the ink or ribbon and the heat, and the match of media and printer.",
        "Decodability" => "Bar and space widths are off their nominal sizes. Check print growth, and that the X-dimension is a whole number of the printer's dots.",
        "Decode" => linear ? "Some scan lines did not read. Check for missing or broken bars, the quiet zones, focus and the aperture."
                           : "The reference decoder had trouble. Check the aperture, focus, the code type, missing finder parts and blocked nozzles.",
        "Print growth" => "Cells or bars print noticeably larger or smaller than their nominal size. Adjust ink amount, heat, laser power or the artwork's bar width reduction.",
        "Codeword yield" => "Codewords along the rows did not read as valid ones. Look for damaged or missing bars, rows printed too short, smearing between rows and a skewed or curved label.",
        "Rmin" => "The darkest bar is not dark enough against the light background (Rmin above half of Rmax). Use darker ink, or check for glare on glossy bars and the light's angle.",
        "Quiet zone" when linear => "Print, an edge or the label border is closer to the first or last bar than the type's minimum quiet zone. Move the code or the artwork; the scan profiles show the marks beside the bars.",
        _ => null,
    };

    /// <summary>The usual 1D aperture for an X-dimension in mil (the application standard decides first): 3, 5, 10 or 20 mil.</summary>
    public static int? RecommendedAperture1D(double xMil) => xMil < 4 ? null : xMil <= 7 ? 3 : xMil <= 13 ? 5 : xMil <= 25 ? 10 : 20;

    /// <summary>The verifier legend's colours: per grade a pale tone for light cells and a deep one for dark cells.</summary>
    public static (float R, float G, float B) CellColour(int grade, bool dark) => (grade, dark) switch
    {
        (4, false) => (0.62f, 0.87f, 0.62f), (4, true) => (0.18f, 0.48f, 0.25f),
        (3, false) => (0.78f, 0.93f, 0.60f), (3, true) => (0.30f, 0.62f, 0.12f),
        (2, false) => (1.00f, 0.98f, 0.75f), (2, true) => (0.70f, 0.70f, 0.05f),
        (1, false) => (1.00f, 0.82f, 0.85f), (1, true) => (0.75f, 0.20f, 0.30f),
        (_, false) => (0.95f, 0.55f, 0.55f), (_, true) => (0.62f, 0.06f, 0.10f),
    };

    private static float ZoneHeightPx(PointF[] c)
    {
        if (c.Length != 4) return 0;
        float D(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        float s01 = D(c[0], c[1]) + D(c[3], c[2]), s03 = D(c[0], c[3]) + D(c[1], c[2]);
        return Math.Min(s01, s03) / 2;
    }

    private static string Dist(double mm, ReportUnits u) => u switch
    {
        ReportUnits.Mm => F($"{mm:0.000} mm"),
        ReportUnits.Um => F($"{mm * 1000:0} µm"),
        _ => F($"{mm / 0.0254:0.0} mil"),
    };

    /// <summary>
    /// The verifier's formal grade: grade / aperture in mil / wavelength nm / light angle, e.g. "3.0/08/660/45".
    /// Null when not reportable: without a reflectance calibration and a mm scale there is no ISO aperture.
    /// </summary>
    public static string? FormalGrade(CodeQuality q, GradeReportInfo info, bool linear, double? scanMean = null)
    {
        // a formal grade needs an ISO aperture (reflectance calibration and a mm scale), the recorded wavelength
        // and angle, and the true grade value (1D: the mean of the engine's scan grades)
        if (linear && scanMean is null || !q.Calibrated || q.XDimMm <= 0 || info.WavelengthNm is not { } nm || info.LightingCode is not { } ang) return null;
        double apMil = Math.Round(0.8 * q.XDimMm / 0.0254, MidpointRounding.AwayFromZero);
        double value = linear ? Math.Floor(scanMean!.Value * 10) / 10 : q.OverallGrade; // a verifier truncates the mean to one decimal
        return F($"{value:0.0}/{apMil:00}/{nm}/{ang}");
    }




    // a histogram of reflectance 0..100 % (dark left); with bands, the bar beneath it as a verifier draws it:
    // the global threshold and the modulation grade zones A, B, C, D, F either side of it
    private static void Histogram(MiniPdf pdf, ref float y, string title, int[] counts, float? threshold, float? contrast, bool bands)
    {
        const float h = 56;
        float w = R - L;
        y = Section(pdf, y, title);
        pdf.Box(L, y - 4, w, h + 4, 0.1f);
        int max = Math.Max(1, counts.Max());
        float bw = w / counts.Length;
        var (gr, gg, gb) = bands ? SymbolAnalysis.GradeColour(4) : (0.95f, 0.95f, 0.95f);
        for (int i = 0; i < counts.Length; i++)
        {
            float bh = (h - 4) * counts[i] / max;
            if (bh > 0) pdf.BoxRgb(L + i * bw + 0.5f, y + h - bh, Math.Max(0.5f, bw - 1), bh, gr, gg, gb);
        }
        y += h + 2;
        if (threshold is { } gt && contrast is { } sc && sc > 0)
        {
            float X(float v) => L + w * Math.Clamp(v / 100f, 0, 1);
            if (bands)
            {
                // zone edges at 2|R - GT| / SC = 0.5, 0.4, 0.3, 0.2 either side of the threshold
                var edges = new[] { (0f, 0.2f, 0), (0.2f, 0.3f, 1), (0.3f, 0.4f, 2), (0.4f, 0.5f, 3), (0.5f, 10f, 4) };
                foreach (var side in new[] { -1, 1 })
                    foreach (var (a, b, g) in edges)
                    {
                        float v0 = gt + side * a * sc / 2, v1 = gt + side * b * sc / 2;
                        float x0 = X(Math.Min(v0, v1)), x1 = X(Math.Max(v0, v1));
                        if (x1 - x0 < 0.5f) continue;
                        var (r, gg2, b2) = SymbolAnalysis.GradeColour(g);
                        pdf.BoxRgb(x0, y, x1 - x0, 11, r, gg2, b2);
                        string letter = CodeQuality.Letter(g);
                        if (x1 - x0 > 8) pdf.Text((x0 + x1) / 2 - 2, y + 8.5f, letter, 6.5f, bold: true);
                    }
                y += 13;
            }
            pdf.BoxRgb(X(gt) - 0.8f, y - (bands ? 13 : 0) - h - 2, 1.6f, h + 2 + (bands ? 11 : 0), 1f, 0.55f, 0.1f);
            pdf.Text(X(gt) - 14, y + 8, "Threshold", 6.5f);
        }
        pdf.Text(L, y + 8, "0 % (dark)", 6.5f);
        pdf.Text(R - 42, y + 8, "100 % (light)", 6.5f);
        y += 22;
    }

    private static void Graph(MiniPdf pdf, ref float y, string title, float[] values, float rmin, float rmax)
    {
        if (values.Length < 2 || y > MiniPdf.PageHeight - 160) return;
        const float h = 90;
        float w = R - L;
        pdf.Text(L, y, title, 9, bold: true);
        y += 8;
        pdf.Box(L, y, w, h, 0.96f);
        float top = y;
        float Y(float v) => top + h - h * Math.Clamp(v / 100f, 0, 1);
        var pts = values.Select((v, i) => (L + w * i / (values.Length - 1), Y(v))).ToList();
        pdf.Polyline(pts, 0.6f, 0.1f, 0.2f, 0.6f);
        float gt = 0.5f * (rmin + rmax);
        pdf.Polyline(new[] { (L, Y(gt)), (R, Y(gt)) }, 0.5f, 0.85f, 0.15f, 0.15f);
        pdf.Text(L + 2, Y(gt) - 2, "GT", 7);
        y += h + 22;
    }


    private static string Clip(string s, float width, float size)
    {
        if (MiniPdf.TextWidth(s, size) <= width) return s;
        while (s.Length > 1 && MiniPdf.TextWidth(s + "...", size) > width) s = s[..^1];
        return s + "...";
    }

    /// <summary>The code's region with a margin of half its size, at most maxSide pixels on its longer side.</summary>
    public static (byte[] Pixels, int Width, int Height) Crop(GrayImage image, PointF[] corners, int maxSide = 600)
    {
        var (px, w, h, _, _, _) = CropAt(image, corners, maxSide);
        return (px, w, h);
    }

    /// <summary>As Crop, with where it was cut from: the crop's pixel (x, y) is image pixel (X0 + x * Step, Y0 + y * Step).</summary>
    public static (byte[] Pixels, int Width, int Height, int X0, int Y0, int Step) CropAt(GrayImage image, PointF[] corners, int maxSide = 600, float margin = 0.25f)
    {
        if (corners.Length == 0) return (Array.Empty<byte>(), 0, 0, 0, 0, 1);
        float x0 = corners.Min(p => p.X), x1 = corners.Max(p => p.X), y0 = corners.Min(p => p.Y), y1 = corners.Max(p => p.Y);
        float mx = margin * (x1 - x0) + 8, my = margin * (y1 - y0) + 8;
        int ax = Math.Clamp((int)(x0 - mx), 0, image.Width), bx = Math.Clamp((int)Math.Ceiling(x1 + mx), 0, image.Width);
        int ay = Math.Clamp((int)(y0 - my), 0, image.Height), by = Math.Clamp((int)Math.Ceiling(y1 + my), 0, image.Height);
        int w = bx - ax, h = by - ay;
        if (w <= 0 || h <= 0) return (Array.Empty<byte>(), 0, 0, 0, 0, 1);
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)maxSide));
        int ow = (w + step - 1) / step, oh = (h + step - 1) / step;
        var outPx = new byte[ow * oh];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                // the mean of each step x step block (a smaller copy keeps the print's look)
                int sum = 0, n = 0;
                for (int dy = 0; dy < step && ay + y * step + dy < by; dy++)
                    for (int dx = 0; dx < step && ax + x * step + dx < bx; dx++, n++)
                        sum += image.Pixels[(ay + y * step + dy) * image.Stride + ax + x * step + dx];
                outPx[y * ow + x] = (byte)(sum / Math.Max(1, n));
            }
        return (outPx, ow, oh, ax, ay, step);
    }

    /// <summary>
    /// A code's text for print: GS1 separators as &lt;GS&gt;, other control characters by their code, and every
    /// character the report's font cannot show (beyond Latin-1) as &lt;U+XXXX&gt;, so the report never misstates
    /// the decoded data.
    /// </summary>
    public static string Printable(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            if (v == 0x1D) sb.Append("<GS>");
            else if (v < 0x20 || v is >= 0x7F and < 0xA0) sb.Append($"<{v:X2}>");
            else if (v > 0xFF) sb.Append($"<U+{v:X4}>");
            else sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    private static string ValueOf(string parameter, CodeQuality q) => parameter switch
    {
        "Symbol contrast" => F($"{q.SymbolContrast:0} %"),
        "Modulation" => F($"{q.Modulation:0.00}"),
        "Edge contrast" when q.Linear is { } l => F($"{l.EdgeContrastMin:0} %"),
        "Defects" when q.Linear is { } l => F($"{l.Defects:0.00}"),
        "Decodability" when q.Linear is { } l => l.Decodability >= 0 ? F($"{l.Decodability:0.00}") : "not measured",
        "Decode" when q.Linear is { } l => $"{l.DecodedScans} of {l.Scans} scans",
        "Grid non-uniformity" when q.Values.TryGetValue(parameter, out var v) && v >= 0 => F($"{v:0.00} module"),
        _ when q.Values.TryGetValue(parameter, out var v) && v >= 0 => F($"{v:0.00}"),
        _ => "-",
    };

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>Prose in lines of at most `width` points, broken between words.</summary>
    public static IEnumerable<string> WrapWords(string s, float width, float size)
    {
        var line = new StringBuilder();
        foreach (var word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && MiniPdf.TextWidth(next, size) > width)
            {
                yield return line.ToString();
                line.Clear().Append(word);
            }
            else line.Clear().Append(next);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>Lines of at most `width` points at this size (cut between characters).</summary>
    public static IEnumerable<string> WrapToWidth(string s, float width, float size)
    {
        if (s.Length == 0) { yield return "-"; yield break; }
        int start = 0;
        while (start < s.Length)
        {
            int end = start + 1;
            while (end < s.Length && MiniPdf.TextWidth(s[start..(end + 1)], size) <= width) end++;
            yield return s[start..end];
            start = end;
        }
    }
}
