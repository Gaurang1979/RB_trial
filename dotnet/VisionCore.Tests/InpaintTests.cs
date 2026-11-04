using System;
using VisionCore;
using Xunit;

public class InpaintTests
{
    [Fact]
    public void DefaultParams_AreCorrect()
    {
        var p = InpaintEngine.DefaultParams();
        Assert.Equal(252, p.GlareThreshold);
        Assert.Equal(2, p.HaloRadius);
        Assert.Equal(4, p.InpaintRadius);
        Assert.Equal(0, p.Method); // Telea
        Assert.True(p.MaxGlarePct >= 30.0f);
    }

    [Fact]
    public void DetectGlare_FindsSaturatedPixels()
    {
        const int w = 50, h = 50;
        var img = new byte[w * h];
        Array.Fill(img, (byte)100);

        // Punch 9 glare pixels
        img[20 * w + 20] = 255;
        img[20 * w + 21] = 255;
        img[21 * w + 20] = 255;
        img[21 * w + 21] = 255;

        var mask = new byte[w * h];
        int res = InpaintEngine.DetectGlare(img, w, h, w, 252, 0, mask, w, out int count);

        Assert.Equal(0, res);
        Assert.Equal(4, count);
        Assert.Equal(255, mask[20 * w + 20]);
    }

    [Fact]
    public void Inpaint_RestoresLineContrastAcrossGlare()
    {
        const int w = 60, h = 60;
        var img = new byte[w * h];
        Array.Fill(img, (byte)210);

        // Horizontal black line across y=28..32
        for (int y = 28; y <= 32; ++y)
            for (int x = 0; x < w; ++x)
                img[y * w + x] = 25;

        // Glare highlight across line: x=27..33, y=26..34
        for (int y = 26; y <= 34; ++y)
            for (int x = 27; x <= 33; ++x)
                img[y * w + x] = 255;

        var dst = new byte[w * h];
        var p = InpaintEngine.DefaultParams();
        p.InpaintRadius = 5;
        p.HaloRadius = 1;

        int res = InpaintEngine.InpaintAuto(img, w, h, w, dst, w, out int glareCount, p);

        Assert.Equal(0, res);
        Assert.True(glareCount > 0);

        byte centerLine = dst[30 * w + 30];
        byte bg = dst[25 * w + 30];

        // Line must be dark enough and contrast margin >= 50
        Assert.True(centerLine <= 135);
        Assert.True(bg >= 180);
        Assert.True((int)bg - (int)centerLine >= 50);
    }

    [Fact]
    public void MassiveGlare_SafelySkipped()
    {
        const int w = 50, h = 50;
        var img = new byte[w * h];
        Array.Fill(img, (byte)255); // 100% glare
        var dst = new byte[w * h];

        var p = InpaintEngine.DefaultParams();
        p.MaxGlarePct = 30.0f;

        int res = InpaintEngine.InpaintAuto(img, w, h, w, dst, w, out int glareCount, p);

        Assert.Equal(1, res); // 1 = Safely skipped
        Assert.Equal(255, dst[25 * w + 25]);
    }

    [Fact]
    public void InpaintTool_ModerateGlare_RestoresAndPasses()
    {
        const int w = 60, h = 60;
        var img = new byte[w * h];
        Array.Fill(img, (byte)210);

        // Horizontal line
        for (int y = 28; y <= 32; ++y)
            for (int x = 0; x < w; ++x)
                img[y * w + x] = 25;

        // Glare highlight: 5x5 pixels (~0.7% area)
        for (int y = 28; y <= 32; ++y)
            for (int x = 28; x <= 32; ++x)
                img[y * w + x] = 255;

        var grayImage = new GrayImage(img, w, h, w);
        var cfg = new InpaintToolConfig
        {
            Name = "Glare Inpaint 1",
            GlareThreshold = 250,
            MaxAllowedGlarePct = 10.0f
        };

        using var tool = new InpaintTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.True(result.Pass);
        Assert.True(result.Values["glare_pixels"] >= 20);
        Assert.True(result.Values["glare_pct"] <= 10.0);
        Assert.NotEmpty(result.Graphics);
    }

    [Fact]
    public void InpaintTool_ExcessiveGlare_FailsSafe()
    {
        const int w = 50, h = 50;
        var img = new byte[w * h];
        Array.Fill(img, (byte)255); // 100% glare

        var grayImage = new GrayImage(img, w, h, w);
        var cfg = new InpaintToolConfig
        {
            Name = "Excessive Glare Test",
            MaxAllowedGlarePct = 25.0f
        };

        using var tool = new InpaintTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.inpaint.excessive_glare", result.Explain);
        Assert.True(result.Values["glare_pct"] > 25.0);
    }

    [Fact]
    public void InpaintTool_InvalidImage_FailsSafe()
    {
        var cfg = new InpaintToolConfig
        {
            Name = "Fail Safe Inpaint"
        };

        using var tool = new InpaintTool(cfg);
        var tiny = new GrayImage(new byte[1], 1, 1, 1);
        var result = tool.Run(tiny, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.inpaint.excessive_glare", result.Explain);
    }
}
