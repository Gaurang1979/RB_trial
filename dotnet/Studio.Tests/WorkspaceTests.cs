using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The Engineer workspace around the job designer (UI-UX-SPEC.md 4.19): job tree, marks after a
/// try, image set and margin after "Try all pictures", messages, panels and shortcuts.</summary>
public class WorkspaceTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();
    private bool Closed;

    private async Task<DesignerViewModel> Open()
    {
        var draft = JobDraft.New("Tablets", 2);
        draft.AddTool(ToolKind.ReadCode, 0);
        var text = draft.AddTool(ToolKind.CheckText, 1);
        text.MinQuality = 60;
        _client.JobFiles["Tablets"] = draft.ToJson();
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => Closed = true);
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        return vm;
    }

    [Fact]
    public async Task The_tree_lists_the_job_its_cameras_and_their_tools_and_follows_renames()
    {
        var vm = await Open();
        Assert.Equal(["Tablets", "Camera 1", "Read code 1", "Camera 2", "Check text 1"], vm.Tree.Select(n => n.Name));
        Assert.Equal([0, 1, 2, 1, 2], vm.Tree.Select(n => n.Level));
        Assert.True(vm.Tree[2].Selected);   // camera 1's first tool

        vm.SelectNodeCommand.Execute(vm.Tree[4]);
        Assert.Equal(1, vm.CameraIndex);
        Assert.Equal("Check text 1", vm.SelectedTool!.Name);
        Assert.True(vm.Tree[4].Selected);
        Assert.False(vm.Tree[2].Selected);

        vm.SelectedTool.Name = "Batch print";
        Assert.Equal("Batch print", vm.Tree[4].Name);
        vm.AddReadCodeCommand.Execute(null);
        Assert.Equal(6, vm.Tree.Count);
        Assert.Equal(vm.StatusText, Make.Strings["design.status.unsaved"]);
    }

    [Fact]
    public async Task A_try_marks_the_tools_of_the_camera_and_shows_the_selected_tools_result_and_times()
    {
        var vm = await Open();
        _client.TryDefault = _client.TryDefault with
        {
            Pass = false,
            Tools = [new ToolTried("Read code 1", "barcode", false, "No code found", 9.5f)],
        };
        await vm.TryCommand.ExecuteAsync(null);
        var node = vm.Tree.Single(n => n.Name == "Read code 1");
        Assert.Equal((Tone.Ng, "✗"), (node.Tone, node.Mark));
        Assert.Equal("No code found", vm.SelectedResult!.Reason);
        Assert.Single(vm.ToolTimes);
        Assert.Contains("9.5", vm.ToolTimesText);
        Assert.Equal(Tone.Neutral, vm.Tree.Single(n => n.Name == "Check text 1").Tone);   // other camera: not tried
    }

    [Fact]
    public async Task Try_all_fills_the_image_set_and_the_text_tools_margin()
    {
        var vm = await Open();
        vm.SelectNodeCommand.Execute(vm.Tree[4]);
        float q = 70;
        foreach (var name in _client.PictureNames)
            _client.TryAnswers[name] = _client.TryDefault with
            {
                Pass = name != "bad1.bmp",
                Tools = [new ToolTried("Check text 1", "ocr", name != "bad1.bmp", name == "bad1.bmp" ? "Line 1 cut short" : "Text OK", 20, q += 5)],
            };
        await vm.RunAllCommand.ExecuteAsync(null);

        Assert.Equal(["good1.bmp", "good2.bmp", "bad1.bmp"], vm.ImageSet.Select(p => p.Name));
        Assert.Equal([true, true, false], vm.ImageSet.Select(p => p.Pass));
        Assert.Contains("Line 1 cut short", vm.ImageSet[2].Reason);
        Assert.Equal(2, vm.BottomTab);   // the image set tab opens
        Assert.True(vm.HasMargin);
        Assert.Equal([75.0, 80.0, 85.0], vm.SelectedMargin!.Values);
        Assert.Equal(60, vm.SelectedMargin.Limit);
        Assert.True(vm.SelectedMargin.Safe);   // the worst is 15 above the limit, a tenth of the scale is 10
        Assert.Contains(vm.Messages, m => m.Text.Contains("2 OK, 1 NG"));

        _client.Tries.Clear();
        await vm.OpenSetPictureCommand.ExecuteAsync(vm.ImageSet[2]);
        Assert.Equal("bad1.bmp", _client.Tries.Single().Stored);
    }

    [Fact]
    public async Task Panels_hide_and_come_back_and_shortcuts_run_the_workspace_actions()
    {
        var vm = await Open();
        vm.ToggleDocksCommand.Execute(null);
        vm.ToggleJobPanelCommand.Execute(null);
        Assert.False(vm.ShowDocks || vm.ShowJobPanel);
        vm.ResetLayoutCommand.Execute(null);
        Assert.True(vm.ShowDocks && vm.ShowJobPanel && vm.ShowPropertiesPanel);

        _client.Tries.Clear();
        vm.Shortcut("try");
        vm.Shortcut("next");
        Assert.Equal(["good1.bmp", "good2.bmp"], _client.Tries.Select(t => t.Stored));
        vm.Shortcut("start");   // a Run key does nothing here
        Assert.Equal(2, _client.Tries.Count);
    }

    [Fact]
    public async Task An_edit_in_the_property_table_shows_unsaved_and_Back_asks_first()
    {
        var vm = await Open();
        vm.Watch();
        Assert.Equal(Make.Strings["design.status.saved"], vm.StatusText);
        vm.SelectNodeCommand.Execute(vm.Tree[4]);
        vm.SelectedTool!.MinQuality = 75;   // a binding edit: no command runs
        await Task.Delay(600);
        vm.Watch();
        Assert.Equal(Make.Strings["design.status.unsaved"], vm.StatusText);

        vm.CloseCommand.Execute(null);
        Assert.False(Closed);
        Assert.Equal(Make.Strings["design.drop_changes"], vm.Question);
        await vm.AnswerYesCommand.ExecuteAsync(null);
        Assert.True(Closed);
    }

    [Fact]
    public async Task The_image_set_goes_with_an_edit_a_camera_change_or_a_stopped_run()
    {
        var vm = await Open();
        vm.SelectNodeCommand.Execute(vm.Tree[4]);
        foreach (var name in _client.PictureNames)
            _client.TryAnswers[name] = _client.TryDefault with { Tools = [new ToolTried("Check text 1", "ocr", true, "Text OK", 20, 80)] };
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.ImageSet.Count);
        Assert.True(vm.HasMargin);

        vm.SelectedTool!.MinQuality = 90;   // the set was made with other settings
        await Task.Delay(600);
        vm.Watch();
        Assert.Empty(vm.ImageSet);
        Assert.False(vm.HasMargin);

        await vm.RunAllCommand.ExecuteAsync(null);
        vm.CameraIndex = 0;
        Assert.Empty(vm.ImageSet);

        vm.CameraIndex = 1;
        _client.TryAnswers["good2.bmp"] = _client.TryDefault with { Ok = false, Refused = new CommandResult(false, "run.reason.stop_first") };
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Single(vm.ImageSet);
        Assert.False(vm.HasMargin);
        Assert.Contains("1 of 3", vm.ImageSetSummary);
    }

    [Fact]
    public async Task A_text_tool_that_read_nothing_on_a_picture_makes_the_margin_not_safe()
    {
        var vm = await Open();
        vm.SelectNodeCommand.Execute(vm.Tree[4]);
        foreach (var name in _client.PictureNames)
            _client.TryAnswers[name] = _client.TryDefault with
            {
                Tools = [new ToolTried("Check text 1", "ocr", name != "bad1.bmp", "", 20, name == "bad1.bmp" ? null : 95)],
            };
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.SelectedMargin!.Values.Count);
        Assert.False(vm.SelectedMargin.Safe);
    }

    [Fact]
    public async Task A_set_picture_opens_by_its_name()
    {
        var vm = await Open();
        await vm.RunAllCommand.ExecuteAsync(null);
        var bad = vm.ImageSet.Single(p => p.Name == "bad1.bmp");
        _client.PictureNames = ["bad1.bmp", "good1.bmp"];   // the folder was read again meanwhile
        await vm.LoadPicturesCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        await vm.OpenSetPictureCommand.ExecuteAsync(bad);
        Assert.Equal("bad1.bmp", _client.Tries.Single().Stored);
    }

    [Fact]
    public void A_margin_close_to_the_limit_is_not_safe()
    {
        Assert.False(new ToolMargin("t", [62, 90], 60, 0, 100).Safe);
        Assert.True(new ToolMargin("t", [70, 90], 60, 0, 100).Safe);
        Assert.False(new ToolMargin("t", [], 60, 0, 100).Safe);
    }
}
