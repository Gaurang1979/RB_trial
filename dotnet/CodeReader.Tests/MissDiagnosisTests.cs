using System;
using System.Collections.Generic;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class MissDiagnosisTests
{
    private static MissDiagnosis Miss(MissReason reason, bool measured = true, float sharpness = 0.2f) =>
        new(new Region(10, 20, 100, 100), reason, measured, sharpness, 80, 120, 40, 0.01f, 0.3f, 3.6f, 3.6f, false, true, 0.6f, 85);

    // a case short by as many codes as it has unread regions
    private static DecodeResult Short(params MissDiagnosis[] misses) =>
        new(Array.Empty<DecodedCode>(), misses.Select(m => m.Box).ToList(),
            new DecodeStats(10, 10 + misses.Length, 10 + misses.Length, misses.Length, 10, 40, 50, false, 150))
        { MissInfo = misses };

    [Fact]
    public void EveryEngineReasonHasATagAndAFix()
    {
        // the C enum cr_miss_reason: 0 unknown .. 7 no finder, in this order
        var expected = new[] { "?", "CUT OFF", "GLARE", "LOW CONTRAST", "COVERED", "TOO SMALL", "BLUR", "NO FINDER" };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], MissDiagnosis.TagOf((MissReason)i));
            Assert.False(string.IsNullOrWhiteSpace(MissDiagnosis.FixOf((MissReason)i)));
        }
        Assert.Equal(8, Enum.GetValues(typeof(MissReason)).Length);
    }

    [Fact]
    public void TooltipCarriesTheNumbersAndTheReadCodesForComparison()
    {
        var t = Miss(MissReason.Blur).Tooltip;
        Assert.StartsWith("BLUR: ", t);
        Assert.Contains("sharpness 0.20 (read codes 0.60)", t);
        Assert.Contains("contrast 80 grey levels (read codes 85), light 120 / dark 40", t);
        Assert.Contains("saturated 1%, code texture 30%", t);
        Assert.Contains("module 3.6 px (product 3.6 px)", t);
        Assert.Contains("finder not found, quiet zone clear", t);
    }

    [Fact]
    public void UndiagnosedMissSaysSo()
    {
        var m = Miss(MissReason.Unknown, measured: false);
        Assert.Equal("?", m.Tag);
        Assert.Contains("Not diagnosed", m.Tooltip);
    }

    [Fact]
    public void TallyCountsReasonsOfShortCasesMostFrequentFirst()
    {
        var tally = new MissReasonTally();
        tally.Add(Short(Miss(MissReason.Blur), Miss(MissReason.TooSmall), Miss(MissReason.Blur)));
        tally.Add(Short(Miss(MissReason.Unknown), Miss(MissReason.Glare, measured: false)));
        tally.Add(Short()); // a complete case: nothing to count
        Assert.Equal(2, tally.Cases);
        Assert.Equal(1, tally.NotDiagnosed);
        var counts = tally.Counts();
        Assert.Equal((MissReason.Blur, 2), counts[0]);
        Assert.Equal(MissReason.Unknown, counts[^1].Reason);
        Assert.Equal(MissReason.Blur, tally.Top());
        tally.Reset();
        Assert.Empty(tally.Counts());
        Assert.Null(tally.Top());
    }

    [Fact]
    public void AdvisorNamesTheTopCauseOfUnreadCodes()
    {
        var advisor = new LineAdvisor();
        for (int i = 0; i < 10; i++) advisor.Add(Short(Miss(MissReason.Glare), Miss(MissReason.Glare)));
        var advice = advisor.Advise(new CameraSettings(), 200);
        var unreadable = Assert.Single(advice, a => a.Code == "unreadable");
        Assert.Contains("GLARE", unreadable.Message);
    }
}
