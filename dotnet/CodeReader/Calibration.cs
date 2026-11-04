using System;
using System.Collections.Generic;
using System.Linq;

namespace BarcodeReading;

/// <summary>
/// The light's timing for one case, in microseconds from the trigger (case sensor or PLC output).
/// The light must be fully on for the whole exposure.
/// </summary>
public sealed record LightTiming(
    LightControl Control,
    float TriggerDelayUs,
    float ExposureUs,
    float LightOnAtUs,
    float LightOffAtUs)
{
    public float LightOnForUs => LightOffAtUs - LightOnAtUs;
    public float ExposureEndUs => TriggerDelayUs + ExposureUs;

    /// <summary>
    /// The timing for a camera setting. The light comes on PreOnUs before the exposure, so the
    /// exposure starts no earlier than PreOnUs after the trigger (the light cannot switch on before
    /// the trigger has happened).
    /// </summary>
    public static LightTiming For(CameraSettings c)
    {
        float delay = Math.Max(c.TriggerDelayUs, c.Light.PreOnUs);
        return new LightTiming(c.Light.Control, delay, c.ExposureUs, delay - c.Light.PreOnUs,
            delay + c.ExposureUs + c.Light.PostOnUs);
    }

    /// <summary>The lines a PLC programmer needs (times from the trigger). framesPerCase above 1:
    /// the whole sequence repeats that many times per case.</summary>
    public IReadOnlyList<string> PlcSteps(int framesPerCase = 1)
    {
        var steps = new List<string>
        {
            $"t = 0: trigger (case in position) -> camera trigger input (Line0)",
            $"t = {LightOnAtUs / 1000:0.###} ms: light ON",
            $"t = {TriggerDelayUs / 1000:0.###} ms: camera exposure starts (camera trigger delay {TriggerDelayUs:0} us)",
            $"t = {ExposureEndUs / 1000:0.###} ms: exposure ends ({ExposureUs:0} us)",
            $"t = {LightOffAtUs / 1000:0.###} ms: light OFF (on for {LightOnForUs / 1000:0.###} ms)",
        };
        if (framesPerCase > 1)
        {
            float period = LightOffAtUs / 1000 + 2;
            steps.Add($"repeat the trigger, light and exposure {framesPerCase} times per case, one every {period:0.###} ms; the station reads the {framesPerCase} frames together");
        }
        return steps;
    }
}

/// <summary>One frame taken for calibration (8-bit grey), and when it was taken. CodeSaturation:
/// the fraction of the code pixels at the sensor maximum, when measured (-1 = not measured).</summary>
public sealed record CalibrationFrame(DecodeResult Result, int Width, int Height, double TimeMs, float CodeSaturation = -1);

public sealed record CalibrationCheck(string Name, bool Ok, string Message);

public sealed record CalibrationResult(
    CameraSettings Proposed,
    LightTiming Light,
    int CodesRead,
    int Expected,
    float LabelBrightness,
    float ModulePx,
    float SpeedPxPerMs,
    int BestFrame,
    IReadOnlyList<CalibrationCheck> Checks)
{
    /// <summary>True when every check passed (the proposal can be applied as it is).</summary>
    public bool AllOk => Checks.All(c => c.Ok);

    /// <summary>The settings the camera search tried, in order (empty for a saved image).</summary>
    public IReadOnlyList<CalibrationTrial> Trials { get; init; } = Array.Empty<CalibrationTrial>();
    /// <summary>Probe frames the search took (short, not decoded).</summary>
    public int ProbeFrames { get; init; }
    /// <summary>What the reader found in the frame the proposal is from (BestFrame).</summary>
    public DecodeResult? BestDecode { get; init; }
    /// <summary>The camera image the proposal is from (BestFrame); null when planned from frames given.</summary>
    public CalibrationShot? Shot { get; init; }
}

/// <summary>
/// Daily auto-calibration: from one frame of a case standing still (or two frames of a moving
/// case, a known time apart) it proposes the exposure and gain, the region of interest around the
/// case, the trigger delay and the light timing. Pure logic over decode results, so it is the same
/// for the camera and for saved images.
/// </summary>
public static class Calibrator
{
    /// <summary>Labels should be at this brightness (0-255): bright, but below glare.</summary>
    public const float TargetBrightness = 170;
    /// <summary>Most gain added to make up for a shorter exposure (noise grows with gain).</summary>
    public const float MaxGainDb = 12;
    /// <summary>Most motion blur allowed during the exposure, in modules.</summary>
    public const float MaxBlurModules = 0.5f;

    /// <summary>Reader settings for calibration frames: whole image, generous time, quality measured.</summary>
    public static ReaderSettings DecodeSettings(ProductMaster p)
    {
        var s = p.ChildReaderSettings();
        s.Roi = null;
        s.TimeBudgetMs = 3000;
        s.GradeQuality = true;
        return s;
    }

    /// <summary>
    /// Calibration on a camera: <see cref="CalibrationSearch"/> finds the exposure and gain (last
    /// good settings first, then a probe frame, then decoded trials with the saturation limit), and
    /// <see cref="Plan"/> works out the light timing, crop and code size from the frames at it.
    /// </summary>
    public static CalibrationResult Tune(ProductMaster product, ICalibrationCamera camera, Func<CalibrationShot, DecodeResult> decode,
                                         string? user = null, CalibrationSearchOptions? options = null)
    {
        var o = options ?? new CalibrationSearchOptions();
        var found = CalibrationSearch.Run(product, camera, decode, o);
        var start = product.ChildCamera.Clone();
        start.ExposureUs = found.ExposureUs;
        start.GainDb = found.GainDb;
        var frames = found.Frames.ToList();
        var shots = new List<CalibrationShot> { found.Shot };
        if (start.Motion == CaseMotion.Moving)
        {
            // the speed: two frames of the moving case, 80 ms apart, at the setting found
            shots = camera.Capture(found.ExposureUs, found.GainDb, 2, 80, probe: false).ToList();
            frames = shots.Select(s =>
            {
                var r = decode(s);
                return new CalibrationFrame(r, s.Width, s.Height, s.TimeMs, CalibrationSearch.CodeSaturation(s, r.Codes));
            }).ToList();
        }
        var result = Plan(product, frames, user, start, o.MaxCodeSaturation);
        string how = found.FromCache ? "the last good setting still reads well"
            : $"{found.ProbeFrames} probe frame{(found.ProbeFrames == 1 ? "" : "s")}, {found.DecodedTrials} decoded trial{(found.DecodedTrials == 1 ? "" : "s")}";
        var checks = result.Checks.Prepend(new CalibrationCheck("Search", true,
            $"{how}; chose {found.ExposureUs:0} us at {found.GainDb:0.0} dB")).ToList();
        return result with { Checks = checks, Trials = found.Trials, ProbeFrames = found.ProbeFrames, Shot = shots[result.BestFrame] };
    }

    /// <summary>
    /// The proposal from frames already taken. <paramref name="start"/>: the camera settings the
    /// frames were taken at (default: the product's). A frame whose codes are clipped beyond
    /// <paramref name="maxCodeSaturation"/> fails the brightness check and makes the proposal darker.
    /// </summary>
    public static CalibrationResult Plan(ProductMaster product, IReadOnlyList<CalibrationFrame> frames, string? user = null,
                                         CameraSettings? start = null, float maxCodeSaturation = 0.02f)
    {
        if (frames.Count == 0) throw new ArgumentException("calibration needs at least one frame");
        var cam = start ?? product.ChildCamera;
        var proposed = cam.Clone();
        var checks = new List<CalibrationCheck>();
        int expected = product.CodesPerCase;

        int bestIndex = 0;
        for (int i = 1; i < frames.Count; i++)
            if (frames[i].Result.Codes.Count > frames[bestIndex].Result.Codes.Count) bestIndex = i;
        var best = frames[bestIndex];
        var codes = best.Result.Codes;
        int read = codes.Count;
        checks.Add(new("Codes read", expected <= 0 || read >= expected,
            expected > 0 ? $"{read} of {expected} codes read" : $"{read} codes read"));

        // ---- brightness -> exposure (and gain)
        float brightness = best.Result.Stats.LabelBrightness;
        float glare = Median(codes.Select(c => c.Quality?.Saturated ?? 0f));
        float scale = brightness <= 1 ? 4f : Math.Clamp(TargetBrightness / brightness, 0.25f, 4f);
        if (glare > 0.05f) scale = Math.Min(scale, 0.7f); // glare on the labels: darker, whatever the average says
        bool clipped = best.CodeSaturation > maxCodeSaturation; // hard limit on code pixels at the sensor maximum
        if (clipped) scale = Math.Min(scale, 0.7f);
        // light needed = exposure x gain factor; exposure first, gain only when exposure is capped
        float wantExposure = cam.ExposureUs * MathF.Pow(10, cam.GainDb / 20) * scale;
        checks.Add(new("Brightness", Math.Abs(brightness - TargetBrightness) <= 30 && glare <= 0.05f && !clipped,
            $"labels at {brightness:0}/255 (target {TargetBrightness:0})" + (glare > 0.05f ? $", glare on {glare:P0} of the modules" : "") +
            (clipped ? $", {best.CodeSaturation:P1} of the code pixels clipped (limit {maxCodeSaturation:P0})" : "") +
            $"; exposure {cam.ExposureUs:0} -> {wantExposure:0} us"));

        // ---- module size (resolution) and focus
        float modulePx = Median(codes.Select(c => c.Quality?.ModuleSizePx ?? 0f).Where(v => v > 0));
        bool moduleMeasured = modulePx > 0;
        if (modulePx <= 0 && codes.Count > 0) modulePx = Median(codes.Select(c => c.SidePx)) / 21f;
        checks.Add(new("Resolution", modulePx >= 2.5f || codes.Count == 0,
            codes.Count == 0 ? "no code read: check the camera is aimed at the case and focused"
            : modulePx >= 2.5f ? $"{modulePx:0.0} px per module"
            : $"{modulePx:0.0} px per module is too small (2.5 or more reads reliably): move the camera closer or use a longer lens"));
        float contrast = Median(codes.Select(c => c.Quality?.SymbolContrast ?? 0f).Where(v => v > 0));
        if (contrast > 0)
            checks.Add(new("Contrast", contrast >= 40, $"code contrast {contrast:0}%" + (contrast < 40 ? ": check focus, glare and light angle" : "")));

        // ---- motion: speed and direction from the same codes in two frames
        float speed = cam.SpeedPxPerMs;
        float dirX = cam.MotionDirX, dirY = cam.MotionDirY;
        if (frames.Count >= 2)
        {
            var first = frames[0];
            var last = frames[^1];
            double dt = last.TimeMs - first.TimeMs;
            var before = first.Result.Codes.GroupBy(c => c.Text).ToDictionary(g => g.Key, g => g.First().Center);
            var moves = last.Result.Codes.Where(c => before.ContainsKey(c.Text))
                .Select(c => (dx: c.Center.X - before[c.Text].X, dy: c.Center.Y - before[c.Text].Y)).ToList();
            if (dt > 0 && moves.Count >= 3)
            {
                float mx = Median(moves.Select(m => m.dx)), my = Median(moves.Select(m => m.dy));
                float dist = MathF.Sqrt(mx * mx + my * my);
                speed = (float)(dist / dt);
                if (dist > 1)
                {
                    dirX = mx / dist;
                    dirY = my / dist;
                }
                if (speed > 0.01f) proposed.Motion = CaseMotion.Moving;
                else if (cam.Motion == CaseMotion.Moving)
                {
                    speed = cam.SpeedPxPerMs; // keep the last measured speed
                    checks.Add(new("Motion", false, "the case did not move between the frames: run a case through while calibrating a moving line"));
                }
            }
            else if (cam.Motion == CaseMotion.Moving)
                checks.Add(new("Motion", false, "the speed could not be measured (the same codes were not read in two frames)"));
        }
        proposed.SpeedPxPerMs = speed;
        proposed.MotionDirX = dirX;
        proposed.MotionDirY = dirY;
        bool moving = proposed.Motion == CaseMotion.Moving && speed > 0;

        // the exposure may blur a moving code by at most half a module; gain makes up the light
        float exposure = wantExposure;
        if (moving && modulePx > 0)
        {
            float maxUs = MaxBlurModules * modulePx / speed * 1000f;
            if (exposure > maxUs) exposure = maxUs;
            checks.Add(new("Motion blur", true,
                $"case moves {speed * 1000:0} px/s; exposure at most {maxUs:0} us for {MaxBlurModules} module of blur"));
        }
        // and the light's longest pulse
        if (cam.Light.MaxOnUs > 0)
            exposure = Math.Min(exposure, cam.Light.MaxOnUs - cam.Light.PreOnUs - cam.Light.PostOnUs);
        exposure = Math.Max(exposure, 20);
        float gain = exposure < wantExposure ? 20f * MathF.Log10(wantExposure / exposure) : 0f;
        bool gainOk = gain <= MaxGainDb;
        gain = Math.Min(gain, MaxGainDb);
        if (!gainOk)
            checks.Add(new("Light", false, "not enough light for this speed: more light, a slower conveyor, or a stop under the camera"));
        proposed.ExposureUs = MathF.Round(exposure);
        proposed.GainDb = MathF.Round(gain * 10) / 10;

        // ---- region of interest: the case and a margin; wider along the motion (cases stop or
        // pass at slightly different places)
        var roi = CaseRoi(best, codes, moving ? (dirX, dirY) : null);
        bool enough = expected <= 0 ? codes.Count > 0 : codes.Count >= expected * 0.8;
        if (roi is not null && enough)
        {
            proposed.Roi = roi;
            double saved = 1 - (double)roi.Width * roi.Height / ((double)best.Width * best.Height);
            checks.Add(new("Crop", true, $"processing {roi.Width} x {roi.Height} at ({roi.X}, {roi.Y}); {saved:P0} of the image is skipped"));
        }
        else
        {
            proposed.Roi = null;
            checks.Add(new("Crop", enough, enough ? "the case fills the image: no crop" : "too few codes read to place the crop safely: whole image kept"));
        }

        // ---- trigger delay: a moving case should be in the middle of the image
        if (moving && codes.Count > 0)
        {
            float cx = Median(codes.Select(c => c.Center.X)) - best.Width / 2f;
            float cy = Median(codes.Select(c => c.Center.Y)) - best.Height / 2f;
            float along = cx * dirX + cy * dirY; // > 0: the case is past the centre, trigger earlier
            float shiftUs = along / speed * 1000f;
            proposed.TriggerDelayUs = MathF.Max(0, MathF.Round(cam.TriggerDelayUs - shiftUs));
            checks.Add(new("Case position", MathF.Abs(along) < 0.1f * Math.Max(best.Width, best.Height),
                $"case centre {along:0} px from the image centre along the motion; trigger delay {cam.TriggerDelayUs:0} -> {proposed.TriggerDelayUs:0} us"));
        }

        var light = LightTiming.For(proposed);
        proposed.TriggerDelayUs = light.TriggerDelayUs;
        // the light covers the whole exposure (by construction) and stays within its longest pulse
        bool pulseOk = cam.Light.MaxOnUs <= 0 || light.LightOnForUs <= cam.Light.MaxOnUs + 0.5f;
        checks.Add(new("Light timing", pulseOk,
            $"light on at {light.LightOnAtUs / 1000:0.###} ms, for {light.LightOnForUs / 1000:0.###} ms, off at {light.LightOffAtUs / 1000:0.###} ms after the trigger" +
            (pulseOk ? "" : $": longer than the light's longest pulse ({cam.Light.MaxOnUs:0} us)")));

        proposed.CalibratedUtc = DateTime.UtcNow;
        proposed.CalibratedBy = user;
        // the measured code size on the proposal: a new reader starts from it (first-case reads)
        // only from a measured module size (the side / 21 fallback would say 21 for every product),
        // and only sizes the engine supports: a square Data Matrix of 10-26, a QR of 21 + 4k
        if (codes.Count > 0 && moduleMeasured)
        {
            float sidePx = Median(codes.Select(c => c.SidePx));
            double exact = sidePx / modulePx;
            int n = (int)Math.Round(exact);
            if (product.ChildFormat == CodeFormat.DataMatrix)
            {
                int n2 = n + (n % 2);
                proposed.CodeModules = n2 >= 10 && n2 <= 26 && Math.Abs(exact - n2) <= 1.0 ? n2 : 0;
            }
            else
            {
                int n4 = 21 + 4 * (int)Math.Round((n - 21) / 4.0);
                proposed.CodeModules = n4 >= 21 && n4 <= 177 && Math.Abs(exact - n4) <= 2.0 ? n4 : 0;
            }
            proposed.CodeSidePx = proposed.CodeModules > 0 ? sidePx : 0;
        }
        return new CalibrationResult(proposed, light, read, expected, brightness, modulePx, speed, bestIndex, checks) { BestDecode = best.Result };
    }

    /// <summary>
    /// Bounding box of the codes (and of regions the finder saw), plus a margin for where the next
    /// case may stand: one spacing between neighbouring codes (one row or column of the case) and
    /// one and a half codes, one more code along the motion. Rounded out to multiples of 8. Null
    /// when it would cover nearly the whole image anyway.
    /// </summary>
    internal static RoiSettings? CaseRoi(CalibrationFrame f, IReadOnlyList<DecodedCode> codes, (float X, float Y)? motion)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        foreach (var c in codes)
            foreach (var p in c.Corners)
            {
                xs.Add(p.X);
                ys.Add(p.Y);
            }
        foreach (var g in f.Result.Regions.Where(g => g.FromAi))
        {
            xs.Add(g.Box.X);
            xs.Add(g.Box.X + g.Box.Width);
            ys.Add(g.Box.Y);
            ys.Add(g.Box.Y + g.Box.Height);
        }
        if (xs.Count == 0) return null;
        float side = codes.Count > 0 ? Median(codes.Select(c => c.SidePx)) : 0.03f * Math.Max(f.Width, f.Height);
        float pitch = codes.Count >= 2 ? Median(codes.Select(c => NearestDistance(c, codes))) : side;
        float mx = 1.5f * side + pitch, my = 1.5f * side + pitch;
        if (motion is { } d)
        {
            mx += side * Math.Abs(d.X);
            my += side * Math.Abs(d.Y);
        }
        int x0 = Math.Max(0, (int)(xs.Min() - mx) / 8 * 8);
        int y0 = Math.Max(0, (int)(ys.Min() - my) / 8 * 8);
        int x1 = Math.Min(f.Width, ((int)(xs.Max() + mx) + 7) / 8 * 8);
        int y1 = Math.Min(f.Height, ((int)(ys.Max() + my) + 7) / 8 * 8);
        if ((double)(x1 - x0) * (y1 - y0) > 0.9 * f.Width * f.Height) return null;
        return new RoiSettings { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };
    }

    // centre-to-centre distance from a code to its nearest neighbour (the grid spacing)
    private static float NearestDistance(DecodedCode c, IReadOnlyList<DecodedCode> all)
    {
        float best = float.MaxValue;
        foreach (var o in all)
        {
            if (ReferenceEquals(o, c)) continue;
            float dx = o.Center.X - c.Center.X, dy = o.Center.Y - c.Center.Y;
            best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy));
        }
        return best == float.MaxValue ? 0 : best;
    }

    private static float Median(IEnumerable<float> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0f : v[v.Count / 2];
    }
}
