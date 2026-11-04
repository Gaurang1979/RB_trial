using System;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class Gs1AssociationTests
{
    private const char GS = '\u001D';
    private static readonly DateTime Now = new(2026, 10, 29, 10, 0, 0);
    private static readonly Gs1Rules NoRules = new() { ExpiryNotPast = false };

    private static Gs1Result Check(string text, Gs1Rules? rules = null) =>
        Gs1Check.Check(text.Replace("{GS}", GS.ToString()), rules ?? NoRules, Now);
    private static string Problems(Gs1Result r) => string.Join("; ", r.Problems.Select(p => p.Message));

    [Theory]
    [InlineData("0109506000134352172912311010LOT42{GS}21SER1")]                      // pharma pack
    [InlineData("00376123450801218856020950600013435210LOT1{GS}3712")]              // SSCC, contained GTIN, batch, count
    [InlineData("4155412345000013" + "8020REF1{GS}12261231")]                       // payment slip: pay to GLN, reference, due date
    [InlineData("4145412345000013254ABC")]                                           // location GLN and its extension
    [InlineData("8006095060001343520102{GS}10LOT{GS}21S1")]                             // trade item component, batch, serial
    [InlineData("01095060001343523103000750")]                                      // net weight with its GTIN
    [InlineData("0109506000134352250X{GS}21S1")]                                    // secondary serial with serial and GTIN
    [InlineData("003761234508012188563300000120")]                                  // logistic gross weight on an SSCC
    public void ValidAssociationsPass(string text)
    {
        var r = Check(text);
        Assert.True(r.Valid, Problems(r));
    }

    [Theory]
    [InlineData("10LOT1", "batch (10) needs a GTIN (01), GTIN of contained trade items (02), GTIN of a made-to-order item (03), trade item component (8006) or contained trade item component (8026)")]
    [InlineData("00376123450801218856" + "17291231", "expiry date (17) needs")]
    [InlineData("01095060001343520209506000134352", "may not be in one code")]
    [InlineData("01095060001343523712", "GTIN (01) and the count of trade items (37) may not be in one code")]
    [InlineData("02095060001343523712{GS}21S1", "serial number (21) needs a GTIN (01), GTIN of a made-to-order item (03) or trade item component (8006)")]
    [InlineData("0209506000134352", "needs a count of trade items (37)")]
    [InlineData("0109506000134352250X", "secondary serial number (250) needs a serial number (21)")]
    [InlineData("010950600013435231020007503103000750", "measure (3102) and the measure (3103) may not be in one code")]
    [InlineData("003761234508012188563100000120", "measure (3100) needs a GTIN (01) or GTIN of contained trade items (02)")]
    [InlineData("8020REF1", "payment slip reference (8020) needs a pay to GLN (415)")]
    [InlineData("01095060001343528006095060001343520102", "may not be in one code")]
    [InlineData("https://id.gs1.org/01/09506000134352?02=09506000134352&37=12", "may not be in one code")]
    public void InvalidAssociationsFail(string text, string message)
    {
        var r = Check(text);
        Assert.False(r.Valid);
        Assert.Contains(r.Problems, p => p.Explain == "explain.gs1.association" && p.Message.Contains(message));
    }

    [Fact]
    public void PharmaRequiredSet()
    {
        var pharma = new Gs1Rules { Required = Gs1Check.RequiredSets["pharma"].ToList() };
        Assert.True(Check("0109506000134352172912311010LOT42{GS}21SER1", pharma).Valid);
        var r = Check("0109506000134352172912311010LOT42", pharma);
        Assert.Equal("explain.gs1.missing_ai", r.Problems.Single().Explain);
        Assert.Contains("serial number (21)", r.Problems[0].Message);
    }

    [Theory]
    [InlineData("https://id.gs1.org/01/09506000134352/22/V1/10/LOT1/21/S1?17=291231&3103=000750&linkType=gs1:pip")]
    [InlineData("https://example.com/products/x/01/09506000134352/21/S1")]          // a path prefix of the owner's choice
    [InlineData("https://id.gs1.org/00/376123450801218856")]
    [InlineData("https://id.gs1.org/414/5412345000013/254/A1")]
    [InlineData("https://id.gs1.org/8006/095060001343520102/10/LOT/21/S1")]
    [InlineData("HTTPS://id.gs1.org/01/09506000134352/10/LOT%2F1")]                 // an escaped "/" inside a value
    public void ValidDigitalLinksPass(string url)
    {
        var r = Check(url);
        Assert.True(r.Valid, Problems(r));
    }

    [Fact]
    public void DigitalLinkValuesAndShortGtins()
    {
        var r = Check("https://id.gs1.org/01/09506000134352/22/V1/10/LOT%2F1/21/S1?17=291231");
        Assert.Equal(new[] { "01", "22", "10", "21", "17" }, r.Elements.Select(e => e.Ai));
        Assert.Equal("LOT/1", r.Get("10"));
        Assert.Equal("09506000134352", Check("https://id.gs1.org/01/9506000134352").Get("01"));  // GTIN-13
        Assert.Equal("00000096385074", Check("https://id.gs1.org/01/96385074").Get("01"));       // GTIN-8
        Assert.True(Check("https://id.gs1.org/01/96385074").Valid);
    }

    [Theory]
    [InlineData("https://id.gs1.org/01/09506000134352/21/S1/10/LOT1", "/10 is out of order")]
    [InlineData("https://id.gs1.org/01/09506000134352/10/A/10/B", "/10 is out of order")]
    [InlineData("https://id.gs1.org/01/09506000134352/17/291231", "/17 is not a key qualifier of /01")]
    [InlineData("https://id.gs1.org/10/LOT1/01/09506000134352", "/10 comes before the primary key /01")]
    [InlineData("https://id.gs1.org/01/09506000134352/00/376123450801218856", "/00 is a second primary key")]
    [InlineData("https://id.gs1.org/01/09506000134352?21=S1", "?21= belongs in the path")]
    [InlineData("https://id.gs1.org/01/09506000134352?00=376123450801218856", "?00= belongs in the path")]
    [InlineData("https://id.gs1.org/01/09506000134352/10", "/01 is not followed by AI and value pairs")]
    [InlineData("https://id.gs1.org/products/42", "holds no GS1 key")]
    [InlineData("https://id.gs1.org/00/376123450801218856/10/LOT1", "/10 is not a key qualifier of /00")]
    [InlineData("https://id.gs1.org/01/09506000134352/7299/X", "/7299 is not a known AI")]
    public void DigitalLinkStructureProblemsFail(string url, string message)
    {
        var r = Check(url);
        Assert.False(r.Valid);
        Assert.Equal("explain.gs1.syntax", r.Problems[0].Explain);
        Assert.Contains(message, r.Problems[0].Message);
    }
}
