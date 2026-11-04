using System;
using VisionCore;
using Xunit;

public class DriftTests
{
    [Fact]
    public void DefaultParams_AreCorrect()
    {
        var p = DriftTracker.DefaultParams();
        Assert.Equal(50, p.WindowSize);
        Assert.True(p.WarnDistanceThreshold >= 4.0f);
        Assert.Equal(2.0f, p.DirtSharpnessSigma);
        Assert.Equal(2.0f, p.LightDropSigma);
        Assert.Equal(4, p.GridStep);
    }

    [Fact]
    public void FeatureExtraction_ComputesMetrics()
    {
        const int w = 100, h = 100;
        var img = new byte[w * h];
        Array.Fill(img, (byte)128);

        // Center square with contrast
        for (int y = 30; y < 70; ++y)
            for (int x = 30; x < 70; ++x)
                img[y * w + x] = 200;

        var feat = DriftTracker.ExtractFeatures(img, w, h, w, gridStep: 2);

        Assert.True(feat.MeanBrightness > 120.0f && feat.MeanBrightness < 160.0f);
        Assert.True(feat.ContrastStd > 10.0f);
        Assert.True(feat.Sharpness > 1.0f);
        Assert.True(feat.Uniformity > 0.5f);
    }

    [Fact]
    public void TeachingAndBaseline_TracksDrift()
    {
        var p = DriftTracker.DefaultParams();
        p.WindowSize = 10;
        p.WarnDistanceThreshold = 5.0f;

        using var tracker = new DriftTracker(p);

        // Teach 20 baseline samples
        var baseFeat = new DriftNative.Features
        {
            MeanBrightness = 140.0f,
            ContrastStd = 35.0f,
            Sharpness = 25.0f,
            Uniformity = 0.95f,
            SaturatedRatio = 0.001f,
            DarkFloor = 10.0f
        };

        for (int i = 0; i < 20; ++i)
        {
            var f = baseFeat;
            f.MeanBrightness += (i % 3) - 1.0f;
            tracker.TeachSample(f);
        }

        tracker.FinalizeTeach();

        // 5 normal parts
        for (int i = 0; i < 5; ++i)
        {
            var res = tracker.Update(baseFeat);
            Assert.Equal(DriftNative.DriftStatus.Ok, res.Status);
            Assert.True(res.Distance < 4.5f);
        }

        // 10 dim parts (simulated LED fading)
        var dimFeat = baseFeat;
        dimFeat.MeanBrightness = 100.0f; // Sizable drop

        (float dist, DriftNative.DriftStatus status, string diag) last = default;
        for (int i = 0; i < 10; ++i)
        {
            last = tracker.Update(dimFeat);
        }

        Assert.Equal(DriftNative.DriftStatus.WarnLightingDrop, last.status);
        Assert.True(last.dist >= 5.0f);
        Assert.Contains("LED", last.diag);
    }

    [Fact]
    public void DriftTool_NominalImage_Passes()
    {
        int w = 80, h = 80;
        byte[] px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(100 + (i % 50));
        var img = new GrayImage(px, w, h);

        var cfg = new DriftToolConfig
        {
            Name = "DriftMonitor1",
            WindowSize = 10,
            WarnDistanceThreshold = 5.0f,
            FailOnDrift = true
        };

        cfg.Teach(GoodParts(px, w, h));   // the baseline is taught, never taken from the line (AUD-01)
        using var tool = new DriftTool(cfg);
        var batch = new BatchFields();

        for (int i = 0; i < 5; i++)
        {
            var res = tool.Run(img, batch);
            Assert.True(res.Pass);
            Assert.NotNull(res.Values);
            Assert.True(res.Values.ContainsKey("drift_distance"));
            Assert.True(res.Values.ContainsKey("mean_brightness"));
            Assert.True(res.Values.ContainsKey("sharpness"));
        }
    }

    [Fact]
    public void DriftTool_LightingDrop_FailsSafe()
    {
        int w = 80, h = 80;
        byte[] nominalPx = new byte[w * h];
        Array.Fill(nominalPx, (byte)160);
        var nominalImg = new GrayImage(nominalPx, w, h);

        var cfg = new DriftToolConfig
        {
            Name = "DriftFailSafe",
            WindowSize = 8,
            WarnDistanceThreshold = 4.0f,
            LightDropSigma = 1.5f,
            FailOnDrift = true
        };

        cfg.Teach(GoodParts(nominalPx, w, h));
        using var tool = new DriftTool(cfg);
        var batch = new BatchFields();

        for (int i = 0; i < 5; i++)
        {
            var r = tool.Run(nominalImg, batch);
            Assert.True(r.Pass);
        }

        // Severely dimmed image (LED failure)
        byte[] dimPx = new byte[w * h];
        Array.Fill(dimPx, (byte)40);
        var dimImg = new GrayImage(dimPx, w, h);

        ToolResult? lastRes = null;
        for (int i = 0; i < 10; i++)
        {
            lastRes = tool.Run(dimImg, batch);
        }

        Assert.NotNull(lastRes);
        Assert.False(lastRes.Pass);
        Assert.Equal("explain.drift.lighting_drop", lastRes.Explain);
    }

    [Fact]
    public void DriftTool_AdvisoryMode_WarnsWithoutRejecting()
    {
        int w = 80, h = 80;
        byte[] nominalPx = new byte[w * h];
        Array.Fill(nominalPx, (byte)160);
        var nominalImg = new GrayImage(nominalPx, w, h);

        var cfg = new DriftToolConfig
        {
            Name = "DriftAdvisory",
            WindowSize = 8,
            WarnDistanceThreshold = 4.0f,
            FailOnDrift = false // advisory warning only
        };

        cfg.Teach(GoodParts(nominalPx, w, h));
        using var tool = new DriftTool(cfg);
        var batch = new BatchFields();

        for (int i = 0; i < 5; i++)
        {
            tool.Run(nominalImg, batch);
        }

        byte[] dimPx = new byte[w * h];
        Array.Fill(dimPx, (byte)40);
        var dimImg = new GrayImage(dimPx, w, h);

        ToolResult? lastRes = null;
        for (int i = 0; i < 10; i++)
        {
            lastRes = tool.Run(dimImg, batch);
        }

        Assert.NotNull(lastRes);
        Assert.True(lastRes.Pass); // Passes in advisory mode
        Assert.NotNull(lastRes.Explain);
    }

    [Fact]
    public void DriftTool_InvalidImage_FailsSafe()
    {
        var cfg = new DriftToolConfig { Name = "InvalidDrift" };
        using var tool = new DriftTool(cfg);
        var batch = new BatchFields();

        var resNull = tool.Run(null!, batch);
        Assert.False(resNull.Pass);
        Assert.Equal("explain.drift.invalid_image", resNull.Explain);

        var tiny = new GrayImage(new byte[4], 2, 2);
        var resTiny = tool.Run(tiny, batch);
        Assert.False(resTiny.Pass);
    }

    // five pictures of a good part with the small brightness spread of a real line
    internal static IEnumerable<GrayImage> GoodParts(byte[] px, int w, int h) =>
        Enumerable.Range(-2, 5).Select(d => new GrayImage(px.Select(v => (byte)Math.Clamp(v + d, 0, 255)).ToArray(), w, h));
}

