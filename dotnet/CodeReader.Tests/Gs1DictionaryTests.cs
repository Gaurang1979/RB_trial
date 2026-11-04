using System;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

/// <summary>The AI table generated from the GS1 Barcode Syntax Dictionary: AIs beyond the common ones,
/// their components and checks, and their rules.</summary>
public class Gs1DictionaryTests
{
    private const char GS = '\u001D';
    private static readonly DateTime Now = new(2026, 10, 29, 10, 0, 0);
    private static readonly Gs1Rules NoRules = new() { ExpiryNotPast = false };

    private static Gs1Result Check(string text) => Gs1Check.Check(text.Replace("{GS}", GS.ToString()), NoRules, Now);
    private static string Problems(Gs1Result r) => string.Join("; ", r.Problems.Select(p => p.Message));

    [Fact]
    public void GeneratedTableIsComplete()
    {
        Assert.Equal("2026-01-27", Gs1Check.DictionaryRelease);
        // a refresh that brings a check this class does not make fails here, not on a line
        Assert.All(Gs1Check.DictionaryLinters, l => Assert.True(Gs1Check.LinterKnown(l), $"GS1 check \"{l}\" is not built"));
        foreach (var ai in new[] { "03", "235", "253", "255", "401", "402", "426", "4300", "7040", "7250", "8010", "8011", "8013", "8017", "8026", "8030", "8200", "91" })
            Assert.True(Gs1Check.Known(ai), ai);
        Assert.False(Gs1Check.Known("7299"));
        Assert.Equal(18, Gs1Check.PredefinedLength("00"));
        Assert.Equal(-1, Gs1Check.PredefinedLength("8006"));   // fixed length, yet ended by a separator
    }

    [Theory]
    [InlineData("0109506000134352235TPX-1")]                                       // third-party serial extension
    [InlineData("2539506000134352DOC-17")]                                          // GDTI with its serial part
    [InlineData("253" + "9506000134352")]                                           // GDTI without it
    [InlineData("255" + "950600013435212345")]                                      // coupon code id
    [InlineData("401" + "9506000ABC/42")]                                           // consignment number
    [InlineData("402" + "9506000134567890" + "7")]                                  // shipment id, N17 with its check digit
    [InlineData("0109506000134352426356")]                                          // country of full processing: India
    [InlineData("0109506000134352423356004{GS}10L1")]                               // initial processing in two countries
    [InlineData("8010" + "9506000AB#-/{GS}8011123")]                                // CPID (39-character set) and its serial
    [InlineData("8017" + "950600013456789018")]                                     // GSRN provider
    [InlineData("00376123450801218856" + "8026095060001343520102{GS}375")]          // contained trade item component with count and SSCC
    [InlineData("8013" + "1987654Ad4X4bL5ttr2310c2K")]                             // GMN, GS1's example check pair
    [InlineData("0109506000134352" + "7240PROT1")]                                  // protocol id
    [InlineData("0109506000134352" + "8008261029103015")]                           // production date and time to the second
    [InlineData("0109506000134352" + "80082610291030")]                             // and to the minute
    [InlineData("0109506000134352" + "7007261001261031")]                           // harvest date range
    [InlineData("0109506000134352" + "80010100005000011" + "1")]                    // roll dimensions
    [InlineData("4155412345000013" + "8007GB82WEST12345698765432{GS}8020REF1")]     // IBAN with pay to GLN and reference
    [InlineData("00376123450801218856" + "4330001250-")]                            // maximum temperature, below zero
    [InlineData("00376123450801218856" + "421356400001")]                           // ship to postal code with country
    [InlineData("01095060001343523103000750" + "39221999")]                         // price with a measure
    [InlineData("0109506000134352" + "21S1{GS}8030" + "ABCdef-_xyz=")]              // digital signature, base64url
    [InlineData("91ANY-DATA{GS}0109506000134352")]
    public void NewAisPass(string text)
    {
        var r = Check(text);
        Assert.True(r.Valid, Problems(r));
    }

    [Theory]
    [InlineData("402" + "9506000134567890" + "8", "explain.gs1.check_digit", "check digit is wrong")]
    [InlineData("8017" + "950600013456789017", "explain.gs1.check_digit", "check digit is wrong")]
    [InlineData("8013" + "1987654Ad4X4bL5ttr2310c2L", "explain.gs1.check_digit", "check characters are wrong")]
    [InlineData("0109506000134352426999", "explain.gs1.syntax", "is not a country code")]
    [InlineData("0109506000134352" + "8008261029103", "explain.gs1.syntax", "do not make whole parts")]
    [InlineData("0109506000134352" + "8008261029253015", "explain.gs1.bad_date", "is not a real time")]
    [InlineData("0109506000134352" + "7007261001261032", "explain.gs1.bad_date", "is not a real date")]
    [InlineData("8010" + "9506000ab{GS}8011123", "explain.gs1.syntax", "character GS1 does not allow")]
    [InlineData("8010" + "9506000AB{GS}80110123", "explain.gs1.syntax", "must not start with 0")]
    [InlineData("0109506000134352" + "80010100005000015" + "1", "explain.gs1.syntax", "winding direction")]
    [InlineData("0109506000134352" + "80010000005000011" + "1", "explain.gs1.syntax", "must not be zero")]
    [InlineData("00376123450801218856" + "8026095060001343520302{GS}375", "explain.gs1.syntax", "piece number")]
    [InlineData("4155412345000013" + "8007GB83WEST12345698765432{GS}8020REF1", "explain.gs1.check_digit", "IBAN")]
    [InlineData("00376123450801218856" + "4330001250+", "explain.gs1.syntax", "must be \"-\"")]
    [InlineData("0109506000134352" + "21S1{GS}8030" + "ABCD=", "explain.gs1.syntax", "base64")]
    public void NewAiValuesFail(string text, string explain, string message)
    {
        var r = Check(text);
        Assert.False(r.Valid);
        Assert.Equal(explain, r.Problems[0].Explain);
        Assert.Contains(message, r.Problems[0].Message);
    }

    [Theory]
    [InlineData("0109506000134352235T1{GS}21S1", "serial number (21) and the third-party serial extension (235) may not be in one code")]
    [InlineData("0109506000134352422356{GS}426356", "country of origin (422) and the country of full processing (426) may not be in one code")]
    [InlineData("8017950600013456789018{GS}" + "8018950600013456789018", "may not be in one code")]
    [InlineData("0209506000134352" + "8026095060001343520102{GS}375", "may not be in one code")]
    [InlineData("8011123", "CPID serial number (8011) needs a component or part id (CPID) (8010)")]
    [InlineData("0209506000134352375", "count of trade items (37) needs a SSCC (00)")]
    [InlineData("235TPX1", "third-party serial extension (235) needs a GTIN (01)")]
    [InlineData("0109506000134352" + "39221999", "price (3922) needs a count (30), measure (31nn)")]
    [InlineData("0109506000134352" + "8030ABCD", "digital signature (8030) needs a SSCC (00), GTIN (01) with serial number (21)")]
    [InlineData("0109506000134352" + "255" + "9506000134352", "may not be in one code")]
    public void NewRulesFail(string text, string message)
    {
        var r = Check(text);
        Assert.False(r.Valid);
        Assert.Contains(r.Problems, p => p.Explain == "explain.gs1.association" && p.Message.Contains(message));
    }

    [Theory]
    [InlineData("https://id.gs1.org/01/09506000134352/235/TPX1")]                    // the second qualifier sequence of a GTIN
    [InlineData("https://id.gs1.org/8010/9506000AB/8011/123")]
    [InlineData("https://id.gs1.org/253/9506000134352DOC1")]
    [InlineData("https://id.gs1.org/8017/950600013456789018/8019/42")]
    [InlineData("https://id.gs1.org/414/5412345000013/7040/1ABC")]
    [InlineData("https://id.gs1.org/01/09506000134352?426=356&7240=P1")]
    public void NewDigitalLinksPass(string url)
    {
        var r = Check(url);
        Assert.True(r.Valid, Problems(r));
    }

    [Theory]
    [InlineData("https://id.gs1.org/01/09506000134352/22/V1/235/T1", "/235 is out of order: after /01 the path holds /22, /10, /21 or /235")]
    [InlineData("https://id.gs1.org/01/09506000134352?03=09506000134352", "?03= is not allowed as a Digital Link data attribute")]
    [InlineData("https://id.gs1.org/01/09506000134352/8011/1", "/8011 is not a key qualifier of /01")]
    public void NewDigitalLinkProblemsFail(string url, string message)
    {
        var r = Check(url);
        Assert.False(r.Valid);
        Assert.Contains(message, r.Problems[0].Message);
    }

    [Fact]
    public void ProductDataReadsDictionaryAis()
    {
        // Gs1Data (product matching) reads the same AIs: a third-party serial extension no longer stops it
        var g = Gs1Data.TryParse("0109506000134352235TPX1");
        Assert.NotNull(g);
        Assert.Equal("TPX1", g!.Fields["235"]);
    }
}
