using VisionCore;
using Xunit;

public class TextFormatTests
{
    private static OcrLine Line(string text, float quality = 90) =>
        new(text, "", false, false, quality, Array.Empty<OcrChar>(), new VisionCore.PointF[4], 0, 0, 20);

    [Theory]
    [InlineData("LOT {A:1}{D:5}", "LOT A12345", true)]
    [InlineData("LOT {A:1}{D:5}", "lot a12345", true)]       // either case
    [InlineData("LOT {A:1}{D:5}", "LOTA12345", true)]        // spacing varies
    [InlineData("LOT {A:1}{D:5}", "LOT A1234", false)]       // one digit short
    [InlineData("LOT {A:1}{D:5}", "LOT A12345X", false)]     // extra print
    [InlineData("LOT {A:1}{D:5}", "LOT 012345", false)]      // a 0 where a letter belongs: never corrected
    [InlineData("B.NO {X:6}", "B.NO K7Q2Z9", true)]
    [InlineData("{D:4-6}", "1234567", false)]
    [InlineData("{re:[A-Z]{2}\\d+}", "AB123", true)]
    [InlineData("\\{{D:2}\\}", "{12}", true)]
    public void LayoutMatches(string format, string text, bool ok) =>
        Assert.Equal(ok, TextFormat.Parse(format).Check(text).Ok);

    [Fact]
    public void DatesMustBeReal()
    {
        var f = TextFormat.Parse("MFD {MFD:DD/MM/YY} EXP {EXP:MMM YY}");
        var good = f.Check("MFD 29/10/26 EXP OCT 28");
        Assert.True(good.Ok);
        Assert.Equal(new DateOnly(2026, 10, 29), good.Dates["MFD"].Date);
        Assert.Equal((new DateOnly(2028, 10, 1), false), good.Dates["EXP"]);
        var bad = f.Check("MFD 31/02/26 EXP OCT 28");
        Assert.False(bad.Ok);
        Assert.Equal("explain.format.date_invalid", bad.Explain);
        Assert.False(f.Check("MFD 29/10/26 EXP OCX 28").Ok);
        Assert.False(TextFormat.Parse("{EXP:MM/YY}").Check("13/27").Ok);
        Assert.True(TextFormat.Parse("{EXP:MM/YYYY}").Check("02/2028").Ok);
    }

    [Theory]
    [InlineData("{D:0}")]
    [InlineData("{D:6-4}")]
    [InlineData("{MFD:DD/MM}")]          // no year
    [InlineData("{MFD:DD/MM/YY/YY}")]    // a part twice
    [InlineData("{BATCH}")]              // a check-mode field, not a format field
    [InlineData("{re:[}")]
    [InlineData("{re:(?x)#}")]          // valid alone, breaks the whole pattern
    [InlineData("LOT {D:5")]
    public void BadFormatsAreRefused(string format) =>
        Assert.Throws<FormatException>(() => TextFormat.Parse(format));

    [Theory]
    [InlineData("LOT A12345", "LOT {X:6}")]
    [InlineData("MFD 29/10/26", "MFD {MFD:DD/MM/YY}")]
    [InlineData("MFD 29.10.2026 EXP 10/28", "MFD {MFD:DD.MM.YYYY} EXP {EXP:MM/YY}")]
    [InlineData("USE BY OCT 2028", "USE BY {EXP:MMM YYYY}")]
    [InlineData("MFD29/10/26", "MFD{MFD:DD/MM/YY}")]
    [InlineData("MRP 12.50", "MRP {D:2}.{D:2}")]
    [InlineData("B.NO 2611", "B.NO {D:4}")]
    public void ProposalFromATaughtLine(string read, string proposed)
    {
        Assert.Equal(proposed, TextFormat.Propose(read));
        Assert.True(TextFormat.Parse(proposed).Check(read).Ok);
    }

    [Fact]
    public void DateRules()
    {
        var now = new DateTime(2026, 10, 29, 10, 0, 0);
        var rules = new DateRules { ExpiryMonths = 24, MfdWithinDays = 2 };
        var f = TextFormat.Parse("{MFD:DD/MM/YY} {EXP:MM/YY}");
        Assert.Null(rules.Check(f.Check("28/10/26 10/28").Dates, now));
        Assert.Contains("24 months", rules.Check(f.Check("28/10/26 09/28").Dates, now));
        Assert.Contains("within 2 days", rules.Check(f.Check("20/10/26 10/28").Dates, now));
        Assert.Contains("before", new DateRules().Check(f.Check("28/10/27 09/27").Dates, now));
        Assert.Contains("already past", new DateRules().Check(f.Check("28/10/24 09/26").Dates, now));
        Assert.Null(new DateRules().Check(f.Check("28/10/26 10/26").Dates, now)); // a month-only expiry lasts its month
        Assert.Contains("No MFD", rules.Check(new Dictionary<string, (DateOnly, bool)>(), now));
        // a month-only production date counts anywhere in its month
        Assert.Null(new DateRules { MfdWithinDays = 3 }.Check(TextFormat.Parse("{MFD:MM/YY}").Check("10/26").Dates, now));
        Assert.NotNull(new DateRules { MfdWithinDays = 3 }.Check(TextFormat.Parse("{MFD:MM/YY}").Check("08/26").Dates, now));
    }

    [Fact]
    public void FormatsFindTheirLinesAndQualityCounts()
    {
        var formats = new[] { TextFormat.Parse("LOT {A:1}{D:5}"), TextFormat.Parse("EXP {EXP:MM/YY}") };
        var now = new DateTime(2026, 10, 29);
        var rules = new DateRules();
        // a stray line above does not shift the formats
        var ok = TextTool.CheckFormats(new[] { Line("MADE IN INDIA"), Line("LOT A12345"), Line("EXP 10/28") }, formats, rules, 60, now);
        Assert.Null(ok.Problem);
        Assert.Contains("EXP 10/2028", ok.Fields);
        var missing = TextTool.CheckFormats(new[] { Line("LOT A12345") }, formats, rules, 60, now);
        Assert.Equal("explain.format.mismatch", missing.Explain);
        var unclear = TextTool.CheckFormats(new[] { Line("LOT A12345", 40), Line("EXP 10/28") }, formats, rules, 60, now);
        Assert.Equal("explain.ocr.unsure", unclear.Explain);
        var noRule = TextTool.CheckFormats(new[] { Line("LOT A12345"), Line("EXP 10/28") }, formats, new DateRules { ExpiryMonths = 24 }, 60, now);
        Assert.Equal("explain.format.date_wrong", noRule.Explain); // no MFD to check the expiry against
    }

    [Fact]
    public void ExpiryWithDaysIsCheckedToTheDay()
    {
        var now = new DateTime(2026, 10, 29);
        var rules = new DateRules { ExpiryMonths = 24 };
        var f = TextFormat.Parse("{MFD:DD/MM/YY} {EXP:DD/MM/YY}");
        Assert.Null(rules.Check(f.Check("29/10/26 29/10/28").Dates, now));
        Assert.Null(rules.Check(f.Check("29/10/26 28/10/28").Dates, now));   // one day before: a common convention
        Assert.NotNull(rules.Check(f.Check("29/10/26 01/10/28").Dates, now));
    }

    [Fact]
    public void TaughtBlockNamesEachDateOnce()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("{MFD:DD/MM/YY}", TextFormat.Propose("29/10/26", used));
        Assert.Equal("{EXP:DD/MM/YY}", TextFormat.Propose("28/10/28", used));
        var labelled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("EXP {EXP:MM/YY}", TextFormat.Propose("EXP 10/28", labelled));
        Assert.Equal("MFD {MFD:MM/YY}", TextFormat.Propose("MFD 10/26", labelled));
    }

    [Fact]
    public void ReadToolSetupProblems()
    {
        TextToolConfig Tool(params string[] lines) => new()
        {
            Name = "T", Mode = TextMode.Read,
            Lines = lines.Select(l => new TextLineSpec { Entry = LineEntry.Format, Template = l }).ToList(),
        };
        Assert.Null(TextTool.SetupProblem(Tool("MFD {MFD:DD/MM/YY}", "EXP {EXP:MM/YY}")));
        Assert.Contains("two lines", TextTool.SetupProblem(Tool("{MFD:DD/MM/YY}", "{MFD:MM/YY}")));
        Assert.Contains("name the date", TextTool.SetupProblem(Tool("{MFG:DD/MM/YY}")));
        Assert.Contains("empty", TextTool.SetupProblem(Tool("LOT {D:5}", " ")));
        Assert.NotNull(TextTool.SetupProblem(Tool("{BATCH}")));                       // left from Check mode
        Assert.NotNull(TextTool.SetupProblem(new TextToolConfig { Mode = TextMode.Read, Dates = { ExpiryMonths = 100000 } }));
        Assert.NotNull(TextTool.SetupProblem(new TextToolConfig { Mode = TextMode.Read, Dates = { MfdWithinDays = -1 } }));
        Assert.NotNull(TextTool.SetupProblem(new TextToolConfig
            { Mode = TextMode.Check, Lines = { new TextLineSpec { Entry = LineEntry.Format, Template = "{D:5}" } } }));
    }
}
