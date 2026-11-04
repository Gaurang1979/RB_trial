using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class AgentTests
{
    private static ProductMaster Product(LearningMode mode = LearningMode.Learning) => new()
    {
        ProductCode = "P1", ChildFormat = CodeFormat.QrCode, CodesPerCase = 48, Mode = mode,
    };

    private static DecodeStats Stats(int codes, int expected = 48, float ms = 100, float brightness = 170) =>
        new(codes, expected, 60, 0, 10, ms - 10, ms, false, brightness);

    private static DecodeResult Result(int codes, int misses = 0, float ms = 100, float brightness = 170) =>
        new(Array.Empty<DecodedCode>(), Enumerable.Repeat(new Region(0, 0, 10, 10), misses).ToList(),
            Stats(codes, 48, ms, brightness));

    [Fact]
    public void TunerFindsTheGammaThatReadsBest()
    {
        // simulated line: gamma 0.7 reads every code, others miss some
        var agent = new TuningAgent(seed: 7) { ExploreRate = 0.3 };
        var p = Product();
        for (int i = 0; i < 400; i++)
        {
            var s = agent.Next(p);
            int codes = Math.Abs(s.Gamma - 0.7f) < 0.01f ? 48 : 40;
            agent.Observe(Stats(codes), s.TimeBudgetMs);
        }
        var gamma = agent.Best().Single(b => b.Knob == "Gamma");
        Assert.Equal(0.7f, gamma.Value);
        Assert.Equal(0.7f, agent.Propose(p).Reader.Gamma);
    }

    [Fact]
    public void TunerLearnsWhereTheEnhancerRuns()
    {
        // simulated caps line: the enhancer early reads most codes, as on the 48-cap sample image
        var agent = new TuningAgent(seed: 11) { ExploreRate = 0.3 };
        var p = Product();
        for (int i = 0; i < 400; i++)
        {
            var s = agent.Next(p);
            int codes = s.Enhancer switch { EnhancerMode.Early => 35, EnhancerMode.Last => 33, _ => 25 };
            agent.Observe(Stats(codes), s.TimeBudgetMs);
        }
        Assert.Equal(EnhancerMode.Early, agent.Propose(p).Reader.Enhancer);
    }

    [Fact]
    public void ManualSettingsAreNotTouched()
    {
        var agent = new TuningAgent(seed: 5) { ExploreRate = 1 };
        var p = Product();
        p.Reader.Gamma = 0.42f;
        p.Reader.AutoKnobs.Remove("Gamma");
        for (int i = 0; i < 100; i++)
        {
            var s = agent.Next(p);
            Assert.Equal(0.42f, s.Gamma);
            agent.Observe(Stats(48), s.TimeBudgetMs);
        }
        Assert.Equal(0.42f, agent.Propose(p).Reader.Gamma);
        Assert.Equal(0, agent.Best().Single(b => b.Knob == "Gamma").Frames);
    }

    [Fact]
    public void LockedProductIsNeverChanged()
    {
        var agent = new TuningAgent(seed: 1) { ExploreRate = 1 };
        var p = Product(LearningMode.Locked);
        p.Reader.Gamma = 0.42f;
        for (int i = 0; i < 20; i++)
        {
            var s = agent.Next(p);
            Assert.Equal(0.42f, s.Gamma);
            agent.Observe(Stats(10), s.TimeBudgetMs);
        }
        Assert.Equal(0, agent.Frames);
    }

    [Fact]
    public void RewardPenalisesTime()
    {
        double fast = TuningAgent.Reward(Stats(48, ms: 50), 200, 0.2);
        double slow = TuningAgent.Reward(Stats(48, ms: 400), 200, 0.2);
        double missing = TuningAgent.Reward(Stats(24, ms: 50), 200, 0.2);
        Assert.True(fast > slow);
        Assert.True(slow > missing);
    }

    [Fact]
    public void LearningSurvivesRestart()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var a = new TuningAgent(seed: 3) { ExploreRate = 0.5 };
            for (int i = 0; i < 100; i++)
            {
                var s = a.Next(Product());
                a.Observe(Stats(s.Gamma > 0.9f ? 48 : 30), s.TimeBudgetMs);
            }
            a.Save(path);
            var b = new TuningAgent(seed: 4);
            b.Load(path);
            Assert.Equal(a.Frames, b.Frames);
            Assert.Equal(a.Best().Select(x => x.Value), b.Best().Select(x => x.Value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AdvisorSuggestsMoreExposureForDarkLabels()
    {
        var adv = new LineAdvisor();
        for (int i = 0; i < 10; i++) adv.Add(Result(40, misses: 8, brightness: 30));
        var advice = adv.Advise(new CameraSettings { ExposureUs = 2000 }, 200);
        var dark = advice.First();
        Assert.Equal("underexposed", dark.Code);
        Assert.InRange(dark.SuggestedExposureUs!.Value, 7000, 8000); // 2000 x min(4, 170/30)
        Assert.Contains(advice, a => a.Code == "unreadable");
        Assert.Equal(40.0 / 48, adv.ReadRate, 3);
    }

    [Fact]
    public void AdvisorSeparatesNotFoundFromSlow()
    {
        var adv = new LineAdvisor();
        for (int i = 0; i < 10; i++) adv.Add(Result(44, misses: 0, ms: 350));
        var codes = adv.Advise(new CameraSettings(), 200).Select(a => a.Code).ToList();
        Assert.Equal(new List<string> { "not-found", "slow" }, codes);
    }

    [Fact]
    public void AdvisorIsQuietWhenAllIsWell()
    {
        var adv = new LineAdvisor();
        for (int i = 0; i < 10; i++) adv.Add(Result(48));
        Assert.Empty(adv.Advise(new CameraSettings(), 200));
    }
}
