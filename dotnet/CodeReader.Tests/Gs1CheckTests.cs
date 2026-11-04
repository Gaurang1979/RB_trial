using System;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class Gs1CheckTests
{
    private const char GS = '\u001D';
    private static readonly DateTime Now = new(2026, 10, 29, 10, 0, 0);
    private static readonly Gs1Rules NoRules = new() { ExpiryNotPast = false };

    private static Gs1Result Check(string text, Gs1Rules? rules = null) => Gs1Check.Check(text, rules ?? NoRules, Now);
    private static string Why(Gs1Result r) => r.Problems.Count == 0 ? "" : r.Problems[0].Explain;

    [Fact]
    public void ValidPharmaCodeIsParsed()
    {
        var r = Check($"0109506000134352172912311010LOT42{GS}21SER1", new Gs1Rules { Required = { "01", "17", "10", "21" } });
        Assert.True(r.Valid, string.Join("; ", r.Problems.Select(p => p.Message)));
        Assert.Equal(new[] { "01", "17", "10", "21" }, r.Elements.Select(e => e.Ai));
        Assert.Equal("10LOT42", r.Get("10"));
        Assert.Equal("SER1", r.Get("21"));
    }

    [Fact]
    public void HumanReadableAndDigitalLinkForms()
    {
        // the bracketed form is accepted from typed or OCR text only
        var typed = Gs1Check.Check("(01)09506000134352(17)291231(10)AB(C)D(21)X1", NoRules, Now, decoded: false);
        Assert.True(typed.Valid);
        Assert.Equal("AB(C)D", typed.Get("10")); // "(" may be in a value
        Assert.True(Check("]d2010950600013435210AB").Valid);     // GS1 DataMatrix identifier
        var dl = Check("https://id.example.com/01/09506000134352/10/LOT42?17=291231");
        Assert.True(dl.Valid);
        Assert.Equal("291231", dl.Get("17"));
    }

    [Theory]
    [InlineData("0109506000134353", "explain.gs1.check_digit")]            // GTIN check digit
    [InlineData("0109506000134352172602301010", "explain.gs1.bad_date")]   // 30 February
    [InlineData("010950600013435211261300", "explain.gs1.bad_date")]       // month 13
    [InlineData("0109506000134352{NUL}10AB", "explain.gs1.syntax")]           // no known AI after a NUL
    [InlineData("010950600013435210AB{NUL}C", "explain.gs1.syntax")]          // a NUL inside the batch
    [InlineData("80060950600013435301", "explain.gs1.syntax")]               // ITIP cut short (18 digits)
    [InlineData("800609506000134353010", "explain.gs1.syntax")]
    [InlineData("800609506000134353010" + "2", "explain.gs1.check_digit")]   // the GTIN inside 8006
    [InlineData("]d1010950600013435210AB", "explain.gs1.syntax")]             // not a GS1 symbology identifier
    [InlineData("(01)09506000134352(10)AB", "explain.gs1.syntax")]           // printed text encoded in the symbol
    [InlineData("https://id.example.com/01/09506000134352?7299=X", "explain.gs1.syntax")] // unknown AI in a link
    [InlineData("01095060001343521729123110LOT#1", "explain.gs1.syntax")]   // # is not a GS1 character
    [InlineData("0109506000134352101234567890123456789012", "explain.gs1.syntax")] // batch over 20
    [InlineData("0109506000134352{GS}", "explain.gs1.syntax")]              // ends with a separator
    [InlineData("01095060001343520109506000134352", "explain.gs1.syntax")] // the same AI twice
    [InlineData("88123", "explain.gs1.syntax")]                             // no known AI
    [InlineData("(01)09506000134352(55)X", "explain.gs1.syntax")]                  // brackets in a symbol
    [InlineData("0109506000134352x", "explain.gs1.syntax")]
    public void ProblemsAreFound(string text, string explain)
    {
        var r = Check(text.Replace("{GS}", GS.ToString()).Replace("{NUL}", "\0"));
        Assert.False(r.Valid);
        Assert.Equal(explain, Why(r));
    }

    [Fact]
    public void DateRules()
    {
        Assert.True(Check("010950600013435217261200").Valid);                              // day 00 = 31 Dec, allowed for expiry
        Assert.True(Check("010950600013435211261000").Valid);                              // and for production
        // production day 00 is read as the 1st: an expiry in the same month is after it
        Assert.True(Check("01095060001343521126100017261015").Valid);
        // 7003 expiry date and time: checked against the picture's time to the minute
        Assert.Contains("already past", Gs1Check.Check($"0109506000134352{"7003"}2610290959", new Gs1Rules(), Now).Problems.Single().Message);
        Assert.True(Gs1Check.Check($"0109506000134352{"7003"}2610291001", new Gs1Rules(), Now).Valid);
        Assert.Equal("explain.gs1.bad_date", Why(Check("01095060001343521126110117261001")));   // expiry before production
        var past = Gs1Check.Check("010950600013435217261028", new Gs1Rules(), Now);
        Assert.Contains("already past", past.Problems.Single().Message);
        Assert.True(Gs1Check.Check("010950600013435217261029", new Gs1Rules(), Now).Valid);  // today is still good
    }

    [Fact]
    public void RequiredAisAndMeasures()
    {
        var r = Check("0109506000134352", new Gs1Rules { Required = { "01", "21" } });
        Assert.Equal("explain.gs1.missing_ai", Why(r));
        Assert.Contains("serial number", r.Problems[0].Message);
        Assert.True(Check("01095060001343523103000750").Valid);                             // net weight 0.750 kg
        Assert.True(Check("00376123450801218856").Valid);                                    // SSCC
        Assert.True(Check("00376123450801218856410541234500001340012345").Valid);             // 410 is fixed length: no separator needed
    }

    [Fact]
    public void AMissingSeparatorNeverYieldsAShortenedValue()
    {
        // the batch runs on into what should have been the expiry: the expiry is then missing
        var r = Check("010950600013435210LOT4217291231", new Gs1Rules { Required = { "17" } });
        Assert.Equal("LOT4217291231", r.Get("10"));
        Assert.Equal("explain.gs1.missing_ai", Why(r));
    }
}
