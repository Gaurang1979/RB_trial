using System;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class Gs1DataTests
{
    private const char GS = '\u001D';

    [Fact]
    public void ParsesRawElementStringWithSeparators()
    {
        var g = Gs1Data.TryParse($"010890123456789017281231" + $"10AB12C{GS}21SER0001");
        Assert.NotNull(g);
        Assert.Equal("08901234567890", g!.Gtin);
        Assert.Equal(new DateOnly(2028, 12, 31), g.Expiry);
        Assert.Equal("AB12C", g.Batch);
        Assert.Equal("SER0001", g.Serial);
        Assert.True(g.CheckDigitValid);
    }

    [Fact]
    public void ParsesHumanReadableForm()
    {
        var g = Gs1Data.TryParse("(01)08901234567890(10)B77(21)XYZ");
        Assert.Equal("B77", g!.Batch);
        Assert.Equal("XYZ", g.Serial);
    }

    [Fact]
    public void ParsesDigitalLink()
    {
        var g = Gs1Data.TryParse("https://id.gs1.org/01/08901234567890/10/B77/21/XYZ?17=281231");
        Assert.Equal("08901234567890", g!.Gtin);
        Assert.Equal("XYZ", g.Serial);
        Assert.Equal(new DateOnly(2028, 12, 31), g.Expiry);
    }

    [Fact]
    public void FlagsBadCheckDigit()
    {
        var g = Gs1Data.TryParse("0108901234567891");
        Assert.False(g!.CheckDigitValid);
    }

    [Fact]
    public void ParsesSscc()
    {
        var g = Gs1Data.TryParse("00089012340000000014");
        Assert.Equal("089012340000000014", g!.Sscc);
        Assert.True(g.CheckDigitValid);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("1234")]
    [InlineData("")]
    public void ReturnsNullForNonGs1(string text) => Assert.Null(Gs1Data.TryParse(text));

    [Theory]
    [InlineData("8901234567890", "08901234567890", true)]
    [InlineData("8901234567891", "08901234567890", false)]  // a misread
    [InlineData("890123456789", "08901234567890", false)]   // a digit lost
    [InlineData("89O1234567890", "08901234567890", false)]  // not digits
    public void EanMustBeTheProductGtin(string ean, string gtin, bool same) => Assert.Equal(same, Gs1Data.SameGtin(ean, gtin));

    [Fact]
    public void DayZeroMeansLastDayOfMonth()
    {
        var g = Gs1Data.TryParse("0108901234567890172802" + "00");
        Assert.Equal(new DateOnly(2028, 2, 29), g!.Expiry);
    }
}
