using System;
using System.Collections.Generic;
using System.Linq;

namespace BarcodeReading;

/// <summary>
/// One calibration image (8-bit grey). <paramref name="Scale"/> is how many full-resolution pixels
/// one pixel of this image covers along each axis (2 for a decimated probe frame, 1 otherwise).
/// </summary>
public sealed record CalibrationShot(byte[] Pixels, int Width, int Height, int Scale, double TimeMs);

/// <summary>
/// A camera calibration can drive: frames at a given exposure and gain, by software trigger, with
/// the line stopped. The light pulse follows the exposure (it stays on PreOnUs before and PostOnUs
/// after). Probe frames may come back decimated (a faster, smaller image); the shot says so.
/// </summary>
public interface ICalibrationCamera
{
    IReadOnlyList<CalibrationShot> Capture(float exposureUs, float gainDb, int count, int intervalMs, bool probe);
}

/// <summary>Brightness of an image area (0-255): black level, percentiles, and the fraction at the sensor maximum.</summary>
public sealed record BrightnessStats(float Black, float P05, float P50, float P95, float P98, float Saturated)
{
    /// <summary>Spread between the dark and the bright pixels: codes need some.</summary>
    public float Spread => P95 - P05;
}

/// <summary>One exposure / gain tried by the calibration search.</summary>
public sealed record CalibrationTrial(
    string Stage,
    float ExposureUs,
    float GainDb,
    int CodesRead,
    float LabelBrightness,
    float CodeSaturation,
    float Score,
    string Verdict)
{
    /// <summary>False when the trial was dropped (no contrast) or rejected (clipped codes).</summary>
    public bool Usable => Score >= 0;
}

/// <summary>Options for <see cref="CalibrationSearch"/>; the defaults are the station's.</summary>
public sealed class CalibrationSearchOptions
{
    /// <summary>Take a short probe frame to set the starting exposure (false: halve / double from the start, for comparison).</summary>
    public bool Probe { get; set; } = true;
    /// <summary>Most decoded trials (the probe frames are extra and cheap).</summary>
    public int MaxTrials { get; set; } = 6;
    /// <summary>Most probe frames (a clipped or black probe is repeated shorter or longer).</summary>
    public int MaxProbes { get; set; } = 3;
    /// <summary>Hard limit: a setting with more of the code pixels at the sensor maximum is rejected, however well it reads.</summary>
    public float MaxCodeSaturation { get; set; } = 0.02f;
    /// <summary>Pre-check: bright minus dark (95th - 5th percentile) below this many grey levels is not decoded at all.</summary>
    public float MinSpread { get; set; } = 24;
    /// <summary>Label brightness within this of the target counts as good.</summary>
    public float BrightnessTolerance { get; set; } = 30;
}

/// <summary>
/// The search the Calibrator runs on a camera before planning (after Cognex's auto-tune ideas, in
/// our own form, see docs/PATENTS.md): (1) the product's last-good settings first, kept when they
/// still read well; (2) a short probe frame, a separate frame at one exposure and gain for the
/// whole sensor, whose black level and bright percentile set the starting exposure and gain;
/// (3) decoded trials of exposure and gain, each refined from the brightness the reader measured.
/// A trial is dropped before decoding when its codes clearly lack contrast, and rejected when more
/// than <see cref="CalibrationSearchOptions.MaxCodeSaturation"/> of the code pixels are at the
/// sensor maximum. Light timing (from the chosen exposure) and the decoder's options (crop, code
/// size) follow in <see cref="Calibrator.Plan"/>.
/// </summary>
public static class CalibrationSearch
{
    public const int SensorMax = 255;

    /// <summary>The finished search: the setting chosen, the frames taken at it, and every trial.</summary>
    public sealed record Outcome(
        float ExposureUs,
        float GainDb,
        IReadOnlyList<CalibrationFrame> Frames,
        CalibrationShot Shot,
        IReadOnlyList<CalibrationTrial> Trials,
        int ProbeFrames,
        int DecodedTrials,
        bool FromCache);

    public static Outcome Run(ProductMaster product, ICalibrationCamera camera, Func<CalibrationShot, DecodeResult> decode,
                              CalibrationSearchOptions? options = null)
    {
        var o = options ?? new CalibrationSearchOptions();
        var cam = product.ChildCamera;
        int expected = product.CodesPerCase;
        float maxExposure = MaxExposureUs(cam);
        var trials = new List<CalibrationTrial>();
        int probes = 0, decoded = 0;
        RoiSettings? area = cam.Roi; // where the codes are: the case crop, then the codes once read
        (CalibrationTrial Trial, CalibrationShot Shot, DecodeResult Result, float Exposure, float Gain)? best = null;

        // one decoded trial; returns the light to try next (null: this one is good, stop)
        float? Try(string stage, float exposure, float gain)
        {
            float light = Light(exposure, gain);
            var shot = camera.Capture(exposure, gain, 1, 0, probe: false)[0];
            decoded++;
            var stats = Measure(shot, area);
            if (stats.Spread < o.MinSpread)
            {
                trials.Add(new(stage, exposure, gain, 0, 0, stats.Saturated, -1,
                    $"dropped before decoding: no contrast ({stats.P05:0}-{stats.P95:0})"));
                return light * NextFromStats(stats, o);
            }
            var r = decode(shot);
            float sat = r.Codes.Count > 0 ? CodeSaturation(shot, r.Codes) : stats.Saturated;
            float bright = r.Codes.Count > 0 && r.Stats.LabelBrightness > 1 ? r.Stats.LabelBrightness : stats.P98;
            float score = Score(r, expected, bright, sat, gain, o);
            bool readAll = expected <= 0 ? r.Codes.Count > 0 : r.Codes.Count >= expected;
            bool good = score >= 0 && readAll && Math.Abs(bright - Calibrator.TargetBrightness) <= o.BrightnessTolerance;
            string verdict = score < 0 ? $"rejected: {sat:P1} of the code pixels clipped (limit {o.MaxCodeSaturation:P0})"
                : good ? "good" : !readAll ? "not all codes read" : $"labels at {bright:0}, target {Calibrator.TargetBrightness:0}";
            var t = new CalibrationTrial(stage, exposure, gain, r.Codes.Count, bright, sat, score, verdict);
            trials.Add(t);
            if (r.Codes.Count > 0) area = CodeArea(r.Codes, shot);
            if (score >= 0 && (best is null || score > best.Value.Trial.Score)) best = (t, shot, r, exposure, gain);
            if (good) return null;
            float f = r.Codes.Count > 0 && r.Stats.LabelBrightness > 1
                ? Math.Clamp(Calibrator.TargetBrightness / r.Stats.LabelBrightness, 0.25f, 4f)
                : NextFromStats(stats, o);
            if (sat > o.MaxCodeSaturation) f = Math.Min(f, 0.7f);
            return light * f;
        }

        // ---- 1. the product's last-good setting (kept when it still reads well)
        float? next = null;
        bool fromCache = false;
        if (cam.CalibratedUtc is not null)
        {
            next = Try("last good", cam.ExposureUs, cam.GainDb);
            fromCache = next is null;
        }

        // ---- 2. probe: one short frame (decimated when the camera can) sets the starting light
        if (!fromCache)
        {
            float light = next ?? Light(cam.ExposureUs, cam.GainDb);
            if (o.Probe)
            {
                light /= 2; // a little under: a clipped probe says less than a dark one
                for (int i = 0; i < o.MaxProbes; i++)
                {
                    var (pe, pg) = Split(light, maxExposure);
                    var shot = camera.Capture(pe, pg, 1, 0, probe: true)[0];
                    probes++;
                    var s = Measure(shot, area);
                    float f = ProbeFactor(s);
                    light *= f;
                    if (s.P98 < SensorMax - 5 && s.P98 - s.Black >= 16) break; // measured, not clipped or lost in the noise
                }
            }

            // ---- 3. exposure and gain
            for (int i = 0; i < o.MaxTrials; i++)
            {
                var (e, g) = Split(light, maxExposure);
                var n = Try("exposure", e, g);
                if (n is null) break;
                if (Math.Abs(n.Value / light - 1) < 0.05f) break; // no better setting to try
                light = n.Value;
            }
        }

        if (best is null)
        {
            var last = trials.LastOrDefault();
            throw new InvalidOperationException(last is null ? "calibration took no frames"
                : $"no usable setting found in {trials.Count} trials (last: {last.Verdict}): check the light, the aim and the focus");
        }
        var b = best.Value;
        var frames = new List<CalibrationFrame>
        {
            new(b.Result, b.Shot.Width, b.Shot.Height, b.Shot.TimeMs, b.Trial.CodeSaturation),
        };
        return new Outcome(b.Exposure, b.Gain, frames, b.Shot, trials, probes, decoded, fromCache);
    }

    /// <summary>Exposure x gain factor: the light a setting collects, in exposure microseconds at 0 dB.</summary>
    public static float Light(float exposureUs, float gainDb) => exposureUs * MathF.Pow(10, gainDb / 20);

    /// <summary>
    /// Exposure first, gain only for what the longest exposure cannot collect (noise grows with
    /// gain), and no more than <see cref="Calibrator.MaxGainDb"/>.
    /// </summary>
    public static (float ExposureUs, float GainDb) Split(float light, float maxExposureUs)
    {
        float e = Math.Clamp(light, 20, Math.Max(20, maxExposureUs));
        float g = light > e ? Math.Min(20 * MathF.Log10(light / e), Calibrator.MaxGainDb) : 0;
        return (MathF.Round(e), MathF.Round(g * 10) / 10);
    }

    /// <summary>
    /// The longest exposure the light and the motion allow: the light's longest pulse less the
    /// time it is on before and after, and half a module of blur for a moving case of known speed.
    /// </summary>
    public static float MaxExposureUs(CameraSettings cam)
    {
        float max = 100_000;
        if (cam.Light.MaxOnUs > 0) max = Math.Min(max, cam.Light.MaxOnUs - cam.Light.PreOnUs - cam.Light.PostOnUs);
        if (cam.Motion == CaseMotion.Moving && cam.SpeedPxPerMs > 0 && cam.CodeModules > 0 && cam.CodeSidePx > 0)
            max = Math.Min(max, Calibrator.MaxBlurModules * (cam.CodeSidePx / cam.CodeModules) / cam.SpeedPxPerMs * 1000f);
        return Math.Max(max, 20);
    }

    /// <summary>
    /// Light factor from a probe: the bright labels (98th percentile above the black level) to the
    /// target. A clipped probe only says "darker"; one lost in the noise only says "brighter".
    /// </summary>
    public static float ProbeFactor(BrightnessStats s)
    {
        float signal = s.P98 - s.Black;
        if (s.P98 >= SensorMax - 5) return 0.25f;
        if (signal < 16) return signal < 2 ? 16f : Math.Min(16f, (Calibrator.TargetBrightness - s.Black) / signal);
        return Math.Clamp((Calibrator.TargetBrightness - s.Black) / signal, 1 / 16f, 16f);
    }

    // a frame with nothing read: probe arithmetic on its own pixels, or (without the probe) only
    // the direction, halving or doubling
    private static float NextFromStats(BrightnessStats s, CalibrationSearchOptions o) =>
        o.Probe ? Math.Clamp(ProbeFactor(s), 0.25f, 4f) : ProbeFactor(s) >= 1 ? 2f : 0.5f;

    /// <summary>
    /// Read rate first, then code contrast, then closeness to the target brightness, less a little
    /// for gain (noise). Negative (rejected) when the codes are clipped beyond the hard limit.
    /// </summary>
    public static float Score(DecodeResult r, int expected, float brightness, float codeSaturation, float gainDb, CalibrationSearchOptions o)
    {
        if (codeSaturation > o.MaxCodeSaturation) return -1;
        float rate = expected > 0 ? Math.Min(1f, (float)r.Codes.Count / expected) : (r.Codes.Count > 0 ? 1 : 0);
        var contrasts = r.Codes.Select(c => c.Quality?.SymbolContrast ?? 0f).Where(v => v > 0).ToList();
        float contrast = contrasts.Count > 0 ? contrasts.Average() : 0;
        return Math.Max(0, 100 * rate + 0.2f * contrast - 0.1f * Math.Abs(brightness - Calibrator.TargetBrightness) - 0.5f * gainDb);
    }

    /// <summary>
    /// Brightness of an area of the shot (full-resolution coordinates; null = the whole image),
    /// from at most about 250 000 sampled pixels. The black level is the darkest 0.5% of the whole
    /// image (capped at 32: a frame with nothing dark in it has no black to measure).
    /// </summary>
    public static BrightnessStats Measure(CalibrationShot shot, RoiSettings? area)
    {
        var whole = Histogram(shot, 0, 0, shot.Width, shot.Height);
        int x0 = 0, y0 = 0, x1 = shot.Width, y1 = shot.Height;
        if (area is not null)
        {
            x0 = Math.Clamp(area.X / shot.Scale, 0, shot.Width);
            y0 = Math.Clamp(area.Y / shot.Scale, 0, shot.Height);
            x1 = Math.Clamp((area.X + area.Width) / shot.Scale, x0, shot.Width);
            y1 = Math.Clamp((area.Y + area.Height) / shot.Scale, y0, shot.Height);
        }
        var h = area is null || x1 - x0 < 4 || y1 - y0 < 4 ? whole : Histogram(shot, x0, y0, x1, y1);
        long n = h.Sum();
        float black = Math.Min(Percentile(whole, 0.005), 32);
        return new BrightnessStats(black, Percentile(h, 0.05), Percentile(h, 0.5), Percentile(h, 0.95), Percentile(h, 0.98),
            n == 0 ? 0 : (float)h[SensorMax] / n);
    }

    /// <summary>Fraction of the pixels inside the read codes that are at the sensor maximum.</summary>
    public static float CodeSaturation(CalibrationShot shot, IReadOnlyList<DecodedCode> codes)
    {
        long all = 0, clipped = 0;
        foreach (var c in codes)
        {
            int x0 = Math.Clamp((int)(c.Corners.Min(p => p.X) / shot.Scale), 0, shot.Width);
            int x1 = Math.Clamp((int)MathF.Ceiling(c.Corners.Max(p => p.X) / shot.Scale), x0, shot.Width);
            int y0 = Math.Clamp((int)(c.Corners.Min(p => p.Y) / shot.Scale), 0, shot.Height);
            int y1 = Math.Clamp((int)MathF.Ceiling(c.Corners.Max(p => p.Y) / shot.Scale), y0, shot.Height);
            int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 4096));
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    all++;
                    if (shot.Pixels[y * shot.Width + x] >= SensorMax) clipped++;
                }
        }
        return all == 0 ? 0 : (float)clipped / all;
    }

    // the box around the read codes, in full-resolution pixels
    private static RoiSettings CodeArea(IReadOnlyList<DecodedCode> codes, CalibrationShot shot)
    {
        float x0 = codes.Min(c => c.Corners.Min(p => p.X)), x1 = codes.Max(c => c.Corners.Max(p => p.X));
        float y0 = codes.Min(c => c.Corners.Min(p => p.Y)), y1 = codes.Max(c => c.Corners.Max(p => p.Y));
        return new RoiSettings { X = (int)x0, Y = (int)y0, Width = (int)MathF.Ceiling(x1 - x0), Height = (int)MathF.Ceiling(y1 - y0) };
    }

    private static long[] Histogram(CalibrationShot shot, int x0, int y0, int x1, int y1)
    {
        var h = new long[SensorMax + 1];
        int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 250_000));
        for (int y = y0; y < y1; y += step)
        {
            int row = y * shot.Width;
            for (int x = x0; x < x1; x += step) h[shot.Pixels[row + x]]++;
        }
        return h;
    }

    private static float Percentile(long[] h, double q)
    {
        long n = h.Sum();
        if (n == 0) return 0;
        long want = (long)Math.Ceiling(q * n), seen = 0;
        for (int v = 0; v < h.Length; v++)
        {
            seen += h[v];
            if (seen >= Math.Max(1, want)) return v;
        }
        return SensorMax;
    }
}
