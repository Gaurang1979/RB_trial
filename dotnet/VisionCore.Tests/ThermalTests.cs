using System;
using VisionCore;
using Xunit;

public class ThermalTests
{
    [Fact]
    public void DefaultParams_AreCorrect()
    {
        var bp = ThermalEngine.DefaultBottleParams();
        Assert.True(bp.MinSealTempC >= 50.0f);
        Assert.True(bp.MaxSealTempC >= 90.0f);
        Assert.True(bp.MaxColdGapDeg >= 10.0f);

        var pp = ThermalEngine.DefaultPouchParams();
        Assert.True(pp.MinSealTempC >= 55.0f);
        Assert.True(pp.MinSealWidthPx >= 5.0f);

        var gp = ThermalEngine.DefaultGlueParams();
        Assert.Equal(2, gp.ExpectedBeadCount);
        Assert.True(gp.MinGlueTempC >= 60.0f);
    }

    [Fact]
    public void BottleSeal_DetectsGoodAndMissingFoil()
    {
        const int w = 120, h = 120;
        const float cx = 60.0f, cy = 60.0f;

        // 1. Good induction heat ring at 78 C (mapped to 148 on 20..120 C scale)
        var goodImg = new byte[w * h];
        Array.Fill(goodImg, (byte)10); // Ambient 24 C

        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float dist = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist >= 25.0f && dist <= 40.0f)
                {
                    goodImg[y * w + x] = 148;
                }
            }
        }

        var bp = ThermalEngine.DefaultBottleParams();
        bp.ExpectedCx = cx;
        bp.ExpectedCy = cy;
        bp.InnerRadiusPx = 25.0f;
        bp.OuterRadiusPx = 40.0f;

        var goodRes = ThermalEngine.InspectBottleSeal(goodImg, w, h, w, bp);
        Assert.Equal(ThermalNative.ThermalVerdict.Pass, (ThermalNative.ThermalVerdict)goodRes.Verdict);
        Assert.Equal(0.0f, goodRes.ColdGapDeg);
        Assert.Equal(100.0f, goodRes.SealIntegrityPct);

        // 2. Missing foil (all cold ambient)
        var coldImg = new byte[w * h];
        Array.Fill(coldImg, (byte)10);

        var coldRes = ThermalEngine.InspectBottleSeal(coldImg, w, h, w, bp);
        Assert.Equal(ThermalNative.ThermalVerdict.NgMissingFoil, (ThermalNative.ThermalVerdict)coldRes.Verdict);
        Assert.Equal(0.0f, coldRes.SealIntegrityPct);
    }

    [Fact]
    public void PouchSeam_DetectsContinuousAndVoidLeak()
    {
        const int w = 150, h = 50;

        // 1. Good seam
        var goodImg = new byte[w * h];
        Array.Fill(goodImg, (byte)10);
        for (int y = 15; y <= 30; ++y)
            for (int x = 0; x < w; ++x)
                goodImg[y * w + x] = 120; // 88 C on 20..160 C scale

        var pp = ThermalEngine.DefaultPouchParams();
        pp.SeamRoiW = w;
        pp.SeamRoiH = h;
        pp.MinSealWidthPx = 10.0f;

        var goodRes = ThermalEngine.InspectPouchSeam(goodImg, w, h, w, pp);
        Assert.Equal(ThermalNative.ThermalVerdict.Pass, (ThermalNative.ThermalVerdict)goodRes.Verdict);
        Assert.Equal(0, goodRes.VoidCount);

        // 2. Seam with leak void channel at x=70..78
        var voidImg = (byte[])goodImg.Clone();
        for (int y = 15; y <= 30; ++y)
            for (int x = 70; x <= 78; ++x)
                voidImg[y * w + x] = 10;

        var voidRes = ThermalEngine.InspectPouchSeam(voidImg, w, h, w, pp);
        Assert.Equal(ThermalNative.ThermalVerdict.NgSeamVoid, (ThermalNative.ThermalVerdict)voidRes.Verdict);
        Assert.True(voidRes.VoidCount >= 1);
        Assert.True(voidRes.WorstVoidLengthPx >= 8.0f);
    }

    [Fact]
    public void HotGlue_DetectsMissingBead()
    {
        const int w = 100, h = 60;
        var img = new byte[w * h];
        Array.Fill(img, (byte)10);

        // Only 1 bead at y=20 (expected 2)
        for (int x = 10; x < 90; ++x)
            for (int dy = -1; dy <= 1; ++dy)
                img[(20 + dy) * w + x] = 180;

        var gp = ThermalEngine.DefaultGlueParams();
        gp.RoiW = w;
        gp.RoiH = h;
        gp.ExpectedBeadCount = 2;

        var res = ThermalEngine.InspectHotGlue(img, w, h, w, gp);
        Assert.Equal(ThermalNative.ThermalVerdict.NgMissingBead, (ThermalNative.ThermalVerdict)res.Verdict);
        Assert.Equal(1, res.DetectedBeadCount);
    }

    [Fact]
    public void Blister_DetectsGoodAndBreachedBarrier()
    {
        const int w = 240, h = 140;
        var card = new byte[w * h];
        Array.Fill(card, (byte)153); // 80 C hot flange

        var bp = ThermalEngine.DefaultBlisterParams();
        bp.CardRoiW = w;
        bp.CardRoiH = h;
        bp.Rows = 2;
        bp.Cols = 3;
        bp.PocketWPx = 30.0f;
        bp.PocketHPx = 20.0f;
        bp.PitchXPx = 70.0f;
        bp.PitchYPx = 50.0f;
        bp.MinBarrierWidthPx = 8.0f;

        // Mask out 6 cold cavities (25 C)
        float marginX = (w - (3 * 70.0f)) * 0.5f;
        float marginY = (h - (2 * 50.0f)) * 0.5f;

        for (int r = 0; r < 2; ++r)
        {
            for (int c = 0; c < 3; ++c)
            {
                int pcX = (int)(marginX + c * 70.0f + 35.0f);
                int pcY = (int)(marginY + r * 50.0f + 25.0f);
                for (int dy = -10; dy <= 10; ++dy)
                    for (int dx = -15; dx <= 15; ++dx)
                        card[(pcY + dy) * w + (pcX + dx)] = 12;
            }
        }

        // 1. Good card passes
        var goodRes = ThermalEngine.InspectBlister(card, w, h, w, bp);
        Assert.Equal(ThermalNative.ThermalVerdict.Pass, (ThermalNative.ThermalVerdict)goodRes.Verdict);
        Assert.Equal(-1, goodRes.CompromisedCavityIdx);
        Assert.True(goodRes.MinBarrierWidthPx >= 8.0f);

        // 2. Breached card (cavity index 1 has leak channel to edge)
        var breachCard = (byte[])card.Clone();
        int leakCx = (int)(marginX + 1 * 70.0f + 35.0f);
        for (int y = 0; y < 45; ++y)
            for (int dx = -4; dx <= 4; ++dx)
                breachCard[y * w + (leakCx + dx)] = 12;

        var breachRes = ThermalEngine.InspectBlister(breachCard, w, h, w, bp);
        Assert.Equal(ThermalNative.ThermalVerdict.NgBlisterBarrierBreach, (ThermalNative.ThermalVerdict)breachRes.Verdict);
        Assert.Equal(1, breachRes.CompromisedCavityIdx);
        Assert.Equal(0.0f, breachRes.MinBarrierWidthPx);
    }

    [Fact]
    public void ToolingDiagnostics_ProvidesActionableAdvice()
    {
        var (code, action, sev) = ThermalEngine.DiagnoseTooling(
            packagingType: 0,
            verdict: ThermalNative.ThermalVerdict.NgCrookedSeal,
            asymmetryMetric: 22.0f);

        Assert.Equal("TOOL_BOTTLE_INDUCT_TILT", code);
        Assert.Contains("guide rails", action);
        Assert.Equal(2, sev);
    }

    [Fact]
    public void ThermalTool_BottleInductionSeal_PassesAndFails()
    {
        const int w = 160, h = 160;
        const float cx = 80.0f, cy = 80.0f;
        var goodImg = new byte[w * h];
        Array.Fill(goodImg, (byte)10);

        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float dist = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist >= 25.0f && dist <= 40.0f)
                    goodImg[y * w + x] = 148;
            }
        }

        var cfg = new ThermalToolConfig
        {
            Name = "Bottle Seal",
            Mode = ThermalInspectionMode.BottleInductionSeal,
            BottleInnerRadiusPx = 25.0f,
            BottleOuterRadiusPx = 40.0f
        };

        using var tool = new ThermalTool(cfg);
        var batch = new BatchFields();

        // 1. Good bottle
        var resGood = tool.Run(new GrayImage(goodImg, w, h), batch);
        Assert.True(resGood.Pass);
        Assert.True(resGood.Values.ContainsKey("mean_temp"));
        Assert.True(resGood.Values["mean_temp"] >= 70.0);

        // 2. Missing foil
        var coldImg = new byte[w * h];
        Array.Fill(coldImg, (byte)10);
        var resCold = tool.Run(new GrayImage(coldImg, w, h), batch);
        Assert.False(resCold.Pass);
        Assert.Equal("explain.thermal.ng_missing_foil", resCold.Explain);
        Assert.Contains(resCold.Details, d => d.Contains("Tooling Diagnostic"));
    }

    [Fact]
    public void ThermalTool_PouchSeam_DetectsVoidLeak()
    {
        const int w = 150, h = 50;
        var goodImg = new byte[w * h];
        Array.Fill(goodImg, (byte)10);
        for (int y = 15; y <= 30; ++y)
            for (int x = 0; x < w; ++x)
                goodImg[y * w + x] = 120;

        var cfg = new ThermalToolConfig
        {
            Name = "Pouch Seam",
            Mode = ThermalInspectionMode.PouchHeatSeal,
            PouchMinSealWidthPx = 10.0f
        };

        using var tool = new ThermalTool(cfg);
        var batch = new BatchFields();

        var resGood = tool.Run(new GrayImage(goodImg, w, h), batch);
        Assert.True(resGood.Pass);

        var voidImg = (byte[])goodImg.Clone();
        for (int y = 15; y <= 30; ++y)
            for (int x = 70; x <= 78; ++x)
                voidImg[y * w + x] = 10;

        var resVoid = tool.Run(new GrayImage(voidImg, w, h), batch);
        Assert.False(resVoid.Pass);
        Assert.Equal("explain.thermal.ng_seam_void", resVoid.Explain);
    }

    [Fact]
    public void ThermalTool_HotGlue_DetectsMissingBead()
    {
        const int w = 100, h = 60;
        var img = new byte[w * h];
        Array.Fill(img, (byte)10);

        for (int x = 10; x < 90; ++x)
            for (int dy = -1; dy <= 1; ++dy)
                img[(20 + dy) * w + x] = 180;

        var cfg = new ThermalToolConfig
        {
            Name = "Hot Glue",
            Mode = ThermalInspectionMode.HotMeltGlue,
            GlueExpectedBeads = 2
        };

        using var tool = new ThermalTool(cfg);
        var batch = new BatchFields();

        var res = tool.Run(new GrayImage(img, w, h), batch);
        Assert.False(res.Pass);
        Assert.Equal("explain.thermal.ng_missing_bead", res.Explain);
        Assert.Equal(1.0, res.Values["bead_count"]);
    }

    [Fact]
    public void ThermalTool_BlisterPocket_DetectsBreach()
    {
        const int w = 240, h = 140;
        var card = new byte[w * h];
        Array.Fill(card, (byte)153);

        float marginX = (w - (3 * 70.0f)) * 0.5f;
        float marginY = (h - (2 * 50.0f)) * 0.5f;

        for (int r = 0; r < 2; ++r)
        {
            for (int c = 0; c < 3; ++c)
            {
                int pcX = (int)(marginX + c * 70.0f + 35.0f);
                int pcY = (int)(marginY + r * 50.0f + 25.0f);
                for (int dy = -10; dy <= 10; ++dy)
                    for (int dx = -15; dx <= 15; ++dx)
                        card[(pcY + dy) * w + (pcX + dx)] = 12;
            }
        }

        var cfg = new ThermalToolConfig
        {
            Name = "Blister Pack",
            Mode = ThermalInspectionMode.BlisterPocket,
            BlisterRows = 2,
            BlisterCols = 3,
            BlisterPocketWPx = 30.0f,
            BlisterPocketHPx = 20.0f,
            BlisterPitchXPx = 70.0f,
            BlisterPitchYPx = 50.0f,
            BlisterMinBarrierWidthPx = 8.0f
        };

        using var tool = new ThermalTool(cfg);
        var batch = new BatchFields();

        var resGood = tool.Run(new GrayImage(card, w, h), batch);
        Assert.True(resGood.Pass);

        var breachCard = (byte[])card.Clone();
        int leakCx = (int)(marginX + 1 * 70.0f + 35.0f);
        for (int y = 0; y < 45; ++y)
            for (int dx = -4; dx <= 4; ++dx)
                breachCard[y * w + (leakCx + dx)] = 12;

        var resBreach = tool.Run(new GrayImage(breachCard, w, h), batch);
        Assert.False(resBreach.Pass);
        Assert.Equal("explain.thermal.ng_blister_barrier_breach", resBreach.Explain);
    }
}
