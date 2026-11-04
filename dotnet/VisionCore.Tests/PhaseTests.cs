using System;
using Xunit;

namespace VisionCore.Tests;

public class PhaseTests
{
    private static byte[] CreateTestPattern(int width, int height, byte bg = 200, byte fg = 40)
    {
        byte[] img = new byte[width * height];
        Array.Fill(img, bg);

        // Outer border
        for (int y = 20; y < height - 20; y++)
        {
            for (int x = 20; x < width - 20; x++)
            {
                if (x <= 23 || x >= width - 24 || y <= 23 || y >= height - 24)
                {
                    img[y * width + x] = fg;
                }
            }
        }

        // Inner asymmetric cross and block
        int cx = width / 2;
        int cy = height / 2;
        for (int y = 20; y < height - 20; y++)
        {
            img[y * width + cx] = fg;
            img[y * width + cx + 1] = fg;
        }
        for (int x = 20; x < width - 20; x++)
        {
            img[cy * width + x] = fg;
            img[(cy + 1) * width + x] = fg;
        }

        // Solid rectangle in one quadrant to break 180-deg symmetry
        for (int y = cy + 5; y < cy + 25; y++)
        {
            for (int x = cx + 5; x < cx + 25; x++)
            {
                if (y < height && x < width) img[y * width + x] = fg;
            }
        }

        return img;
    }

    [Fact]
    public void Lifecycle_DefaultParameters_InitializesProperly()
    {
        using var engine = new PhaseEngine();
        Assert.True(engine.EstimateRotation);
        Assert.Equal(7.0f, engine.MinPsr);
        Assert.True(engine.ApplyHannWindow);
    }

    [Fact]
    public void CorrelateTranslation_SubPixelAccuracy()
    {
        const int width = 128;
        const int height = 128;
        byte[] refImg = CreateTestPattern(width, height);
        byte[] shifted = new byte[width * height];
        Array.Fill(shifted, (byte)200);

        // Integer shift dx = 5, dy = -4
        const int dx = 5;
        const int dy = -4;
        for (int y = 0; y < height; y++)
        {
            int srcY = y - dy;
            if (srcY < 0 || srcY >= height) continue;
            for (int x = 0; x < width; x++)
            {
                int srcX = x - dx;
                if (srcX < 0 || srcX >= width) continue;
                shifted[y * width + x] = refImg[srcY * width + srcX];
            }
        }

        var (shiftX, shiftY, score, psr) = PhaseEngine.CorrelateTranslation(refImg, shifted, width, height);

        Assert.InRange(shiftX, 4.8f, 5.2f);
        Assert.InRange(shiftY, -4.2f, -3.8f);
        Assert.True(psr >= 10.0f);
    }

    [Fact]
    public void CorrelateTranslation_ExtremeContrastFade()
    {
        const int width = 128;
        const int height = 128;
        byte[] refImg = CreateTestPattern(width, height, 210, 30);
        byte[] shiftedFaded = new byte[width * height];

        // Shift dx = -3, dy = +5 and compress contrast by 95%
        const int dx = -3;
        const int dy = 5;
        for (int y = 0; y < height; y++)
        {
            int srcY = y - dy;
            for (int x = 0; x < width; x++)
            {
                int srcX = x - dx;
                byte v = (srcY >= 0 && srcY < height && srcX >= 0 && srcX < width)
                    ? refImg[srcY * width + srcX]
                    : (byte)210;

                // Contrast fade: 5% original signal + 100 base offset
                float faded = v * 0.05f + 100.0f;
                shiftedFaded[y * width + x] = (byte)Math.Clamp(MathF.Round(faded), 0.0f, 255.0f);
            }
        }

        var (shiftX, shiftY, score, psr) = PhaseEngine.CorrelateTranslation(refImg, shiftedFaded, width, height);

        Assert.InRange(shiftX, -3.2f, -2.8f);
        Assert.InRange(shiftY, 4.8f, 5.2f);
        Assert.True(psr >= 8.0f);
    }

    [Fact]
    public void PhaseTool_NominalPattern_Passes()
    {
        const int width = 128;
        const int height = 128;
        byte[] refImg = CreateTestPattern(width, height);
        var tImg = new GrayImage(refImg, width, height);

        var cfg = new PhaseToolConfig
        {
            Name = "PhaseTool1",
            MinCorrelationScore = 0.15f,
            MinPsr = 7.0f,
            EstimateRotation = false
        };

        cfg.Teach(tImg);   // the reference is taught, never taken from a part (AUD-01)
        using var tool = new PhaseTool(cfg);

        var batch = new BatchFields();
        var res = tool.Run(tImg, batch);

        Assert.True(res.Pass);
        Assert.NotNull(res.Values);
        Assert.True(res.Values.ContainsKey("shift_x"));
        Assert.True(res.Values.ContainsKey("shift_y"));
        Assert.True(res.Values.ContainsKey("correlation_score"));
        Assert.True(res.Values.ContainsKey("psr"));
        Assert.InRange(res.Values["shift_x"], -0.2, 0.2);
        Assert.InRange(res.Values["shift_y"], -0.2, 0.2);
    }

    [Fact]
    public void PhaseTool_ExtremeContrastFade_Passes()
    {
        const int width = 128;
        const int height = 128;
        byte[] refImg = CreateTestPattern(width, height, 210, 30);
        var tImg = new GrayImage(refImg, width, height);

        var cfg = new PhaseToolConfig
        {
            Name = "PhaseFadeTool",
            MinCorrelationScore = 0.10f,
            MinPsr = 5.0f,
            EstimateRotation = false
        };

        cfg.Teach(tImg);   // the reference is taught, never taken from a part (AUD-01)
        using var tool = new PhaseTool(cfg);

        // 95% contrast compression
        byte[] faded = new byte[width * height];
        for (int i = 0; i < faded.Length; i++)
        {
            faded[i] = (byte)Math.Clamp(MathF.Round(refImg[i] * 0.05f + 100.0f), 0.0f, 255.0f);
        }
        var testImg = new GrayImage(faded, width, height);

        var batch = new BatchFields();
        var res = tool.Run(testImg, batch);

        Assert.True(res.Pass);
        Assert.InRange(res.Values["shift_x"], -0.2, 0.2);
        Assert.InRange(res.Values["shift_y"], -0.2, 0.2);
    }

    [Fact]
    public void PhaseTool_InvalidImage_FailsSafe()
    {
        var cfg = new PhaseToolConfig { Name = "InvalidPhaseTool" };
        using var tool = new PhaseTool(cfg);
        var batch = new BatchFields();

        var resNull = tool.Run(null!, batch);
        Assert.False(resNull.Pass);
        Assert.Equal("explain.phase.invalid_image", resNull.Explain);

        var tiny = new GrayImage(new byte[4], 2, 2);
        var resTiny = tool.Run(tiny, batch);
        Assert.False(resTiny.Pass);
    }
}
