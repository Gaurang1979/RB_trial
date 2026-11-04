using System;
using System.IO;
using VisionCore;
using Xunit;

public class AnomalyTests
{
    [Fact]
    public void DefaultParams_AreCorrect()
    {
        var p = AnomalyEngine.DefaultParams();
        Assert.Equal(16, p.PatchSize);
        Assert.Equal(8, p.PatchStride);
        Assert.Equal(32, p.FeatureDim);
        Assert.True(p.CoresetRatio > 0.05f && p.CoresetRatio <= 0.25f);
        Assert.True(p.MaxCoresetSize >= 1000);
        Assert.Equal(16, p.HnswM);
        Assert.Equal(64, p.HnswEfConstruction);
        Assert.Equal(24, p.HnswEfSearch);
        Assert.Equal(1.0f, p.Sensitivity);
        Assert.Equal(20, p.MinDefectAreaPx);
    }

    [Fact]
    public void TeachAndInspect_DetectsScratchDefect()
    {
        var p = AnomalyEngine.DefaultParams();
        p.PatchSize = 16;
        p.PatchStride = 8;
        p.FeatureDim = 32;
        p.CoresetRatio = 0.25f;

        using var engine = new AnomalyEngine(p);

        const int w = 128, h = 128;
        var golden = new byte[w * h];

        // Normal textured surface (horizontal stripe grating)
        for (int y = 0; y < h; ++y)
        {
            byte val = (byte)(128 + 35 * Math.Sin(y * 0.4));
            for (int x = 0; x < w; ++x)
            {
                golden[y * w + x] = val;
            }
        }

        // Teach 3 golden variations
        for (int t = 0; t < 3; ++t)
        {
            var teachImg = (byte[])golden.Clone();
            for (int i = 0; i < teachImg.Length; ++i)
            {
                teachImg[i] = (byte)Math.Clamp(teachImg[i] + (t - 1) * 2, 0, 255);
            }
            engine.TeachImage(teachImg, w, h, w);
        }

        var (threshold, coresetCount) = engine.FinalizeTeach();
        Assert.True(threshold > 0.0f);
        Assert.True(coresetCount > 0);

        // Inspect normal image
        var normalResult = engine.Inspect(golden, w, h, w);
        Assert.Equal(AnomalyNative.AnomalyVerdict.Pass, normalResult.Verdict);
        Assert.True(normalResult.Score < 1.0f);

        // Create scratch defect (dark diagonal scratch across center)
        var defectImg = (byte[])golden.Clone();
        for (int i = -10; i <= 10; ++i)
        {
            int cx = 64 + i;
            int cy = 60 + i;
            if (cx >= 0 && cx < w && cy >= 0 && cy < h)
            {
                defectImg[cy * w + cx] = 10;
                defectImg[cy * w + Math.Min(w - 1, cx + 1)] = 10;
            }
        }

        var defectResult = engine.Inspect(defectImg, w, h, w);
        Assert.Equal(AnomalyNative.AnomalyVerdict.Defect, defectResult.Verdict);
        Assert.True(defectResult.Score >= 1.0f);
        Assert.True(defectResult.Defect.AreaPx > 0);
        Assert.True(defectResult.Defect.BBoxW > 0);
        Assert.True(defectResult.Defect.BBoxH > 0);
    }

    [Fact]
    public void SaveAndLoad_ModelRoundTrip()
    {
        var p = AnomalyEngine.DefaultParams();
        p.PatchSize = 16;
        p.PatchStride = 8;
        p.FeatureDim = 32;

        string tempPath = Path.Combine(Path.GetTempPath(), $"patchcore_test_{Guid.NewGuid():N}.vsan");

        try
        {
            const int w = 96, h = 96;
            var golden = new byte[w * h];
            Array.Fill(golden, (byte)150);

            using (var engine1 = new AnomalyEngine(p))
            {
                engine1.TeachImage(golden, w, h, w);
                engine1.FinalizeTeach();
                engine1.SaveModel(tempPath);
            }

            Assert.True(File.Exists(tempPath));
            Assert.True(new FileInfo(tempPath).Length > 0);

            using (var engine2 = AnomalyEngine.LoadModel(tempPath))
            {
                var res = engine2.Inspect(golden, w, h, w);
                Assert.Equal(AnomalyNative.AnomalyVerdict.Pass, res.Verdict);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void AnomalyTool_ExecutesThroughJobRunnerAndFailsSafe()
    {
        var cfg = new AnomalyToolConfig
        {
            Name = "Surface Anomaly",
            Camera = 0,
            MinDefectAreaPx = 15,
            Sensitivity = 1.0f
        };

        using var tool = new AnomalyTool(cfg);

        // 1. Un-modeled tool fails safe (never OK)
        var dummyImg = new GrayImage(new byte[64 * 64], 64, 64);
        var unTaughtRes = tool.Run(dummyImg, new BatchFields());
        Assert.False(unTaughtRes.Pass);
        Assert.Equal("explain.anomaly.no_model", unTaughtRes.Explain);

        // 2. Train model and attach
        var p = AnomalyEngine.DefaultParams();
        p.PatchSize = 16;
        p.PatchStride = 8;
        var engine = new AnomalyEngine(p);

        const int w = 64, h = 64;
        var golden = new byte[w * h];
        Array.Fill(golden, (byte)140);
        engine.TeachImage(golden, w, h, w);
        engine.FinalizeTeach();

        tool.SetEngine(engine);

        // 3. Normal image passes
        var passImg = new GrayImage(golden, w, h);
        var passRes = tool.Run(passImg, new BatchFields());
        Assert.True(passRes.Pass);
        Assert.Equal("OK", passRes.Summary);

        // 4. Injected scratch defect fails and draws defect bounding box
        var defectPixels = (byte[])golden.Clone();
        for (int y = 25; y < 35; ++y)
            for (int x = 25; x < 35; ++x)
                defectPixels[y * w + x] = 10;

        var defectImg = new GrayImage(defectPixels, w, h);
        var defectRes = tool.Run(defectImg, new BatchFields());
        Assert.False(defectRes.Pass);
        Assert.Contains("exceeds threshold", defectRes.Summary);
        Assert.Single(defectRes.Graphics);
        Assert.Equal(GraphicKind.Fail, defectRes.Graphics[0].Kind);
    }
}
