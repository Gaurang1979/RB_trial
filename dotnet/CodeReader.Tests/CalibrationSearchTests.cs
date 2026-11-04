using System;
using System.Collections.Generic;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

/// <summary>
/// The calibration search without hardware: a fake camera renders a case (dark conveyor, cardboard,
/// 12 white labels with 12 x 12 module codes) at the exposure and gain asked for, scales, clips at
/// 255 and adds noise; a fake reader reads a code when its light and dark modules are 40 or more
/// grey levels apart and the dark modules have not bloomed.
/// </summary>
public class CalibrationSearchTests
{
    private sealed class FakeScene
    {
        public const int W = 800, H = 600, Module = 5, Modules = 12, Side = Module * Modules;
        public const float Black = 6;
        // labels (reflectance 0.85) at the 170 target with 1000 us at 0 dB
        public const float K = (Calibrator.TargetBrightness - Black) / (0.85f * 1000f);
        public readonly float[] Reflectance = new float[W * H];
        public readonly List<(int X, int Y, bool[] Dark)> Codes = new();

        public FakeScene()
        {
            var rnd = new Random(7);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    Reflectance[y * W + x] = x >= 100 && x < 700 && y >= 80 && y < 520 ? 0.35f : 0.05f;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                {
                    int cx = 150 + c * 130, cy = 120 + r * 130;
                    var dark = Enumerable.Range(0, Modules * Modules).Select(_ => rnd.Next(2) == 0).ToArray();
                    for (int y = cy - 10; y < cy + Side + 10; y++)
                        for (int x = cx - 10; x < cx + Side + 10; x++)
                        {
                            int mx = (x - cx) / Module, my = (y - cy) / Module;
                            bool inCode = x >= cx && x < cx + Side && y >= cy && y < cy + Side;
                            Reflectance[y * W + x] = inCode && dark[my * Modules + mx] ? 0.06f : 0.85f;
                        }
                    Codes.Add((cx, cy, dark));
                }
        }
    }

    private sealed class FakeCamera : ICalibrationCamera
    {
        private readonly FakeScene _scene;
        private readonly Random _noise = new(11);
        public readonly List<(float Exposure, float Gain, bool Probe)> Calls = new();
        public FakeCamera(FakeScene scene) => _scene = scene;

        public IReadOnlyList<CalibrationShot> Capture(float exposureUs, float gainDb, int count, int intervalMs, bool probe)
        {
            var shots = new List<CalibrationShot>();
            for (int i = 0; i < count; i++)
            {
                Calls.Add((exposureUs, gainDb, probe));
                int scale = probe ? 2 : 1; // probe frames come back decimated
                int w = FakeScene.W / scale, h = FakeScene.H / scale;
                float gain = MathF.Pow(10, gainDb / 20);
                var px = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float r = _scene.Reflectance[y * scale * FakeScene.W + x * scale];
                        double n = Math.Sqrt(-2 * Math.Log(1 - _noise.NextDouble())) * Math.Cos(2 * Math.PI * _noise.NextDouble());
                        float v = FakeScene.Black + r * exposureUs * gain * FakeScene.K + (float)n * 1.5f * gain;
                        px[y * w + x] = (byte)Math.Clamp(MathF.Round(v), 0, 255);
                    }
                shots.Add(new CalibrationShot(px, w, h, scale, i * intervalMs));
            }
            return shots;
        }
    }

    private sealed class FakeReader
    {
        private readonly FakeScene _scene;
        public int Calls;
        public FakeReader(FakeScene scene) => _scene = scene;

        public DecodeResult Decode(CalibrationShot shot)
        {
            Assert.Equal(1, shot.Scale); // probe frames are never decoded
            Calls++;
            var codes = new List<DecodedCode>();
            var whites = new List<float>();
            int i = 0;
            foreach (var (cx, cy, dark) in _scene.Codes)
            {
                double light = 0, black = 0;
                int nl = 0, nd = 0, sat = 0;
                for (int my = 0; my < FakeScene.Modules; my++)
                    for (int mx = 0; mx < FakeScene.Modules; mx++)
                    {
                        byte v = shot.Pixels[(cy + my * FakeScene.Module + 2) * shot.Width + cx + mx * FakeScene.Module + 2];
                        if (dark[my * FakeScene.Modules + mx]) { black += v; nd++; }
                        else { light += v; nl++; if (v >= 255) sat++; }
                    }
                light /= nl;
                black /= nd;
                i++;
                if (light - black < 40 || black > 200) continue;
                whites.Add((float)light);
                var corners = new[] { new PointF(cx, cy), new PointF(cx + FakeScene.Side, cy), new PointF(cx + FakeScene.Side, cy + FakeScene.Side), new PointF(cx, cy + FakeScene.Side) };
                codes.Add(new DecodedCode($"RJ{i:0000}", CodeFormat.DataMatrix, ReadStep.Plain, corners, 1)
                {
                    Quality = new CodeQuality((float)((light - black) / 2.55), 0.6f, FakeScene.Module, 0.9f, (float)sat / nl, 4),
                });
            }
            var stats = new DecodeStats(codes.Count, 12, 12, 12 - codes.Count, 5, 20, 25, false, whites.Count > 0 ? whites.Average() : 0);
            return new DecodeResult(codes, Array.Empty<Region>(), stats);
        }
    }

    private static readonly FakeScene Scene = new();

    private static ProductMaster Product(float exposureUs, bool calibrated = false)
    {
        var p = new ProductMaster { ProductCode = "P", CodesPerCase = 12, ChildFormat = CodeFormat.DataMatrix };
        p.ChildCamera.ExposureUs = exposureUs;
        if (calibrated) p.ChildCamera.CalibratedUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        return p;
    }

    [Fact]
    public void TheProbeReachesAGoodExposureInFewerTrials()
    {
        // far too dark to start with (labels at about 22/255)
        var cam = new FakeCamera(Scene);
        var reader = new FakeReader(Scene);
        var probed = Calibrator.Tune(Product(100), cam, reader.Decode);

        var blindCam = new FakeCamera(Scene);
        var blind = Calibrator.Tune(Product(100), blindCam, new FakeReader(Scene).Decode, options: new CalibrationSearchOptions { Probe = false });

        Assert.Equal(12, probed.CodesRead);
        Assert.Equal(12, blind.CodesRead);
        Assert.InRange(probed.Proposed.ExposureUs, 850, 1150);
        Assert.InRange(probed.LabelBrightness, 140, 200);
        // with the probe: two cheap probe frames (decimated, never decoded) and one decoded trial
        Assert.InRange(probed.ProbeFrames, 1, 2);
        Assert.Single(probed.Trials);
        Assert.Equal(1, reader.Calls);
        // halving / doubling from the start needs several full decoded trials
        Assert.Equal(0, blind.ProbeFrames);
        Assert.True(blind.Trials.Count >= 4, $"blind search took {blind.Trials.Count} trials");
        // a dark trial is too dark, not rejected: only clipped codes are
        Assert.All(blind.Trials, t => Assert.True(t.Usable || t.Verdict.StartsWith("dropped"), t.Verdict));
        Assert.True(cam.Calls.Count < blindCam.Calls.Count);
        Assert.True(cam.Calls.Take(probed.ProbeFrames).All(c => c.Probe)); // probe first
    }

    [Fact]
    public void TheSaturationLimitRejectsAClippedSettingThatReadsEveryCode()
    {
        // the last good setting is now three times too bright: labels clip at 255, yet every code reads
        var reader = new FakeReader(Scene);
        var r = Calibrator.Tune(Product(3000, calibrated: true), new FakeCamera(Scene), reader.Decode);

        var first = r.Trials[0];
        Assert.Equal("last good", first.Stage);
        Assert.Equal(12, first.CodesRead);
        Assert.False(first.Usable);
        Assert.Contains("clipped", first.Verdict);
        Assert.True(first.CodeSaturation > 0.02f);
        // the setting chosen is the darker one, within the limit
        Assert.InRange(r.Proposed.ExposureUs, 850, 1150);
        Assert.All(r.Trials.Where(t => t.Usable), t => Assert.True(t.CodeSaturation <= 0.02f));
        Assert.Equal(12, r.CodesRead);
        Assert.True(r.AllOk, string.Join("; ", r.Checks.Where(c => !c.Ok).Select(c => c.Message)));
    }

    [Fact]
    public void PlanFailsTheBrightnessCheckForClippedCodes()
    {
        var p = Product(1000);
        var shot = new FakeCamera(Scene).Capture(2500, 0, 1, 0, probe: false)[0];
        var res = new FakeReader(Scene).Decode(shot);
        float sat = CalibrationSearch.CodeSaturation(shot, res.Codes);
        var r = Calibrator.Plan(p, new[] { new CalibrationFrame(res, shot.Width, shot.Height, 0, sat) });
        Assert.Contains(r.Checks, c => c.Name == "Brightness" && !c.Ok && c.Message.Contains("clipped"));
        Assert.True(r.Proposed.ExposureUs <= 700); // at most 0.7 of the clipped setting
    }

    [Fact]
    public void TheCachedSettingIsTriedFirstAndKeptWhenItReadsWell()
    {
        var cam = new FakeCamera(Scene);
        var r = Calibrator.Tune(Product(1000, calibrated: true), cam, new FakeReader(Scene).Decode);
        Assert.Equal((1000f, 0f, false), cam.Calls[0]);
        Assert.Single(cam.Calls); // nothing else taken
        Assert.Equal("last good", Assert.Single(r.Trials).Stage);
        Assert.Equal(0, r.ProbeFrames);
        Assert.Contains("last good setting still reads well", r.Checks[0].Message);
        Assert.InRange(r.Proposed.ExposureUs, 900, 1100);
        Assert.True(r.AllOk);
    }

    [Fact]
    public void AProductNeverCalibratedSkipsTheCacheAndProbes()
    {
        var cam = new FakeCamera(Scene);
        Calibrator.Tune(Product(1000), cam, new FakeReader(Scene).Decode);
        Assert.True(cam.Calls[0].Probe);
    }

    [Fact]
    public void TheContrastPreCheckDropsAFlatFrameWithoutDecoding()
    {
        var reader = new FakeReader(Scene);
        var r = Calibrator.Tune(Product(60), new FakeCamera(Scene), reader.Decode, options: new CalibrationSearchOptions { Probe = false });
        Assert.StartsWith("dropped before decoding", r.Trials[0].Verdict);
        Assert.Equal(r.Trials.Count(t => !t.Verdict.StartsWith("dropped")), reader.Calls);
        Assert.True(reader.Calls < r.Trials.Count);
    }

    [Fact]
    public void TheLightsLongestPulseCapsEveryTrialAndGainMakesUpTheRest()
    {
        var p = Product(1000);
        p.ChildCamera.Light.PreOnUs = 100;
        p.ChildCamera.Light.PostOnUs = 50;
        p.ChildCamera.Light.MaxOnUs = 650; // exposure at most 500 us
        var cam = new FakeCamera(Scene);
        var r = Calibrator.Tune(p, cam, new FakeReader(Scene).Decode);
        Assert.All(cam.Calls, c => Assert.True(c.Exposure <= 500, $"tried {c.Exposure} us"));
        Assert.Equal(500, r.Proposed.ExposureUs, 0);
        Assert.InRange(r.Proposed.GainDb, 4.5f, 7.5f); // about 6 dB for twice the light
        Assert.True(r.Light.LightOnForUs <= 650);
        Assert.True(r.Light.LightOnForUs >= r.Proposed.ExposureUs + 150);
        Assert.True(r.Light.LightOnAtUs <= r.Light.TriggerDelayUs && r.Light.LightOffAtUs >= r.Light.ExposureEndUs);
    }

    [Fact]
    public void SplitUsesExposureBeforeGain()
    {
        Assert.Equal((800f, 0f), CalibrationSearch.Split(800, 2000));
        var (e, g) = CalibrationSearch.Split(4000, 1000);
        Assert.Equal(1000, e);
        Assert.Equal(12, g, 1); // 4x the light = 12 dB, the most allowed
        Assert.Equal(12, CalibrationSearch.Split(16000, 1000).GainDb, 1);
    }

    [Fact]
    public void ProbeStatsReadADecimatedFrameInFullResolutionCoordinates()
    {
        var shot = new FakeCamera(Scene).Capture(1000, 0, 1, 0, probe: true)[0];
        Assert.Equal(2, shot.Scale);
        // one label, given in full-resolution pixels
        var s = CalibrationSearch.Measure(shot, new RoiSettings { X = 140, Y = 110, Width = 80, Height = 80 });
        Assert.InRange(s.P98, 160, 180);
        Assert.InRange(s.Black, 0, 12);
        Assert.Equal(0, s.Saturated);
    }
}
