using BarcodeReading;
using VisionCore;
using VisionStudio.Services;
using Xunit;

public sealed class PharmacodeBarsFieldTests
{
    [Fact]
    public void OneNumberSetsBothBoundsOfTheLock()
    {
        var c = new CodeToolConfig { Format = CodeFormat.Pharmacode, Expected = "1234" };
        Assert.Null(PharmacodeBarsField.Get(c));
        PharmacodeBarsField.Set(c, CodeFormats.PharmacodeBars("1234"));
        Assert.Equal(c.MinLength, c.MaxLength);
        Assert.Equal(CodeFormats.PharmacodeBars("1234"), PharmacodeBarsField.Get(c));
        Assert.Null(CodeTool.SetupProblem(c));
    }

    [Fact]
    public void ARangeFromTeachingIsShownUntilANumberIsTyped()
    {
        var c = new CodeToolConfig { Format = CodeFormat.PharmacodeReverse, MinLength = 5, MaxLength = 7 };
        Assert.Null(PharmacodeBarsField.Get(c));
        Assert.Equal("5–7", PharmacodeBarsField.Range(c));
        PharmacodeBarsField.Set(c, 6);
        Assert.Equal((6, 6), (c.MinLength, c.MaxLength));
        Assert.Null(PharmacodeBarsField.Range(c));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public void AnEmptyOrImpossibleCountClearsTheLockAndIsASetupError(int? bars)
    {
        // never "any number of bars": without both bounds a missing end bar reads as a shorter code
        var c = new CodeToolConfig { Format = CodeFormat.Pharmacode, Expected = "1234", MinLength = 11, MaxLength = 11 };
        PharmacodeBarsField.Set(c, bars);
        Assert.Equal((0, 0), (c.MinLength, c.MaxLength));
        Assert.NotNull(CodeTool.SetupProblem(c));
    }
}
