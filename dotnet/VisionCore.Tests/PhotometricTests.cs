using System;
using Xunit;

namespace VisionCore.Tests;

public class PhotometricTests
{
    [Fact]
    public void Lifecycle_DefaultParameters_InitializesProperly()
    {
        using var engine = new PhotometricEngine();
        Assert.Equal(4, engine.NumLights);
        Assert.Equal(140.0f, engine.ShapeContrastGain);
    }

    [Fact]
    public void Compute_DecouplesPrintFromRelief()
    {
        using var engine = new PhotometricEngine();

        const int width = 100;
        const int height = 100;
        const int size = width * height;

        byte[][] inputs = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            inputs[i] = new byte[size];
        }

        const float sin45 = 0.70710678f;
        const float cos45 = 0.70710678f;
        float[][] lights = new float[][]
        {
            new float[] { 0.0f, sin45, cos45 },   // Top
            new float[] { sin45, 0.0f, cos45 },   // Right
            new float[] { 0.0f, -sin45, cos45 },  // Bottom
            new float[] { -sin45, 0.0f, cos45 }   // Left
        };

        // Synthesize scene:
        // Background albedo = 160.
        // Region 1: Flat 2D print (barcode bar) at x in [20..30], y in [20..80], albedo = 40, normal = [0, 0, 1].
        // Region 2: 3D bump at center (70, 50), radius 20, height 8. Albedo = 160.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float albedo = 160.0f;
                float nx = 0.0f, ny = 0.0f, nz = 1.0f;

                if (x >= 20 && x <= 30 && y >= 20 && y <= 80)
                {
                    albedo = 40.0f;
                }

                float dx = x - 70.0f;
                float dy = y - 50.0f;
                float dist2 = dx * dx + dy * dy;
                if (dist2 < 400.0f)
                {
                    float dzdx = -2.0f * 8.0f * dx / 400.0f;
                    float dzdy = -2.0f * 8.0f * dy / 400.0f;
                    float len = MathF.Sqrt(dzdx * dzdx + dzdy * dzdy + 1.0f);
                    nx = -dzdx / len;
                    ny = -dzdy / len;
                    nz = 1.0f / len;
                }

                for (int k = 0; k < 4; k++)
                {
                    float dot = nx * lights[k][0] + ny * lights[k][1] + nz * lights[k][2];
                    if (dot < 0.0f) dot = 0.0f;
                    float val = Math.Clamp(albedo * dot, 0.0f, 255.0f);
                    inputs[k][y * width + x] = (byte)Math.Round(val);
                }
            }
        }

        var (albedoOut, shapeOut, normalMap) = engine.ComputeWithNormals(inputs, width, height);

        // 1. Albedo check:
        // Inside printed bar (x=25, y=50), albedo is dark ~40
        byte printAlbedo = albedoOut[50 * width + 25];
        Assert.InRange(printAlbedo, 30, 50);

        // On the 3D bump (x=70, y=50), albedo is ~160 (shading removed)
        byte bumpAlbedo = albedoOut[50 * width + 70];
        Assert.InRange(bumpAlbedo, 150, 170);

        // 2. Shape check:
        // Flat background (x=10, y=50) -> shape == 128
        Assert.Equal(128, shapeOut[50 * width + 10]);

        // Inside printed bar (x=25, y=50) -> shape == 128 (print stripped from shape!)
        Assert.Equal(128, shapeOut[50 * width + 25]);

        // On 3D bump (x=70, y=50) -> shape != 128 (convex curvature contrast!)
        Assert.NotEqual(128, shapeOut[50 * width + 70]);

        // 3. Lighting synthesis check:
        byte[] shadedRight = PhotometricEngine.SynthesizeLighting(albedoOut, normalMap, width, height, 0.0f);
        byte[] shadedLeft = PhotometricEngine.SynthesizeLighting(albedoOut, normalMap, width, height, 180.0f);

        // On right slope of bump (x=78, y=50), shadedRight > shadedLeft
        Assert.True(shadedRight[50 * width + 78] > shadedLeft[50 * width + 78]);
    }

    private static GrayImage CreateQuadrantMosaic(int subW, int subH, Func<int, int, (float Albedo, float Nx, float Ny, float Nz)> sceneFunc)
    {
        const float sin45 = 0.70710678f;
        const float cos45 = 0.70710678f;
        float[][] lights = new float[][]
        {
            new float[] { 0.0f, sin45, cos45 },   // Top
            new float[] { sin45, 0.0f, cos45 },   // Right
            new float[] { 0.0f, -sin45, cos45 },  // Bottom
            new float[] { -sin45, 0.0f, cos45 }   // Left
        };

        byte[][] subImages = new byte[4][];
        for (int k = 0; k < 4; k++) subImages[k] = new byte[subW * subH];

        for (int y = 0; y < subH; y++)
        {
            for (int x = 0; x < subW; x++)
            {
                var (albedo, nx, ny, nz) = sceneFunc(x, y);
                for (int k = 0; k < 4; k++)
                {
                    float dot = nx * lights[k][0] + ny * lights[k][1] + nz * lights[k][2];
                    if (dot < 0.0f) dot = 0.0f;
                    float val = Math.Clamp(albedo * dot, 0.0f, 255.0f);
                    subImages[k][y * subW + x] = (byte)Math.Round(val);
                }
            }
        }

        int totalW = subW * 2;
        int totalH = subH * 2;
        byte[] mosaic = new byte[totalW * totalH];

        for (int y = 0; y < subH; y++)
        {
            Buffer.BlockCopy(subImages[0], y * subW, mosaic, y * totalW, subW);
            Buffer.BlockCopy(subImages[1], y * subW, mosaic, y * totalW + subW, subW);
            Buffer.BlockCopy(subImages[2], y * subW, mosaic, (subH + y) * totalW, subW);
            Buffer.BlockCopy(subImages[3], y * subW, mosaic, (subH + y) * totalW + subW, subW);
        }

        return new GrayImage(mosaic, totalW, totalH, totalW);
    }

    [Fact]
    public void PhotometricTool_CurvatureRelief_PassesOnFlatSurface()
    {
        var img = CreateQuadrantMosaic(40, 40, (x, y) => (150.0f, 0.0f, 0.0f, 1.0f));

        var cfg = new PhotometricToolConfig
        {
            Name = "Surface Relief Check",
            Mode = PhotometricMode.CurvatureRelief,
            Layout = PhotometricLayout.Quadrant2x2,
            ReliefThreshold = 15,
            MaxAllowedDefectPx = 5
        };

        using var tool = new PhotometricTool(cfg);
        var result = tool.Run(img, new BatchFields());

        Assert.True(result.Pass);
        Assert.Equal(0, result.Values["defect_pixels"]);
        Assert.NotEmpty(result.Graphics);
    }

    [Fact]
    public void PhotometricTool_CurvatureRelief_FailsOnDentDefect()
    {
        var img = CreateQuadrantMosaic(60, 60, (x, y) =>
        {
            float dx = x - 30.0f;
            float dy = y - 30.0f;
            float dist2 = dx * dx + dy * dy;
            if (dist2 < 100.0f)
            {
                // Deep dent slope
                float dzdx = -2.0f * 6.0f * dx / 100.0f;
                float dzdy = -2.0f * 6.0f * dy / 100.0f;
                float len = MathF.Sqrt(dzdx * dzdx + dzdy * dzdy + 1.0f);
                return (150.0f, -dzdx / len, -dzdy / len, 1.0f / len);
            }
            return (150.0f, 0.0f, 0.0f, 1.0f);
        });

        var cfg = new PhotometricToolConfig
        {
            Name = "Dent Detector",
            Mode = PhotometricMode.CurvatureRelief,
            Layout = PhotometricLayout.Quadrant2x2,
            ReliefThreshold = 10,
            MaxAllowedDefectPx = 5
        };

        using var tool = new PhotometricTool(cfg);
        var result = tool.Run(img, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.photometric.surface_defect", result.Explain);
        Assert.True(result.Values["defect_pixels"] > 5);
    }

    [Fact]
    public void PhotometricTool_EmbossPresence_VerifiesFeature()
    {
        var img = CreateQuadrantMosaic(60, 60, (x, y) =>
        {
            float dx = x - 30.0f;
            float dy = y - 30.0f;
            float dist2 = dx * dx + dy * dy;
            if (dist2 < 100.0f)
            {
                float dzdx = -2.0f * 6.0f * dx / 100.0f;
                float dzdy = -2.0f * 6.0f * dy / 100.0f;
                float len = MathF.Sqrt(dzdx * dzdx + dzdy * dzdy + 1.0f);
                return (150.0f, -dzdx / len, -dzdy / len, 1.0f / len);
            }
            return (150.0f, 0.0f, 0.0f, 1.0f);
        });

        var cfg = new PhotometricToolConfig
        {
            Name = "Emboss Presence Tool",
            Mode = PhotometricMode.EmbossPresence,
            Layout = PhotometricLayout.Quadrant2x2,
            ReliefThreshold = 10,
            MinFeaturePx = 20,
            MaxFeaturePx = 500
        };

        using var tool = new PhotometricTool(cfg);
        var result = tool.Run(img, new BatchFields());

        Assert.True(result.Pass);
        Assert.True(result.Values["feature_pixels"] >= 20);
    }

    [Fact]
    public void PhotometricTool_InvalidImage_FailsSafe()
    {
        var cfg = new PhotometricToolConfig
        {
            Name = "Fail Safe Test"
        };

        using var tool = new PhotometricTool(cfg);
        var tinyImage = new GrayImage(new byte[1], 1, 1, 1);
        var result = tool.Run(tinyImage, new BatchFields());

        Assert.False(result.Pass);
        Assert.Equal("explain.photometric.surface_defect", result.Explain);
    }
}
