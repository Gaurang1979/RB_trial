using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The Engineer workspace's "Picture check" (requirements AS-13, UI-UX-SPEC.md 4.19 "Centre, Picture check"):
/// the runtime measures sharpness and brightness in the four corners and the centre of each tried picture;
/// here they become green, yellow or red marks with their values, the best sharpness seen since the check was
/// turned on (the engineer turns the lens and watches it rise), and one line of advice above the picture and
/// in the Messages dock. Advice only: it never changes a tool or a result.
/// </summary>
public sealed partial class DesignerViewModel
{
    /// <summary>The toolbar toggle; turning it on starts the best-so-far afresh.</summary>
    [ObservableProperty] private bool _pictureCheckOn;
    /// <summary>The advice for the picture shown, null when there is none or the check is off.</summary>
    [ObservableProperty] private string? _pictureAdvice;

    private PictureCheckOutcome? _check;
    /// <summary>Each zone's best sharpness since the check was turned on, on this camera.</summary>
    private readonly Dictionary<string, float> _bestSharpness = [];
    private string? _adviceLogged;

    [RelayCommand]
    private void TogglePictureCheck() => PictureCheckOn = !PictureCheckOn;

    partial void OnPictureCheckOnChanged(bool value)
    {
        _bestSharpness.Clear();
        _adviceLogged = null;
        if (value && _check is not null) RememberBest(_check);
        UpdateAdvice();
        DrawGraphics();
    }

    /// <summary>A picture was tried: its check, the best so far, the advice.</summary>
    private void CheckPicture(TryOutcome r)
    {
        _check = r.PictureCheck;
        if (!PictureCheckOn) return;
        if (_check is not null) RememberBest(_check);
        UpdateAdvice();
        DrawGraphics();
    }

    /// <summary>No picture (another camera or job): no marks, and the best so far starts again.</summary>
    private void ClearPictureCheck()
    {
        _check = null;
        _bestSharpness.Clear();
        _adviceLogged = null;
        UpdateAdvice();
    }

    private void RememberBest(PictureCheckOutcome check)
    {
        foreach (var z in check.Zones)
            if (z.Sharpness is { } s && (!_bestSharpness.TryGetValue(z.Zone, out var best) || s > best)) _bestSharpness[z.Zone] = s;
    }

    private void UpdateAdvice()
    {
        PictureAdvice = PictureCheckOn && _check is { Advice.Count: > 0 } c
            ? string.Join(" ", c.Advice.Select(AdviceText).Where(t => t.Length > 0)) is { Length: > 0 } text ? text : null
            : null;
        if (PictureAdvice is not { } advice) _adviceLogged = null;   // advice that comes back later is new again
        else if (advice != _adviceLogged)
        {
            Log(advice, Tone.Warn);   // once per change: in Live the same advice is not repeated each picture
            _adviceLogged = advice;
        }
    }

    /// <summary>The five zones' marks in the shown picture's pixels, a little inside their cells so neighbours do not touch.</summary>
    private IEnumerable<ResultGraphic> PictureCheckGraphics()
    {
        if (!PictureCheckOn || _check is null || _last is null) yield break;
        var s = ShownScale;   // JPEG pixels, or camera pixels once the full picture is shown (AS-17)
        foreach (var z in _check.Zones)
        {
            double dx = z.Width * 0.04, dy = z.Height * 0.04;
            double x0 = (z.X + dx) * s, y0 = (z.Y + dy) * s, x1 = (z.X + z.Width - dx) * s, y1 = (z.Y + z.Height - dy) * s;
            yield return new ResultGraphic(GraphicKind.Polygon, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)],
                ZoneVerdict(z.State), MarkText(z), ZoneName(z.Zone));
        }
    }

    private static Verdict ZoneVerdict(string state) => state switch
    {
        "good" => Verdict.Ok,
        "close" => Verdict.Warn,
        "bad" => Verdict.Ng,
        _ => Verdict.Info,
    };

    /// <summary>"Sharp 42 · light 118", with the best so far when an earlier picture was sharper.</summary>
    public string MarkText(PictureZoneCheck z)
    {
        var light = z.Brightness.ToString("0", CultureInfo.CurrentCulture);
        if (z.Sharpness is not { } sharp) return _strings.Format("ws.pc.mark.plain", light);
        var now = sharp.ToString("0", CultureInfo.CurrentCulture);
        return _bestSharpness.TryGetValue(z.Zone, out var best) && best >= sharp + 1
            ? _strings.Format("ws.pc.mark.best", now, best.ToString("0", CultureInfo.CurrentCulture), light)
            : _strings.Format("ws.pc.mark", now, light);
    }

    public string AdviceText(PictureAdviceLine a) => SetupAdvice.Picture(_strings, a);

    private string ZoneName(string zone) => SetupAdvice.ZoneName(_strings, zone);
}
