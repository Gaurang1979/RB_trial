using VisionCore;
using Xunit;

/// <summary>
/// Line safety of the tools added by the Antigravity pull requests #245–#259 (requirements 17.10 AUD-01..04):
/// a taught reference only, the camera image never changed, only tools that check the part decide, and every
/// area follows the part.
/// </summary>
public class ToolLineSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-linesafety-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // a grid of dark bars on light grey: texture for the phase correlator and the mesh matcher
    private static GrayImage Pattern(int w, int h, int dx = 0, int dy = 0)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int u = x - dx, v = y - dy;
                bool bar = u >= 40 && u < 160 && v >= 40 && v < 160 && ((u * 7 + v * 3) % 23 < 6 || (u / 9 + v / 13) % 4 == 0);
                px[y * w + x] = (byte)(bar ? 40 : 200 + (x * 13 + y * 7) % 11);
            }
        return new GrayImage(px, w, h);
    }

    private static readonly RoiBox Area = new(40, 40, 120, 120);

    // every tool of #245–#259 set up on Area, taught where it needs teaching
    private static IEnumerable<ToolConfig> AllNewTools(GrayImage teach)
    {
        var deform = new DeformToolConfig { Name = "Pouch", Roi = Area, MeshCols = 5, MeshRows = 5, PyramidLevels = 2 };
        deform.Teach(teach);
        var phase = new PhaseToolConfig { Name = "Phase", Roi = Area, EstimateRotation = false };
        phase.Teach(teach);
        var drift = new DriftToolConfig { Name = "Drift", Roi = Area };
        drift.Teach(DriftTests.GoodParts(teach.Pixels, teach.Width, teach.Height));
        return new ToolConfig[]
        {
            new InspectToolConfig { Name = "Inspect", Roi = Area },
            new AnomalyToolConfig { Name = "Anomaly", Roi = Area },
            new ThermalToolConfig { Name = "Thermal", Roi = Area },
            new InfraredToolConfig { Name = "Infrared", Roi = Area },
            new PolarToolConfig { Name = "Polar", Roi = Area },
            new PhotometricToolConfig { Name = "Photometric", Roi = Area },
            new WaveletToolConfig { Name = "Wavelet", Roi = Area },
            new InpaintToolConfig { Name = "Inpaint", Roi = Area },
            new RectifyToolConfig { Name = "Rectify", Roi = Area, LineSpeedMmS = 2000, MaxAllowedShearDeg = 45 },
            drift, deform, phase,
            new MetamorphicToolConfig { Name = "Metamorphic", Roi = Area },
            new AutotuneToolConfig { Name = "Autotune", Roi = Area },
        };
    }

    private static ITool Build(ToolConfig c) => c switch
    {
        InspectToolConfig x => new InspectTool(x),
        AnomalyToolConfig x => new AnomalyTool(x),
        ThermalToolConfig x => new ThermalTool(x),
        InfraredToolConfig x => new InfraredTool(x),
        PolarToolConfig x => new PolarTool(x),
        PhotometricToolConfig x => new PhotometricTool(x),
        WaveletToolConfig x => new WaveletTool(x),
        InpaintToolConfig x => new InpaintTool(x),
        RectifyToolConfig x => new RectifyTool(x),
        DriftToolConfig x => new DriftTool(x),
        DeformToolConfig x => new DeformTool(x),
        PhaseToolConfig x => new PhaseTool(x),
        MetamorphicToolConfig x => new MetamorphicTool(x),
        AutotuneToolConfig x => new AutotuneTool(x),
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    // ---- AUD-01: a taught reference only ----

    [Fact]
    public void DeformAndPhase_NotTaught_AreNg_EvenOnAPerfectPart()
    {
        var img = Pattern(200, 200);
        using var deform = new DeformTool(new DeformToolConfig { Name = "Pouch", Roi = Area });
        using var phase = new PhaseTool(new PhaseToolConfig { Name = "Phase", Roi = Area });
        foreach (var r in new[] { deform.Run(img, new BatchFields()), phase.Run(img, new BatchFields()) })
        {
            Assert.False(r.Pass);
            Assert.Equal(ToolArea.NotTaughtExplain, r.Explain);
        }
    }

    [Fact]
    public void DeformAndPhase_ReferenceOfAnotherSize_AreNg()
    {
        var img = Pattern(200, 200);
        var deform = new DeformToolConfig { Name = "Pouch", Roi = Area };
        deform.Teach(img);
        deform.Roi = new RoiBox(40, 40, 100, 100);   // area changed after teaching
        var phase = new PhaseToolConfig { Name = "Phase", Roi = Area };
        phase.Teach(img);
        phase.Roi = new RoiBox(40, 40, 100, 100);
        using var d = new DeformTool(deform);
        using var p = new PhaseTool(phase);
        Assert.Equal(ToolArea.NotTaughtExplain, d.Run(img, new BatchFields()).Explain);
        Assert.Equal(ToolArea.NotTaughtExplain, p.Run(img, new BatchFields()).Explain);
        Assert.False(d.Run(img, new BatchFields()).Pass);
    }

    [Fact]
    public void Drift_NotTaught_IsNg_AndNeverSeedsFromTheLine()
    {
        var img = Pattern(200, 200);
        using var tool = new DriftTool(new DriftToolConfig { Name = "Drift", Roi = Area });
        for (int i = 0; i < 8; i++)
        {
            var r = tool.Run(img, new BatchFields());
            Assert.False(r.Pass);
            Assert.Equal(ToolArea.NotTaughtExplain, r.Explain);
        }
        Assert.Throws<InvalidOperationException>(() => new DriftToolConfig { Name = "Drift" }.Teach(new[] { img, img }));
    }

    [Fact]
    public void Validate_RefusesUntaughtReferenceTools()
    {
        foreach (var t in new ToolConfig[]
                 {
                     new DeformToolConfig { Name = "Pouch", Roi = Area },
                     new PhaseToolConfig { Name = "Phase", Roi = Area },
                     new DriftToolConfig { Name = "Drift", Roi = Area },
                 })
        {
            var job = new Job { Name = "J", Tools = { new BlobToolConfig { Name = "Spots" }, t } };
            var e = Assert.Throws<ArgumentException>(() => job.Validate());
            Assert.Contains(t.Name, e.Message);
            t.Enabled = false;
            job.Validate();   // a switched-off tool does not stop the job
        }
    }

    [Fact]
    public void TaughtReferencesAndBaseline_AreSavedWithTheJob_AndLoadBack()
    {
        var img = Pattern(200, 200);
        var store = new JobStore(_dir);
        var tools = AllNewTools(img).ToList();
        var job = new Job { Name = "Pouches", Tools = { new BlobToolConfig { Name = "Spots" } } };
        job.Tools.AddRange(tools.Where(t => t is DeformToolConfig or PhaseToolConfig or DriftToolConfig));
        store.Save(job, "test");

        var loaded = store.Load("Pouches")!;
        var deform = loaded.Tools.OfType<DeformToolConfig>().Single();
        Assert.NotNull(deform.ReferenceFile);
        Assert.True(File.Exists(Path.Combine(_dir, "assets", deform.ReferenceFile!)));
        Assert.Equal(Area.Width, deform.ReferenceWidth);
        Assert.Equal(tools.OfType<DeformToolConfig>().Single().Reference, deform.Reference);
        Assert.Equal(DriftToolConfig.MinSamples, loaded.Tools.OfType<DriftToolConfig>().Single().Baseline.Count);

        using var phase = new PhaseTool(loaded.Tools.OfType<PhaseToolConfig>().Single());
        Assert.True(phase.Run(img, new BatchFields()).Pass);
        using var drift = new DriftTool(loaded.Tools.OfType<DriftToolConfig>().Single());
        Assert.True(drift.Run(img, new BatchFields()).Pass);

        // a clone (the teach screen's draft) keeps the taught picture
        var clone = JobStore.Clone(loaded);
        Assert.Equal(deform.Reference, clone.Tools.OfType<DeformToolConfig>().Single().Reference);

        // the reference file gone: the job does not load
        File.Delete(Path.Combine(_dir, "assets", deform.ReferenceFile!));
        Assert.Throws<JobLoadException>(() => store.Load("Pouches"));
    }

    // ---- AUD-02: the camera image is never changed ----

    [Fact]
    public void EveryNewTool_LeavesTheCameraImageUnchanged_AndRepeats()
    {
        var teach = Pattern(200, 200);
        var img = Pattern(200, 200, 2, 1);
        var before = (byte[])img.Pixels.Clone();
        foreach (var cfg in AllNewTools(teach))
        {
            using var tool = Build(cfg);
            var first = tool.Run(img, new BatchFields());
            Assert.True(before.AsSpan().SequenceEqual(img.Pixels), $"{cfg.Name} changed the camera image");
            var second = tool.Run(img, new BatchFields());
            Assert.True(before.AsSpan().SequenceEqual(img.Pixels), $"{cfg.Name} changed the camera image on the second run");
            if (cfg is not DriftToolConfig)   // drift keeps a moving window by design
            {
                Assert.True(first.Pass == second.Pass && first.Explain == second.Explain, $"{cfg.Name}: {first.Summary} / {second.Summary}");
            }
        }
    }

    [Fact]
    public void RectifyAndDeform_WholeImage_DoNotWriteBack()
    {
        var img = Pattern(200, 200);
        var before = (byte[])img.Pixels.Clone();
        using (var rect = new RectifyTool(new RectifyToolConfig { Name = "Rectify", LineSpeedMmS = 5000, MaxAllowedShearDeg = 45 }))
        {
            var a = rect.Run(img, new BatchFields());
            var b = rect.Run(img, new BatchFields());
            Assert.Equal(a.Values["max_shift_px"], b.Values["max_shift_px"]);
        }
        Assert.Equal(before, img.Pixels);
        var def = new DeformToolConfig { Name = "Pouch", UnwarpInPlace = true, MeshCols = 5, MeshRows = 5, PyramidLevels = 2 };
        def.Teach(Pattern(200, 200, 3, 2));
        using (var deform = new DeformTool(def))
        {
            var a = deform.Run(img, new BatchFields());
            var b = deform.Run(img, new BatchFields());
            Assert.Equal(a.Pass, b.Pass);
            Assert.Equal(a.Values["alignment_score"], b.Values["alignment_score"]);
        }
        Assert.Equal(before, img.Pixels);
    }

    // ---- AUD-03: only tools that check the part decide ----

    [Fact]
    public void CorrectionAndAdvisoryTools_DoNotDecide()
    {
        Assert.False(ToolArea.Decides(new InpaintToolConfig()));
        Assert.False(ToolArea.Decides(new RectifyToolConfig()));
        Assert.False(ToolArea.Decides(new AutotuneToolConfig { FailOnCriticalTilt = true }));
        Assert.False(ToolArea.Decides(new PhotometricToolConfig { Mode = PhotometricMode.VirtualLighting }));
        Assert.False(ToolArea.Decides(new DriftToolConfig { FailOnDrift = false }));
        Assert.False(ToolArea.Decides(new MetamorphicToolConfig { FailOnNonRobust = false }));
        Assert.True(ToolArea.Decides(new DriftToolConfig { FailOnDrift = true }));
        Assert.True(ToolArea.Decides(new MetamorphicToolConfig { FailOnNonRobust = true }));
        Assert.True(ToolArea.Decides(new PhotometricToolConfig { Mode = PhotometricMode.CurvatureRelief }));
        Assert.True(ToolArea.Decides(new WaveletToolConfig()));
        Assert.True(TextTool.Decides(new BlobToolConfig()));
    }

    [Fact]
    public void ACameraWithOnlyCorrectionTools_IsRefused_AndNeverPasses()
    {
        var job = new Job
        {
            Name = "OnlyFixes",
            Tools =
            {
                new InpaintToolConfig { Name = "Inpaint" },
                new RectifyToolConfig { Name = "Rectify", LineSpeedMmS = 0 },
                new PhotometricToolConfig { Name = "Light", Mode = PhotometricMode.VirtualLighting },
            },
        };
        Assert.Contains("nothing decides", Assert.Throws<ArgumentException>(() => job.Validate()).Message);

        // even run without Validate (a hand-edited file), no part passes on these tools alone
        using var runner = new JobRunner(job, new ModelPaths(""));
        var r = runner.Run(0, Pattern(200, 200), new BatchFields());
        Assert.False(r.Pass);
        Assert.All(r.Tools, t => Assert.True(t.ReportOnly));
    }

    [Fact]
    public void AnAdvisoryToolThatFails_StillFailsThePart()
    {
        var job = new Job
        {
            Name = "Mixed",
            Tools =
            {
                new WaveletToolConfig { Name = "Scratch", MaxAllowedDefectPx = int.MaxValue },
                new InpaintToolConfig { Name = "Inpaint", GlareThreshold = 1, MaxAllowedGlarePct = 0 },   // everything is glare
            },
        };
        using var runner = new JobRunner(job, new ModelPaths(""));
        var r = runner.Run(0, Pattern(200, 200), new BatchFields());
        Assert.True(r.Tools.Single(t => t.ToolName == "Scratch").Pass);
        Assert.False(r.Tools.Single(t => t.ToolName == "Inpaint").Pass);
        Assert.False(r.Pass);
    }

    [Fact]
    public void UnknownMode_IsNg_AndRefused()
    {
        var img = Pattern(200, 200);
        var tools = new ToolConfig[]
        {
            new PolarToolConfig { Name = "Polar", Mode = (PolarInspectionMode)99 },
            new PhotometricToolConfig { Name = "Photo", Mode = (PhotometricMode)99 },
            new WaveletToolConfig { Name = "Wavelet", Mode = (WaveletInspectionMode)99, MaxAllowedDefectPx = int.MaxValue },
            new ThermalToolConfig { Name = "Thermal", Mode = (ThermalInspectionMode)99 },
            new InfraredToolConfig { Name = "Infrared", Mode = (InfraredInspectionMode)99 },
        };
        foreach (var t in tools)
        {
            using var tool = Build(t);
            Assert.False(tool.Run(img, new BatchFields()).Pass);
            var job = new Job { Name = "J", Tools = { t } };
            Assert.Contains("not one this software knows", Assert.Throws<ArgumentException>(() => job.Validate()).Message);
        }
    }

    [Fact]
    public void ProcessStabilityMap_WithoutAProjection_IsNeverOk()
    {
        using var engine = new HaystackEngine(new HaystackConfig { FeatureDim = 4, MaxPoints = 10 });
        // an embedding of the wrong length cannot be projected: no made-up point near "golden"
        var pt = engine.Project(new[] { 0f, 0f }, 1);
        Assert.Equal(HaystackVerdict.Ng, pt.Verdict);
        Assert.NotEqual(0, pt.ClusterId);
    }

    // ---- AUD-04: every area follows the part ----

    [Fact]
    public void EveryNewTool_FailsWhenThePartMovesItsAreaOutOfThePicture()
    {
        var img = Pattern(200, 200);
        var gone = new Fixture(0, 0, 150, 150, 0);   // the part moved 150 px right and down
        foreach (var cfg in AllNewTools(img))
        {
            using var tool = Build(cfg);
            var r = tool.Run(img, new BatchFields(), gone);
            Assert.False(r.Pass, $"{cfg.Name} passed an area outside the picture");
            if (cfg is not (InspectToolConfig or AnomalyToolConfig))   // no model taught: NG before the area
                Assert.True(r.Explain == ToolArea.OutsideExplain, $"{cfg.Name}: {r.Explain} {r.Summary}");
        }
    }

    [Fact]
    public void Phase_FollowsAShiftedPart()
    {
        var teach = Pattern(240, 240);
        var cfg = new PhaseToolConfig { Name = "Phase", Roi = Area, EstimateRotation = false };
        cfg.Teach(teach);
        using var tool = new PhaseTool(cfg);
        var moved = Pattern(240, 240, 20, 15);
        var followed = tool.Run(moved, new BatchFields(), new Fixture(0, 0, 20, 15, 0));
        Assert.True(followed.Pass, followed.Summary);
        Assert.InRange(followed.Values["shift_x"], -0.5, 0.5);
        Assert.InRange(followed.Values["shift_y"], -0.5, 0.5);
        Assert.Equal(60, followed.Graphics[0].Points[0].X);
        Assert.Equal(55, followed.Graphics[0].Points[0].Y);
    }

    [Fact]
    public void AreaTools_MapTheirAreaThroughTheFixture()
    {
        var a = ToolArea.Resolve(new RoiBox(10, 20, 30, 40), new Fixture(0, 0, 5, 7, 0), 200, 200)!;
        Assert.Equal(new RoiBox(15, 27, 30, 40), a);
        Assert.Null(ToolArea.Resolve(new RoiBox(10, 20, 30, 40), new Fixture(0, 0, 180, 0, 0), 200, 200));
        Assert.Null(ToolArea.Resolve(new RoiBox(190, 20, 30, 40), null, 200, 200));   // never clamped into the picture
        Assert.Equal(new RoiBox(0, 0, 200, 100), ToolArea.Resolve(null, new Fixture(0, 0, 50, 50, 0), 200, 100));
        var turned = ToolArea.ResolveTaughtSize(new RoiBox(50, 50, 40, 20), new Fixture(70, 60, 100, 100, 90), 200, 200)!;
        Assert.Equal((40, 20), (turned.Width, turned.Height));
        Assert.Equal((80, 90), (turned.X, turned.Y));   // centred on where the taught centre is now
    }
}
