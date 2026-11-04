using System.Globalization;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The setup checks' advice in the user's language (requirements AS-13 picture check, AS-14 pixel check), one wording
/// for the job designer and the Run screen; and the Run screen's lines for the checks at job load. Advice only.
/// </summary>
public static class SetupAdvice
{
    /// <summary>One line of picture check advice; "" for a kind a newer runtime knows and this app does not.</summary>
    public static string Picture(Strings strings, PictureAdviceLine a) => a.Kind switch
    {
        "tilt" => strings.Format("ws.pc.advice.tilt", Where(strings, a.Zones)),
        "focus" => strings["ws.pc.advice.focus"],
        "clean_lens" => strings["ws.pc.advice.clean_lens"],
        "vignetting" => strings["ws.pc.advice.vignetting"],
        "dark" => strings.Format("ws.pc.advice.dark", Where(strings, a.Zones)),
        "bright" => strings.Format("ws.pc.advice.bright", Where(strings, a.Zones)),
        _ => "",
    };

    /// <summary>"Read code 1: needs 1.2× more pixels: camera 1.2× nearer, a 30 mm lens instead of 25 mm, or a camera
    /// with 1.5× the pixels"; the lens as a factor when its focal length is not known.</summary>
    public static string Pixels(Strings strings, string tool, PixelCheckTried p)
    {
        var k = p.Factor.ToString("0.0", CultureInfo.CurrentCulture);
        var lens = p.LensAdvisedMm > 0
            ? strings.Format("px.lens.mm", p.LensAdvisedMm.ToString("0", CultureInfo.CurrentCulture), p.LensMm.ToString("0.#", CultureInfo.CurrentCulture))
            : strings.Format("px.lens.factor", k);
        var text = strings.Format("px.advice", tool, k, lens, p.PixelsTimes.ToString("0.0", CultureInfo.CurrentCulture));
        return p.Unsure ? text + " " + strings["px.unsure"] : text;
    }

    public static string ZoneName(Strings strings, string zone) => strings["ws.pc.zone." + zone];

    /// <summary>The zones in words: a side or all corners when they make one, else each zone.</summary>
    public static string Where(Strings strings, IReadOnlyList<string> zones)
    {
        var set = zones.ToHashSet();
        bool Is(params string[] these) => set.SetEquals(these);
        var key = set.Count == 5 ? "ws.pc.where.all"
            : Is("top_left", "top_right", "bottom_left", "bottom_right") ? "ws.pc.where.corners"
            : Is("top_left", "top_right") ? "ws.pc.where.top"
            : Is("bottom_left", "bottom_right") ? "ws.pc.where.bottom"
            : Is("top_left", "bottom_left") ? "ws.pc.where.left"
            : Is("top_right", "bottom_right") ? "ws.pc.where.right"
            : null;
        return key is not null ? strings[key] : string.Join(", ", zones.Select(z => ZoneName(strings, z)));
    }

    /// <summary>
    /// The Run screen's lines for the checks at job load: "Checking…" while pictures are taken; then per camera
    /// (named when there are two or more) why no picture came, the picture advice, each tool with too few pixels and
    /// each tool that found nothing to measure, or one green line when all is good. A dark grabbed picture says the
    /// PLC may keep the light off outside a run. Empty when there is nothing to say.
    /// </summary>
    public static List<CheckLine> Lines(Strings strings, SetupCheckOutcome? check)
    {
        var lines = new List<CheckLine>();
        if (check is null) return lines;
        if (check.State == SetupCheckState.Checking)
        {
            lines.Add(new CheckLine(Tone.Info, Icons.Wait, strings["run.setup.checking"]));
            return lines;
        }
        if (check.State == SetupCheckState.Stopped)
        {
            lines.Add(new CheckLine(Tone.Neutral, Icons.Info, strings["run.setup.stopped"]));
            return lines;
        }
        var named = check.Cameras.Count > 1;
        string On(string camera, string text) => named ? strings.Format("run.setup.camera", camera, text) : text;
        foreach (var c in check.Cameras)
        {
            var before = lines.Count;
            if (c.Pictures == 0)
            {
                lines.Add(new CheckLine(Tone.Warn, Icons.Camera, On(c.Camera, strings.Format("run.setup.no_picture", c.Error))));
                continue;
            }
            if (c.Error.Length > 0) lines.Add(new CheckLine(Tone.Warn, Icons.Bang, On(c.Camera, c.Error)));
            foreach (var a in c.PictureCheck?.Advice ?? [])
                if (Picture(strings, a) is { Length: > 0 } text)
                    lines.Add(new CheckLine(Tone.Warn, Icons.Camera,
                        On(c.Camera, a.Kind == "dark" && c.Source == "camera" ? text + " " + strings["run.setup.dark_light"] : text)));
            foreach (var t in c.Tools)
                if (t.Pixels.TooFew) lines.Add(new CheckLine(Tone.Warn, Icons.Bang, On(c.Camera, Pixels(strings, t.Tool, t.Pixels))));
            foreach (var tool in c.Unmeasured)
                lines.Add(new CheckLine(Tone.Info, Icons.Info, On(c.Camera, strings.Format("run.setup.unmeasured", tool))));
            if (lines.Count == before) lines.Add(new CheckLine(Tone.Ok, Icons.Check, On(c.Camera, strings["run.setup.good"])));
        }
        return lines;
    }
}
