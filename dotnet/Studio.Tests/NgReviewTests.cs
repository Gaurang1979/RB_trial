using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The supervisor's review in the NG image viewer and learning from it (owner, 2026-10-31).</summary>
public class NgReviewTests
{
    private sealed class Overlays : IOverlayHost
    {
        public void Show(object overlay) { }
        public void Close() { }
        public void CloseIf(object overlay) { }
    }

    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();
    private string CameraName = "Top";

    private NgViewerViewModel Viewer(bool mayReview, params string[] ids)
    {
        _link.Connect(_client, Make.Stopped());
        _client.ReviewCameras = [new CameraReviewCounts(0, CameraName, 0, 0, 0)];
        var parts = ids.Select((id, i) => new NgThumb(null, $"08:00:0{i}", Tone.Ng, Icons.Cross,
            Make.Frame(Verdict.Ng, (ulong)i + 1) with { ImageId = id, Camera = "Top" })).ToList();
        return new NgViewerViewModel(Make.Strings, new RuntimeCalls(_link), parts, 0, _ => null, new Overlays()) { MayReview = mayReview };
    }

    [Fact]
    public async Task An_operator_sees_no_review_and_cannot_decide()
    {
        var v = Viewer(false, "1-0");
        Assert.False(v.CanReviewPart);
        await v.AcceptCommand.ExecuteAsync(null);
        Assert.Empty(_client.Reviews);
    }

    [Fact]
    public async Task A_supervisor_accepts_or_confirms_each_part_and_sees_the_counts()
    {
        var v = Viewer(true, "1-0", "2-0");
        Assert.True(v.CanReviewPart);
        await v.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(("1-0", ReviewDecision.Accepted, false), (_client.Reviews[0].Id, _client.Reviews[0].Decision, _client.Reviews[0].Confirm));
        Assert.Equal(Make.Strings["review.done.ok"], v.ReviewText);
        await v.NextCommand.ExecuteAsync(null);
        Assert.Equal("", v.ReviewText);   // the next part is not decided yet
        await v.ConfirmNgCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings.Format("review.counts", 1, 1, 0), v.CountsText);
        await v.PreviousCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["review.done.ok"], v.ReviewText);   // the decision stays with its part
    }

    [Fact]
    public async Task A_part_the_software_finds_clearly_bad_asks_to_double_check_and_is_not_learned_from_by_default()
    {
        _client.LooksBad.Add("1-0");
        var v = Viewer(true, "1-0");
        await v.AcceptCommand.ExecuteAsync(null);
        Assert.True(v.DoubleCheck);
        Assert.Equal(["Batch: a character is wrong"], v.Faults);
        Assert.Empty(_client.Reviews);   // nothing kept on the first tap

        await v.AcceptRecordOnlyCommand.ExecuteAsync(null);
        var r = Assert.Single(_client.Reviews);
        Assert.True(r.Confirm);   // accepted as a record, never taught
        Assert.False(v.DoubleCheck);
        Assert.Equal(Make.Strings["review.done.record"], v.ReviewText);
        Assert.Equal(Make.Strings.Format("review.counts", 0, 0, 1), v.CountsText);
    }

    [Fact]
    public async Task Double_checked_it_can_still_be_confirmed_bad()
    {
        _client.LooksBad.Add("1-0");
        var v = Viewer(true, "1-0");
        await v.AcceptCommand.ExecuteAsync(null);
        v.CancelDoubleCheckCommand.Execute(null);
        Assert.False(v.DoubleCheck);
        await v.AcceptCommand.ExecuteAsync(null);
        await v.ConfirmNgCommand.ExecuteAsync(null);
        var r = Assert.Single(_client.Reviews);
        Assert.Equal(ReviewDecision.ConfirmedNg, r.Decision);
        Assert.Equal(Make.Strings["review.done.ng"], v.ReviewText);
    }

    [Fact]
    public async Task Learning_refuses_a_picture_whose_camera_is_not_in_the_open_job()
    {
        CameraName = "Side";   // the open job's only camera is another one
        var v = Viewer(true, "1-0");
        await v.LearnCommand.ExecuteAsync(null);
        Assert.False(v.HasLearning);
        Assert.Equal(Make.Strings["review.camera_unknown"], v.ReviewText);
        Assert.DoesNotContain(_client.Calls, c => c.StartsWith("Learn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Learning_shows_what_would_change_and_applies_only_a_proof_passed_proposal()
    {
        var v = Viewer(true, "1-0");
        await v.LearnCommand.ExecuteAsync(null);
        Assert.True(v.HasLearning);
        var line = Assert.Single(v.LearnLines);
        Assert.True(line.Looser);
        Assert.Equal("Batch: strictness 60 → 52 (accepts more)", line.Text);
        Assert.Contains("all 5 bad parts fail", v.LearnText);
        Assert.True(v.CanApplyLearning);
        await v.ApplyLearningCommand.ExecuteAsync(null);
        Assert.Contains("Apply p1", _client.Calls);
        Assert.False(v.CanApplyLearning);
        Assert.Equal(Make.Strings.Format("review.applied", "Whisky 750 ml", 4), v.ReviewText);

        _client.LearnAnswer = _client.LearnAnswer with { CanApply = false, ProposalId = "", Problem = "Confirm at least 5 bad parts first." };
        await v.LearnCommand.ExecuteAsync(null);
        Assert.False(v.CanApplyLearning);
        Assert.Contains("Confirm at least 5", v.LearnText);
        _client.Calls.Clear();
        await v.ApplyLearningCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_client.Calls, c => c.StartsWith("Apply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_is_shown_in_the_runtimes_words()
    {
        _client.LearnAnswer = LearnOutcome.Refused(new CommandResult(false, null, "Stop the line to learn from the reviews."));
        var v = Viewer(true, "1-0");
        await v.LearnCommand.ExecuteAsync(null);
        Assert.False(v.HasLearning);
        Assert.Equal("Stop the line to learn from the reviews.", v.ReviewText);
        Assert.Equal(Tone.Ng, v.ReviewTone);
    }
}
