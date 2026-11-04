using System;
using System.Collections.Generic;

namespace BarcodeReading;

/// <summary>One frame of a test burst: its exposure and gain, the light pulse that covers the
/// exposure, and the brightness it gives against the calibrated setting (1 = calibrated).</summary>
public sealed record BurstShot(float ExposureUs, float GainDb, float LightOnForUs, float Brightness);

/// <summary>
/// The "Save test frames" burst: frames of varied brightness around the calibrated setting
/// (1x, 0.6x, 1.6x, ...), so a code under glare gets a darker frame and a faint code a brighter
/// one, and the multi-frame reader has a usable frame of each. Brightness comes from exposure
/// where the light and the motion allow it, else from gain; the light is always on for longer
/// than the exposure it covers.
/// </summary>
public static class TestBurst
{
    /// <summary>Brightness of each frame against the calibrated setting, in order.</summary>
    public static readonly float[] Brightness = { 1f, 0.6f, 1.6f, 0.8f, 1.3f, 0.45f, 2f, 1f };

    /// <summary>Shortest exposure used (as the calibrator).</summary>
    public const float MinExposureUs = 20;

    public static IReadOnlyList<BurstShot> Plan(CameraSettings cam, int count)
    {
        count = Math.Clamp(count, 1, Brightness.Length);
        float e0 = cam.ExposureUs, g0 = cam.GainDb;
        float maxUs = MaxExposureUs(cam);
        var shots = new List<BurstShot>(count);
        for (int i = 0; i < count; i++)
        {
            float want = Brightness[i];
            float e = Math.Clamp(e0 * want, MinExposureUs, Math.Max(maxUs, e0));
            // the rest from gain: up to the calibrator's limit, never below 0 dB
            float g = g0 + 20f * MathF.Log10(e0 * want / e);
            g = Math.Clamp(g, 0f, Math.Max(g0, Calibrator.MaxGainDb));
            e = MathF.Round(e);
            g = MathF.Round(g * 10) / 10;
            var c = cam.Clone();
            c.ExposureUs = e;
            float got = e / e0 * MathF.Pow(10f, (g - g0) / 20f);
            shots.Add(new BurstShot(e, g, LightTiming.For(c).LightOnForUs, got));
        }
        return shots;
    }

    /// <summary>
    /// The longest exposure a burst frame may take. A PLC-switched light is programmed for the
    /// calibrated exposure, so no frame may be longer than that; a camera-strobed light follows
    /// each frame's exposure up to its longest pulse. A moving case limits the exposure to half a
    /// module of blur, and one whose speed or module size is not measured to the calibrated one.
    /// </summary>
    public static float MaxExposureUs(CameraSettings cam)
    {
        float max = float.MaxValue;
        if (cam.Light.Control == LightControl.Plc) max = cam.ExposureUs;
        if (cam.Light.MaxOnUs > 0) max = Math.Min(max, cam.Light.MaxOnUs - cam.Light.PreOnUs - cam.Light.PostOnUs);
        if (cam.Motion == CaseMotion.Moving)
        {
            float modulePx = cam.CodeModules > 0 ? cam.CodeSidePx / cam.CodeModules : 0;
            max = cam.SpeedPxPerMs > 0 && modulePx > 0
                ? Math.Min(max, Calibrator.MaxBlurModules * modulePx / cam.SpeedPxPerMs * 1000f)
                : Math.Min(max, cam.ExposureUs);
        }
        return Math.Max(max, MinExposureUs);
    }
}
