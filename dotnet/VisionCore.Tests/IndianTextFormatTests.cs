using VisionCore;
using Xunit;

// Read text formats as Indian packs print them (study 04c, 7.4.1, 7.4.3, 7.4.4): month names, MRP and unit
// sale price under the Legal Metrology (Packaged Commodities) Rules 2011, and best-before shelf life.
public class IndianTextFormatTests
{
    private static readonly DateTime Now = new(2026, 10, 31);

    // the AI reader's charset (ml/models/ocr_charset.txt)
    private static readonly HashSet<int> AiCharset =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz .,:/-#%()+&*'₹¥".Select(c => (int)c).ToHashSet();

    private static OcrLine Line(string text) =>
        new(text, "", false, false, 90, Array.Empty<OcrChar>(), new VisionCore.PointF[4], 0, 0, 20);

    [Theory]
    [InlineData("EXP MARCH 2027", 3)]
    [InlineData("EXP SEPT 2027", 9)]
    [InlineData("EXP June 2027", 6)]
    [InlineData("EXP JUL 2027", 7)]
    [InlineData("EXP SEPTEMBER 2027", 9)]
    public void IndianEnglishMonthNames(string text, int month)
    {
        var r = TextFormat.Parse("EXP {EXP:MMM YYYY}", new[] { "en-IN" }).Check(text);
        Assert.True(r.Ok, r.Problem);
        Assert.Equal(new DateOnly(2027, month, 1), r.Dates["EXP"].Date);
    }

    [Theory]
    [InlineData("EXP MARX 2027")]      // misprint
    [InlineData("EXP 2027 MARCH")]     // order
    [InlineData("EXP MARCHE 2027")]    // a French spelling is not Indian English
    [InlineData("EXP MAR 27")]         // two-digit year where four are printed
    public void WrongIndianMonthsFail(string text) =>
        Assert.False(TextFormat.Parse("EXP {EXP:MMM YYYY}", new[] { "en-IN" }).Check(text).Ok);

    [Fact]
    public void FullMonthNamesOnlyWhenTheToolSaysIndianEnglish()
    {
        Assert.False(TextFormat.Parse("EXP {EXP:MMM YYYY}").Check("EXP MARCH 2027").Ok);
        Assert.Equal("BEST BEFORE {EXP:MMM YYYY}", TextFormat.Propose("BEST BEFORE MARCH 2027", null, new[] { "en-IN" }));
        // no Devanagari in the reader's charset yet: Hindi month names are not offered
        Assert.Throws<FormatException>(() => TextFormat.MonthSpellings(new[] { "hi" }));
        Assert.Null(TextFormat.Parse("EXP {EXP:MMM YYYY}", new[] { "en-IN" }).ReaderProblem(AiCharset, "The AI reader"));
    }

    [Theory]
    [InlineData("{MRP} (INCL. OF ALL TAXES)", "MRP ₹ 46.00 (Incl. of all taxes)", true)]
    [InlineData("{MRP} (incl. of all taxes)", "M.R.P. Rs. 46.00 (incl. of all taxes)", true)]
    [InlineData("{MRP}", "MRP: ₹159.35", true)]                          // Harpic
    [InlineData("{MRP}", "MRP : Rs 159.35", true)]
    [InlineData("{MRP}", "M.R.P INR 46.00", true)]
    [InlineData("{MRP} inclusive of all taxes", "Maximum Retail Price ₹ 46.00 inclusive of all taxes", true)]
    [InlineData("{MRP}", "MRP ₹ 46", false)]                             // paise missing
    [InlineData("{MRP}", "MRP $ 46.00", false)]                          // not rupees
    [InlineData("{MRP}", "MRP ₹ 46.O0", false)]                          // a letter O is never a 0
    [InlineData("{MRP}", "₹ 46.00", false)]                              // no MRP label
    [InlineData("{MRP}", "MRP ₹ 4600", false)]                           // the point lost
    [InlineData("{MRP}", "MRP ₹ 46.000", false)]
    [InlineData("{MRP}", "MRP ₹ 1,250.00", false)]                       // grouping only with the amount given
    [InlineData("{MRP} (INCL. OF ALL TAXES)", "MRP ₹ 46.00", false)]     // the taxes phrase missing
    [InlineData("{MRP:whole}/-", "MRP Rs.10/-", true)]
    [InlineData("{MRP:whole}/-", "MRP Rs.10.50/-", false)]
    [InlineData("{MRP:46.00}", "MRP ₹ 46.00", true)]
    [InlineData("{MRP:46.00}", "MRP ₹ 64.00", false)]                    // not the batch's price
    [InlineData("{MRP:46.00}", "MRP ₹ 46.50", false)]
    [InlineData("{MRP:46.00}", "MRP ₹ 146.00", false)]
    [InlineData("{MRP:1,250.00}", "MRP ₹ 1,250.00", true)]               // Indian grouping
    [InlineData("{MRP:12,34,567}", "MRP ₹ 12,34,567", true)]
    public void MrpLines(string format, string text, bool ok) =>
        Assert.Equal(ok, TextFormat.Parse(format).Check(text).Ok);

    [Fact]
    public void MrpAmountIsReadAndZeroIsNoPrice()
    {
        var f = TextFormat.Parse("{MRP} (Incl. of all taxes)");
        var r = f.Check("MRP: ₹159.35 (Incl. of all taxes)");
        Assert.True(r.Ok);
        Assert.Equal(159.35m, r.Amounts["MRP"]);
        var zero = f.Check("MRP: ₹0.00 (Incl. of all taxes)");
        Assert.False(zero.Ok);
        Assert.Equal("explain.format.price_invalid", zero.Explain);
    }

    [Theory]
    [InlineData("UNIT SALE PRICE {PRICE} {PER}", "Unit sale price ₹ 0.92 per g", true)]
    [InlineData("UNIT SALE PRICE {PRICE} {PER}", "Unit sale price Rs. 3.18 per 100 ml", true)]
    [InlineData("USP {PRICE}{PER}", "USP ₹0.46/ml", true)]
    [InlineData("USP {PRICE}{PER}", "USP ₹0.46/1 kg", true)]
    [InlineData("USP {PRICE}{PER}", "USP ₹0.46/gm", true)]
    [InlineData("USP {PRICE}{PER}", "USP ₹0.46/oz", false)]               // not a Legal Metrology unit
    [InlineData("USP {PRICE}{PER}", "USP ₹0.46/gx", false)]
    [InlineData("USP {PRICE}{PER}", "USP ₹0.46 ml", false)]               // per or / missing
    public void UnitSalePrice(string format, string text, bool ok) =>
        Assert.Equal(ok, TextFormat.Parse(format).Check(text).Ok);

    [Fact]
    public void TwoPricesOnOneLine()
    {
        var r = TextFormat.Parse("{MRP} USP {PRICE}{PER}").Check("MRP ₹46.00 USP ₹0.46/ml");
        Assert.True(r.Ok);
        Assert.Equal(46.00m, r.Amounts["MRP"]);
        Assert.Equal(0.46m, r.Amounts["PRICE"]);
        Assert.Equal(new[] { "PRICE", "PRICE2" }, TextFormat.Parse("{PRICE} {PRICE}").Check("₹1.00 ₹2.00").Amounts.Keys.Order());
    }

    [Theory]
    [InlineData("{MRP:0}")]
    [InlineData("{MRP:0.00}")]
    [InlineData("{MRP:46.000}")]
    [InlineData("{MRP:1,234,567}")]   // western grouping is not printed on Indian packs
    [InlineData("{MRP:abc}")]
    [InlineData("{PER:g}")]
    [InlineData("{LIFE:18}")]
    [InlineData("{LIFE} {LIFE}")]
    public void BadFieldsAreRefused(string format) =>
        Assert.Throws<FormatException>(() => TextFormat.Parse(format));

    [Fact]
    public void TheEngineReadsPricesUnderTheirFormat()
    {
        var mrp = TextFormat.Parse("{MRP} (Incl. of all taxes)").EngineFormat!;
        Assert.StartsWith("{|MRP|MRP :|M.R.P.|M.R.P. :|M.R.P|M.R.P :|MAXIMUM RETAIL PRICE|MAXIMUM RETAIL PRICE :}{|₹|RS.|RS|INR}{D:1-6}.{D:2}", mrp);
        Assert.EndsWith("(Incl. of all taxes)", mrp);
        Assert.EndsWith("{|₹|RS.|RS|INR}46.00", TextFormat.Parse("{MRP:46.00}").EngineFormat);
        Assert.EndsWith("{D:1-6}/-", TextFormat.Parse("{MRP:whole}/-").EngineFormat);
        Assert.Contains("{|PER|/}{|G|GM|", TextFormat.Parse("{PRICE}{PER}").EngineFormat);
        Assert.Equal("BEST BEFORE {D:1-3} MONTHS", TextFormat.Parse("BEST BEFORE {LIFE} MONTHS").EngineFormat);
    }

    [Fact]
    public void TheReaderMustReadOneSpellingOfEachWord()
    {
        var f = TextFormat.Parse("{MRP} USP {PRICE}{PER}");
        Assert.Null(f.ReaderProblem(AiCharset, "The AI reader"));
        // a taught font without ₹ still reads Rs.
        Assert.Null(f.ReaderProblem(AiCharset.Where(c => c != '₹').ToHashSet(), "The taught font"));
        var noRupee = "0123456789.MRP".Select(c => (int)c).ToHashSet();
        Assert.Contains("cannot read ₹ or RS. or RS or INR", TextFormat.Parse("{MRP}").ReaderProblem(noRupee, "The taught font"));
    }

    [Fact]
    public void ExpiryAfterManufactureOnIndianLabels()
    {
        var f = TextFormat.Parse("MFG. {MFD:MM/YYYY} EXP. {EXP:MM/YYYY}");
        var rules = new DateRules();
        Assert.Null(rules.Check(f.Check("Mfg. 10/2026 Exp. 09/2028").Dates, Now));
        Assert.Contains("before", rules.Check(f.Check("Mfg. 12/2026 Exp. 11/2026").Dates, Now));   // EXP before MFG
        Assert.Contains("already past", rules.Check(f.Check("Mfg. 01/2024 Exp. 09/2026").Dates, Now));
        Assert.Null(rules.Check(f.Check("Mfg. 10/2025 Exp. 10/2026").Dates, Now));                 // lasts to 31/10/26

        var pkd = TextFormat.Parse("PKD {MFD:DD/MM/YY} USE BY {EXP:DD/MM/YY}");
        Assert.Null(rules.Check(pkd.Check("PKD 28/10/26 USE BY 27/04/27").Dates, Now));
        Assert.Contains("already past", rules.Check(pkd.Check("PKD 28/10/26 USE BY 30/10/26").Dates, Now));
        Assert.Contains("6 months", new DateRules { ExpiryMonths = 6 }.Check(pkd.Check("PKD 28/10/26 USE BY 27/05/27").Dates, Now));
    }

    [Fact]
    public void BestBeforeMonthsFromManufacture()
    {
        var mfg = TextFormat.Parse("MFG. {MFD:MM/YYYY}");
        var life = TextFormat.Parse("BEST BEFORE {LIFE} MONTHS FROM MFG.");
        var r = life.Check("Best before 18 months from mfg.");
        Assert.True(r.Ok);
        Assert.Equal(18, r.ShelfMonths);
        var rules = new DateRules();
        Assert.Null(rules.Check(mfg.Check("Mfg. 10/2026").Dates, Now, 18));
        // the shelf life ran out: 01/2025 + 18 months = 07/2026
        Assert.Contains("already past", rules.Check(mfg.Check("Mfg. 01/2025").Dates, Now, 18));
        Assert.Null(rules.Check(mfg.Check("Mfg. 04/2025").Dates, Now, 18));                     // to 31/10/26
        // the product master says 24 months
        Assert.Contains("should be 24 months", new DateRules { ExpiryMonths = 24 }.Check(mfg.Check("Mfg. 10/2026").Dates, Now, 18));
        // no production date to count from
        Assert.Contains("No MFD date", rules.Check(new Dictionary<string, (DateOnly, bool)>(), Now, 18));
        // a printed expiry must agree with the printed shelf life
        var both = TextFormat.Parse("MFG {MFD:MM/YY} EXP {EXP:MM/YY}").Check("MFG 10/26 EXP 10/28").Dates;
        Assert.Null(rules.Check(both, Now, 24));
        Assert.Contains("18 months after", rules.Check(both, Now, 18));
        // 0 or 300 months is not a shelf life
        Assert.Equal("explain.format.date_invalid", life.Check("Best before 0 months from mfg.").Explain);
        Assert.False(life.Check("Best before 300 months from mfg.").Ok);
    }

    [Fact]
    public void AHarpicPackChecksThroughTheTool()
    {
        var formats = new[]
        {
            TextFormat.Parse("{MRP} (Incl. of all taxes)"),
            TextFormat.Parse("MFG. {MFD:MMM YYYY}", new[] { "en-IN" }),
            TextFormat.Parse("BEST BEFORE {LIFE} MONTHS FROM MFG."),
        };
        var good = TextTool.CheckFormats(new[] { Line("MRP: ₹159.35 (Incl. of all taxes)"), Line("MFG. SEPT 2026"), Line("BEST BEFORE 24 MONTHS FROM MFG.") },
                                         formats, new DateRules(), 60, Now);
        Assert.Null(good.Problem);
        Assert.Contains("MRP 159.35", good.Fields);
        Assert.Contains("LIFE 24 months", good.Fields);
        Assert.Contains("MFD 09/2026", good.Fields);

        var oldStock = TextTool.CheckFormats(new[] { Line("MRP: ₹159.35 (Incl. of all taxes)"), Line("MFG. SEPT 2023"), Line("BEST BEFORE 24 MONTHS FROM MFG.") },
                                             formats, new DateRules(), 60, Now);
        Assert.Equal("explain.format.date_wrong", oldStock.Explain);
        var noPrice = TextTool.CheckFormats(new[] { Line("MRP: ₹159.35"), Line("MFG. SEPT 2026"), Line("BEST BEFORE 24 MONTHS FROM MFG.") },
                                            formats, new DateRules(), 60, Now);
        Assert.Equal("explain.format.mismatch", noPrice.Explain);
    }

    [Fact]
    public void ASetupWithAShelfLifeNeedsTheProductionDate()
    {
        var cfg = new TextToolConfig { Mode = TextMode.Read, Lines = { new TextLineSpec { Entry = LineEntry.Format, Template = "BEST BEFORE {LIFE} MONTHS" } } };
        Assert.Contains("needs the MFD date", TextTool.SetupProblem(cfg));
        cfg.Lines.Add(new TextLineSpec { Entry = LineEntry.Format, Template = "MFG {MFD:MM/YY}" });
        Assert.Null(TextTool.SetupProblem(cfg));
        cfg.Lines.Add(new TextLineSpec { Entry = LineEntry.Format, Template = "USE WITHIN {LIFE} MONTHS" });
        Assert.Contains("on two lines", TextTool.SetupProblem(cfg));
    }

    [Theory]
    [InlineData("MRP: ₹159.35 (Incl. of all taxes)", "{MRP} (Incl. of all taxes)")]
    [InlineData("M.R.P. Rs. 46.00 (incl. of all taxes)", "{MRP} (incl. of all taxes)")]
    [InlineData("MRP Rs.10/-", "{MRP:whole}/-")]
    [InlineData("MRP ₹ 1,250.00", "{MRP:1,250.00}")]
    [InlineData("Unit sale price ₹ 0.92 per g", "Unit sale price {PRICE} {PER}")]
    [InlineData("Best before 18 months from manufacture", "Best before {LIFE} months from manufacture")]
    [InlineData("Mfg. Dt. 10/2026", "Mfg. Dt. {MFD:MM/YYYY}")]
    [InlineData("Pkd. On: 28/10/26", "Pkd. On: {MFD:DD/MM/YY}")]
    [InlineData("Use before 27.04.27", "Use before {EXP:DD.MM.YY}")]
    [InlineData("Hrs 10", "Hrs {D:2}")]                                  // not a rupee amount
    public void TeachingProposesIndianFields(string read, string proposed)
    {
        Assert.Equal(proposed, TextFormat.Propose(read));
        Assert.True(TextFormat.Parse(proposed).Check(read).Ok);
    }

    [Theory]
    [InlineData("Best before 18 months from manufacture", "expiry date")]
    [InlineData("M.R.P. Rs. 46.00 (incl. of all taxes)", "price")]
    [InlineData("Unit sale price ₹ 0.92 per g", "price")]
    public void AutoFindNamesTheIndianFields(string read, string field) =>
        Assert.Equal(field, AutoFind.LineField(read, TextFormat.Propose(read)));
}
