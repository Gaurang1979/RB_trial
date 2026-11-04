using System;
using System.Linq;
using VisionCore;
using Xunit;

namespace VisionCore.Tests;

public class HaystackEngineTests
{
    [Fact]
    public void Haystack_InitialClusters_ContainsGoldenOk()
    {
        using var engine = new HaystackEngine(new HaystackConfig { FeatureDim = 16, MaxPoints = 100 });
        var clusters = engine.GetClusters();

        Assert.NotEmpty(clusters);
        var golden = clusters.FirstOrDefault(c => c.ClusterId == 0);
        Assert.NotNull(golden);
        Assert.Equal("Golden OK", golden.Name);
        Assert.Equal(0f, golden.CenterU);
        Assert.Equal(0f, golden.CenterV);
    }

    [Fact]
    public void Haystack_Projection_GoldenPoint_ProducesOkVerdict()
    {
        using var engine = new HaystackEngine(new HaystackConfig
        {
            FeatureDim = 4,
            MaxPoints = 100,
            GoldenRadius = 0.35f,
            AmbiguityMargin = 0.15f
        });

        // Set identity projection
        engine.SetProjection([0f, 0f, 0f, 0f], [1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], 1f, 1f);

        // Point near origin: (0.1, 0.1) -> dist = 0.141 < 0.35
        float[] goldenVector = [0.10f, 0.10f, 0f, 0f];
        var pt = engine.Project(goldenVector, 1001);

        Assert.Equal(HaystackVerdict.Ok, pt.Verdict);
        Assert.Equal(0, pt.ClusterId);
        Assert.Equal("Golden OK", pt.DefectName);
        Assert.True(pt.Confidence >= 0.7f);
        Assert.False(pt.IsTriaged);
    }

    [Fact]
    public void Haystack_Projection_BorderlinePoint_ProducesAmbiguousVerdict()
    {
        using var engine = new HaystackEngine(new HaystackConfig
        {
            FeatureDim = 4,
            MaxPoints = 100,
            GoldenRadius = 0.30f,
            AmbiguityMargin = 0.15f
        });

        engine.SetProjection([0f, 0f, 0f, 0f], [1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], 1f, 1f);

        // Point at (0.38, 0.0) -> dist = 0.38 which lies in [0.30, 0.45]
        float[] borderlineVector = [0.38f, 0.0f, 0f, 0f];
        var pt = engine.Project(borderlineVector, 2002);

        Assert.Equal(HaystackVerdict.Ambiguous, pt.Verdict);
        Assert.Equal(-1, pt.ClusterId);
        Assert.Contains("Borderline", pt.DefectName);

        // Verify it shows up in ambiguous queue
        var ambList = engine.GetAmbiguousPoints();
        Assert.Single(ambList);
        Assert.Equal(2002UL, ambList[0].ImageId);
    }

    [Fact]
    public void Haystack_Projection_DefectPoint_MatchesDefectCluster()
    {
        using var engine = new HaystackEngine(new HaystackConfig
        {
            FeatureDim = 4,
            MaxPoints = 100,
            GoldenRadius = 0.30f,
            AmbiguityMargin = 0.15f
        });

        engine.SetProjection([0f, 0f, 0f, 0f], [1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], 1f, 1f);
        engine.AddCluster(1, "Heat Seal Leak", 0.75f, 0.0f, 0.20f, 0xEF4444FF);

        // Point at (0.76, 0.02) -> close to defect cluster 1
        float[] defectVector = [0.76f, 0.02f, 0f, 0f];
        var pt = engine.Project(defectVector, 3003);

        Assert.Equal(HaystackVerdict.Ng, pt.Verdict);
        Assert.Equal(1, pt.ClusterId);
        Assert.Equal("Heat Seal Leak", pt.DefectName);
    }

    [Fact]
    public void Haystack_ActiveLearning_TriagePoint_UpdatesVerdictAndPendingList()
    {
        using var engine = new HaystackEngine(new HaystackConfig
        {
            FeatureDim = 4,
            MaxPoints = 100,
            GoldenRadius = 0.30f,
            AmbiguityMargin = 0.15f
        });

        engine.SetProjection([0f, 0f, 0f, 0f], [1f, 0f, 0f, 0f], [0f, 1f, 0f, 0f], 1f, 1f);

        // Borderline point
        float[] borderlineVector = [0.36f, 0.0f, 0f, 0f];
        engine.Project(borderlineVector, 4004);

        Assert.Single(engine.GetAmbiguousPoints());

        // Operator triages and confirms as Golden variation
        bool success = engine.Triage(4004, HaystackVerdict.Ok, 0, "Approved Material Variation");
        Assert.True(success);

        // Pending ambiguous count should now be 0
        Assert.Empty(engine.GetAmbiguousPoints());

        // Check the point in the overall list
        var pt = engine.GetPoints().FirstOrDefault(p => p.ImageId == 4004);
        Assert.NotNull(pt);
        Assert.Equal(HaystackVerdict.Ok, pt.Verdict);
        Assert.Equal(0, pt.ClusterId);
        Assert.True(pt.IsTriaged);
        Assert.Equal("Approved Material Variation", pt.DefectName);
    }

    [Fact]
    public void Haystack_RollingBuffer_RespectsMaxPointsLimit()
    {
        using var engine = new HaystackEngine(new HaystackConfig
        {
            FeatureDim = 2,
            MaxPoints = 5,
            GoldenRadius = 0.5f,
            AmbiguityMargin = 0.2f
        });

        for (ulong i = 1; i <= 8; i++)
        {
            engine.Project([0.1f, 0.1f], i);
        }

        var pts = engine.GetPoints();
        Assert.Equal(5, pts.Count);
        // Oldest points 1, 2, 3 should have rolled out
        Assert.DoesNotContain(pts, p => p.ImageId == 1);
        Assert.DoesNotContain(pts, p => p.ImageId == 2);
        Assert.DoesNotContain(pts, p => p.ImageId == 3);
        Assert.Contains(pts, p => p.ImageId == 8);
    }
}
