using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The review follow-ups (requirements AS-04i..k): the bad parts waiting on the Run screen, the reviewed
/// pictures with a decision changed or removed, and the operator's "Was OK" flag in the supervisor's list.</summary>
public class ReviewListTests
{
    private sealed class Overlays : IOverlayHost
    {
        public object? Shown { get; private set; }
        public void Show(object overlay) => Shown = overlay;
        public void Close() => Shown = null;
        public void CloseIf(object overlay) { if (ReferenceEquals(Shown, overlay)) Shown = null; }
    }

    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();
    private readonly Overlays _overlays = new();

    private static SortPicture Reviewed(string id, ReviewDecision decision, bool learned) =>
        new(id, 0, "Top", false, ["Read code: no code found"], DateTimeOffset.UtcNow, false)
        {
            Reviewed = new ReviewedInfo(decision, learned, "sup", DateTimeOffset.UtcNow),
        };

    private SortViewModel Sorter()
    {
        _link.Connect(_client, Make.Stopped());
        _client.ReviewCameras = [new CameraReviewCounts(0, "Top", 0, 0, 0)];
        return new SortViewModel(Make.Strings, new RuntimeCalls(_link), _ => null, _overlays);
    }

    [Fact]
    public async Task The_reviewed_list_shows_each_decision_and_a_decision_is_changed_or_removed()
    {
        _client.ReviewedItems.Add(Reviewed("r1", ReviewDecision.ConfirmedNg, true));
        _client.ReviewedItems.Add(Reviewed("r2", ReviewDecision.Accepted, false));
        _client.Unreadable.Add("r1");
        var s = Sorter();
        await s.LoadAsync();
        s.Filter = SortFilter.Reviewed;
        await Task.Yield();
        Assert.Equal("r1", s.Current!.ImageId);
        Assert.True(s.IsReviewed);
        Assert.StartsWith(Make.Strings.Format("sort.reviewed.ng", "sup", "").TrimEnd(')', ' '), s.ReviewedText);
        Assert.Equal(Make.Strings.Format("ng.position", 1, 2), s.PositionText);

        // a change follows the same rules: the second validation for a part the software could not read
        await s.AcceptCommand.ExecuteAsync(null);
        Assert.True(s.DoubleCheck);
        Assert.Empty(_client.Decisions);
        await s.AcceptCheckedCommand.ExecuteAsync(null);
        Assert.Equal(("r1", ReviewDecision.Accepted, ReviewConfirm.Unreadable), Assert.Single(_client.Decisions));
        Assert.Equal("r1", s.Current!.ImageId);   // the changed decision stays on screen
        Assert.Equal(Make.Strings["review.done.ok"], s.Message);

        await s.NextCommand.ExecuteAsync(null);
        Assert.Contains("record", s.ReviewedText, StringComparison.OrdinalIgnoreCase);
        // the changed picture moves to the top of the list: the screen stays on it, never on the one that slid in
        await s.RejectCommand.ExecuteAsync(null);
        Assert.Equal("r2", s.Current!.ImageId);
        Assert.Equal(0, s.Current is { } c ? _client.ReviewedItems.IndexOf(c) : -1);
        Assert.StartsWith("Confirmed NG", s.ReviewedText);
        await s.RemoveReviewCommand.ExecuteAsync(null);
        Assert.Equal(["r2"], _client.Removed);
        Assert.Equal(Make.Strings["sort.reviewed.removed"], s.Message);
        Assert.Equal("r1", s.Current!.ImageId);
        Assert.Contains("ListToSort -1", _client.Calls);   // the removed picture may wait for a review again
        Assert.Contains("GetImage r1", _client.Calls);      // the reviewed picture is shown
    }

    [Fact]
    public async Task The_Was_OK_filter_shows_only_the_bad_parts_an_operator_marked()
    {
        _client.ToSort.Add(new SortPicture("b1", 0, "Top", false, [], DateTimeOffset.UtcNow, false) { FlaggedBy = "op", FlaggedAt = DateTimeOffset.UtcNow });
        _client.ToSort.Add(new SortPicture("b2", 0, "Top", false, [], DateTimeOffset.UtcNow, false));
        _client.ToSort.Add(new SortPicture("g1", 0, "Top", true, [], DateTimeOffset.UtcNow, false));
        var s = Sorter();
        await s.LoadAsync();
        Assert.Equal("b1", s.Current!.ImageId);
        Assert.StartsWith("op ", s.FlagText);
        s.Filter = SortFilter.WasOk;
        await Task.Yield();
        Assert.Equal("b1", s.Current!.ImageId);
        Assert.False(s.HasNext);
        await s.RejectCommand.ExecuteAsync(null);
        Assert.Equal(("b1", ReviewDecision.ConfirmedNg), (_client.Decisions[0].Id, _client.Decisions[0].Decision));
        s.Filter = SortFilter.Bad;
        await Task.Yield();
        await s.NextCommand.ExecuteAsync(null);
        Assert.Equal("b2", s.Current!.ImageId);
        Assert.Equal("", s.FlagText);
        Assert.False(s.IsReviewed);
    }

    private NgViewerViewModel Viewer(bool mayReview, params string[] ids)
    {
        _link.Connect(_client, Make.Stopped());
        _client.ReviewCameras = [new CameraReviewCounts(0, "Top", 0, 0, 0)];
        var parts = ids.Select((id, i) => new NgThumb(null, $"08:00:0{i}", Tone.Ng, Icons.Cross,
            Make.Frame(Verdict.Ng, (ulong)i + 1) with { ImageId = id, Camera = "Top" })).ToList();
        return new NgViewerViewModel(Make.Strings, new RuntimeCalls(_link), parts, 0, _ => null, _overlays) { MayReview = mayReview };
    }

    [Fact]
    public async Task An_operator_marks_a_bad_part_Was_OK_once_and_the_supervisor_sees_it_with_the_part()
    {
        var op = Viewer(false, "p1", "p2");
        Assert.True(op.CanFlagPart);
        await op.WasOkCommand.ExecuteAsync(null);
        Assert.Contains("FlagWasOk p1", _client.Calls);
        Assert.Equal(Make.Strings["ng.was_ok.done"], op.FlagText);
        Assert.False(op.CanFlagPart);   // once per part
        Assert.Empty(_client.Decisions);   // it decides nothing
        await op.NextCommand.ExecuteAsync(null);
        Assert.True(op.CanFlagPart);
        Assert.Equal("", op.FlagText);

        _client.FlagResult = new CommandResult(false, "runtime.refused.alreadyReviewed", "A supervisor has already decided this part.");
        await op.WasOkCommand.ExecuteAsync(null);
        Assert.Equal(Tone.Ng, op.ReviewTone);
        Assert.True(op.CanFlagPart);

        var sup = Viewer(true, "p1");
        Assert.False(sup.CanFlagPart);   // a supervisor decides instead
        await sup.LoadReviewsAsync();
        Assert.StartsWith("op ", sup.FlagText);
        await sup.ConfirmNgCommand.ExecuteAsync(null);
        Assert.Equal("", sup.FlagText);   // decided: the mark is done with
    }

    [Fact]
    public void The_Run_screen_tells_a_supervisor_how_many_bad_parts_wait_and_opens_them()
    {
        var vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, new ManualTime(), _ => null) { MayReview = () => true };
        _link.StatusReceived += st => vm.Apply(_link.State, st);
        var running = Make.Stopped() with { Learning = new LearningInfo(true, 5, false, 10, 40, true) { BadToReview = 12, FlaggedOk = 3 } };
        _client.Status = running;
        _link.Connect(_client, running);
        vm.Apply(_link.State, running);
        Assert.True(vm.HasReviewWaiting);
        Assert.False(vm.HasLearningNotice);   // not idle: no corner notice, the count is shown all the same
        Assert.Equal(Make.Strings.Format("run.review.waiting_flagged", 12, 3), vm.ReviewWaitingText);
        vm.OpenReviewWaitingCommand.Execute(null);
        Assert.Equal(SortFilter.WasOk, Assert.IsType<SortViewModel>(_overlays.Shown).Filter);

        _overlays.Close();
        _link.Push(running with { Learning = running.Learning! with { Enabled = false, FlaggedOk = 0 } });
        Assert.Equal(Make.Strings.Format("run.review.waiting", 12), vm.ReviewWaitingText);
        vm.OpenReviewWaitingCommand.Execute(null);
        Assert.Null(_overlays.Shown);   // the session off and no bad part in the strip: the sorting is not offered

        _link.Push(running with { Learning = running.Learning! with { BadToReview = 0 } });
        Assert.False(vm.HasReviewWaiting);

        var op = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, new ManualTime(), _ => null) { MayReview = () => false };
        op.Apply(_link.State, running);
        Assert.False(op.HasReviewWaiting);   // a supervisor's count
    }
}
