using System;
using VisionCore;
using Xunit;

public class InspectTests
{
    [Fact]
    public void DefaultParams_AreCorrect()
    {
        var p = VariationModel.DefaultParams();
        Assert.Equal(1.0f, p.Sensitivity);
        Assert.Equal(12.0f, p.BaseOffset);
        Assert.Equal(1.5f, p.EdgeTolerancePx);
        Assert.Equal(0.15f, p.EdgeMarginWeight);
        Assert.Equal(15, p.MinDefectAreaPx);
        Assert.Equal(1, p.NormalizeLighting);
    }

    [Fact]
    public void TeachAndInspect_DetectsDefects()
    {
        const int w = 64, h = 64;
        var p = VariationModel.DefaultParams();
        p.BaseOffset = 10.0f;
        p.MinDefectAreaPx = 10;

        using var model = new VariationModel(w, h, p);

        // Golden image with background 180 and central square 80
        var golden = new byte[w * h];
        Array.Fill(golden, (byte)180);
        for (int y = 20; y < 44; ++y)
            for (int x = 20; x < 44; ++x)
                golden[y * w + x] = 80;

        // Add 3 samples
        for (int i = 0; i < 3; ++i)
        {
            model.AddSample(golden, w, h, w);
        }

        int trained = model.FinalizeTeach();
        Assert.Equal(3, trained);

        // 1. Normal image passes
        var passRes = model.Run(golden, w, h, w);
        Assert.Equal(InspectNative.InspectVerdict.Pass, (InspectNative.InspectVerdict)passRes.Verdict);
        Assert.Equal(0, passRes.DefectCount);
        Assert.True(passRes.Confidence >= 75.0f);

        // 2. Defect image fails (punched dark void of 16 pixels at x=50, y=50)
        var defectImg = (byte[])golden.Clone();
        for (int y = 50; y < 54; ++y)
            for (int x = 50; x < 54; ++x)
                defectImg[y * w + x] = 20;

        var defectRes = model.Run(defectImg, w, h, w);
        Assert.Equal(InspectNative.InspectVerdict.Defect, (InspectNative.InspectVerdict)defectRes.Verdict);
        Assert.True(defectRes.DefectCount >= 1);
        Assert.Equal(16, defectRes.WorstDefect.AreaPx);
        Assert.Equal((int)InspectNative.DefectType.Dark, defectRes.WorstDefect.DefectType);
    }

    [Fact]
    public void EdgeAwareTolerance_SuppressesJitter()
    {
        const int w = 100, h = 100;
        var p = VariationModel.DefaultParams();
        p.EdgeTolerancePx = 2.0f;
        p.EdgeMarginWeight = 0.25f;
        p.BaseOffset = 10.0f;
        p.MinDefectAreaPx = 15;

        using var model = new VariationModel(w, h, p);

        // Sharp edge from x=0..49 (100) to x=50..99 (200)
        var golden = new byte[w * h];
        Array.Fill(golden, (byte)100);
        for (int y = 0; y < h; ++y)
            for (int x = 50; x < w; ++x)
                golden[y * w + x] = 200;

        for (int i = 0; i < 3; ++i)
        {
            model.AddSample(golden, w, h, w);
        }
        model.FinalizeTeach();

        // 1-pixel jittered edge at x=51
        var jittered = new byte[w * h];
        Array.Fill(jittered, (byte)100);
        for (int y = 0; y < h; ++y)
            for (int x = 51; x < w; ++x)
                jittered[y * w + x] = 200;

        var res = model.Run(jittered, w, h, w);
        Assert.Equal(InspectNative.InspectVerdict.Pass, (InspectNative.InspectVerdict)res.Verdict);
    }

    [Fact]
    public void InspectTool_ExecutesThroughJobRunnerAndFailsSafe()
    {
        var cfg = new InspectToolConfig
        {
            Name = "Surface Check",
            Camera = 0,
            MinDefectAreaPx = 10,
            BaseOffset = 10.0f
        };

        using var tool = new InspectTool(cfg);

        // 1. Un-modeled tool fails safe (never OK)
        var dummyImg = new GrayImage(new byte[64 * 64], 64, 64);
        var unTaughtRes = tool.Run(dummyImg, new BatchFields());
        Assert.False(unTaughtRes.Pass);
        Assert.Equal("explain.inspect.no_model", unTaughtRes.Explain);

        // 2. Train model and attach
        var p = VariationModel.DefaultParams();
        p.BaseOffset = 10.0f;
        p.MinDefectAreaPx = 10;
        var model = new VariationModel(64, 64, p);
        var golden = new byte[64 * 64];
        Array.Fill(golden, (byte)150);
        model.AddSample(golden, 64, 64, 64);
        model.FinalizeTeach();

        tool.SetModel(model);

        // 3. Normal image passes
        var passImg = new GrayImage(golden, 64, 64);
        var passRes = tool.Run(passImg, new BatchFields());
        Assert.True(passRes.Pass);
        Assert.Equal("OK", passRes.Summary);

        // 4. Defective image fails and draws defect graphics
        var defectPixels = (byte[])golden.Clone();
        for (int y = 20; y < 25; ++y)
            for (int x = 20; x < 25; ++x)
                defectPixels[y * 64 + x] = 20;

        var defectImg = new GrayImage(defectPixels, 64, 64);
        var defectRes = tool.Run(defectImg, new BatchFields());
        Assert.False(defectRes.Pass);
        Assert.Contains("Defect of", defectRes.Summary);
        Assert.Single(defectRes.Graphics);
        Assert.Equal(GraphicKind.Fail, defectRes.Graphics[0].Kind);
    }
}
