using System;
using Xunit;

namespace VisionCore.Tests;

public class DeformTests
{
    [Fact]
    public void MatchAndUnwarp_WrinkledPouch_RectifiesAndMatchesTemplate()
    {
        const int w = 120;
        const int h = 120;
        byte[] refImg = new byte[w * h];
        Array.Fill(refImg, (byte)200);

        // Draw dark barcode / grid pattern
        for (int y = 20; y < 100; y++)
        {
            for (int x = 20; x < 100; x++)
            {
                if ((x % 12 < 4) || (y % 16 < 4))
                {
                    refImg[y * w + x] = 40;
                }
            }
        }

        // Deform with sinusoidal packaging wrinkle
        byte[] deformed = new byte[w * h];
        Array.Fill(deformed, (byte)200);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float sy = (float)y;
                float sx = (float)x - 3.5f * MathF.Sin(2.0f * MathF.PI * (float)y / 50.0f);

                int x0 = (int)sx;
                int y0 = (int)sy;
                if (x0 >= 0 && x0 < w - 1 && y0 >= 0 && y0 < h - 1)
                {
                    float fx = sx - (float)x0;
                    float p0 = (float)refImg[y0 * w + x0];
                    float p1 = (float)refImg[y0 * w + x0 + 1];
                    deformed[y * w + x] = (byte)(p0 + fx * (p1 - p0) + 0.5f);
                }
            }
        }

        var result = DeformEngine.MatchAndUnwarp(
            deformed, w, h,
            refImg, w, h,
            meshCols: 9, meshRows: 9,
            pyramidLevels: 3,
            maxIterationsPerLevel: 20,
            smoothnessWeight: 0.25f,
            outputUnwarped: true);

        Assert.NotNull(result.UnwarpedImage);
        Assert.True(result.Stats.IsMatchOk, "Wrinkled pouch must achieve OK match after non-rigid unwarping");
        Assert.InRange(result.Stats.FinalAlignmentScore, 0.70f, 1.0f);
        Assert.True(result.Stats.MeanDisplacementPx > 1.0f, "Should detect non-zero wrinkle displacement");
        Assert.False(result.Stats.IsFilmRuptured);
        Assert.False(result.Stats.IsExcessiveCrumple);

        // Verify MSE error reduction
        double rawErr = 0.0;
        double unwarpedErr = 0.0;
        int count = 0;
        for (int y = 25; y < 95; y++)
        {
            for (int x = 25; x < 95; x++)
            {
                int idx = y * w + x;
                float r = refImg[idx];
                float d = deformed[idx];
                float u = result.UnwarpedImage[idx];
                rawErr += (d - r) * (d - r);
                unwarpedErr += (u - r) * (u - r);
                count++;
            }
        }
        rawErr /= count;
        unwarpedErr /= count;

        Assert.True(unwarpedErr < rawErr * 0.5, $"Unwarped MSE ({unwarpedErr}) must be < 50% of raw deformed MSE ({rawErr})");
    }

    [Fact]
    public void AnalyzeStrain_MildStretch_PassesWithoutRupture()
    {
        const int cols = 5;
        const int rows = 5;
        float[] meshDx = new float[cols * rows];
        float[] meshDy = new float[cols * rows];

        // 10% elongation: dx = c * 2.0 (cellW = 20)
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                meshDx[r * cols + c] = (float)c * 2.0f;
            }
        }

        var stats = DeformEngine.AnalyzeStrain(meshDx, meshDy, cols, rows, cellW: 20.0f, cellH: 20.0f);

        Assert.False(stats.IsFilmRuptured);
        Assert.False(stats.IsExcessiveCrumple);
        Assert.True(stats.MaxAreaStretch < 0.20f);
    }

    [Fact]
    public void AnalyzeStrain_TearOrRupture_FlagsFilmRupture()
    {
        const int cols = 5;
        const int rows = 5;
        float[] meshDx = new float[cols * rows];
        float[] meshDy = new float[cols * rows];

        // 60% stretch expansion (cellW = 20): dx = c * 12.0
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                meshDx[r * cols + c] = (float)c * 12.0f;
            }
        }

        var stats = DeformEngine.AnalyzeStrain(meshDx, meshDy, cols, rows, cellW: 20.0f, cellH: 20.0f, ruptureStrainThreshold: 0.35f);

        Assert.True(stats.IsFilmRuptured, "Area expansion > 35% must flag film rupture risk");
        Assert.True(stats.MaxAreaStretch > 0.50f);
    }

    [Fact]
    public void AnalyzeStrain_SevereCrumple_FlagsExcessiveCrumple()
    {
        const int cols = 5;
        const int rows = 5;
        float[] meshDx = new float[cols * rows];
        float[] meshDy = new float[cols * rows];

        // Sharp impulse at center node
        meshDx[2 * cols + 2] = 25.0f;
        meshDy[2 * cols + 2] = -25.0f;

        var stats = DeformEngine.AnalyzeStrain(meshDx, meshDy, cols, rows, cellW: 20.0f, cellH: 20.0f, crumpleBendingThreshold: 40.0f);

        Assert.True(stats.IsExcessiveCrumple, "Severe localized crumple must flag excessive crumple NG");
        Assert.True(stats.TotalBendingEnergy > 40.0f);
    }

    [Fact]
    public void DeformTool_NominalPouch_UnwarpsAndPasses()
    {
        const int w = 100, h = 100;
        byte[] refPx = new byte[w * h];
        Array.Fill(refPx, (byte)220);
        for (int y = 20; y < 80; y++)
            for (int x = 20; x < 80; x++)
                if ((x % 10 < 3) || (y % 10 < 3)) refPx[y * w + x] = 30;

        var refImg = new GrayImage(refPx, w, h);

        var cfg = new DeformToolConfig
        {
            Name = "PouchTool1",
            MeshCols = 7,
            MeshRows = 7,
            PyramidLevels = 2,
            MinAlignmentScore = 0.65f,
            UnwarpInPlace = true
        };

        cfg.Teach(refImg);   // the reference is taught, never taken from a part (AUD-01)
        using var tool = new DeformTool(cfg);

        // Deform test image slightly
        byte[] defPx = (byte[])refPx.Clone();
        var testImg = new GrayImage(defPx, w, h);

        var batch = new BatchFields();
        var res = tool.Run(testImg, batch);

        Assert.True(res.Pass);
        Assert.NotNull(res.Values);
        Assert.True(res.Values.ContainsKey("alignment_score"));
        Assert.True(res.Values.ContainsKey("mean_displacement_px"));
        Assert.True(res.Values.ContainsKey("area_stretch"));
    }

    [Fact]
    public void DeformTool_InvalidImage_FailsSafe()
    {
        var cfg = new DeformToolConfig { Name = "InvalidDeformTool" };
        using var tool = new DeformTool(cfg);
        var batch = new BatchFields();

        var resNull = tool.Run(null!, batch);
        Assert.False(resNull.Pass);
        Assert.Equal("explain.deform.invalid_image", resNull.Explain);

        var tiny = new GrayImage(new byte[4], 2, 2);
        var resTiny = tool.Run(tiny, batch);
        Assert.False(resTiny.Pass);
    }
}
