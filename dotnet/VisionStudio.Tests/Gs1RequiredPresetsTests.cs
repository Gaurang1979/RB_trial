using BarcodeReading;
using VisionCore;
using VisionStudio.Services;
using Xunit;

public sealed class Gs1RequiredPresetsTests
{
    [Fact]
    public void Pharma_is_01_17_10_21_in_the_field_text()
    {
        Assert.Equal("01 17 10 21", Gs1RequiredPresets.Format(Gs1RequiredPresets.Pharma));
    }

    [Fact]
    public void The_field_text_parses_back_to_the_pharma_set_and_every_AI_is_known()
    {
        var ais = CodeTeach.ParseAis(Gs1RequiredPresets.Format(Gs1RequiredPresets.Pharma));
        Assert.Equal(["01", "17", "10", "21"], ais);
        Assert.True(Gs1RequiredPresets.IsPharma(ais));
    }

    [Theory]
    [InlineData("21 10 17 01", true)]
    [InlineData("01 17 10", false)]
    [InlineData("01 17 10 21 11", false)]
    public void IsPharma_ignores_order_only(string text, bool expected) =>
        Assert.Equal(expected, Gs1RequiredPresets.IsPharma(CodeTeach.ParseAis(text)));

    [Fact]
    public void A_preset_fills_the_existing_required_setting()
    {
        var g = new Gs1Rules { Required = CodeTeach.ParseAis("01") };
        g.Required = CodeTeach.ParseAis(Gs1RequiredPresets.Format(Gs1RequiredPresets.Pharma));
        Assert.Equal(["01", "17", "10", "21"], g.Required);
    }
}
