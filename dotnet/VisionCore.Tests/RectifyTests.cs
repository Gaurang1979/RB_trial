using System;
using Xunit;

namespace VisionCore.Tests;

public class RectifyTests
{
    [Fact]
    public void DefaultParams_HaveZeroShear()
    {
        var opt = new RectifyOptions();
        var (kx, ky) = RectifyEngine.CalculateShear(opt);
        Assert.Equal(0.0f, kx);
        Assert.Equal(0.0f, ky);
    }

    [Fact]
    public void CalculateShear_MatchesAnalytical()
    {
        var opt = new RectifyOptions
        {
            LineSpeedMmS = 500.0f,
            MmPerPx = 0.1f,
            RowTimeUs = 20.0f,
            MotionAngleDeg = 0.0f
        };
        var (kx, ky) = RectifyEngine.CalculateShear(opt);
        Assert.Equal(0.1f, kx, precision: 5);
        Assert.Equal(0.0f, ky, precision: 5);
    }

    [Fact]
    public void CoordinateRoundTrip_IsExact()
    {
        var opt = new RectifyOptions
        {
            LineSpeedMmS = 750.0f,
            MmPerPx = 0.08f,
            RowTimeUs = 22.5f,
            RefY = 500.0f
        };
        float origX = 640.0f;
        float origY = 320.0f;

        var (rectX, rectY) = RectifyEngine.DistortedToRectified(origX, origY, opt);
        var (backX, backY) = RectifyEngine.RectifiedToDistorted(rectX, rectY, opt);

        Assert.Equal(origX, backX, precision: 2);
        Assert.Equal(origY, backY, precision: 2);
    }

    [Fact]
    public void RectifyInPlace_DoesNotCrash()
    {
        int w = 200;
        int h = 100;
        byte[] buffer = new byte[w * h];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 128;

        var opt = new RectifyOptions
        {
            LineSpeedMmS = 600.0f,
            MmPerPx = 0.1f,
            RowTimeUs = 22.5f
        };

        var report = RectifyEngine.RectifyInPlace(buffer, w, h, w, opt);
        Assert.True(report.MaxShiftPx > 0);
    }

    [Fact]
    public void RectifyTool_NominalConveyor_Passes()
    {
        int w = 100, h = 100;
        byte[] px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i % 256);
        var img = new GrayImage(px, w, h);

        var cfg = new RectifyToolConfig
        {
            Name = "RectifyTool1",
            LineSpeedMmS = 300.0f,
            MmPerPx = 0.08f,
            RowTimeUs = 22.5f,
            MaxAllowedShearDeg = 15.0f
        };

        using var tool = new RectifyTool(cfg);
        var batch = new BatchFields();
        var res = tool.Run(img, batch);

        Assert.True(res.Pass);
        Assert.NotNull(res.Values);
        Assert.True(res.Values.ContainsKey("shear_angle_deg"));
        Assert.True(res.Values.ContainsKey("max_shift_px"));
        Assert.True(res.Values.ContainsKey("rectify_ms"));
    }

    [Fact]
    public void RectifyTool_ExcessiveShear_FailsSafe()
    {
        int w = 100, h = 100;
        byte[] px = new byte[w * h];
        var img = new GrayImage(px, w, h);

        var cfg = new RectifyToolConfig
        {
            Name = "RectifyToolShearLimit",
            LineSpeedMmS = 4000.0f,
            MmPerPx = 0.01f,
            RowTimeUs = 100.0f,
            MaxAllowedShearDeg = 5.0f
        };

        using var tool = new RectifyTool(cfg);
        var batch = new BatchFields();
        var res = tool.Run(img, batch);

        Assert.False(res.Pass);
        Assert.Equal("explain.rectify.excessive_shear", res.Explain);
    }

    [Fact]
    public void RectifyTool_InvalidImage_FailsSafe()
    {
        var cfg = new RectifyToolConfig { Name = "InvalidImgTool" };
        using var tool = new RectifyTool(cfg);
        var batch = new BatchFields();

        var resNull = tool.Run(null!, batch);
        Assert.False(resNull.Pass);

        var tiny = new GrayImage(new byte[4], 2, 2);
        var resTiny = tool.Run(tiny, batch);
        Assert.False(resTiny.Pass);
    }
}
