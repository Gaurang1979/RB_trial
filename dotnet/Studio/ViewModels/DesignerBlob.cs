using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>A suggestion of Auto tolerance waiting for Accept / Not this, for one Blob tool, in the unit it was measured in.</summary>
public sealed record ToleranceSuggestion(string Tool, int Camera, string Unit, float MaxDeviation, float MaxDentDepth, string Text);

/// <summary>
/// The Blob tool in the job designer (requirements 17.12 BLB-OUT-03..06): its outline unit, px or mm (mm only with
/// the camera's calibration, the limits converted so they keep their meaning), and "Auto tolerance": the runtime
/// measures the taught outline on stored pictures of good parts and suggests the largest deviation and largest dent.
/// The suggestion changes nothing until the engineer accepts it; Accept is one undo step (AS-18).
/// </summary>
public sealed partial class DesignerViewModel
{
    /// <summary>Most pictures one suggestion sends (the runtime's limit).</summary>
    public const int MaxTolerancePictures = 30;

    public IReadOnlyList<string> BlobThresholds => ToolDraft.BlobThresholds;
    public IReadOnlyList<string> BlobPolarities => ToolDraft.BlobPolarities;
    public IReadOnlyList<string> BlobMorphologies => ToolDraft.BlobMorphologies;
    public IReadOnlyList<string> BlobSorts => ToolDraft.BlobSorts;
    public IReadOnlyList<string> BlobPresences => ToolDraft.BlobPresences;
    public IReadOnlyList<int> Connectivities => ToolDraft.Connectivities;

    [ObservableProperty] private ToleranceSuggestion? _toleranceSuggestion;

    /// <summary>"Teach a spot" is on: the next tap on the picture picks the spot of the selected Blob tool whose outline is
    /// taught (requirements 17.12 BLB-OUT-07).</summary>
    [ObservableProperty] private bool _pickingSpot;

    /// <summary>The draft as it was tried when the picture's spots were numbered; a spot is taught only on that draft, so a
    /// changed threshold or filter never makes the runtime teach another spot under the clicked number.</summary>
    private string? _spotsOf;

    partial void OnPickingSpotChanged(bool value)
    {
        if (!value) return;
        if (SelectedTool is not { IsBlob: true } tool) { PickingSpot = false; return; }
        SayText(_strings.Format("design.blob.pick_spot", tool.Name), Tone.Info);
    }

    public bool HasToleranceSuggestion => ToleranceSuggestion is not null;
    partial void OnToleranceSuggestionChanged(ToleranceSuggestion? value) => OnPropertyChanged(nameof(HasToleranceSuggestion));

    /// <summary>The camera of the selected tool has a real size (its calibration's mm per pixel).</summary>
    public bool HasCalibration => Camera?.MmPerPx is > 0;

    /// <summary>The unit choices: "px", and "mm", which says it needs a calibration when the camera has none.</summary>
    public IReadOnlyList<string> OutlineUnits => ["px", HasCalibration ? "mm" : _strings["design.blob.mm_needs_calibration"]];

    /// <summary>The selected Blob tool's outline unit as one of <see cref="OutlineUnits"/>. Mm without a calibration is
    /// refused with the reason, and the choice goes back.</summary>
    public string? BlobUnit
    {
        get => SelectedTool is { IsBlob: true } t ? OutlineUnits[t.OutlineInMm ? 1 : 0] : null;
        set
        {
            if (value is null || SelectedTool is not { IsBlob: true } tool) return;
            var unit = value == OutlineUnits[1] ? "Mm" : "Px";
            if (unit == tool.OutlineUnit) return;
            Record(typing: true);   // a setting typed just before is a step of its own
            if (!tool.ChangeOutlineUnit(unit, Camera?.MmPerPx))
            {
                Say(unit == "Mm" ? "design.blob.needs_calibration" : "design.blob.cannot_convert", Tone.Warn);
                OnPropertyChanged(nameof(BlobUnit));   // the combo box shows the unit kept
                return;
            }
            ToleranceSuggestion = null;   // measured in the other unit
            Changed();
            OnPropertyChanged();
        }
    }

    public string BlobUnitHint => HasCalibration
        ? _strings.Format("design.blob.calibrated", Camera!.MmPerPx!.Value.ToString("0.####", CultureInfo.CurrentCulture))
        : _strings["design.blob.no_calibration"];

    public string TaughtOutlineText => SelectedTool is { IsBlob: true } t
        ? t.HasTaughtOutline ? _strings.Format("design.blob.taught", t.TaughtPoints) : _strings["design.blob.not_taught"] : "";

    /// <summary>Another tool or camera is shown, or the draft changed under it: the unit control follows, and a suggestion
    /// for another tool goes.</summary>
    private void BlobToolChanged()
    {
        if (ToleranceSuggestion is { } s && (SelectedTool?.Name != s.Tool || CameraIndex != s.Camera)) ToleranceSuggestion = null;
        if (SelectedTool is not { IsBlob: true }) PickingSpot = false;
        OnPropertyChanged(nameof(HasCalibration));
        OnPropertyChanged(nameof(OutlineUnits));
        OnPropertyChanged(nameof(BlobUnit));
        OnPropertyChanged(nameof(BlobUnitHint));
        OnPropertyChanged(nameof(TaughtOutlineText));
    }

    /// <summary>The pictures Auto tolerance measures: those that passed the last "Try all pictures" of this camera (bad
    /// parts left out), or every stored picture when it was not run; at most <see cref="MaxTolerancePictures"/>.</summary>
    public IReadOnlyList<string> TolerancePictures() =>
        (ImageSet.Count > 0 && _setCamera == CameraIndex ? ImageSet.Where(p => p.Pass).Select(p => p.Name) : Pictures)
        .Take(MaxTolerancePictures).ToList();

    /// <summary>"Auto tolerance" on the selected Blob tool: the runtime's suggestion from good pictures, shown with
    /// Accept / Not this. Nothing changes here.</summary>
    [RelayCommand]
    private async Task AutoTolerance()
    {
        if (Job is null || Busy || SelectedTool is not { IsBlob: true } tool) return;
        if (!tool.HasTaughtOutline) { Say("design.blob.teach_first", Tone.Warn); return; }
        var pictures = TolerancePictures();
        if (pictures.Count < 3) { Say("design.blob.three_pictures", Tone.Warn); return; }
        ToleranceSuggestion = null;
        Busy = true;
        try
        {
            var json = Job.ToJson();
            var camera = CameraIndex;
            var (reached, r) = await _calls.TryDesignAsync((c, ct) => c.SuggestBlobToleranceAsync(json, camera, tool.Name, pictures, ct), TryLimit);
            if (!reached) { Say("run.reason.not_connected", Tone.Ng); return; }
            if (r is not { Ok: true }) { Refused(r?.Refused); return; }
            if (!r.Found) { SayText(r.Reason.Length > 0 ? r.Reason : _strings["design.blob.no_suggestion"], Tone.Warn); return; }
            string N(double v) => v.ToString("0.0##", CultureInfo.CurrentCulture);
            string Now(decimal? v) => v is { } x ? N((double)x) : _strings["design.blob.no_limit"];
            var text = _strings.Format("design.blob.suggestion", N(r.MaxDeviation), N(r.MaxDentDepth), r.Unit, r.Pictures,
                N(r.WorstDeviation), N(r.WorstDent), Now(tool.MaxDeviation), Now(tool.MaxDentDepth));
            ToleranceSuggestion = new ToleranceSuggestion(tool.Name, camera, r.Unit, r.MaxDeviation, r.MaxDentDepth, text);
            Say(null);
        }
        finally { Busy = false; }
    }

    /// <summary>Accept: the suggested largest deviation and largest dent go into the tool, as one undo step. Refused when the
    /// tool's unit changed since it was measured.</summary>
    [RelayCommand]
    private void AcceptTolerance()
    {
        if (ToleranceSuggestion is not { } s) return;
        ToleranceSuggestion = null;
        if (Job?.Tools.FirstOrDefault(t => t.Name == s.Tool && t.Camera == s.Camera && t.IsBlob) is not { } tool) return;
        if (tool.OutlineUnitText != s.Unit) { Say("design.blob.unit_changed", Tone.Warn); return; }
        Record(typing: true);   // a setting typed just before is a step of its own
        tool.MaxDeviation = Math.Round((decimal)s.MaxDeviation, 4);
        tool.MaxDentDepth = Math.Round((decimal)s.MaxDentDepth, 4);
        Changed();
        SayText(_strings.Format("design.blob.accepted", s.Tool), Tone.Ok);
    }

    /// <summary>The spot of the tool's outlines on the last try under the point (pixels of the picture shown), by its number
    /// as drawn; null beside every spot. Only polygons the runtime names as the tool's and numbered count (not its marks).</summary>
    public static int? SpotAt(IReadOnlyList<ResultGraphic> graphics, string tool, Point2 at)
    {
        var spots = graphics.Where(g => g.Tool == tool && g.Kind == GraphicKind.Polygon && g.Points.Count >= 3
                                        && int.TryParse(g.Label, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1).ToList();
        return GraphicHit.Find(spots, at, 0) is { } hit ? int.Parse(hit.Label!, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A tap while "Teach a spot" is on: the spot under it is taught, as Teach does on the first spot (one undo step).
    /// Beside every spot nothing is taught, and the mode stays on for another tap.</summary>
    private async Task SpotTappedAsync(Point2 at)
    {
        if (Busy || SelectedTool is not { IsBlob: true } tool) { PickingSpot = false; return; }
        if (_spotsOf != Job?.ToJson()) { Say("design.blob.try_first", Tone.Warn); return; }
        if (SpotAt(ShownGraphics().ToList(), tool.Name, at) is not { } spot) { Say("design.blob.no_spot_here", Tone.Warn); return; }
        PickingSpot = false;
        await RunAsync(tool.Name, spot);
    }

    /// <summary>Not this: the suggestion goes, nothing changed.</summary>
    [RelayCommand]
    private void DismissTolerance() => ToleranceSuggestion = null;
}
