using System;
using Xunit;

namespace VisionCore.Tests;

public class PolarTests
{
    [Fact]
    public void ProcessChannels_SpecularGlare_EliminatesHotspotAndExtractsDolp()
    {
        const int width = 100;
        const int height = 100;
        int size = width * height;

        byte[] i0 = new byte[size];
        byte[] i45 = new byte[size];
        byte[] i90 = new byte[size];
        byte[] i135 = new byte[size];

        // Background: unpolarized diffuse reflectance (180)
        Array.Fill(i0, (byte)180);
        Array.Fill(i45, (byte)180);
        Array.Fill(i90, (byte)180);
        Array.Fill(i135, (byte)180);

        // Center specular glare hotspot: strongly polarized horizontally (I0=250, I90=100)
        for (int y = 40; y < 60; y++)
        {
            for (int x = 40; x < 60; x++)
            {
                int idx = y * width + x;
                i0[idx] = 250;
                i45[idx] = 175;
                i90[idx] = 100;
                i135[idx] = 175;
            }
        }

        var result = PolarEngine.ProcessChannels(
            i0, i45, i90, i135,
            width, height,
            glareSuppressAlpha: 1.0f,
            specularGain: 2.0f,
            dolpDefectThresh: 0.40f,
            outputDiffuse: true,
            outputSpecular: true,
            outputDolp: true,
            outputAolp: true);

        Assert.NotNull(result.Diffuse);
        Assert.NotNull(result.Specular);
        Assert.NotNull(result.Dolp);
        Assert.NotNull(result.Aolp);

        // Blinding glare at center (250) must be suppressed down to base diffuse reflectance (<= 110)
        int centerIdx = 50 * width + 50;
        Assert.True(result.Diffuse[centerIdx] <= 110, $"Expected diffuse <= 110, got {result.Diffuse[centerIdx]}");

        // Unpolarized background must retain its natural intensity (~180)
        int bgIdx = 10 * width + 10;
        Assert.InRange(result.Diffuse[bgIdx], 175, 185);

        // DoLP in glare area must be high (~0.428 -> ~109 in byte scale, > 0.40 in float scale)
        Assert.True(result.MaxDolp >= 0.40f, $"Expected MaxDolp >= 0.40, got {result.MaxDolp}");
        Assert.True(result.GlarePixelCount >= 350, $"Expected >= 350 glare pixels, got {result.GlarePixelCount}");
    }

    [Fact]
    public void ProcessMosaic_SonyPolarsens_DemuxesAndDecouples()
    {
        const int width = 120;
        const int height = 120;
        byte[] raw = new byte[width * height];

        // Fill background with unpolarized 160
        Array.Fill(raw, (byte)160);

        // Synthesize glare on 2x2 superpixels in region [30..50, 30..50]:
        // Sony Polarsens layout: (2y, 2x)=0 deg, (2y, 2x+1)=45 deg, (2y+1, 2x)=135 deg, (2y+1, 2x+1)=90 deg
        for (int y = 30; y < 50; y += 2)
        {
            for (int x = 30; x < 50; x += 2)
            {
                raw[y * width + x] = 250;           // I0
                raw[y * width + (x + 1)] = 175;     // I45
                raw[(y + 1) * width + x] = 175;     // I135
                raw[(y + 1) * width + (x + 1)] = 90;// I90
            }
        }

        var result = PolarEngine.ProcessMosaic(
            raw, width, height,
            glareSuppressAlpha: 1.0f,
            specularGain: 2.0f,
            dolpDefectThresh: 0.40f,
            outputDiffuse: true,
            outputSpecular: true,
            outputDolp: true,
            outputAolp: true);

        int outW = width / 2;
        int outH = height / 2;
        Assert.NotNull(result.Diffuse);
        Assert.Equal(outW * outH, result.Diffuse.Length);

        // Center superpixel (y=20, x=20 in output coords) should have reduced glare
        int centerOutIdx = 20 * outW + 20;
        Assert.True(result.Diffuse[centerOutIdx] <= 110, $"Expected diffuse <= 110, got {result.Diffuse[centerOutIdx]}");

        // Background superpixel (y=5, x=5) should be intact ~160
        int bgOutIdx = 5 * outW + 5;
        Assert.InRange(result.Diffuse[bgOutIdx], 155, 165);
        Assert.True(result.MaxDolp >= 0.40f);
    }

    [Fact]
    public void ProcessChannels_StressBirefringence_FlagsStressConcentration()
    {
        const int width = 80;
        const int height = 80;
        int size = width * height;

        byte[] i0 = new byte[size];
        byte[] i45 = new byte[size];
        byte[] i90 = new byte[size];
        byte[] i135 = new byte[size];

        // Background: unpolarized 120
        Array.Fill(i0, (byte)120);
        Array.Fill(i45, (byte)120);
        Array.Fill(i90, (byte)120);
        Array.Fill(i135, (byte)120);

        // Region A (x: 20..39, y: 20..60): Vertical polarization (I0=40, I90=200 -> AoLP = 90 deg)
        for (int y = 20; y <= 60; y++)
        {
            for (int x = 20; x < 40; x++)
            {
                int idx = y * width + x;
                i0[idx] = 40;
                i45[idx] = 120;
                i90[idx] = 200;
                i135[idx] = 120;
            }
        }

        // Region B (x: 40..60, y: 20..60): Horizontal polarization (I0=200, I90=40 -> AoLP = 0 deg)
        for (int y = 20; y <= 60; y++)
        {
            for (int x = 40; x <= 60; x++)
            {
                int idx = y * width + x;
                i0[idx] = 200;
                i45[idx] = 120;
                i90[idx] = 40;
                i135[idx] = 120;
            }
        }

        var result = PolarEngine.ProcessChannels(
            i0, i45, i90, i135,
            width, height,
            stressGradientThresh: 35.0f,
            outputAolp: true);

        Assert.True(result.IsStressDefective, "Should flag stress defect at the sharp polarization boundary");
        Assert.True(result.MaxStressGradient >= 35.0f, $"Expected MaxStressGradient >= 35, got {result.MaxStressGradient}");
    }

    [Fact]
    public void PolarTool_GlareRemoval_PassesWhenWithinLimit()
    {
        const int width = 100;
        const int height = 100;
        byte[] raw = new byte[width * height];
        Array.Fill(raw, (byte)150);

        var grayImage = new GrayImage(raw, width, height, width);
        var cfg = new PolarToolConfig
        {
            Name = "Polar Test 1",
            Mode = PolarInspectionMode.GlareRemoval,
            MaxAllowedGlarePct = 5.0f
        };

        var tool = new PolarTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.True(result.Pass);
        Assert.Contains("glare_pixels", result.Values.Keys);
        Assert.Contains("glare_pct", result.Values.Keys);
        Assert.True(result.Values["glare_pct"] <= 5.0);
        Assert.NotEmpty(result.Graphics);
    }

    [Fact]
    public void PolarTool_GlareRemoval_FailsWhenExcessiveGlare()
    {
        const int width = 100;
        const int height = 100;
        byte[] raw = new byte[width * height];
        Array.Fill(raw, (byte)150);

        // Inject high polarization glare across large region (30x30 superpixels = 60x60 raw)
        for (int y = 20; y < 80; y += 2)
        {
            for (int x = 20; x < 80; x += 2)
            {
                raw[y * width + x] = 250;           // I0
                raw[y * width + (x + 1)] = 175;     // I45
                raw[(y + 1) * width + x] = 175;     // I135
                raw[(y + 1) * width + (x + 1)] = 80;// I90
            }
        }

        var grayImage = new GrayImage(raw, width, height, width);
        var cfg = new PolarToolConfig
        {
            Name = "Polar Test 2",
            Mode = PolarInspectionMode.GlareRemoval,
            MaxAllowedGlarePct = 2.0f,
            DolpDefectThresh = 0.35f
        };

        var tool = new PolarTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.polar.glare_excessive", result.Explain);
        Assert.True(result.Values["glare_pct"] > 2.0);
    }

    [Fact]
    public void PolarTool_StressBirefringence_DetectsFlaw()
    {
        const int width = 80;
        const int height = 80;
        byte[] raw = new byte[width * height];
        Array.Fill(raw, (byte)120);

        // Region A (x: 10..38, y: 10..70): Vertical polarization
        for (int y = 10; y < 70; y += 2)
        {
            for (int x = 10; x < 40; x += 2)
            {
                raw[y * width + x] = 40;            // I0
                raw[y * width + (x + 1)] = 120;     // I45
                raw[(y + 1) * width + x] = 120;     // I135
                raw[(y + 1) * width + (x + 1)] = 200;// I90
            }
        }

        // Region B (x: 40..70, y: 10..70): Horizontal polarization
        for (int y = 10; y < 70; y += 2)
        {
            for (int x = 40; x < 70; x += 2)
            {
                raw[y * width + x] = 200;           // I0
                raw[y * width + (x + 1)] = 120;     // I45
                raw[(y + 1) * width + x] = 120;     // I135
                raw[(y + 1) * width + (x + 1)] = 40;// I90
            }
        }

        var grayImage = new GrayImage(raw, width, height, width);
        var cfg = new PolarToolConfig
        {
            Name = "Polar Stress 1",
            Mode = PolarInspectionMode.StressBirefringence,
            StressGradientThresh = 30.0f
        };

        var tool = new PolarTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.polar.stress_flaw", result.Explain);
        Assert.True(result.Values["max_stress_gradient"] >= 30.0);
    }
}
