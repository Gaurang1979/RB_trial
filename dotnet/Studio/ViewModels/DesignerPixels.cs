using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// The pixel check per tool (requirements AS-14, UI-UX-SPEC.md 4.19 "Pixel check"): each code and text line found
/// shows its size in its label, with a yellow outline below what its tool needs; the selected tool's advice gives the
/// factor and the three remedies, and the tool shows "too few pixels" in the job tree until an engineer presses
/// "Accept as is". Advice only: it never passes a part; a part below the need is still decided by its tool.
/// </summary>
public sealed partial class DesignerViewModel
{
    /// <summary>The signed-in engineer, shown on "Accept as is" at once; the runtime records its own user and time
    /// for the acceptance when the job is saved.</summary>
    public string UserName { get; init; } = "";

    public PixelCheckTried? SelectedPixels => SelectedResult?.Pixels;
    public bool HasPixelCheck => SelectedPixels is not null;
    public bool HasPixelAdvice => SelectedPixels is { Enough: false };
    public bool CanAcceptPixels => SelectedPixels is { TooFew: true };
    public string PixelNeedText => SelectedPixels is { } p
        ? _strings.Format("px.need", Size(p.Measure, p.FoundPx), Size(p.Measure, p.NeedPx)) : "";
    public string PixelAdviceText => SelectedResult is { Pixels: { Enough: false } p } r ? Advice(r.Name, p) : "";
    public string PixelAcceptedText => SelectedPixels is { Accepted: true } p
        ? _strings.Format("px.accepted", p.AcceptedBy, p.AcceptedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "") : "";

    private void OnPixelsChanged()
    {
        OnPropertyChanged(nameof(SelectedPixels));
        OnPropertyChanged(nameof(HasPixelCheck));
        OnPropertyChanged(nameof(HasPixelAdvice));
        OnPropertyChanged(nameof(CanAcceptPixels));
        OnPropertyChanged(nameof(PixelNeedText));
        OnPropertyChanged(nameof(PixelAdviceText));
        OnPropertyChanged(nameof(PixelAcceptedText));
    }

    /// <summary>"Read code 1: needs 1.2× more pixels: camera 1.2× nearer, a 30 mm lens instead of 25 mm, or a camera
    /// with 1.5× the pixels", in the user's language; the lens as a factor when its focal length is not known.</summary>
    public string Advice(string tool, PixelCheckTried p) => SetupAdvice.Pixels(_strings, tool, p);

    /// <summary>A size as a label shows it: "QR 2.1 px/module", "EAN-13 1.6 px/bar", "text 14 px high".</summary>
    public string Size(string measure, float px, string name = "") => measure switch
    {
        "module" => _strings.Format("px.size.module", name, px.ToString("0.0", CultureInfo.CurrentCulture)).Trim(),
        "bar" => _strings.Format("px.size.bar", name, px.ToString("0.0", CultureInfo.CurrentCulture)).Trim(),
        _ => _strings.Format("px.size.letter", px.ToString("0", CultureInfo.CurrentCulture)),
    };

    /// <summary>Each item's size under it; below its need a yellow outline too, in the pixels of the picture shown
    /// (JPEG, or camera once the full picture is shown, AS-17).</summary>
    private void AddPixelGraphics(List<ResultGraphic> list, TryOutcome r)
    {
        var s = ShownScale;
        foreach (var t in r.Tools ?? [])
            foreach (var i in t.Pixels?.Items ?? [])
            {
                if (i.Corners.Count == 0) continue;
                var points = i.Corners.Select(c => new Point2(c.X * s, c.Y * s)).ToArray();
                if (i.Below) list.Add(new ResultGraphic(GraphicKind.Polygon, points, Verdict.Warn, Tooltip: t.Name) { Tagged = false });
                list.Add(new ResultGraphic(GraphicKind.Text, points, i.Below ? Verdict.Warn : Verdict.Info, Size(i.Measure, i.SizePx, i.Name), t.Name));
            }
    }

    /// <summary>The tools with too few pixels on the picture tried, each with its advice, in the messages.</summary>
    private void LogPixels(TryOutcome r)
    {
        foreach (var t in r.Tools ?? [])
            if (t.Pixels is { TooFew: true } p) Log(Advice(t.Name, p), Tone.Warn);
    }

    /// <summary>
    /// Before a save (owner, 2026-11-01: advice "when tools are selected on image and before saving the job to run"):
    /// the picture check's advice and each tool with too few pixels on the picture last tried go to the messages again.
    /// Advice only: the save goes ahead. Nothing is checked in a running job. Returns how many lines were given.
    /// </summary>
    private int AdviseBeforeSave()
    {
        var advice = new List<string>();
        if (_check is { Advice.Count: > 0 } c) advice.AddRange(c.Advice.Select(AdviceText).Where(t => t.Length > 0));
        foreach (var t in Results)
            if (t.Pixels is { TooFew: true } p) advice.Add(Advice(t.Name, p));
        foreach (var a in advice) Log(a, Tone.Warn);
        return advice.Count;
    }

    /// <summary>The save's message, with how much advice there was.</summary>
    private string Saved(string text, int advice) => advice > 0 ? text + " " + _strings.Format("design.saved.advice", advice) : text;

    /// <summary>
    /// "Accept as is": the selected tool keeps fewer pixels than it needs. Written into the draft with the engineer
    /// and the time (the runtime records its own on save) and the size found, so a picture with a clearly smaller
    /// size asks again. The tool's result is not changed.
    /// </summary>
    [RelayCommand]
    private void AcceptPixels()
    {
        if (SelectedTool is not { } tool || SelectedResult is not { Pixels: { TooFew: true } p } r || _last is null) return;
        var now = DateTimeOffset.Now;
        tool.Node["PixelsAccepted"] = new JsonObject
        {
            ["By"] = UserName,
            ["At"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["FoundPx"] = p.FoundPx,
        };
        var accepted = r with { Pixels = p with { Accepted = true, AcceptedBy = UserName, AcceptedAt = now } };
        _last = _last with { Tools = (_last.Tools ?? []).Select(t => t == r ? accepted : t).ToArray() };
        var at = Results.IndexOf(r);
        if (at >= 0) Results[at] = accepted;
        Changed();
        ShowSelection();
    }
}
