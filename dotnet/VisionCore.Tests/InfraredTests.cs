using System;
using Xunit;

namespace VisionCore.Tests;

public class InfraredTests
{
    [Fact]
    public void FillLevel_AmberBottle_MeasuresMeniscusAndRejectsFoam()
    {
        const int width = 120;
        const int height = 150;
        byte[] nir = new byte[width * height];

        // Synthesize amber bottle under NIR backlighting:
        // Headspace: 210, Foam (40..55): 180, Liquid (55..150): 110
        for (int y = 0; y < height; y++)
        {
            byte val = 210;
            if (y >= 40 && y < 55) val = (byte)(180 + (y % 3) * 4);
            else if (y >= 55) val = 110;

            for (int x = 0; x < width; x++)
            {
                nir[y * width + x] = val;
            }
        }

        var result = InfraredEngine.MeasureFillLevel(
            nir, width, height,
            roiX: 20, roiY: 10, roiW: 80, roiH: 90,
            datumY: 150.0f,
            pxPerMm: 10.0f,
            targetFillMm: 9.5f,
            tolUnderfillMm: 1.0f,
            tolOverfillMm: 1.0f,
            ignoreFoam: true);

        Assert.True(result.IsOk);
        Assert.False(result.IsUnderfill);
        Assert.False(result.IsOverfill);
        Assert.InRange(result.MeniscusYPx, 54.0f, 56.0f);
        Assert.InRange(result.FillHeightMm, 9.35f, 9.65f);
        Assert.True(result.FoamThicknessMm > 0.8f);
    }

    [Fact]
    public void Metameric_DyeVsCarbon_VerifiesAuthenticCode()
    {
        const int width = 100;
        const int height = 100;
        byte[] vis = new byte[width * height];
        byte[] nir = new byte[width * height];
        Array.Fill(vis, (byte)220);
        Array.Fill(nir, (byte)220);

        // Decorative dye (disappears in NIR)
        for (int y = 20; y <= 60; y++)
        {
            for (int x = 10; x <= 40; x++)
            {
                vis[y * width + x] = 50;
                nir[y * width + x] = 220;
            }
        }

        // Carbon security ink (absorbs both)
        for (int y = 20; y <= 60; y++)
        {
            for (int x = 60; x <= 80; x++)
            {
                vis[y * width + x] = 40;
                nir[y * width + x] = 45;
            }
        }

        var result = InfraredEngine.VerifyMetameric(vis, nir, width, height, minSecurityAreaPx: 100);

        Assert.True(result.IsAuthentic);
        Assert.True(result.VerifiedInkPixels >= 800);
        Assert.True(result.TransparentDyePixels >= 1000);
        Assert.NotNull(result.SecurityMask);
        Assert.Equal((byte)255, result.SecurityMask[30 * width + 70]);
        Assert.Equal((byte)0, result.SecurityMask[30 * width + 25]);
    }

    [Fact]
    public void Moisture_SeamLiquidIngress_DetectsBreach()
    {
        const int width = 120;
        const int height = 60;
        byte[] swirLeak = new byte[width * height];
        Array.Fill(swirLeak, (byte)180);

        // Liquid channel spanning through flange at x=60
        for (int y = 0; y < height; y++)
        {
            swirLeak[y * width + 60] = 30;
            swirLeak[y * width + 61] = 30;
        }

        var result = InfraredEngine.DetectMoistureSeam(
            swirLeak, width, height,
            seamRoiX: 0, seamRoiY: 0, seamRoiW: width, seamRoiH: height,
            absorptionThresh: 40.0f,
            minSafeBarrierPx: 25.0f);

        Assert.True(result.HasCompleteBreach);
        Assert.False(result.IsSealOk);
        Assert.Equal(0.0f, result.MinRemainingBarrierPx);
    }

    [Fact]
    public void Cracks_SolarCellEL_FlagsFracture()
    {
        const int width = 120;
        const int height = 120;
        byte[] el = new byte[width * height];
        Array.Fill(el, (byte)160);

        // Fracture line
        for (int i = 0; i <= 40; i++)
        {
            el[(30 + i) * width + (30 + i)] = 50;
        }

        var result = InfraredEngine.ExtractCracks(el, width, height, crackResponseThresh: 15.0f);

        Assert.True(result.IsCracked);
        Assert.True(result.CrackPixelCount >= 25);
        Assert.NotNull(result.CrackMask);
        Assert.Equal((byte)255, result.CrackMask[50 * width + 50]);
    }

    [Fact]
    public void InfraredTool_FillLevel_PassesAndFailsUnderfill()
    {
        const int width = 100;
        const int height = 120;
        byte[] goodImg = new byte[width * height];

        // Air above y=55 (180), liquid below y=55 (30)
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                goodImg[y * width + x] = (byte)(y < 55 ? 180 : 30);

        var cfg = new InfraredToolConfig
        {
            Name = "Bottle Fill",
            Mode = InfraredInspectionMode.FillLevel,
            DatumY = 110.0f,
            PxPerMm = 5.0f,
            TargetFillMm = 11.0f, // (110 - 55) / 5 = 11.0 mm
            TolUnderfillMm = 1.0f,
            TolOverfillMm = 1.0f
        };

        using var tool = new InfraredTool(cfg);
        var batch = new BatchFields();

        var resGood = tool.Run(new GrayImage(goodImg, width, height), batch);
        Assert.True(resGood.Pass);
        Assert.InRange(resGood.Values["fill_height_mm"], 10.8, 11.3);

        // Underfilled bottle (liquid below y=85 -> only 5mm)
        byte[] underImg = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                underImg[y * width + x] = (byte)(y < 85 ? 180 : 30);

        var resUnder = tool.Run(new GrayImage(underImg, width, height), batch);
        Assert.False(resUnder.Pass);
        Assert.Equal("explain.infrared.underfill", resUnder.Explain);
    }

    [Fact]
    public void InfraredTool_MoistureBarrier_DetectsBreach()
    {
        const int width = 100;
        const int height = 50;
        byte[] goodImg = new byte[width * height];
        Array.Fill(goodImg, (byte)210);

        var cfg = new InfraredToolConfig
        {
            Name = "Moisture Seam",
            Mode = InfraredInspectionMode.MoistureBarrier,
            AbsorptionThresh = 40.0f,
            MinSafeBarrierPx = 15.0f
        };

        using var tool = new InfraredTool(cfg);
        var batch = new BatchFields();

        var resGood = tool.Run(new GrayImage(goodImg, width, height), batch);
        Assert.True(resGood.Pass);

        // Leak channel
        byte[] leakImg = (byte[])goodImg.Clone();
        for (int y = 0; y < height; y++)
            for (int x = 45; x <= 55; x++)
                leakImg[y * width + x] = 20;

        var resLeak = tool.Run(new GrayImage(leakImg, width, height), batch);
        Assert.False(resLeak.Pass);
        Assert.Equal("explain.infrared.barrier_breach", resLeak.Explain);
    }

    [Fact]
    public void InfraredTool_SolarCell_ExtractsMicroCrack()
    {
        const int width = 120;
        const int height = 120;
        byte[] cleanCell = new byte[width * height];
        Array.Fill(cleanCell, (byte)160);

        var cfg = new InfraredToolConfig
        {
            Name = "Solar EL Wafer",
            Mode = InfraredInspectionMode.CrackDetection,
            CrackResponseThresh = 15.0f
        };

        using var tool = new InfraredTool(cfg);
        var batch = new BatchFields();

        var resClean = tool.Run(new GrayImage(cleanCell, width, height), batch);
        Assert.True(resClean.Pass);

        // Crack line
        byte[] crackedCell = (byte[])cleanCell.Clone();
        for (int i = 0; i <= 40; i++)
            crackedCell[(30 + i) * width + (30 + i)] = 50;

        var resCracked = tool.Run(new GrayImage(crackedCell, width, height), batch);
        Assert.False(resCracked.Pass);
        Assert.Equal("explain.infrared.crack_detected", resCracked.Explain);
    }
}
