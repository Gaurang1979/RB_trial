using System;
using System.Linq;
using Xunit;

namespace VisionCore.Tests;

public class AutotuneToolTests
{
    [Fact]
    public void MountTilt_UniformGrid_ReturnsHighUniformity()
    {
        const int w = 90;
        const int h = 90;
        byte[] px = new byte[w * h];

        // Uniform checkerboard
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[y * w + x] = ((x / 3 + y / 3) % 2 == 0) ? (byte)210 : (byte)45;
            }
        }

        var rep = AutotuneEngine.CheckMountTilt(px, w, h);

        Assert.False(rep.IsMountTilted);
        Assert.True(rep.UniformityRatio >= 0.80f, $"Uniformity was {rep.UniformityRatio}");
        Assert.True(rep.MeanSharpness > 15.0f);
        Assert.Contains("Optimal", rep.TiltDirection);
    }

    [Fact]
    public void MountTilt_TiltedFocalPlane_FlagsScheimpflugTilt()
    {
        const int w = 90;
        const int h = 90;
        byte[] px = new byte[w * h];

        // Top third is sharp, bottom third is completely blurred / flat
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (y < 30)
                {
                    px[y * w + x] = ((x / 3) % 2 == 0) ? (byte)220 : (byte)40;
                }
                else
                {
                    px[y * w + x] = (byte)(128 + (x % 4));
                }
            }
        }

        var rep = AutotuneEngine.CheckMountTilt(px, w, h);

        Assert.True(rep.IsMountTilted);
        Assert.True(rep.UniformityRatio < 0.50f);
        Assert.Contains("Bottom Blur", rep.TiltDirection);
        Assert.NotEmpty(rep.Advice);
    }

    [Fact]
    public void LensDust_DirtyOptic_DetectsDustSpots()
    {
        const int w = 100;
        const int h = 100;
        byte[] px = new byte[w * h];
        Array.Fill(px, (byte)200);

        // Add 6 synthetic dust spots
        var spots = new[] { (20, 20), (35, 25), (70, 30), (25, 75), (60, 60), (80, 80) };
        foreach (var (sx, sy) in spots)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    px[(sy + dy) * w + (sx + dx)] = 40;
                }
            }
        }

        var rep = AutotuneEngine.CheckLensDust(px, w, h, dustThresholdDn: 25, maxAllowedSpots: 3);

        Assert.True(rep.IsLensContaminated);
        Assert.True(rep.DustSpotsFound >= 5);
        Assert.Contains("Lens hygiene", rep.Advice);
    }

    [Fact]
    public void StrobeMinusAmbient_CancelsSunlightAndCeilingLights()
    {
        const int w = 60;
        const int h = 60;
        byte[] ambient = new byte[w * h];
        Array.Fill(ambient, (byte)50); // Ambient light leakage

        byte[] strobe = new byte[w * h];
        Array.Fill(strobe, (byte)50);

        // Center feature illuminated by strobe pulse (+170 DN)
        for (int y = 20; y < 40; y++)
        {
            for (int x = 20; x < 40; x++)
            {
                strobe[y * w + x] = 220;
            }
        }

        var (clean, leakage) = AutotuneEngine.StrobeMinusAmbient(strobe, ambient, w, h);

        Assert.NotNull(clean);
        // Ambient background should be subtracted to 0
        Assert.Equal(0, clean[5 * w + 5]);
        // Target feature should be 220 - 50 = 170
        Assert.Equal(170, clean[30 * w + 30]);
        Assert.True(leakage > 0.0f);
    }

    [Fact]
    public void AnalyzeLighting_SolarIngress_RecommendsTrim()
    {
        const int w = 60;
        const int h = 60;
        byte[] px = new byte[w * h];
        Array.Fill(px, (byte)185); // High solar glare

        var rep = AutotuneEngine.AnalyzeLighting(px, w, h, targetMeanDn: 128.0f);

        Assert.True(rep.IsDaytime);
        Assert.True(rep.SuggestedExposureTrim < 0.80f, $"Trim was {rep.SuggestedExposureTrim}");
        Assert.Contains("Solar ingress", rep.LightingAdvice);
    }

    [Fact]
    public void ApplicatorGuidance_DriftingCap_CalculatesLeadScrewTurns()
    {
        // Cap at (100, 100), Mark at (108.5, 100) -> +8.5 px
        // 10 px/mm -> +0.85 mm offset
        // Screw pitch 0.5 mm/turn -> -1.70 turns CCW
        var adv = AutotuneEngine.CalculateApplicatorGuidance(
            capCx: 100.0f, capCy: 100.0f, capRadius: 40.0f,
            markCx: 108.5f, markCy: 100.0f, markRadius: 10.0f,
            pxPerMm: 10.0f, screwPitchMmPerTurn: 0.5f, maxAllowedEccentricityMm: 1.20f);

        Assert.Equal(0.85f, adv.DeltaXMm, 2);
        Assert.Equal(0.85f, adv.TotalEccentricityMm, 2);
        Assert.True(adv.IsDriftCritical);
        Assert.Equal(-1.70f, adv.XScrewTurns, 2);
        Assert.Contains("Screw X", adv.AdvisoryPill);
        Assert.Contains("CCW", adv.AdvisoryPill);
    }

    [Fact]
    public void AutotuneTool_Run_NominalImage_ProducesExpectedValues()
    {
        const int w = 90;
        const int h = 90;
        byte[] px = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[y * w + x] = ((x / 3 + y / 3) % 2 == 0) ? (byte)210 : (byte)45;
            }
        }

        var cfg = new AutotuneToolConfig
        {
            Name = "AutoTuneMain",
            CheckMountTilt = true,
            AnalyzeLighting = true,
            CheckLensDust = false,
            TrackApplicator = true,
            PxPerMm = 10.0f
        };

        using var tool = new AutotuneTool(cfg);
        var img = new GrayImage(px, w, h);
        var res = tool.Run(img, new BatchFields());

        Assert.True(res.Pass);
        Assert.Contains("autotune.uniformity", res.Values.Keys);
        Assert.Contains("autotune.mean_sharpness", res.Values.Keys);
        Assert.Contains("autotune.mean_luminance", res.Values.Keys);
        Assert.NotEmpty(res.Graphics);
    }
}
