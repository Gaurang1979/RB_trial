using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The Engineer workspace's Picture check (requirements AS-13, UI-UX-SPEC.md 4.19): marks on the
/// corners and centre with their values, the best sharpness so far, and one line of advice. Advice only.</summary>
public class PictureCheckTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private async Task<DesignerViewModel> Open()
    {
        var draft = JobDraft.New("Tablets", 2);
        draft.AddTool(ToolKind.ReadCode, 0);
        _client.JobFiles["Tablets"] = draft.ToJson();
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        return vm;
    }

    /// <summary>A 1280 x 960 picture's check: the top-left corner at the given sharpness, the rest sharp.</summary>
    private static PictureCheckOutcome Check(float topLeft, string state = "bad", params PictureAdviceLine[] advice) => new(
    [
        new("top_left", 0, 0, 426, 320, topLeft, 120, state),
        new("top_right", 853, 0, 427, 320, 48, 121, "good"),
        new("bottom_left", 0, 640, 426, 320, 47, 119, "good"),
        new("bottom_right", 853, 640, 427, 320, null, 12, "no_detail"),
        new("centre", 426, 320, 427, 320, 50, 140, "good"),
    ], advice);

    private static readonly PictureAdviceLine TiltTopLeft = new("tilt", ["top_left"]);

    private void Answer(PictureCheckOutcome check) => _client.TryDefault = _client.TryDefault with { PictureCheck = check };

    private static IReadOnlyList<ResultGraphic> Marks(DesignerViewModel vm) =>
        vm.Graphics.Where(g => g.Tooltip is { } t && t.Contains("corner") || g.Tooltip == "centre").ToList();

    [Fact]
    public async Task Off_by_default_nothing_is_drawn_or_said()
    {
        var vm = await Open();
        Answer(Check(15, "bad", TiltTopLeft));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.False(vm.PictureCheckOn);
        Assert.Empty(Marks(vm));
        Assert.Null(vm.PictureAdvice);
    }

    [Fact]
    public async Task On_it_marks_the_five_zones_with_their_values_and_colours()
    {
        var vm = await Open();
        Answer(Check(15, "bad", TiltTopLeft));
        await vm.TryCommand.ExecuteAsync(null);
        vm.TogglePictureCheckCommand.Execute(null);

        var marks = Marks(vm);
        Assert.Equal(5, marks.Count);
        var tl = marks.Single(m => m.Tooltip == "top-left corner");
        Assert.Equal(Verdict.Ng, tl.Verdict);
        Assert.Equal("Sharp 15 · light 120", tl.Label);
        // full-image pixels to the shown picture's (scale 0.5), a little inside the cell
        Assert.InRange(tl.Points[0].X, 8, 10);
        Assert.InRange(tl.Points[2].X, 203, 205);
        Assert.Equal(Verdict.Ok, marks.Single(m => m.Tooltip == "centre").Verdict);
        var plain = marks.Single(m => m.Tooltip == "bottom-right corner");
        Assert.Equal(Verdict.Info, plain.Verdict);
        Assert.Equal("No detail · light 12", plain.Label);
    }

    [Fact]
    public async Task The_advice_is_shown_above_the_picture_and_once_in_the_messages()
    {
        var vm = await Open();
        Answer(Check(15, "bad", TiltTopLeft, new PictureAdviceLine("vignetting", ["top_left", "top_right", "bottom_left", "bottom_right"])));
        vm.TogglePictureCheckCommand.Execute(null);
        await vm.TryCommand.ExecuteAsync(null);

        Assert.StartsWith("Blurred: top-left corner; sharp elsewhere.", vm.PictureAdvice);
        Assert.Contains("All corners darker than the centre", vm.PictureAdvice);
        Assert.Equal(1, vm.Messages.Count(m => m.Text == vm.PictureAdvice));
        await vm.TryCommand.ExecuteAsync(null);   // the same advice again (Live): not repeated
        Assert.Equal(1, vm.Messages.Count(m => m.Text == vm.PictureAdvice));
        Assert.All(vm.Results, r => Assert.True(r.Pass));   // advice only: the result stays as the tools gave it

        vm.TogglePictureCheckCommand.Execute(null);
        Assert.Null(vm.PictureAdvice);
        Assert.Empty(Marks(vm));
    }

    [Fact]
    public async Task Sides_and_corners_are_named_as_one()
    {
        var vm = await Open();
        Assert.Equal("Blurred: left side; sharp elsewhere. Check that the camera and lens are square to the part and the part lies flat, or the lens is not sharp at its edge.",
            vm.AdviceText(new PictureAdviceLine("tilt", ["top_left", "bottom_left"])));
        Assert.Equal("Too dark: all four corners. Adjust the light.",
            vm.AdviceText(new PictureAdviceLine("dark", ["top_left", "top_right", "bottom_left", "bottom_right"])));
        Assert.Equal("Too bright: top-right corner, centre. Adjust the light, or remove the glare there.",
            vm.AdviceText(new PictureAdviceLine("bright", ["top_right", "centre"])));
        Assert.Equal("Whole picture is soft and dull: clean the lens or the window in front of it.",
            vm.AdviceText(new PictureAdviceLine("clean_lens", ["top_left", "top_right", "bottom_left", "bottom_right", "centre"])));
        Assert.Equal("", vm.AdviceText(new PictureAdviceLine("something_newer", [])));
    }

    [Fact]
    public async Task While_the_lens_turns_each_mark_shows_the_best_sharpness_so_far()
    {
        var vm = await Open();
        vm.TogglePictureCheckCommand.Execute(null);
        Answer(Check(30, "good"));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal("Sharp 30 · light 120", Marks(vm).Single(m => m.Tooltip == "top-left corner").Label);

        Answer(Check(22, "close"));   // turned past the best focus
        await vm.TryCommand.ExecuteAsync(null);
        var tl = Marks(vm).Single(m => m.Tooltip == "top-left corner");
        Assert.Equal("Sharp 22 (best 30) · light 120", tl.Label);
        Assert.Equal(Verdict.Warn, tl.Verdict);

        // turning the check on again, or another camera, starts the best afresh
        vm.TogglePictureCheckCommand.Execute(null);
        vm.TogglePictureCheckCommand.Execute(null);
        Assert.Equal("Sharp 22 · light 120", Marks(vm).Single(m => m.Tooltip == "top-left corner").Label);
        Answer(Check(40, "good"));
        await vm.TryCommand.ExecuteAsync(null);
        vm.CameraIndex = 1;
        Assert.Empty(Marks(vm));
        Assert.Null(vm.PictureAdvice);
    }

    [Fact]
    public async Task Try_asks_for_the_check_and_try_all_pictures_does_not()
    {
        var vm = await Open();
        await vm.TryCommand.ExecuteAsync(null);
        Assert.False(_client.Tries[^1].SkipPictureCheck);
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(3, _client.Tries.Skip(1).Count(t => t.SkipPictureCheck));
    }

    [Fact]
    public async Task Advice_that_comes_back_after_a_clean_picture_is_logged_again()
    {
        var vm = await Open();
        vm.TogglePictureCheckCommand.Execute(null);
        Answer(Check(15, "bad", TiltTopLeft));
        await vm.TryCommand.ExecuteAsync(null);
        var advice = vm.PictureAdvice;
        Answer(Check(48, "good"));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Null(vm.PictureAdvice);
        Answer(Check(15, "bad", TiltTopLeft));
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Messages.Count(m => m.Text == advice));
    }

    [Fact]
    public async Task A_runtime_without_the_check_draws_nothing()
    {
        var vm = await Open();
        vm.TogglePictureCheckCommand.Execute(null);
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Empty(Marks(vm));
        Assert.Null(vm.PictureAdvice);
    }
}
