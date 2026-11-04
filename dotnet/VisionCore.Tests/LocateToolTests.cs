using System.Text.Json;
using Xunit;

namespace VisionCore.Tests;

public class LocateToolTests
{
    [Fact]
    public void Fixture_None_MapsPointUnchanged()
    {
        var fx = Fixture.None;
        var p = fx.Map(42.5f, 99.1f);
        Assert.Equal(42.5f, p.X, precision: 4);
        Assert.Equal(99.1f, p.Y, precision: 4);
    }

    [Fact]
    public void Fixture_Map_PreservesEuclideanDistanceAndCalculatesRotatedShift()
    {
        // Origin at (100, 100), detected at (200, 300), rotated 90 degrees CCW
        var fx = new Fixture(100f, 100f, 200f, 300f, 90f);

        // Point A: (100, 100) -> should map exactly to centre (200, 300)
        var pA = fx.Map(100f, 100f);
        Assert.Equal(200f, pA.X, precision: 3);
        Assert.Equal(300f, pA.Y, precision: 3);

        // Point B: (150, 100) -> 50 px along X from origin
        // In CCW 90 deg: mapped X = Cx + 0 = 200, mapped Y = Cy - s*50 = 300 - 50 = 250
        var pB = fx.Map(150f, 100f);
        Assert.Equal(200f, pB.X, precision: 3);
        Assert.Equal(250f, pB.Y, precision: 3);

        // Distance between A and B must remain exactly 50 px
        double distOriginal = Math.Sqrt(Math.Pow(150 - 100, 2) + Math.Pow(100 - 100, 2));
        double distMapped = Math.Sqrt(Math.Pow(pB.X - pA.X, 2) + Math.Pow(pB.Y - pA.Y, 2));
        Assert.Equal(distOriginal, distMapped, precision: 3);
    }

    [Fact]
    public void Fixture_InverseMap_ReversesForwardMappingAccurately()
    {
        var fx = new Fixture(145.2f, 210.8f, 380.4f, 512.6f, 37.5f);

        float originalX = 180.5f;
        float originalY = 240.2f;

        var mapped = fx.Map(originalX, originalY);
        var recovered = fx.InverseMap(mapped.X, mapped.Y);

        Assert.Equal(originalX, recovered.X, precision: 2);
        Assert.Equal(originalY, recovered.Y, precision: 2);
    }

    [Fact]
    public void Fixture_MapRoiBox_ComputesCorrectRotatedEnclosingBoundingBox()
    {
        var fx = new Fixture(50f, 50f, 100f, 100f, 45f);
        var box = new RoiBox(40, 40, 20, 20); // 20x20 box centered around (50, 50)

        var mappedBox = fx.Map(box, imageWidth: 500, imageHeight: 500);

        Assert.True(mappedBox.Width > 20, "Rotated box bounding box width must be larger than original square side");
        Assert.True(mappedBox.Height > 20, "Rotated box bounding box height must be larger than original square side");
        Assert.True(mappedBox.X >= 0 && mappedBox.Y >= 0);
    }

    [Fact]
    public void PatternQuality_Evaluate_ReturnsDegenerateForTinyRegions()
    {
        var img = new GrayImage(new byte[100 * 100], 100, 100, 100);
        var quality = PatternQuality.Evaluate(img, new RoiBox(10, 10, 12, 12));

        Assert.False(quality.IsSufficient);
        Assert.Equal("Degenerate", quality.Rating);
        Assert.Contains(quality.Warnings, w => w.Contains("minimum 16×16 pixels"));
    }

    [Fact]
    public void PatternQuality_Evaluate_IdentifiesOptimalTexture()
    {
        // Generate high-contrast synthetic checkerboard/cross-hatch with multi-directional edges
        int w = 80, h = 80;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Multi-frequency checker pattern
                bool c1 = ((x / 8) % 2) == 0;
                bool c2 = ((y / 8) % 2) == 0;
                bool diag = (((x + y) / 10) % 2) == 0;
                pixels[y * w + x] = (byte)(c1 ^ c2 ? (diag ? 240 : 190) : 25);
            }
        }

        var img = new GrayImage(pixels, w, h, w);
        var quality = PatternQuality.Evaluate(img, new RoiBox(4, 4, 72, 72));

        Assert.True(quality.IsSufficient);
        Assert.True(quality.Score >= 70.0, $"Quality score should be >= 70 for rich texture, got {quality.Score}");
        Assert.True(quality.EdgeCount >= 50, "Should detect multiple edge points");
        Assert.True(quality.OrientationEntropy >= 0.5, "Entropy should be high for multi-directional pattern");
        Assert.True(quality.Rating is "Optimal" or "Good");
    }

    [Fact]
    public void PatternQuality_Evaluate_WarnsWhenEdgesAreOneDimensional()
    {
        // Generate purely horizontal stripes (edges only in Y direction, gradient entirely vertical)
        int w = 60, h = 60;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            byte val = (byte)((y / 6 % 2 == 0) ? 220 : 30);
            for (int x = 0; x < w; x++)
            {
                pixels[y * w + x] = val;
            }
        }

        var img = new GrayImage(pixels, w, h, w);
        var quality = PatternQuality.Evaluate(img, new RoiBox(4, 4, 52, 52));

        Assert.Contains(quality.Warnings, w => w.Contains("mostly parallel"));
        Assert.True(quality.OrientationEntropy < 0.45, "1D parallel stripes should have low directional entropy");
    }

    [Fact]
    public void PatternQuality_Evaluate_FlatImageReturnsDegenerate()
    {
        int w = 50, h = 50;
        var pixels = new byte[w * h];
        Array.Fill(pixels, (byte)128); // uniform solid gray

        var img = new GrayImage(pixels, w, h, w);
        var quality = PatternQuality.Evaluate(img, new RoiBox(5, 5, 40, 40));

        Assert.Equal("Degenerate", quality.Rating);
        Assert.Equal(0, quality.EdgeCount);
        Assert.Contains(quality.Warnings, w => w.Contains("insufficient dynamic range"));
    }

    [Fact]
    public void LocateToolConfig_Serialization_PreservesAllShapeModelSettings()
    {
        var config = new LocateToolConfig
        {
            Name = "Locate Packaging Blister",
            Enabled = true,
            Camera = 1,
            SearchArea = new RoiBox(50, 60, 400, 300),
            MaxAngleDeg = 25.0f,
            MinScore = 0.75f,
            Metric = LocateMetric.IgnoreGlobalPolarity,
            SearchMode = LocateSearchMode.EdgeGeometric,
            PyramidLevels = 4,
            MinScale = 0.90f,
            MaxScale = 1.10f,
            OriginX = 250f,
            OriginY = 210f,
            PatternWidth = 80,
            PatternHeight = 60,
            Quality = new PatternQuality(92.4, 185, 0.88, 38.5, "Optimal", [])
        };

        var json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<LocateToolConfig>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Locate Packaging Blister", deserialized.Name);
        Assert.Equal(LocateMetric.IgnoreGlobalPolarity, deserialized.Metric);
        Assert.Equal(LocateSearchMode.EdgeGeometric, deserialized.SearchMode);
        Assert.Equal(4, deserialized.PyramidLevels);
        Assert.Equal(0.90f, deserialized.MinScale);
        Assert.Equal(1.10f, deserialized.MaxScale);
        Assert.Equal(25.0f, deserialized.MaxAngleDeg);
        Assert.Equal(0.75f, deserialized.MinScore);
        Assert.NotNull(deserialized.Quality);
        Assert.Equal("Optimal", deserialized.Quality.Rating);
        Assert.Equal(92.4, deserialized.Quality.Score);
    }
}
