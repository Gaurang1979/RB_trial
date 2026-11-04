using System;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class TestBurstTests
{
    private static CameraSettings Camera(LightControl light = LightControl.CameraStrobe) => new()
    {
        ExposureUs = 1000,
        GainDb = 0,
        Light = new LightSettings { Control = light, PreOnUs = 100, PostOnUs = 50 },
    };

    [Fact]
    public void AStillCaseWithACameraStrobeVariesTheExposure()
    {
        var shots = TestBurst.Plan(Camera(), 3);
        Assert.Equal(new[] { 1000f, 600f, 1600f }, new[] { shots[0].ExposureUs, shots[1].ExposureUs, shots[2].ExposureUs });
        Assert.All(shots, s => Assert.Equal(0f, s.GainDb));
        Assert.Equal(1.6f, shots[2].Brightness, 2);
        // the light covers every exposure: on before, during and after
        Assert.All(shots, s => Assert.Equal(s.ExposureUs + 150, s.LightOnForUs));
    }

    [Fact]
    public void APlcLightKeepsItsPulseSoTheBrightFrameUsesGain()
    {
        // the PLC's light pulse is programmed for the calibrated exposure: no frame may be longer
        var shots = TestBurst.Plan(Camera(LightControl.Plc), 3);
        Assert.Equal(600f, shots[1].ExposureUs);
        Assert.Equal(1000f, shots[2].ExposureUs);
        Assert.Equal(20 * MathF.Log10(1.6f), shots[2].GainDb, 1);
        Assert.Equal(1.6f, shots[2].Brightness, 1);
        Assert.All(shots, s => Assert.True(s.LightOnForUs <= LightTiming.For(Camera(LightControl.Plc)).LightOnForUs));
    }

    [Fact]
    public void TheLightsLongestPulseAndMotionBlurLimitTheExposure()
    {
        var c = Camera();
        c.Light.MaxOnUs = 1450; // 100 + 1300 + 50
        var shots = TestBurst.Plan(c, 3);
        Assert.Equal(1300f, shots[2].ExposureUs);
        Assert.Equal(20 * MathF.Log10(1600f / 1300f), shots[2].GainDb, 1);

        var m = Camera();
        m.Motion = CaseMotion.Moving;
        m.SpeedPxPerMs = 1.5f;
        m.CodeModules = 21;
        m.CodeSidePx = 63; // 3 px modules: half a module of blur is 1000 us at 1.5 px/ms
        shots = TestBurst.Plan(m, 3);
        Assert.Equal(1000f, shots[2].ExposureUs);
        Assert.True(shots[2].GainDb > 0);

        // moving, speed not measured: never longer than calibrated
        m.SpeedPxPerMs = 0;
        Assert.Equal(1000f, TestBurst.Plan(m, 3)[2].ExposureUs);
    }

    [Fact]
    public void GainStaysWithinTheCalibratorsLimit()
    {
        var c = Camera(LightControl.Plc);
        c.GainDb = 10;
        var shots = TestBurst.Plan(c, 7);
        Assert.All(shots, s => Assert.InRange(s.GainDb, 0f, Calibrator.MaxGainDb));
        Assert.Equal(7, shots.Count);
        Assert.Equal(1f, shots[0].Brightness, 2);
    }
}
