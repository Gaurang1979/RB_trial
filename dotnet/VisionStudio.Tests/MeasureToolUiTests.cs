using System.Text.Json;
using VisionCore;
using VisionStudio.Services;
using Xunit;

public sealed class MeasureToolUiTests
{
    [Fact]
    public void StationMapsMeasureAndBlobToCorrectLicenceNames()
    {
        var measure = new MeasureToolConfig();
        var blob = new BlobToolConfig();
        var inspect = new InspectToolConfig();
        var anomaly = new AnomalyToolConfig();
        var thermal = new ThermalToolConfig();
        var infrared = new InfraredToolConfig();
        var polar = new PolarToolConfig();
        var photometric = new PhotometricToolConfig();
        var wavelet = new WaveletToolConfig();
        var inpaint = new InpaintToolConfig();
        var rectify = new RectifyToolConfig();
        var drift = new DriftToolConfig();
        var deform = new DeformToolConfig();
        var phase = new PhaseToolConfig();
        var metamorphic = new MetamorphicToolConfig();
        var autotune = new AutotuneToolConfig();
        var locate = new LocateToolConfig();
        var code = new CodeToolConfig();
        var text = new TextToolConfig();

        Assert.Equal("measure", Station.LicenceToolOf(measure));
        Assert.Equal("blob", Station.LicenceToolOf(blob));
        Assert.Equal("inspect", Station.LicenceToolOf(inspect));
        Assert.Equal("anomaly", Station.LicenceToolOf(anomaly));
        Assert.Equal("thermal", Station.LicenceToolOf(thermal));
        Assert.Equal("infrared", Station.LicenceToolOf(infrared));
        Assert.Equal("polar", Station.LicenceToolOf(polar));
        Assert.Equal("photometric", Station.LicenceToolOf(photometric));
        Assert.Equal("wavelet", Station.LicenceToolOf(wavelet));
        Assert.Equal("inpaint", Station.LicenceToolOf(inpaint));
        Assert.Equal("rectify", Station.LicenceToolOf(rectify));
        Assert.Equal("drift", Station.LicenceToolOf(drift));
        Assert.Equal("deform", Station.LicenceToolOf(deform));
        Assert.Equal("phase", Station.LicenceToolOf(phase));
        Assert.Equal("metamorphic", Station.LicenceToolOf(metamorphic));
        Assert.Equal("autotune", Station.LicenceToolOf(autotune));
        Assert.Equal("locate", Station.LicenceToolOf(locate));
        Assert.Equal("barcode", Station.LicenceToolOf(code));
        Assert.Equal("ocr", Station.LicenceToolOf(text));
    }

    [Fact]
    public void ToolLocalizationKeysExistInEnglishTable()
    {
        string? dir = AppContext.BaseDirectory;
        string? en = null;
        for (var d = new DirectoryInfo(dir); d is not null && en is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VisionStudio", "i18n", "en.json"))) en = Path.Combine(d.FullName, "VisionStudio", "i18n", "en.json");
        Assert.NotNull(en);
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(en!))!;

        Assert.True(keys.ContainsKey("tool.measure"), "tool.measure missing");
        Assert.True(keys.ContainsKey("tool.blob"), "tool.blob missing");
        Assert.True(keys.ContainsKey("tool.inspect"), "tool.inspect missing");
        Assert.True(keys.ContainsKey("tool.anomaly"), "tool.anomaly missing");
        Assert.True(keys.ContainsKey("measure.hint"), "measure.hint missing");
        Assert.True(keys.ContainsKey("blob.hint"), "blob.hint missing");
        Assert.True(keys.ContainsKey("inspect.hint"), "inspect.hint missing");
        Assert.True(keys.ContainsKey("anomaly.hint"), "anomaly.hint missing");
        Assert.True(keys.ContainsKey("tool.thermal"), "tool.thermal missing");
        Assert.True(keys.ContainsKey("thermal.hint"), "thermal.hint missing");
        Assert.True(keys.ContainsKey("tool.infrared"), "tool.infrared missing");
        Assert.True(keys.ContainsKey("infrared.hint"), "infrared.hint missing");
        Assert.True(keys.ContainsKey("tool.polar"), "tool.polar missing");
        Assert.True(keys.ContainsKey("polar.hint"), "polar.hint missing");
        Assert.True(keys.ContainsKey("tool.photometric"), "tool.photometric missing");
        Assert.True(keys.ContainsKey("photometric.hint"), "photometric.hint missing");
        Assert.True(keys.ContainsKey("tool.wavelet"), "tool.wavelet missing");
        Assert.True(keys.ContainsKey("wavelet.hint"), "wavelet.hint missing");
        Assert.True(keys.ContainsKey("tool.inpaint"), "tool.inpaint missing");
        Assert.True(keys.ContainsKey("inpaint.hint"), "inpaint.hint missing");
        Assert.True(keys.ContainsKey("tool.rectify"), "tool.rectify missing");
        Assert.True(keys.ContainsKey("rectify.hint"), "rectify.hint missing");
        Assert.True(keys.ContainsKey("tool.drift"), "tool.drift missing");
        Assert.True(keys.ContainsKey("drift.hint"), "drift.hint missing");
        Assert.True(keys.ContainsKey("tool.deform"), "tool.deform missing");
        Assert.True(keys.ContainsKey("deform.hint"), "deform.hint missing");
        Assert.True(keys.ContainsKey("tool.phase"), "tool.phase missing");
        Assert.True(keys.ContainsKey("phase.hint"), "phase.hint missing");
        Assert.True(keys.ContainsKey("measure.add_caliper"), "measure.add_caliper missing");
        Assert.True(keys.ContainsKey("measure.add_check"), "measure.add_check missing");
    }

    [Fact]
    public void MeasureToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new MeasureToolConfig
        {
            Name = "Measure 1",
            Camera = 0,
            Features =
            {
                new CaliperFeature
                {
                    Name = "Width 1",
                    Mode = CaliperMode.Pair,
                    Polarity = EdgePolarity.DarkToLight,
                    Box = new RotBox(120, 80, 50, 25, 15f)
                }
            },
            Checks =
            {
                new MeasureCheck
                {
                    Name = "Width Check",
                    Value = MeasureValue.Width,
                    Feature = "Width 1",
                    Unit = MeasureUnit.Px,
                    Min = 10.0,
                    Max = 15.0
                }
            }
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"measure\"", json);
        Assert.Contains("\"kind\":\"caliper\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as MeasureToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Measure 1", restored.Name);
        Assert.Single(restored.Features);
        Assert.IsType<CaliperFeature>(restored.Features[0]);
        var caliper = (CaliperFeature)restored.Features[0];
        Assert.Equal("Width 1", caliper.Name);
        Assert.Equal(CaliperMode.Pair, caliper.Mode);
        Assert.Equal(15f, caliper.Box.AngleDeg);
        Assert.Single(restored.Checks);
        Assert.Equal(10.0, restored.Checks[0].Min);
        Assert.Equal(15.0, restored.Checks[0].Max);
    }

    [Fact]
    public void BlobToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new BlobToolConfig
        {
            Name = "Blob 1",
            Camera = 1,
            Threshold = BlobThreshold.Auto,
            Polarity = BlobPolarity.DarkOnLight,
            MinArea = 25f,
            MaxArea = 1000f,
            MinCount = 2,
            MaxCount = 5
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"blob\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as BlobToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Blob 1", restored.Name);
        Assert.Equal(1, restored.Camera);
        Assert.Equal(BlobThreshold.Auto, restored.Threshold);
        Assert.Equal(BlobPolarity.DarkOnLight, restored.Polarity);
        Assert.Equal(25f, restored.MinArea);
        Assert.Equal(1000f, restored.MaxArea);
        Assert.Equal(2, restored.MinCount);
        Assert.Equal(5, restored.MaxCount);
    }

    [Fact]
    public void InspectToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new InspectToolConfig
        {
            Name = "Inspect 1",
            Camera = 0,
            Sensitivity = 1.2f,
            BaseOffset = 15.0f,
            EdgeTolerancePx = 2.0f,
            EdgeMarginWeight = 0.2f,
            MinDefectAreaPx = 20,
            NormalizeLighting = true,
            ModelPath = "/tmp/test.vsim"
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"inspect\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as InspectToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Inspect 1", restored.Name);
        Assert.Equal(1.2f, restored.Sensitivity);
        Assert.Equal(15.0f, restored.BaseOffset);
        Assert.Equal(2.0f, restored.EdgeTolerancePx);
        Assert.Equal(0.2f, restored.EdgeMarginWeight);
        Assert.Equal(20, restored.MinDefectAreaPx);
        Assert.True(restored.NormalizeLighting);
        Assert.Equal("/tmp/test.vsim", restored.ModelPath);
    }

    [Fact]
    public void AnomalyToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new AnomalyToolConfig
        {
            Name = "AI Anomaly 1",
            Camera = 1,
            Sensitivity = 1.3f,
            PatchSize = 32,
            PatchStride = 16,
            MinDefectAreaPx = 25,
            ModelPath = "/tmp/test.vsan"
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"anomaly\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as AnomalyToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("AI Anomaly 1", restored.Name);
        Assert.Equal(1, restored.Camera);
        Assert.Equal(1.3f, restored.Sensitivity);
        Assert.Equal(32, restored.PatchSize);
        Assert.Equal(16, restored.PatchStride);
        Assert.Equal(25, restored.MinDefectAreaPx);
        Assert.Equal("/tmp/test.vsan", restored.ModelPath);
    }

    [Fact]
    public void ThermalToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new ThermalToolConfig
        {
            Name = "Bottle Seal 1",
            Camera = 0,
            Mode = ThermalInspectionMode.BottleInductionSeal,
            BottleInnerRadiusPx = 28.0f,
            BottleOuterRadiusPx = 48.0f,
            BottleMinSealTempC = 62.0f,
            BottleMaxSealTempC = 92.0f,
            BottleMaxColdGapDeg = 12.0f
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"thermal\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as ThermalToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Bottle Seal 1", restored.Name);
        Assert.Equal(ThermalInspectionMode.BottleInductionSeal, restored.Mode);
        Assert.Equal(28.0f, restored.BottleInnerRadiusPx);
        Assert.Equal(48.0f, restored.BottleOuterRadiusPx);
        Assert.Equal(62.0f, restored.BottleMinSealTempC);
        Assert.Equal(92.0f, restored.BottleMaxSealTempC);
        Assert.Equal(12.0f, restored.BottleMaxColdGapDeg);
    }

    [Fact]
    public void InfraredToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new InfraredToolConfig
        {
            Name = "Vial Fill 1",
            Camera = 0,
            Mode = InfraredInspectionMode.FillLevel,
            TargetFillMm = 28.5f,
            PxPerMm = 6.2f,
            TolUnderfillMm = 1.5f,
            TolOverfillMm = 1.5f
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"infrared\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as InfraredToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Vial Fill 1", restored.Name);
        Assert.Equal(InfraredInspectionMode.FillLevel, restored.Mode);
        Assert.Equal(28.5f, restored.TargetFillMm);
        Assert.Equal(6.2f, restored.PxPerMm);
        Assert.Equal(1.5f, restored.TolUnderfillMm);
        Assert.Equal(1.5f, restored.TolOverfillMm);
    }

    [Fact]
    public void PolarToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new PolarToolConfig
        {
            Name = "Polar Filter 1",
            Camera = 0,
            Mode = PolarInspectionMode.GlareRemoval,
            GlareSuppressAlpha = 0.90f,
            SpecularGain = 1.8f,
            DolpDefectThresh = 0.35f,
            StressGradientThresh = 45.0f,
            MaxAllowedGlarePct = 4.5f
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"polar\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as PolarToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Polar Filter 1", restored.Name);
        Assert.Equal(PolarInspectionMode.GlareRemoval, restored.Mode);
        Assert.Equal(0.90f, restored.GlareSuppressAlpha);
        Assert.Equal(1.8f, restored.SpecularGain);
        Assert.Equal(0.35f, restored.DolpDefectThresh);
        Assert.Equal(45.0f, restored.StressGradientThresh);
        Assert.Equal(4.5f, restored.MaxAllowedGlarePct);
    }

    [Fact]
    public void PhotometricToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new PhotometricToolConfig
        {
            Name = "LumiTrax 1",
            Camera = 0,
            Mode = PhotometricMode.CurvatureRelief,
            Layout = PhotometricLayout.Quadrant2x2,
            ShapeContrastGain = 150.0f,
            AlbedoGamma = 1.1f,
            ReliefThreshold = 30,
            MaxAllowedDefectPx = 25,
            MinFeaturePx = 80,
            MaxFeaturePx = 1000,
            VirtualTiltDeg = 60.0f,
            VirtualSlantDeg = 30.0f
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"photometric\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as PhotometricToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("LumiTrax 1", restored.Name);
        Assert.Equal(PhotometricMode.CurvatureRelief, restored.Mode);
        Assert.Equal(PhotometricLayout.Quadrant2x2, restored.Layout);
        Assert.Equal(150.0f, restored.ShapeContrastGain);
        Assert.Equal(1.1f, restored.AlbedoGamma);
        Assert.Equal(30, restored.ReliefThreshold);
        Assert.Equal(25, restored.MaxAllowedDefectPx);
        Assert.Equal(80, restored.MinFeaturePx);
        Assert.Equal(1000, restored.MaxFeaturePx);
        Assert.Equal(60.0f, restored.VirtualTiltDeg);
        Assert.Equal(30.0f, restored.VirtualSlantDeg);
    }

    [Fact]
    public void WaveletToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new WaveletToolConfig
        {
            Name = "Scratch 1",
            Camera = 0,
            Mode = WaveletInspectionMode.ScratchDetection,
            TextureMode = TextureOrientation.Horizontal,
            Levels = 3,
            SuppressionStrength = 0.90f,
            ScratchGain = 4.0f,
            HomomorphicEnable = true,
            DefectContrastThresh = 40.0f,
            MinDefectAreaPx = 8,
            MaxAllowedDefectPx = 0
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"wavelet\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as WaveletToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Scratch 1", restored.Name);
        Assert.Equal(WaveletInspectionMode.ScratchDetection, restored.Mode);
        Assert.Equal(TextureOrientation.Horizontal, restored.TextureMode);
        Assert.Equal(3, restored.Levels);
        Assert.Equal(0.90f, restored.SuppressionStrength);
        Assert.Equal(4.0f, restored.ScratchGain);
        Assert.True(restored.HomomorphicEnable);
        Assert.Equal(40.0f, restored.DefectContrastThresh);
        Assert.Equal(8, restored.MinDefectAreaPx);
        Assert.Equal(0, restored.MaxAllowedDefectPx);
    }

    [Fact]
    public void InpaintToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new InpaintToolConfig
        {
            Name = "Glare Inpainter 1",
            Camera = 0,
            GlareThreshold = 252,
            HaloRadius = 3,
            InpaintRadius = 4,
            Method = InpaintNative.InpaintMethod.Telea,
            MaxAllowedGlarePct = 25.0f
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"inpaint\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as InpaintToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Glare Inpainter 1", restored.Name);
        Assert.Equal(252, restored.GlareThreshold);
        Assert.Equal(3, restored.HaloRadius);
        Assert.Equal(4, restored.InpaintRadius);
        Assert.Equal(InpaintNative.InpaintMethod.Telea, restored.Method);
        Assert.Equal(25.0f, restored.MaxAllowedGlarePct);
    }

    [Fact]
    public void RectifyToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new RectifyToolConfig
        {
            Name = "Rectify 1",
            Camera = 0,
            LineSpeedMmS = 450.0f,
            MmPerPx = 0.05f,
            RowTimeUs = 18.2f,
            MotionAngleDeg = 5.0f,
            Bilinear = true,
            MaxAllowedShearDeg = 12.0f
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"rectify\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as RectifyToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Rectify 1", restored.Name);
        Assert.Equal(450.0f, restored.LineSpeedMmS);
        Assert.Equal(0.05f, restored.MmPerPx);
        Assert.Equal(18.2f, restored.RowTimeUs);
        Assert.Equal(5.0f, restored.MotionAngleDeg);
        Assert.True(restored.Bilinear);
        Assert.Equal(12.0f, restored.MaxAllowedShearDeg);
    }

    [Fact]
    public void DriftToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new DriftToolConfig
        {
            Name = "Drift Tracker 1",
            Camera = 0,
            WindowSize = 40,
            WarnDistanceThreshold = 5.2f,
            DirtSharpnessSigma = 2.5f,
            LightDropSigma = 1.8f,
            GridStep = 3,
            FailOnDrift = false
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"drift\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as DriftToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Drift Tracker 1", restored.Name);
        Assert.Equal(40, restored.WindowSize);
        Assert.Equal(5.2f, restored.WarnDistanceThreshold);
        Assert.Equal(2.5f, restored.DirtSharpnessSigma);
        Assert.Equal(1.8f, restored.LightDropSigma);
        Assert.Equal(3, restored.GridStep);
        Assert.False(restored.FailOnDrift);
    }

    [Fact]
    public void DeformToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new DeformToolConfig
        {
            Name = "Pouch Matcher 1",
            Camera = 0,
            MeshCols = 11,
            MeshRows = 11,
            PyramidLevels = 4,
            MaxIterationsPerLevel = 25,
            SmoothnessWeight = 0.45f,
            RuptureStrainThreshold = 0.40f,
            CrumpleBendingThreshold = 55.0f,
            MinAlignmentScore = 0.75f,
            UnwarpInPlace = true
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"deform\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as DeformToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Pouch Matcher 1", restored.Name);
        Assert.Equal(11, restored.MeshCols);
        Assert.Equal(11, restored.MeshRows);
        Assert.Equal(4, restored.PyramidLevels);
        Assert.Equal(25, restored.MaxIterationsPerLevel);
        Assert.Equal(0.45f, restored.SmoothnessWeight);
        Assert.Equal(0.40f, restored.RuptureStrainThreshold);
        Assert.Equal(55.0f, restored.CrumpleBendingThreshold);
        Assert.Equal(0.75f, restored.MinAlignmentScore);
        Assert.True(restored.UnwarpInPlace);
    }

    [Fact]
    public void PhaseToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new PhaseToolConfig
        {
            Name = "Phase Correlator 1",
            Camera = 0,
            EstimateRotation = true,
            EstimateScale = true,
            MinCorrelationScore = 0.22f,
            MinPsr = 8.5f,
            ApplyHannWindow = true
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"phase\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as PhaseToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Phase Correlator 1", restored.Name);
        Assert.True(restored.EstimateRotation);
        Assert.True(restored.EstimateScale);
        Assert.Equal(0.22f, restored.MinCorrelationScore);
        Assert.Equal(8.5f, restored.MinPsr);
        Assert.True(restored.ApplyHannWindow);
    }

    [Fact]
    public void MetamorphicToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new MetamorphicToolConfig
        {
            Name = "Metamorphic Fuzzer 1",
            Camera = 0,
            MaxRotationDeg = 4.5f,
            RotationSteps = 9,
            MaxIllumGainDelta = 0.15f,
            IllumSteps = 7,
            MaxScaleDelta = 0.08f,
            ScaleSteps = 5,
            MaxNoiseSigma = 6.0f,
            NoiseSteps = 4,
            MaxQuietZonePx = 10,
            MaxAllowedJitterPx = 0.85f,
            MinAcceptableConfidence = 0.65f,
            FailOnNonRobust = true
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"metamorphic\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as MetamorphicToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("Metamorphic Fuzzer 1", restored.Name);
        Assert.Equal(4.5f, restored.MaxRotationDeg);
        Assert.Equal(9, restored.RotationSteps);
        Assert.Equal(0.15f, restored.MaxIllumGainDelta);
        Assert.Equal(7, restored.IllumSteps);
        Assert.Equal(0.08f, restored.MaxScaleDelta);
        Assert.Equal(5, restored.ScaleSteps);
        Assert.Equal(6.0f, restored.MaxNoiseSigma);
        Assert.Equal(4, restored.NoiseSteps);
        Assert.Equal(10, restored.MaxQuietZonePx);
        Assert.Equal(0.85f, restored.MaxAllowedJitterPx);
        Assert.Equal(0.65f, restored.MinAcceptableConfidence);
        Assert.True(restored.FailOnNonRobust);
    }

    [Fact]
    public void AutotuneToolConfigSerializesPolymorphicallyAsToolConfig()
    {
        var tool = new AutotuneToolConfig
        {
            Name = "AutoTune 1",
            Camera = 0,
            CheckMountTilt = true,
            MinAcceptableUniformity = 0.70f,
            AnalyzeLighting = true,
            TargetMeanDn = 135.0f,
            CheckLensDust = true,
            MaxAllowedDustSpots = 5,
            TrackApplicator = true,
            PxPerMm = 12.5f,
            ScrewPitchMmPerTurn = 0.5f,
            MaxAllowedEccentricityMm = 1.0f,
            FailOnCriticalTilt = true
        };

        ToolConfig polymorphic = tool;
        string json = JsonSerializer.Serialize(polymorphic);
        Assert.Contains("\"kind\":\"autotune\"", json);

        var restored = JsonSerializer.Deserialize<ToolConfig>(json) as AutotuneToolConfig;
        Assert.NotNull(restored);
        Assert.Equal("AutoTune 1", restored.Name);
        Assert.True(restored.CheckMountTilt);
        Assert.Equal(0.70f, restored.MinAcceptableUniformity);
        Assert.True(restored.AnalyzeLighting);
        Assert.Equal(135.0f, restored.TargetMeanDn);
        Assert.True(restored.CheckLensDust);
        Assert.Equal(5, restored.MaxAllowedDustSpots);
        Assert.True(restored.TrackApplicator);
        Assert.Equal(12.5f, restored.PxPerMm);
        Assert.Equal(0.5f, restored.ScrewPitchMmPerTurn);
        Assert.Equal(1.0f, restored.MaxAllowedEccentricityMm);
        Assert.True(restored.FailOnCriticalTilt);
    }
}
