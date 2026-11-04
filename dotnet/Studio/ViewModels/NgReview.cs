using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.ViewModels;

/// <summary>A setting that would accept more parts is shown in bold, so it is read before Apply.</summary>
public static class ReviewConverters
{
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight> LooserWeight =
        new(looser => looser ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal);
}

/// <summary>A line of the learning proposal: one tool setting, before and after, and whether it accepts more.</summary>
public sealed record LearnLine(string Text, bool Looser);

/// <summary>
/// The supervisor's review in the NG image viewer (owner, 2026-10-31: "each NG image can be selected, zoomed and
/// practically decided that it is accepted or NG. based on that Ai should start auto tuning and learning"):
/// Accept (the part was good) or Confirm NG for the part shown, while the line runs; and "Learn from reviews"
/// with the proposal and Apply, when the line is stopped. A part the software found clearly bad is not
/// accepted on one tap: the supervisor double-checks (owner, 2026-10-31: "if vision software find image is
/// really bad can ask supervisor for double check and mention not to add in good records"), and when it is
/// accepted all the same it is kept as a record only, never learned from (the runtime enforces it).
/// Supervisor, QA, Engineer and Admin only; the runtime checks it too.
/// </summary>
public sealed partial class NgViewerViewModel
{
    /// <summary>What the supervisor decided on each part shown in this viewer, by its index.</summary>
    private readonly Dictionary<int, string> _decided = [];
    /// <summary>The bad parts operators marked "Was OK" (AS-04k), by image id; loaded for a supervisor.</summary>
    private readonly Dictionary<string, WasOkMark> _flags = new(StringComparer.Ordinal);
    /// <summary>The parts marked "Was OK" in this viewer, by its index.</summary>
    private readonly HashSet<int> _flagged = [];

    /// <summary>The signed-in user may review (Supervisor, QA, Engineer, Admin).</summary>
    public bool MayReview { get; init; }

    public ObservableCollection<string> Faults { get; } = [];
    public ObservableCollection<LearnLine> LearnLines { get; } = [];

    /// <summary>The software found the part clearly bad, or could not read it: the supervisor is asked to look again.</summary>
    [ObservableProperty] private bool _doubleCheck;
    /// <summary>The double-check is for a part the software could not read (owner, 2026-11-04): it may be accepted as
    /// good once checked, or kept as a record only.</summary>
    [ObservableProperty] private bool _unreadable;
    [ObservableProperty] private bool _reviewBusy;
    /// <summary>The decision on the part shown, or what went wrong, in words.</summary>
    [ObservableProperty] private string _reviewText = "";
    [ObservableProperty] private Tone _reviewTone = Tone.Info;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private bool _hasLearning;
    [ObservableProperty] private string _learnText = "";
    [ObservableProperty] private bool _canApplyLearning;
    private string _proposalId = "";
    /// <summary>"Ramesh marked this part Was OK at 14:32" for the supervisor, or "Marked Was OK" after the operator's tap.</summary>
    [ObservableProperty] private string _flagText = "";
    [ObservableProperty] private bool _flagBusy;

    /// <summary>"Was OK" (UI-UX-SPEC.md 4.3, AS-04k): anyone may mark the part shown, once; a supervisor decides instead.</summary>
    public bool CanFlagPart => !MayReview && PartImageId is not null && !_flagged.Contains(_index);

    /// <summary>The part shown has a picture the runtime can find (its image id).</summary>
    public bool CanReviewPart => MayReview && _parts.Count > 0 && _parts[_index].Frame.ImageId is not null;

    private string? PartImageId => _parts.Count == 0 ? null : _parts[_index].Frame.ImageId;

    /// <summary>The review of the part shown (called by <see cref="Show"/>).</summary>
    private void ShowReview()
    {
        DoubleCheck = false;
        Unreadable = false;
        Faults.Clear();
        ReviewText = _decided.GetValueOrDefault(_index, "");
        ReviewTone = ReviewText.Length == 0 ? Tone.Info : Tone.Ok;
        OnPropertyChanged(nameof(CanReviewPart));
        ShowFlag();
    }

    /// <summary>The "Was OK" line of the part shown, and whether it may still be marked.</summary>
    private void ShowFlag()
    {
        FlagText = _flagged.Contains(_index) ? _strings["ng.was_ok.done"]
            : PartImageId is { } id && _flags.TryGetValue(id, out var f) && !_decided.ContainsKey(_index)
                ? _strings.Format("sort.flagged", f.User, f.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";
        OnPropertyChanged(nameof(CanFlagPart));
    }

    /// <summary>The operator's "I think this part was good": kept for the supervisor, no change to the job.</summary>
    [RelayCommand]
    private async Task WasOk()
    {
        if (!CanFlagPart || FlagBusy || PartImageId is not { } id) return;
        var index = _index;
        FlagBusy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.FlagWasOkAsync(id, "", ct));
            if (index != _index) return;
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Ok) { Say(_strings.KeyOrText(r.ReasonKey, r.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            _flagged.Add(index);
            ShowFlag();
        }
        finally { FlagBusy = false; }
    }

    [RelayCommand]
    private Task Accept() => Decide(ReviewDecision.Accepted, ReviewConfirm.None);

    [RelayCommand]
    private Task ConfirmNg() => Decide(ReviewDecision.ConfirmedNg, ReviewConfirm.None);

    /// <summary>Double-checked and still good: kept as a record, never learned from.</summary>
    [RelayCommand]
    private Task AcceptRecordOnly() => Decide(ReviewDecision.Accepted, Unreadable ? ReviewConfirm.KeepAsRecord : ReviewConfirm.LooksBad);

    /// <summary>A part the software could not read, checked again and good: it teaches the job.</summary>
    [RelayCommand]
    private Task AcceptChecked() => Decide(ReviewDecision.Accepted, ReviewConfirm.Unreadable);

    [RelayCommand]
    private void CancelDoubleCheck()
    {
        DoubleCheck = false;
        Unreadable = false;
        Faults.Clear();
    }

    private async Task Decide(ReviewDecision decision, ReviewConfirm confirm)
    {
        if (!CanReviewPart || ReviewBusy || PartImageId is not { } id) return;
        var index = _index;
        ReviewBusy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.ReviewPartAsync(id, decision, confirm, "", ct));
            if (index != _index) return;   // another part is shown now
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (r.LooksBad || r.Unreadable)
            {
                Faults.Clear();
                foreach (var f in r.Faults) Faults.Add(f);
                Unreadable = r.Unreadable;
                DoubleCheck = true;
                Say("", Tone.Warn);   // the double-check panel says it
                return;
            }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            DoubleCheck = false;
            Unreadable = false;
            Faults.Clear();
            var text = _strings[decision == ReviewDecision.ConfirmedNg ? "review.done.ng" : r.Learned ? "review.done.ok" : "review.done.record"];
            _decided[index] = text;
            ShowFlag();
            Say(text, Tone.Ok);
            ShowCounts(r.Cameras);
        }
        finally { ReviewBusy = false; }
    }

    /// <summary>"Acceptable 91 % → 98 %, not acceptable 100 % → 100 %" (owner, 2026-11-04), or null when not known.</summary>
    internal static string? Percentages(LearnOutcome r, Strings strings)
    {
        static string P(double v) => v < 0 ? "–" : v.ToString("0.#", CultureInfo.CurrentCulture) + " %";
        if (r.GoodPassBefore < 0 && r.BadFailBefore < 0) return null;
        return strings.Format("review.percent", P(r.GoodPassBefore), P(r.GoodPassAfter), P(r.BadFailBefore), P(r.BadFailAfter));
    }

    private void Say(string text, Tone tone)
    {
        ReviewText = text;
        ReviewTone = tone;
    }

    private void ShowCounts(IReadOnlyList<CameraReviewCounts> cameras)
    {
        var name = _parts.Count == 0 ? "" : _parts[_index].Frame.Camera;
        var c = cameras.FirstOrDefault(x => x.Name == name) ?? cameras.FirstOrDefault();
        CountsText = c is null ? "" : _strings.Format("review.counts", c.Accepted, c.Confirmed, c.RecordOnly);
    }

    /// <summary>The counts of the camera shown, when the viewer opens.</summary>
    public async Task LoadReviewsAsync()
    {
        if (!MayReview) return;
        var (reached, cameras) = await _calls.TryReviewAsync((c, ct) => c.GetReviewsAsync(ct));
        if (reached && cameras is not null) ShowCounts(cameras);
        // the operators' "Was OK" marks, shown with the part (AS-04k)
        var (got, list) = await _calls.TryReviewAsync((c, ct) => c.ListReviewedAsync(-1, ct));
        if (!got || list is not { Result.Ok: true }) return;
        _flags.Clear();
        foreach (var f in list.Flags) _flags[f.ImageId] = f;
        ShowFlag();
    }

    /// <summary>Learns from the reviewed pictures of the camera shown: what the job would change, proved on them.</summary>
    [RelayCommand]
    private async Task Learn()
    {
        if (!MayReview || ReviewBusy || _parts.Count == 0) return;
        ReviewBusy = true;
        LearnLines.Clear();
        HasLearning = false;
        CanApplyLearning = false;
        _proposalId = "";
        try
        {
            var (reached, cameras) = await _calls.TryReviewAsync((c, ct) => c.GetReviewsAsync(ct));
            if (!reached || cameras is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            // the camera of the part shown, in the open job; never a guess
            var name = _parts[_index].Frame.Camera;
            var match = cameras.Where(x => x.Name == name).ToList();
            if (match.Count != 1 && !(name.Length == 0 && cameras.Count == 1)) { Say(_strings["review.camera_unknown"], Tone.Ng); return; }
            var camera = match.Count == 1 ? match[0].Camera : cameras[0].Camera;
            Say(_strings["review.learning"], Tone.Info);
            var (got, r) = await _calls.TryReviewAsync((c, ct) => c.LearnAsync(camera, ct), GrpcRuntimeClient.LearnTimeout);
            if (!got || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            HasLearning = true;
            foreach (var s in r.Settings)
                LearnLines.Add(new LearnLine(_strings.Format(s.Looser ? "review.setting.looser" : "review.setting.stricter",
                    s.Tool, s.Setting, s.Current, s.Proposed), s.Looser));
            var parts = new List<string>();
            if (r.Before.Length > 0) parts.Add(_strings.Format("review.before", r.Before));
            if (r.After.Length > 0) parts.Add(_strings.Format("review.after", r.After));
            if (Percentages(r, _strings) is { } pct) parts.Add(pct);
            if (r.Problem.Length > 0) parts.Add(r.Problem);
            if (r.StillRejected.Count > 0) parts.Add(_strings.Format("review.still_rejected", string.Join(", ", r.StillRejected.Take(8))));
            LearnText = string.Join("\n", parts);
            _proposalId = r.ProposalId;
            CanApplyLearning = r.CanApply && r.ProposalId.Length > 0;
            Say(_strings[CanApplyLearning ? "review.learned" : "review.nothing_to_apply"], CanApplyLearning ? Tone.Ok : Tone.Warn);
        }
        finally { ReviewBusy = false; }
    }

    /// <summary>The proposal becomes a new job version (refused while the line runs).</summary>
    [RelayCommand]
    private async Task ApplyLearning()
    {
        if (!CanApplyLearning || ReviewBusy) return;
        ReviewBusy = true;
        try
        {
            var (reached, r) = await _calls.TryReviewAsync((c, ct) => c.ApplyLearningAsync(_proposalId, ct));
            if (!reached || r is null) { Say(_strings["run.reason.not_connected"], Tone.Ng); return; }
            if (!r.Result.Ok) { Say(_strings.KeyOrText(r.Result.ReasonKey, r.Result.ReasonText, "run.reason.refused"), Tone.Ng); return; }
            CanApplyLearning = false;
            _proposalId = "";
            Say(_strings.Format("review.applied", r.Job, r.Version), Tone.Ok);
        }
        finally { ReviewBusy = false; }
    }
}
