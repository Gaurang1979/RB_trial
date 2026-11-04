using System;
using Xunit;

namespace VisionCore.Tests;

public class WaveletTests
{
    [Fact]
    public void Lifecycle_DefaultParameters_InitializesProperly()
    {
        using var engine = new WaveletEngine();
        Assert.Equal(2, engine.Levels);
        Assert.Equal(TextureOrientation.Auto, engine.TextureMode);
        Assert.Equal(0.85f, engine.SuppressionStrength);
    }

    [Fact]
    public void Process_BrushedMetalWithScratch_IsolatesDefect()
    {
        using var engine = new WaveletEngine(
            levels: 2,
            textureMode: TextureOrientation.Auto,
            suppressionStrength: 0.90f,
            scratchGain: 3.5f,
            defectContrastThresh: 30.0f,
            minDefectAreaPx: 10);

        const int width = 128;
        const int height = 128;
        byte[] src = new byte[width * height];

        // Synthesize horizontal grain
        for (int y = 0; y < height; y++)
        {
            float grain = 30.0f * MathF.Sin(2.0f * MathF.PI * y / 4.0f);
            for (int x = 0; x < width; x++)
            {
                src[y * width + x] = (byte)Math.Clamp(MathF.Round(140.0f + grain), 0.0f, 255.0f);
            }
        }

        // Add 1-px wide hairline scratch along x=64, y in [30..90]
        for (int y = 30; y <= 90; y++)
        {
            src[y * width + 64] = (byte)Math.Clamp(src[y * width + 64] - 45, 0, 255);
        }

        var result = engine.Process(src, width, height);

        Assert.True(result.IsDefective, "Hairline scratch should trigger NG");
        Assert.Equal(TextureOrientation.Horizontal, result.DetectedOrientation);
        Assert.True(result.DefectPixelCount >= 40, $"Expected >= 40 defect pixels, got {result.DefectPixelCount}");
        Assert.NotNull(result.Residual);
        Assert.NotNull(result.Mask);
    }

    [Fact]
    public void Process_CleanBrushedMetal_GivesZeroFalseRejects()
    {
        using var engine = new WaveletEngine(
            levels: 2,
            textureMode: TextureOrientation.Horizontal,
            suppressionStrength: 0.90f,
            defectContrastThresh: 35.0f,
            minDefectAreaPx: 5);

        const int width = 128;
        const int height = 128;
        byte[] src = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            float grain = 25.0f * MathF.Sin(2.0f * MathF.PI * y / 4.0f);
            for (int x = 0; x < width; x++)
            {
                src[y * width + x] = (byte)Math.Clamp(MathF.Round(135.0f + grain), 0.0f, 255.0f);
            }
        }

        var result = engine.Process(src, width, height);

        Assert.False(result.IsDefective, "Clean textured surface should NOT be flagged as defective");
        Assert.Equal(0, result.DefectPixelCount);
    }

    [Fact]
    public void Process_KnurledPinhole_FlagsPuncture()
    {
        using var engine = new WaveletEngine(
            levels: 3,
            textureMode: TextureOrientation.KnurledGrid,
            suppressionStrength: 0.85f,
            scratchGain: 3.5f,
            defectContrastThresh: 30.0f,
            minDefectAreaPx: 4);

        const int width = 128;
        const int height = 128;
        byte[] src = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float grid = 18.0f * MathF.Cos(2.0f * MathF.PI * x / 6.0f) +
                             18.0f * MathF.Cos(2.0f * MathF.PI * y / 6.0f);
                src[y * width + x] = (byte)Math.Clamp(MathF.Round(140.0f + grid), 0.0f, 255.0f);
            }
        }

        // Pinhole puncture
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx * dx + dy * dy <= 4)
                {
                    src[(50 + dy) * width + (50 + dx)] = 30;
                }
            }
        }

        var result = engine.Process(src, width, height);

        Assert.True(result.IsDefective, "Pinhole puncture in knurled seal must trigger NG");
        Assert.True(result.DefectPixelCount >= 5);
        Assert.NotNull(result.Mask);
        Assert.Equal((byte)255, result.Mask[50 * width + 50]);
    }

    [Fact]
    public void WaveletTool_CleanTexturedSurface_Passes()
    {
        const int width = 128;
        const int height = 128;
        byte[] src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            float grain = 20.0f * MathF.Sin(2.0f * MathF.PI * y / 4.0f);
            for (int x = 0; x < width; x++)
            {
                src[y * width + x] = (byte)Math.Clamp(MathF.Round(135.0f + grain), 0.0f, 255.0f);
            }
        }

        var grayImage = new GrayImage(src, width, height, width);
        var cfg = new WaveletToolConfig
        {
            Name = "Wavelet Scratch 1",
            Mode = WaveletInspectionMode.ScratchDetection,
            TextureMode = TextureOrientation.Horizontal,
            SuppressionStrength = 0.90f,
            DefectContrastThresh = 35.0f,
            MaxAllowedDefectPx = 0
        };

        using var tool = new WaveletTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.True(result.Pass);
        Assert.Equal(0, result.Values["defect_pixels"]);
        Assert.NotEmpty(result.Graphics);
    }

    [Fact]
    public void WaveletTool_ScratchOnBrushedMetal_FailsSafe()
    {
        const int width = 128;
        const int height = 128;
        byte[] src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            float grain = 30.0f * MathF.Sin(2.0f * MathF.PI * y / 4.0f);
            for (int x = 0; x < width; x++)
            {
                src[y * width + x] = (byte)Math.Clamp(MathF.Round(140.0f + grain), 0.0f, 255.0f);
            }
        }

        // Add 1-px wide scratch
        for (int y = 30; y <= 90; y++)
        {
            src[y * width + 64] = (byte)Math.Clamp(src[y * width + 64] - 50, 0, 255);
        }

        var grayImage = new GrayImage(src, width, height, width);
        var cfg = new WaveletToolConfig
        {
            Name = "Scratch Inspector",
            Mode = WaveletInspectionMode.ScratchDetection,
            TextureMode = TextureOrientation.Auto,
            SuppressionStrength = 0.90f,
            ScratchGain = 3.5f,
            DefectContrastThresh = 30.0f,
            MinDefectAreaPx = 10,
            MaxAllowedDefectPx = 0
        };

        using var tool = new WaveletTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.wavelet.scratch_detected", result.Explain);
        Assert.True(result.Values["defect_pixels"] >= 30);
    }

    [Fact]
    public void WaveletTool_PinholeOnPackaging_FailsSafe()
    {
        const int width = 128;
        const int height = 128;
        byte[] src = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float grid = 18.0f * MathF.Cos(2.0f * MathF.PI * x / 6.0f) +
                             18.0f * MathF.Cos(2.0f * MathF.PI * y / 6.0f);
                src[y * width + x] = (byte)Math.Clamp(MathF.Round(140.0f + grid), 0.0f, 255.0f);
            }
        }

        // Pinhole puncture
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx * dx + dy * dy <= 4)
                {
                    src[(50 + dy) * width + (50 + dx)] = 25;
                }
            }
        }

        var grayImage = new GrayImage(src, width, height, width);
        var cfg = new WaveletToolConfig
        {
            Name = "Pinhole Inspector",
            Mode = WaveletInspectionMode.PinholeDefect,
            TextureMode = TextureOrientation.KnurledGrid,
            DefectContrastThresh = 30.0f,
            MinDefectAreaPx = 4,
            MaxAllowedDefectPx = 0
        };

        using var tool = new WaveletTool(cfg);
        var result = tool.Run(grayImage, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.wavelet.pinhole_detected", result.Explain);
        Assert.True(result.Values["defect_pixels"] >= 4);
    }

    [Fact]
    public void WaveletTool_InvalidImage_FailsSafe()
    {
        var cfg = new WaveletToolConfig
        {
            Name = "Fail Safe Test"
        };

        using var tool = new WaveletTool(cfg);
        var tiny = new GrayImage(new byte[1], 1, 1, 1);
        var result = tool.Run(tiny, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.wavelet.scratch_detected", result.Explain);
    }
}
