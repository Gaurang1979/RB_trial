using System;
using System.Collections.Generic;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class CalibrationTests
{
    // a 4 x 3 grid of 60 px codes (3 px modules) starting at (x0, y0), in a 2000 x 1500 image
    private static CalibrationFrame Frame(float x0, float y0, float brightness, double timeMs = 0, int cols = 4, int rows = 3)
    {
        var codes = new List<DecodedCode>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                float x = x0 + c * 150, y = y0 + r * 150;
                var corners = new[] { new PointF(x, y), new PointF(x + 60, y), new PointF(x + 60, y + 60), new PointF(x, y + 60) };
                codes.Add(new DecodedCode($"RJ{r * cols + c:0000}", CodeFormat.QrCode, ReadStep.Plain, corners, 1)
                {
                    Quality = new CodeQuality(70, 0.6f, 3f, 0.9f, 0f, 4),
                });
            }
        var stats = new DecodeStats(codes.Count, cols * rows, codes.Count, 0, 10, 40, 50, false, brightness);
        return new CalibrationFrame(new DecodeResult(codes, Array.Empty<Region>(), stats), 2000, 1500, timeMs);
    }

    private static ProductMaster Product(int count = 12) => new() { ProductCode = "P", CodesPerCase = count };

    [Fact]
    public void DarkLabelsGetALongerExposure()
    {
        var p = Product();
        p.ChildCamera.ExposureUs = 1000;
        var r = Calibrator.Plan(p, new[] { Frame(500, 400, 85) });
        Assert.Equal(2000, r.Proposed.ExposureUs, 0);
        Assert.Equal(0, r.Proposed.GainDb);
    }

    [Fact]
    public void CropSurroundsTheCaseWithAMargin()
    {
        var r = Calibrator.Plan(Product(), new[] { Frame(500, 400, 170) });
        var roi = r.Proposed.Roi!;
        // codes span x 500..1010, y 400..760; margin 1.5 codes (90 px) + one grid spacing (150 px)
        Assert.InRange(roi.X, 250, 260);
        Assert.InRange(roi.Y, 160, 170);
        Assert.InRange(roi.X + roi.Width, 1250, 1260);
        Assert.InRange(roi.Y + roi.Height, 1000, 1010);
        Assert.True(r.AllOk);
    }

    [Fact]
    public void TooFewCodesKeepTheWholeImage()
    {
        var r = Calibrator.Plan(Product(48), new[] { Frame(500, 400, 170) });
        Assert.Null(r.Proposed.Roi);
        Assert.False(r.AllOk);
    }

    [Fact]
    public void TwoFramesMeasureTheSpeedAndLimitTheExposure()
    {
        var p = Product();
        p.ChildCamera.ExposureUs = 2000;
        // the case moved 100 px to the right in 50 ms: 2 px/ms
        var r = Calibrator.Plan(p, new[] { Frame(500, 400, 170, 0), Frame(600, 400, 170, 50) });
        Assert.Equal(CaseMotion.Moving, r.Proposed.Motion);
        Assert.Equal(2f, r.SpeedPxPerMs, 2);
        Assert.Equal(1f, r.Proposed.MotionDirX, 2);
        // half a 3 px module at 2 px/ms = 750 us; the missing light comes from gain
        Assert.Equal(750, r.Proposed.ExposureUs, 0);
        Assert.Equal(20 * MathF.Log10(2000f / 750f), r.Proposed.GainDb, 1);
    }

    [Fact]
    public void LightIsOnBeforeAndAfterTheExposure()
    {
        var c = new CameraSettings { ExposureUs = 800, TriggerDelayUs = 0 };
        c.Light.PreOnUs = 100;
        c.Light.PostOnUs = 50;
        var t = LightTiming.For(c);
        Assert.Equal(100, t.TriggerDelayUs); // the exposure waits for the light
        Assert.Equal(0, t.LightOnAtUs);
        Assert.Equal(950, t.LightOffAtUs);
        Assert.Equal(950, t.LightOnForUs);
        Assert.Equal(5, t.PlcSteps().Count);
        Assert.Equal(6, t.PlcSteps(3).Count);
        Assert.Contains("3 times per case", t.PlcSteps(3)[5]);
    }

    [Fact]
    public void TheLightsLongestPulseLimitsTheExposure()
    {
        var p = Product();
        p.ChildCamera.ExposureUs = 2000;
        p.ChildCamera.Light.MaxOnUs = 1150; // 100 before + 1000 exposure + 50 after
        var r = Calibrator.Plan(p, new[] { Frame(500, 400, 170) });
        Assert.Equal(1000, r.Proposed.ExposureUs, 0);
        Assert.True(r.Light.LightOnForUs <= 1150);
    }

    [Fact]
    public void AdvisorWarnsWhenCodesReachTheCropEdge()
    {
        var advisor = new LineAdvisor();
        var cam = new CameraSettings { Roi = new RoiSettings { X = 520, Y = 0, Width = 1000, Height = 1000 } };
        for (int i = 0; i < 10; i++) advisor.Add(Frame(500, 400, 170).Result); // left column at x 500..560
        Assert.Contains(advisor.Advise(cam, 500), a => a.Code == "crop-edge");
        cam.Roi = new RoiSettings { X = 0, Y = 0, Width = 2000, Height = 1500 };
        Assert.DoesNotContain(advisor.Advise(cam, 500), a => a.Code == "crop-edge");
    }
}
