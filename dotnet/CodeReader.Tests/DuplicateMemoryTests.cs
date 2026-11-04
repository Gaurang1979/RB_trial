using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class DuplicateMemoryTests
{
    [Fact]
    public void RemembersAcceptedCodes()
    {
        var m = new DuplicateMemory(10);
        m.Add(new[] { "A", "B" });
        Assert.Equal(new[] { "B" }, m.Duplicates(new[] { "C", "B" }));
        Assert.True(m.Contains("A"));
        Assert.False(m.Contains("C"));
    }

    [Fact]
    public void ForgetsTheOldestBeyondCapacity()
    {
        var m = new DuplicateMemory(3);
        m.Add(new[] { "1", "2", "3", "4" });
        Assert.Equal(3, m.Count);
        Assert.False(m.Contains("1"));
        Assert.True(m.Contains("4"));
    }

    [Fact]
    public void LoweringCapacityForgetsAtOnce()
    {
        var m = new DuplicateMemory(100);
        m.Add(Enumerable.Range(0, 50).Select(i => i.ToString()));
        m.Capacity = 10;
        Assert.Equal(10, m.Count);
        Assert.False(m.Contains("39"));
        Assert.True(m.Contains("40"));
    }

    [Fact]
    public void ACodeAddedTwiceStaysUntilBothAreForgotten()
    {
        var m = new DuplicateMemory(2);
        m.Add("X");
        m.Add("X");
        m.Add("Y");
        Assert.True(m.Contains("X")); // the second X is still among the last 2
        m.Add("Z");
        Assert.False(m.Contains("X"));
    }

    [Fact]
    public void ZeroCapacityRemembersNothing()
    {
        var m = new DuplicateMemory(0);
        m.Add("A");
        Assert.Empty(m.Duplicates(new[] { "A" }));
    }
}
