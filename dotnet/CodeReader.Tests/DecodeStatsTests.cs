using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class DecodeStatsTests
{
    private static DecodeStats Stats(int codes, int expected) => new(codes, expected, 0, 0, 0, 0, 0, false, 0);

    [Fact]
    public void MoreCodesThanTheCaseHoldsIsExtra()
    {
        Assert.True(Stats(13, 12).Extra);
        Assert.True(Stats(13, 12).Complete);   // every code read; the extra one fails the case on its own
        Assert.False(Stats(12, 12).Extra);
        Assert.False(Stats(11, 12).Extra);
        Assert.False(Stats(40, 0).Extra);      // count unknown: no upper limit
    }

    [Fact]
    public void ACodeSeenMoreOftenThanItsLabelCarriesIsExtra()
    {
        Assert.True((Stats(12, 12) with { CopiesOver = 1 }).Extra);  // an extra pack carrying a copy of a code in the case
        Assert.False((Stats(12, 12) with { ExtraUnchecked = 3 }).Extra); // reported, not judged
        Assert.False((Stats(12, 0) with { CopiesOver = 1 }).Extra);  // count unknown: nothing to compare with
    }
}
