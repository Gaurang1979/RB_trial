using VisionCore;
using VisionStudio.Services;
using Xunit;

public sealed class MonthLanguagesTests
{
    [Fact]
    public void TickingAddsAndUntickingTheLastLeavesEnglish()
    {
        var t = new TextToolConfig();
        Assert.True(MonthLanguages.Has(t, "en"));
        MonthLanguages.Set(t, "es", true);
        Assert.Equal(["en", "es"], t.MonthLanguages);
        MonthLanguages.Set(t, "en", false);
        MonthLanguages.Set(t, "ES", false);
        Assert.Equal(["en"], t.MonthLanguages);
        t.MonthLanguages.Clear();
        Assert.True(MonthLanguages.Has(t, "EN"));
        Assert.False(MonthLanguages.Has(t, "fr"));
    }

    [Fact]
    public void TeachProposesTheMonthNamesOfTheTickedLanguages()
    {
        var t = new TextToolConfig();
        Assert.DoesNotContain("MMM", TextFormat.Propose("EXP ENE 28", null, MonthLanguages.Known(t)));
        MonthLanguages.Set(t, "es", true);
        Assert.Equal("EXP {EXP:MMM YY}", TextFormat.Propose("EXP ENE 28", null, MonthLanguages.Known(t)));
    }

    [Fact]
    public void AnUnknownCodeFromAJobFileIsLeftOutOfTeach()
    {
        var t = new TextToolConfig { MonthLanguages = ["xx", "fr"] };
        Assert.Equal(["fr"], MonthLanguages.Known(t));
        Assert.Equal("EXP {EXP:MMM YYYY}", TextFormat.Propose("EXP FÉVR 2028", null, MonthLanguages.Known(t)));
    }

    [Fact]
    public void LanguagesShowInTheirOwnName()
    {
        Assert.Equal("Español", MonthLanguages.Name("es"));
        Assert.Equal("Deutsch", MonthLanguages.Name("de"));
    }
}
