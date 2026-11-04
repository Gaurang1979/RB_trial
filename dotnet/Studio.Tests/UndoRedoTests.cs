using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using VisionSystem.Studio.Views;

namespace VisionSystem.Studio.Tests;

/// <summary>The history under undo and redo (AS-18): states, steps, typing kept as one step.</summary>
public class DraftHistoryTests
{
    private long _now;
    private DraftHistory New(string first = "{\"A\":1}")
    {
        var h = new DraftHistory(() => _now);
        h.Reset(first);
        return h;
    }

    [Fact]
    public void Each_new_state_is_a_step_undone_and_redone_in_order()
    {
        var h = New();
        Assert.False(h.CanUndo);
        Assert.True(h.Record("{\"A\":2}"));
        Assert.True(h.Record("{\"A\":3}"));
        Assert.False(h.Record("{\"A\":3}"));   // the same state is no step
        Assert.Equal(2, h.Steps);
        Assert.Equal("{\"A\":2}", h.Undo());
        Assert.Equal("{\"A\":1}", h.Undo());
        Assert.Null(h.Undo());
        Assert.True(h.CanRedo);
        Assert.Equal("{\"A\":2}", h.Redo());
        Assert.Equal("{\"A\":3}", h.Redo());
        Assert.Null(h.Redo());
    }

    [Fact]
    public void A_new_step_after_an_undo_drops_what_could_be_redone()
    {
        var h = New();
        h.Record("{\"A\":2}");
        h.Undo();
        h.Record("{\"A\":5}");
        Assert.False(h.CanRedo);
        Assert.Equal("{\"A\":1}", h.Undo());
    }

    [Fact]
    public void Typing_into_one_setting_is_one_step_until_a_pause_or_another_setting()
    {
        var h = New("{\"Name\":\"\",\"N\":0}");
        h.Record("{\"Name\":\"a\",\"N\":0}", typing: true);
        _now += 500;
        h.Record("{\"Name\":\"ab\",\"N\":0}", typing: true);
        _now += 1500;
        h.Record("{\"Name\":\"abc\",\"N\":0}", typing: true);
        Assert.Equal(1, h.Steps);
        h.Record("{\"Name\":\"abc\",\"N\":7}", typing: true);   // another setting
        Assert.Equal(2, h.Steps);
        _now += DraftHistory.TypingMs + 1;
        h.Record("{\"Name\":\"abc\",\"N\":70}", typing: true);   // after a pause
        Assert.Equal(3, h.Steps);
        h.Record("{\"Name\":\"abc\",\"N\":71}");   // not typing: a command's change is always a step
        Assert.Equal(4, h.Steps);
        Assert.Equal("{\"Name\":\"abc\",\"N\":70}", h.Undo());
        Assert.Equal("{\"Name\":\"abc\",\"N\":7}", h.Undo());
        Assert.Equal("{\"Name\":\"abc\",\"N\":0}", h.Undo());
        Assert.Equal("{\"Name\":\"\",\"N\":0}", h.Undo());
    }

    [Fact]
    public void A_typed_change_right_after_an_undo_is_a_step_of_its_own()
    {
        var h = New("{\"N\":0}");
        h.Record("{\"N\":1}", typing: true);
        h.Record("{\"N\":2}", typing: true);
        Assert.Equal("{\"N\":0}", h.Undo());
        h.Record("{\"N\":3}", typing: true);
        Assert.Equal("{\"N\":0}", h.Undo());
        Assert.Null(h.Undo());
    }

    [Fact]
    public void Only_the_last_steps_are_kept()
    {
        var h = New("{\"N\":-1}");
        for (var i = 0; i < DraftHistory.Kept + 5; i++) h.Record($"{{\"N\":{i}}}");
        Assert.Equal(DraftHistory.Kept, h.Steps);
        string? last = null;
        while (h.Undo() is { } s) last = s;
        Assert.Equal("{\"N\":4}", last);
    }

    [Theory]
    [InlineData("{\"A\":{\"B\":[1,2]}}", "{\"A\":{\"B\":[1,3]}}", "/A/B/1")]
    [InlineData("{\"A\":1,\"B\":2}", "{\"A\":2,\"B\":3}", null)]   // two values
    [InlineData("{\"A\":[1]}", "{\"A\":[1,2]}", null)]   // a tool added: another shape
    [InlineData("{\"A\":1}", "{\"B\":1}", null)]
    [InlineData("{\"A\":1}", "not json", null)]
    public void The_one_value_changed_is_found(string a, string b, string? path) =>
        Assert.Equal(path, DraftHistory.OnlyChange(a, b));
}

/// <summary>Undo and redo in the job designer (UI-UX-SPEC.md 5, AS-18): every create, move, resize, setting change
/// and delete is one step; a dropped tool is one step with its snapping.</summary>
public class UndoRedoViewModelTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private async Task<DesignerViewModel> Open()
    {
        _client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        _client.JobFiles["Syrup"] = JobDraft.New("Syrup", 1).ToJson();
        _client.Products = [new("Tablets", ""), new("Syrup", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        return vm;
    }

    [Fact]
    public async Task An_added_tool_is_undone_and_redone()
    {
        var vm = await Open();
        Assert.False(vm.CanUndo);
        vm.AddReadCodeCommand.Execute(null);
        Assert.True(vm.CanUndo);
        Assert.True(vm.Dirty);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.CameraTools);
        Assert.False(vm.Dirty);   // back where the job was opened
        Assert.False(vm.CanUndo);
        Assert.True(vm.CanRedo);
        Assert.Equal(0, vm.CameraIndex);
        Assert.Equal(0, vm.PictureIndex);
        Assert.Single(vm.Results);   // the picture tried stays shown

        vm.RedoCommand.Execute(null);
        Assert.Equal("Read code 1", vm.SelectedTool!.Name);
        Assert.Equal("Read code 1", Assert.Single(vm.Tree, n => n.IsTool).Name);
        Assert.True(vm.Dirty);
        Assert.Empty(_client.Tries);   // undo and redo change only the draft: nothing is sent
    }

    [Fact]
    public async Task A_removed_tool_comes_back_selected_with_its_settings()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        vm.AddReadTextCommand.Execute(null);
        var code = vm.CameraTools.First(t => t.IsCode);
        vm.SelectedTool = code;
        code.Format = "DataMatrix";
        vm.RemoveToolCommand.Execute(null);
        Assert.Equal(Make.Strings.Format("design.removed", "Read code 1"), vm.Message);
        Assert.DoesNotContain(vm.CameraTools, t => t.IsCode);

        vm.UndoCommand.Execute(null);
        Assert.Equal("Read code 1", vm.SelectedTool!.Name);
        Assert.Equal("DataMatrix", vm.SelectedTool.Format);
        Assert.Equal(2, vm.CameraTools.Count);
    }

    [Fact]
    public async Task A_moved_or_resized_area_is_put_back()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        vm.AreaDrawn(new Point2(20, 10), new Point2(220, 110));
        vm.AreaDrawn(new Point2(30, 20), new Point2(130, 70));   // drawn again: moved and smaller
        Assert.Equal((60, 40, 200, 100), vm.SelectedTool!.Roi);
        vm.UndoCommand.Execute(null);
        Assert.Equal((40, 20, 400, 200), vm.SelectedTool!.Roi);
        Assert.Contains(vm.Graphics, g => g.Verdict == Verdict.Info && g.Points[0].X == 20);   // the area drawn where it was
        vm.UndoCommand.Execute(null);
        Assert.Null(vm.SelectedTool!.Roi);
        vm.RedoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        Assert.Equal((60, 40, 200, 100), vm.SelectedTool!.Roi);
    }

    [Fact]
    public async Task A_setting_changed_in_the_property_table_is_a_step_even_before_it_is_seen()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        vm.SelectedTool!.Format = "QR";   // the property table changes the draft without a command
        vm.Shortcut("undo");
        Assert.NotEqual("QR", vm.SelectedTool!.Format);
        Assert.Equal("Read code 1", vm.SelectedTool.Name);   // only the setting was taken back
        vm.Shortcut("redo");
        Assert.Equal("QR", vm.SelectedTool!.Format);
    }

    [Fact]
    public async Task Typing_the_job_name_is_one_step()
    {
        var vm = await Open();
        vm.JobName = "T";
        vm.JobName = "Ta";
        vm.JobName = "Tab";
        vm.UndoCommand.Execute(null);
        Assert.Equal("Tablets", vm.JobName);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task A_dropped_tool_is_taken_back_whole_by_one_undo()
    {
        var vm = await Open();
        _client.TryWith = r => r.TeachTool is null ? null
            : _client.TryDefault with { TaughtJson = Teach(r, t => { t.Format = "DataMatrix"; t.Roi = (600, 450, 80, 80); }), TaughtText = "Found Data Matrix: ABC" };
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));
        Assert.Equal((600, 450, 80, 80), vm.SelectedTool!.Roi);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.CameraTools);
        Assert.False(vm.Dirty);
        vm.RedoCommand.Execute(null);
        Assert.Equal((600, 450, 80, 80), vm.SelectedTool!.Roi);   // as taught, not as dropped
        Assert.Equal("DataMatrix", vm.SelectedTool.Format);
    }

    [Fact]
    public async Task A_drop_that_found_nothing_still_stops_Save_after_undo_and_redo()
    {
        var vm = await Open();
        _client.TryWith = r => r.TeachTool is null ? null
            : new TryOutcome(false, Refused: new CommandResult(false, "runtime.refused.teach", "No code was read here."));
        await vm.DropToolAsync(ToolKind.ReadCode, new Point2(320, 240));
        Assert.Equal("!", vm.Tree.Single(n => n.IsTool).Mark);
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        Assert.Equal("!", vm.Tree.Single(n => n.IsTool).Mark);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_client.Saved);
        Assert.Equal(Make.Strings.Format("design.drop.save_first", "Read code 1"), vm.Message);
    }

    [Fact]
    public async Task A_teach_is_one_step()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        _client.TryWith = r => r.TeachTool is null ? null
            : _client.TryDefault with { TaughtJson = Teach(r, t => t.Format = "DataMatrix"), TaughtText = "Found" };
        await vm.TeachCommand.ExecuteAsync(null);
        Assert.Equal("DataMatrix", vm.SelectedTool!.Format);
        vm.UndoCommand.Execute(null);
        Assert.Equal("Read code 1", vm.SelectedTool!.Name);
        Assert.NotEqual("DataMatrix", vm.SelectedTool.Format);
    }

    [Fact]
    public async Task Undo_past_a_save_makes_the_draft_unsaved_and_keeps_the_saved_version()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        vm.SelectedTool!.Format = "QR";
        vm.SelectedTool.ExpectedCount = 1;
        vm.SelectedTool.Roi = (10, 10, 100, 100);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(_client.Saved);
        Assert.False(vm.Dirty);
        Assert.Equal(4, vm.Job!.Version);

        vm.UndoCommand.Execute(null);
        Assert.True(vm.Dirty);
        Assert.Equal(4, vm.Job!.Version);   // the version Save gave is never undone
        vm.RedoCommand.Execute(null);
        Assert.False(vm.Dirty);
    }

    [Fact]
    public async Task Another_job_opened_starts_its_own_history()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        await vm.OpenAsync("Syrup");
        Assert.False(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal("Syrup", vm.JobName);
    }

    [Fact]
    public async Task An_added_camera_undone_leaves_the_last_camera_shown()
    {
        var vm = await Open();
        vm.AddCameraCommand.Execute(null);
        Assert.Equal(2, vm.CameraIndex);
        vm.UndoCommand.Execute(null);
        Assert.Equal(2, vm.Cameras.Count);
        Assert.Equal(1, vm.CameraIndex);
    }

    [Fact]
    public async Task Accept_all_is_one_step()
    {
        var vm = await Open();
        _client.FindAnswer = new FindOutcome(true, [Suggestion("Read code 9"), Suggestion("Read code 8")]);
        await vm.FindEverythingCommand.ExecuteAsync(null);
        await vm.AcceptAllSuggestionsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CameraTools.Count);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.CameraTools);
    }

    [Fact]
    public async Task Nothing_is_undone_while_the_designer_is_busy()
    {
        var vm = await Open();
        vm.AddReadCodeCommand.Execute(null);
        vm.Busy = true;
        vm.UndoCommand.Execute(null);
        Assert.Single(vm.CameraTools);
    }

    private static string Teach(TryRequest r, Action<ToolDraft> teach)
    {
        var job = JobDraft.Parse(r.Json);
        teach(job.Tools.Single(t => t.Name == r.TeachTool));
        return job.ToJson();
    }

    private static ToolSuggestion Suggestion(string name)
    {
        var tool = JobDraft.New("x", 1).AddTool(ToolKind.ReadCode, 0);
        tool.Name = name;
        return new ToolSuggestion("code", name, [], [], [], tool.Node.ToJsonString());
    }

    private static readonly ToolSuggestion Qr = new("code", "QR Code", ["A1"], ["code"],
        [new(900, 100), new(1100, 100), new(1100, 300), new(900, 300)],
        """{"kind":"code","Name":"QR Code","Camera":0,"Enabled":true,"Format":"QrCode","ExpectedCount":1}""");

    /// <summary>An accepted suggestion that is undone is a suggestion again, and a redo takes it off the list (AS-18, AS-01).</summary>
    [Fact]
    public async Task Undo_of_Accept_brings_the_suggestion_back_and_redo_takes_it_off()
    {
        var vm = await Open();
        _client.FindAnswer = new FindOutcome(true, [Qr], 10, [1, 2, 3], 640, 480, 0.5, 1280, 960);
        await vm.FindEverythingCommand.ExecuteAsync(null);
        Assert.Single(vm.Suggestions);

        await vm.AcceptSuggestionCommand.ExecuteAsync(vm.Suggestions[0]);
        Assert.Empty(vm.Suggestions);
        Assert.Single(vm.CameraTools);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.CameraTools);
        Assert.Equal([Qr.Summary], vm.Suggestions.Select(s => s.Summary));   // offered again, never added by the undo
        Assert.True(vm.HasSuggestions);
        Assert.Contains(vm.Graphics, g => g.Tooltip == Qr.Summary);          // and on the picture

        vm.RedoCommand.Execute(null);
        Assert.Single(vm.CameraTools);
        Assert.Empty(vm.Suggestions);
        Assert.False(vm.HasSuggestions);
    }

    /// <summary>A tool that was never a suggestion does not become one when its add is undone.</summary>
    [Fact]
    public async Task Undo_of_a_tool_added_by_hand_offers_no_suggestion()
    {
        var vm = await Open();
        _client.FindAnswer = new FindOutcome(true, [Qr], 10, [1, 2, 3], 640, 480, 0.5, 1280, 960);
        await vm.FindEverythingCommand.ExecuteAsync(null);
        vm.DismissSuggestionCommand.Execute(vm.Suggestions[0]);
        vm.AddReadCodeCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Suggestions);
    }
}

public class UndoRedoKeyTests
{
    [AvaloniaFact]
    public async Task Ctrl_Z_undoes_outside_a_text_box_and_Ctrl_Y_redoes()
    {
        var link = new ManualLink();
        var client = new ScriptedClient();
        var vm = new MainViewModel(Make.Strings, link, new ImmediateUi(), TimeProvider.System, App.Decode, AppTheme.Dark, _ => { });
        var window = new MainWindow { DataContext = vm, Width = 1366, Height = 768 };
        window.Show();
        client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        client.Products = [new("Tablets", "")];
        link.Connect(client, Make.Stopped());
        vm.User = new SignedInUser("eng", "Vision Engineer", Role.Engineer, true, MayDesignJobs: true);
        await vm.OpenDesignerCommand.ExecuteAsync(null);
        var designer = vm.Designer!;
        designer.SelectedJob = "Tablets";
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        designer.AddReadCodeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<DesignerView>().First();
        Assert.True(view.FindControl<MenuItem>("UndoMenu")!.IsEnabled);   // the Edit menu follows the history
        Assert.False(view.FindControl<MenuItem>("RedoMenu")!.IsEnabled);

        window.FindDescendant<Button>("TryButton").Focus();
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(designer.CameraTools);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(designer.CameraTools);
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(designer.CameraTools);

        // in a text box Ctrl+Z is the text box's own
        var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.IsEnabled);
        box.Focus();
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(designer.CameraTools);
    }

    [AvaloniaFact]
    public async Task The_toolbar_fits_1366_by_768_and_its_Undo_and_Redo_buttons_follow_the_history()
    {
        var link = new ManualLink();
        var client = new ScriptedClient();
        var vm = new MainViewModel(Make.Strings, link, new ImmediateUi(), TimeProvider.System, App.Decode, AppTheme.Dark, _ => { });
        var window = new MainWindow { DataContext = vm, Width = 1366, Height = 768 };
        window.Show();
        client.JobFiles["Tablets"] = JobDraft.New("Tablets", 2).ToJson();
        client.Products = [new("Tablets", "")];
        link.Connect(client, Make.Stopped());
        vm.User = new SignedInUser("eng", "Vision Engineer", Role.Engineer, true, MayDesignJobs: true);
        await vm.OpenDesignerCommand.ExecuteAsync(null);
        var designer = vm.Designer!;
        designer.SelectedJob = "Tablets";
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        var bar = window.FindDescendant<Control>("DesignerToolbar");
        Assert.True(bar.Bounds.Width <= 1366);
        var save = window.FindDescendant<Button>("SaveJob");
        var name = window.FindDescendant<TextBox>("JobName");
        var number = window.FindDescendant<TextBox>("JobNumber");
        var rightEdge = new Control[] { name, number, save }.Min(c => c.TranslatePoint(new Point(0, 0), window)!.Value.X);
        var left = bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Name != "SaveJob" && b.Name != "JobMenu").ToList();
        Assert.NotEmpty(left);
        foreach (var b in left)
        {
            var x = b.TranslatePoint(new Point(0, 0), window)!.Value.X;
            Assert.True(x + b.Bounds.Width <= rightEdge, $"{b.Name ?? b.Command?.ToString()} ends at {x + b.Bounds.Width}, under the name box at {rightEdge}");
        }
        var saveRight = save.TranslatePoint(new Point(0, 0), window)!.Value.X + save.Bounds.Width;
        Assert.True(saveRight <= 1366, $"Save ends at {saveRight}");

        var undo = window.FindDescendant<Button>("UndoButton");
        var redo = window.FindDescendant<Button>("RedoButton");
        Assert.False(undo.IsEnabled);
        designer.AddReadCodeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(undo.IsEnabled);
        Assert.False(redo.IsEnabled);
        undo.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(designer.CameraTools);
        Assert.True(redo.IsEnabled);
        redo.Command!.Execute(null);
        Assert.Single(designer.CameraTools);
        window.Close();
    }
}
