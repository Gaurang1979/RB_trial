using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The learning session (owner, 2026-11-04): the supervisor sorts the saved good and bad pictures nearly full
/// screen, an unreadable part is validated twice, Learn shows the two percentages, and the Run screen's idle notice.</summary>
public class SortingTests
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

    private static SortPicture Pic(string id, bool ok, bool near = false) =>
        new(id, 0, "Top", ok, ok ? [] : ["Read code: no code found"], DateTimeOffset.UtcNow, near);

    private async Task<SortViewModel> Sorter(params SortPicture[] pictures)
    {
        _link.Connect(_client, Make.Stopped());
        _client.ReviewCameras = [new CameraReviewCounts(0, "Top", 0, 0, 0)];
        _client.ToSort.AddRange(pictures);
        var s = new SortViewModel(Make.Strings, new RuntimeCalls(_link), _ => null, _overlays);
        await s.LoadAsync();
        return s;
    }

    [Fact]
    public async Task Accept_and_reject_move_to_the_next_picture_and_back_and_skip_leave_it_unsorted()
    {
        var s = await Sorter(Pic("g1", true, near: true), Pic("b1", false), Pic("g2", true));
        Assert.Equal("g1", s.Current!.ImageId);
        Assert.Equal(Make.Strings.Format("ng.position", 1, 3), s.PositionText);
        Assert.Equal(Tone.Ok, s.Tone);
        await s.AcceptCommand.ExecuteAsync(null);
        Assert.Equal("b1", s.Current!.ImageId);    // the next one not sorted
        Assert.Equal(["Read code: no code found"], s.Faults);
        await s.SkipCommand.ExecuteAsync(null);
        Assert.Equal("g2", s.Current!.ImageId);
        await s.RejectCommand.ExecuteAsync(null);
        await s.PreviousCommand.ExecuteAsync(null);
        await s.PreviousCommand.ExecuteAsync(null);
        Assert.Equal("g1", s.Current!.ImageId);
        Assert.Equal(Make.Strings["review.done.ok"], s.Message);   // the decision stays with its picture
        Assert.Equal([("g1", ReviewDecision.Accepted), ("g2", ReviewDecision.ConfirmedNg)], _client.Decisions.Select(d => (d.Id, d.Decision)));
        Assert.Contains("GetImage g1", _client.Calls);
    }

    [Fact]
    public async Task A_passed_part_rejected_says_a_bad_part_may_have_left_the_line()
    {
        _client.Passed.Add("g1");
        var s = await Sorter(Pic("g1", true));
        await s.RejectCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["sort.done.escape"], s.Message);
        Assert.Equal(Tone.Warn, s.MessageTone);
    }

    [Fact]
    public async Task An_unreadable_part_is_validated_again_and_may_be_kept_as_a_record_only()
    {
        _client.Unreadable.Add("b1");
        _client.Unreadable.Add("b2");
        var s = await Sorter(Pic("b1", false), Pic("b2", false));
        await s.AcceptCommand.ExecuteAsync(null);
        Assert.True(s.DoubleCheck);
        Assert.True(s.Unreadable);
        Assert.Empty(_client.Decisions);   // nothing kept on the first tap
        await s.KeepAsRecordCommand.ExecuteAsync(null);
        Assert.Equal(ReviewConfirm.KeepAsRecord, Assert.Single(_client.Decisions).Confirm);
        Assert.Equal("b2", s.Current!.ImageId);

        await s.AcceptCommand.ExecuteAsync(null);
        await s.AcceptCheckedCommand.ExecuteAsync(null);
        Assert.Equal(ReviewConfirm.Unreadable, _client.Decisions[1].Confirm);
        Assert.Equal(Make.Strings["review.done.ok"], s.Message);
    }

    [Fact]
    public async Task Filters_show_passed_or_rejected_pictures_only()
    {
        var s = await Sorter(Pic("g1", true), Pic("b1", false), Pic("g2", true));
        s.Filter = SortFilter.Bad;
        Assert.Equal("b1", s.Current!.ImageId);
        Assert.Equal(Make.Strings.Format("ng.position", 1, 1), s.PositionText);
        s.Filter = SortFilter.Good;
        Assert.Equal(Make.Strings.Format("ng.position", 1, 2), s.PositionText);
        var none = await new SortingTests().Sorter();
        Assert.False(none.HasPicture);
        Assert.Equal(Make.Strings["sort.none"], none.Message);
    }

    [Fact]
    public async Task Learn_shows_the_acceptable_and_not_acceptable_percentages_and_apply_saves()
    {
        _client.LearnAnswer = new LearnOutcome(CommandResult.Done, "p1", [new TunedSettingInfo("Batch", "strictness", "60", "52", true)],
            "b", "a", true, "", [], 40, 12) { GoodPassBefore = 91, GoodPassAfter = 97.5, BadFailBefore = 100, BadFailAfter = 100 };
        var s = await Sorter(Pic("g1", true));
        await s.LearnCommand.ExecuteAsync(null);
        Assert.Contains(Make.Strings.Format("review.percent", "91 %", "97.5 %", "100 %", "100 %"), s.LearnText);
        Assert.True(s.CanApplyLearning);
        await s.ApplyLearningCommand.ExecuteAsync(null);
        Assert.Contains("Apply p1", _client.Calls);
        Assert.False(s.CanApplyLearning);
    }

    [Fact]
    public async Task The_setting_is_loaded_and_saved_and_a_bad_idle_time_is_refused()
    {
        _client.LearningSettings = new LearningSettingsInfo(true, 5, "sup", DateTimeOffset.UtcNow);
        var s = await Sorter();
        Assert.True(s.Enabled);
        Assert.Equal(5, s.IdleMinutes);
        s.Enabled = false;
        s.IdleMinutes = 8;
        await s.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("SetLearningSettings False 8", _client.Calls);
        Assert.Equal(Make.Strings["sort.settings.saved"], s.Message);
        s.IdleMinutes = 1;
        await s.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(Tone.Ng, s.MessageTone);
    }

    [Fact]
    public void The_idle_notice_shows_only_when_on_idle_and_pictures_wait_and_stays_closed_until_the_line_works()
    {
        var vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, new ManualTime(), _ => null) { MayReview = () => false };
        _link.StatusReceived += st => vm.Apply(_link.State, st);
        var idle = Make.Stopped() with { Learning = new LearningInfo(true, 5, true, 400, 37, true) };
        _client.Status = idle;
        _link.Connect(_client, idle);
        vm.Apply(_link.State, idle);
        Assert.True(vm.HasLearningNotice);
        Assert.Equal(Make.Strings.Format("run.learning.notice", 37), vm.LearningNoticeText);
        Assert.False(vm.CanSort);   // an operator sees the notice, not the Open button

        vm.CloseLearningNoticeCommand.Execute(null);
        _link.Push(idle);
        Assert.False(vm.HasLearningNotice);
        _link.Push(idle with { Learning = idle.Learning! with { Idle = false } });
        _link.Push(idle);
        Assert.True(vm.HasLearningNotice);   // idle again after work: shown again

        _link.Push(idle with { Learning = idle.Learning! with { Enabled = false } });
        Assert.False(vm.HasLearningNotice);
        _link.Push(idle with { Learning = idle.Learning! with { ToSort = 0 } });
        Assert.False(vm.HasLearningNotice);
        vm.OpenSortingCommand.Execute(null);
        Assert.Null(_overlays.Shown);   // only a supervisor opens the sorting
    }

    [Fact]
    public async Task Stop_learning_ends_it_at_once_and_keeps_the_link()
    {
        var s = await Sorter(Pic("g1", true), Pic("b1", false));
        _client.LearnHangs = true;
        var learn = s.LearnCommand.ExecuteAsync(null);
        await Task.Delay(50);
        Assert.True(s.Learning);
        s.StopLearningCommand.Execute(null);
        await learn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(s.Learning);
        Assert.False(s.Busy);
        Assert.False(s.HasLearning);
        Assert.Equal(Make.Strings["sort.learn.stopped"], s.Message);
        Assert.Equal(0, _link.Broken);   // a stop is not a broken link
    }
}
