using VisionCore;
using Xunit;

// Month names in other languages and formats given to the OCR engine (format-constrained reading).
public class TextFormatDecodingTests
{
    private static OcrLine Line(string text, int format = -1, bool pass = true, params OcrChar[] chars) =>
        new(text, "", format >= 0, pass, pass ? 90 : 30, chars, new VisionCore.PointF[4], 0, 0, 20) { Format = format };

    private static OcrChar Ch(string c, float score, CharStatus status = CharStatus.Ok) =>
        new(c, c, score, status, new VisionCore.PointF[4]);

    [Theory]
    [InlineData("en", "EXP SEPT 28", 9)]
    [InlineData("es", "EXP ENE 28", 1)]
    [InlineData("es", "EXP DIC 28", 12)]
    [InlineData("fr", "EXP FÉVR 28", 2)]
    [InlineData("fr", "EXP fevr 28", 2)]   // accents dropped, either case
    [InlineData("fr", "EXP AOÛT 28", 8)]
    [InlineData("de", "EXP MRZ 28", 3)]
    [InlineData("de", "EXP OKT 28", 10)]
    [InlineData("pt", "EXP OUT 28", 10)]
    [InlineData("it", "EXP GIU 28", 6)]
    public void MonthNamesInOtherLanguages(string lang, string text, int month)
    {
        var r = TextFormat.Parse("EXP {EXP:MMM YY}", new[] { lang }).Check(text);
        Assert.True(r.Ok, r.Problem);
        Assert.Equal(new DateOnly(2028, month, 1), r.Dates["EXP"].Date);
    }

    [Fact]
    public void OnlyTheToolsLanguagesCount()
    {
        Assert.False(TextFormat.Parse("{EXP:MMM YY}").Check("MAI 28").Ok);              // English only by default
        Assert.True(TextFormat.Parse("{EXP:MMM YY}", new[] { "en", "de" }).Check("MAI 28").Ok);
        Assert.True(TextFormat.Parse("{EXP:MMM YY}", new[] { "en", "de" }).Check("MAY 28").Ok);
        Assert.False(TextFormat.Parse("{EXP:MMM YY}").Check("XYZ 28").Ok);
        Assert.Throws<FormatException>(() => TextFormat.Parse("{EXP:MMM YY}", new[] { "xx" }));
        var cfg = new TextToolConfig { Mode = TextMode.Read, ReportOnly = true, MonthLanguages = new() { "xx" } };
        Assert.Contains("No month names", TextTool.SetupProblem(cfg));
    }

    [Fact]
    public void NoSpellingMeansTwoMonths()
    {
        // a spelling shared by languages (MAR, AGO) must name the same month in each
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, months) in TextFormat.MonthNames)
        {
            Assert.Equal(12, months.Length);
            for (int m = 0; m < 12; m++)
                foreach (var n in months[m])
                {
                    Assert.True(!seen.TryGetValue(n, out var other) || other == m + 1, $"{n} ({lang}) is month {m + 1} and {other}");
                    seen.TryAdd(n, m + 1);
                }
        }
    }

    [Fact]
    public void ProposesMonthNamesOfTheLanguages()
    {
        Assert.Equal("EXP {EXP:MMM YYYY}", TextFormat.Propose("EXP FÉVR 2028", null, new[] { "fr" }));
        Assert.Equal("EXP {EXP:MMM YY}", TextFormat.Propose("EXP SEPT 28"));
    }

    [Theory]
    [InlineData("LOT {A:1}{D:5}", "LOT {A:1}{D:5}")]
    [InlineData("B.NO {X:6}", "B.NO {X:6}")]
    [InlineData("{D:4-6} {?}", "{D:4-6} {?}")]
    [InlineData("\\{{D:2}\\}", "\\{{D:2}}")]
    [InlineData("A|B", "A\\|B")]
    public void EngineFormatKeepsTheLayout(string format, string engine) =>
        Assert.Equal(engine, TextFormat.Parse(format).EngineFormat);

    [Fact]
    public void EngineFormatLimitsDatesAndLeavesExpressionsToTheCheck()
    {
        var e = TextFormat.Parse("EXP {EXP:DD/MM/YY}").EngineFormat!;
        Assert.StartsWith("EXP {|01|02|03|", e);
        Assert.Contains("|31}/{|01|", e);
        Assert.EndsWith("|12}/{D:2}", e);
        var mmm = TextFormat.Parse("{EXP:MMM YYYY}", new[] { "en", "fr" }).EngineFormat!;
        Assert.Contains("SEPT", mmm);
        Assert.Contains("FÉVR", mmm);
        Assert.EndsWith("} {D:4}", mmm);
        Assert.Null(TextFormat.Parse("{re:[A-Z]{2}\\d+}").EngineFormat);
        Assert.Null(TextFormat.Parse("{D:65}").EngineFormat);                         // longer than the engine reads
    }

    [Fact]
    public void FormatsReadByTheEngineTakeTheEnginesLine()
    {
        var formats = new[] { TextFormat.Parse("LOT {A:1}{D:5}"), TextFormat.Parse("EXP {EXP:MM/YY}") };
        var now = new DateTime(2026, 10, 29);
        var rules = new DateRules();
        var engine = new HashSet<int> { 0, 1 };
        var ok = TextTool.CheckFormats(new[] { Line("MADE IN INDIA"), Line("LOT A12345", 0), Line("EXP 10/28", 1) }, formats, rules, 60, now, engine);
        Assert.Null(ok.Problem);
        Assert.Contains("EXP 10/2028", ok.Fields);

        // the engine's weak character: unclear, named by place
        var weak = Line("LOT A10345", 0, false, Ch("L", 99), Ch("O", 99), Ch("T", 99), Ch("A", 99), Ch("1", 99), Ch("0", 40, CharStatus.Wrong),
                        Ch("3", 99), Ch("4", 99), Ch("5", 99));
        var unclear = TextTool.CheckFormats(new[] { weak, Line("EXP 10/28", 1) }, formats, rules, 60, now, engine);
        Assert.Equal("explain.ocr.unsure", unclear.Explain);
        Assert.Contains("Character 6", unclear.Problem);

        var extra = Line("LOT A12345", 0, false, Ch("A", 99), Ch("6", 95, CharStatus.Extra));
        Assert.Equal("explain.format.mismatch", TextTool.CheckFormats(new[] { extra, Line("EXP 10/28", 1) }, formats, rules, 60, now, engine).Explain);

        // the engine's line cut short (the text may go on past it): said so, whatever it reads
        var cut = Line("LOT A12345", 0, false, Ch("A", 99), Ch("6", 95, CharStatus.Extra)) with { CutShort = true };
        var cutRes = TextTool.CheckFormats(new[] { cut, Line("EXP 10/28", 1) }, formats, rules, 60, now, engine);
        Assert.Equal("explain.ocr.cut_short", cutRes.Explain);
        Assert.Contains("may go on past \"LOT A12345\"", cutRes.Problem);
        // a free-read line cut short that fits the format: still not passed
        var freeCut = new OcrLine("LOT A12345", "", false, false, 59, Array.Empty<OcrChar>(), new VisionCore.PointF[4], 0, 0, 20) { CutShort = true };
        Assert.Equal("explain.ocr.cut_short", TextTool.CheckFormats(new[] { freeCut }, new[] { formats[0] }, rules, 60, now).Explain);

        // the engine found no line for the expiry: a free-read line that would fit is not taken instead
        var missing = TextTool.CheckFormats(new[] { Line("LOT A12345", 0), Line("EXP 10/28"), Line("", 1, false) }, formats, rules, 60, now, engine);
        Assert.Equal("explain.format.mismatch", missing.Explain);

        // a real date is still checked on the engine's line
        var notReal = TextTool.CheckFormats(new[] { Line("LOT A12345", 0), Line("EXP 13/28", 1) }, formats, rules, 60, now, engine);
        Assert.Equal("explain.format.date_invalid", notReal.Explain);

        // a format the engine did not take ({re:}) is matched on the lines the engine left
        var mixed = new[] { TextFormat.Parse("LOT {A:1}{D:5}"), TextFormat.Parse("{re:[A-Z]{2}\\d+}") };
        Assert.Null(TextTool.CheckFormats(new[] { Line("LOT A12345", 0), Line("AB123") }, mixed, rules, 60, now, new HashSet<int> { 0 }).Problem);
        Assert.NotNull(TextTool.CheckFormats(new[] { Line("AB123", 0, false) }, mixed, rules, 60, now, new HashSet<int> { 0 }).Problem);
    }
}
