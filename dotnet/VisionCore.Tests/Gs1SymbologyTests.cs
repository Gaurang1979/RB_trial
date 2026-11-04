using BarcodeReading;
using VisionCore;
using Xunit;

// The GS1 check with the engine's symbology identifier (cr_get_code_ext): a symbol without FNC1 first fails.
public class Gs1SymbologyTests
{
    private static readonly DateTime Now = new(2026, 10, 30);
    private static readonly Dictionary<string, string> NoValues = new();
    private const string Data = "0109506000134352\u001D10AB12";
    private static readonly CodeToolConfig Cfg = new() { Name = "C", Format = CodeFormat.DataMatrix, Gs1 = new Gs1Rules { Required = { "01", "10" } } };

    private static CodeVerdict Judge(CodeFormat f, string id) =>
        CodeTool.Judge(new[] { (f, Data) }, Cfg, null, NoValues, Now, null, new[] { id });

    [Theory]
    [InlineData(CodeFormat.DataMatrix, "]d2")]
    [InlineData(CodeFormat.QrCode, "]Q3")]
    [InlineData(CodeFormat.Gs1_128, "]C1")]
    [InlineData(CodeFormat.DataBar, "]e0")]
    public void AGs1SymbolPasses(CodeFormat f, string id) => Assert.True(Judge(f, id).Pass);

    [Theory]
    [InlineData(CodeFormat.DataMatrix, "]d1")]
    [InlineData(CodeFormat.QrCode, "]Q1")]
    [InlineData(CodeFormat.Gs1_128, "]C0")]
    public void TheSameDataWithoutFnc1Fails(CodeFormat f, string id)
    {
        var v = Judge(f, id);
        Assert.False(v.Pass);
        Assert.Equal("explain.gs1.syntax", v.Explain);
        Assert.Contains(id, v.Summary);
    }

    [Fact]
    public void AnUnknownIdentifierFailsAGs1Tool()
    {
        // the engine reads no identifier only for a code it matched as a pattern: FNC1 first is in doubt, so NG
        var v = Judge(CodeFormat.DataMatrix, "");
        Assert.False(v.Pass);
        Assert.Equal("explain.gs1.syntax", v.Explain);
        Assert.Contains("FNC1", v.Summary);
        // texts judged alone (no engine read) are checked on their data
        Assert.True(CodeTool.Judge(new[] { (CodeFormat.DataMatrix, Data) }, Cfg, null, NoValues, Now).Pass);
    }

    [Fact]
    public void AnUnknownIdentifierDoesNotBorrowAnotherCodesOne()
    {
        var codes = new[] { (CodeFormat.DataMatrix, Data), (CodeFormat.DataMatrix, Data + "9"), (CodeFormat.DataMatrix, Data + "8") };
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.DataMatrix, ExpectedCount = 3, Gs1 = Cfg.Gs1 };
        Assert.Equal(new[] { true, false, false }, CodeTool.Judge(codes, cfg, null, NoValues, Now, null, new[] { "]d2", "", "]d1" }).CodeOk);
        Assert.Equal(new[] { true, false, true }, CodeTool.Judge(codes, cfg, null, NoValues, Now, null, new[] { "]d2", "", "]d2" }).CodeOk);
    }

    [Fact]
    public void ADigitalLinkInAPlainQrIsNotRefusedForItsIdentifier()
    {
        var cfg = new CodeToolConfig { Name = "C", Format = CodeFormat.QrCode, Gs1 = new Gs1Rules { Required = { "01" } } };
        const string url = "https://id.gs1.org/01/09506000134352/10/AB12";
        var plain = CodeTool.Judge(new[] { (CodeFormat.QrCode, url) }, cfg, null, NoValues, Now, null, new[] { "]Q1" });
        var none = CodeTool.Judge(new[] { (CodeFormat.QrCode, url) }, cfg, null, NoValues, Now);
        Assert.Equal(none.Pass, plain.Pass);
        Assert.DoesNotContain("symbology identifier", plain.Summary);
    }

    [Fact]
    public void DecodedCodeCarriesItsIdentifier()
    {
        var c = new DecodedCode(Data, CodeFormat.DataMatrix, ReadStep.Plain, new BarcodeReading.PointF[4], 1) { SymbologyId = "]d2" };
        Assert.Equal("]d2" + Data, c.TextWithId);
        Assert.Equal("", (c with { SymbologyId = "" }).SymbologyId);
    }
}
