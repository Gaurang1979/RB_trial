using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using VisionSystem.Studio.Views;

[assembly: AvaloniaTestApplication(typeof(VisionSystem.Studio.Tests.HeadlessApp))]

namespace VisionSystem.Studio.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        App.Headless = true;
        return AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>
/// The Run screen rendered at the minimum display size, 1366 x 768 (UI-UX-SPEC.md 7): everything
/// fits without scrolling, touch targets are at least 32 px (one compact density, principle 3), and
/// the toolbar actions are 40 px with icon and label. With VS_SNAPSHOT_DIR set, the screens are also
/// saved as PNG.
/// </summary>
public class LayoutTests
{
    private static (MainWindow Window, MainViewModel Vm, ManualLink Link, ScriptedClient Client) Open(
        AppTheme theme = AppTheme.Dark, int width = 1366, int height = 768)
    {
        Application.Current!.RequestedThemeVariant = App.VariantFor(theme);
        var link = new ManualLink();
        var vm = new MainViewModel(Make.Strings, link, new ImmediateUi(), TimeProvider.System, App.Decode, theme,
            t => Application.Current!.RequestedThemeVariant = App.VariantFor(t));
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, link, new ScriptedClient());
    }

    private static Rect BoundsIn(Visual root, Control c) =>
        c.TranslatePoint(new Point(0, 0), root) is { } p ? new Rect(p, c.Bounds.Size) : default;

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Run_screen_fits_1366_by_768_with_banner_strip_and_action_bar()
    {
        var (window, _, link, client) = Open();
        link.Connect(client, Make.Running(12480, 37) with { Warning = "Image is getting darker. Clean the lens or check the light.",
            Batch = Make.Batch, Plc = Make.Plc });
        for (ulong i = 1; i <= 25; i++) link.Push(Synthetic.Frame(i, i % 5 == 0 ? Verdict.Ng : Verdict.Ok) with { Tools = Make.SixTools });
        Settle(window);

        var screen = new Rect(0, 0, 1366, 768);
        foreach (var name in new[] { "StatusPill", "UserButton", "LanguageButton", "ImageFrame", "ResultBlock", "Counters", "CyclePanel", "LastPartPanel", "BatchPlcPanel", "PlcLast", "BottomPanel", "MinutesChart", "ReasonsChart", "CycleChart", "Banner", "ActionBar" })
        {
            var control = window.FindControl<Control>(name) ?? window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
            Assert.True(control.IsEffectivelyVisible, $"{name} hidden");
            var bounds = BoundsIn(window, control);
            Assert.True(screen.Contains(bounds), $"{name} at {bounds} is outside 1366 x 768");
            Assert.True(bounds.Height > 0, $"{name} squeezed to nothing");
        }
        var image = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "ImageFrame");
        Assert.True(image.Bounds.Height >= 300, $"live image only {image.Bounds.Height} px high");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ScrollViewer>(),
            s => s.Name != null && s.Extent.Height > s.Viewport.Height + 1);
    }

    [AvaloniaFact]
    public async Task Yield_ring_help_button_NG_viewer_and_steps_fit_1366_by_768()
    {
        var (window, vm, link, client) = Open();
        client.Counters = new RunCounters(12517, 12480, 37, 99.7, 48, 91);
        link.Connect(client, Make.Running(12480, 37) with
        {
            Warning = "Codes are getting harder to read. Check the printer.",
            WarningHelp = "Look at the last bad parts.\nClean the print head.\nIf the print stays faint, call maintenance.",
        });
        for (ulong i = 1; i <= 6; i++) link.Push(Synthetic.Frame(i, i % 3 == 0 ? Verdict.Ng : Verdict.Ok));
        vm.Run.Tick();
        Settle(window);

        var screen = new Rect(0, 0, 1366, 768);
        var counters = window.FindDescendant<Control>("Counters");
        var grid = counters.GetVisualDescendants().OfType<Grid>().First();
        Assert.True(grid.DesiredSize.Height <= counters.Bounds.Height, $"counters need {grid.DesiredSize.Height} px, have {counters.Bounds.Height}");
        Assert.True(BoundsIn(window, counters).Contains(BoundsIn(window, window.FindDescendant<Control>("YieldRing"))), "yield ring outside the counters");
        var cycle = window.FindDescendant<Control>("CyclePanel");
        Assert.True(screen.Contains(BoundsIn(window, cycle)));
        var cycleText = window.FindDescendant<TextBlock>("CycleText");
        Assert.Equal("48 ms", cycleText.Text);
        Assert.True(BoundsIn(window, cycle).Contains(BoundsIn(window, cycleText)), "cycle time cut off");
        var help = window.FindDescendant<Button>("WhatToDoButton");
        Assert.True(help.IsEffectivelyVisible);
        Assert.True(BoundsIn(window, window.FindDescendant<Control>("Banner")).Contains(BoundsIn(window, help)));
        Assert.True(screen.Contains(BoundsIn(window, window.FindDescendant<Control>("ActionBar"))));

        void Fits(params string[] names)
        {
            Settle(window);
            foreach (var name in names)
            {
                var c = window.FindDescendant<Control>(name);
                Assert.True(c.IsEffectivelyVisible, $"{name} hidden");
                Assert.True(screen.Contains(BoundsIn(window, c)), $"{name} at {BoundsIn(window, c)} is outside 1366 x 768");
            }
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("vs")))
                Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 32, $"{button.Name} is {button.Bounds.Size}");
        }

        vm.Run.OpenNgCommand.Execute(vm.Run.NgStrip[0]);
        await ((NgViewerViewModel)vm.Overlay!).LoadAsync();
        Fits("NgImageFrame", "NgChecks", "NgBackButton", "NgPreviousButton", "NgNextButton");
        Assert.True(window.FindDescendant<Control>("NgImageFrame").Bounds.Height >= 400);
        vm.Close();

        vm.Run.WhatToDoCommand.Execute(null);
        Fits("AlarmHelpPanel");
        vm.Close();
    }

    [AvaloniaFact]
    public async Task The_supervisor_review_and_learning_fit_1366_by_768()
    {
        var (window, vm, link, client) = Open();
        link.Connect(client, Make.Running(820, 9));
        vm.User = new SignedInUser("sup", "Shift Supervisor", Role.Supervisor, true);
        for (ulong i = 1; i <= 3; i++) link.Push(Synthetic.Frame(i * 2 + 1, Verdict.Ng) with { ImageId = $"{i}-0" });
        Settle(window);
        client.LooksBad.Add(vm.Run.NgStrip[0].Frame.ImageId!);
        vm.Run.OpenNgCommand.Execute(vm.Run.NgStrip[0]);
        var viewer = (NgViewerViewModel)vm.Overlay!;
        await viewer.LoadAsync();
        Assert.True(viewer.MayReview);
        await viewer.AcceptCommand.ExecuteAsync(null);
        Settle(window);
        var screen = new Rect(0, 0, 1366, 768);
        foreach (var name in new[] { "ReviewPanel", "DoubleCheckPanel", "DoubleCheckBad", "DoubleCheckRecord", "NgImageFrame" })
        {
            var c = window.FindDescendant<Control>(name);
            Assert.True(c.IsEffectivelyVisible, $"{name} hidden");
            Assert.True(screen.Contains(BoundsIn(window, c)), $"{name} at {BoundsIn(window, c)} is outside 1366 x 768");
        }
        SaveSnapshot(window, "15-ng-review-double-check-dark");
        await viewer.AcceptRecordOnlyCommand.ExecuteAsync(null);
        await viewer.LearnCommand.ExecuteAsync(null);
        Settle(window);
        foreach (var name in new[] { "LearnResult", "ApplyLearning", "ReviewText", "ReviewCounts" })
        {
            var c = window.FindDescendant<Control>(name);
            Assert.True(c.IsEffectivelyVisible, $"{name} hidden");
            Assert.True(screen.Contains(BoundsIn(window, c)), $"{name} at {BoundsIn(window, c)} is outside 1366 x 768");
        }
        SaveSnapshot(window, "16-ng-review-learn-dark");

        // an operator opens the same image without the review
        vm.Close();
        vm.User = new SignedInUser("op", "Line Operator", Role.Operator, true);
        vm.Run.OpenNgCommand.Execute(vm.Run.NgStrip[0]);
        Settle(window);
        Assert.False(window.FindDescendant<Control>("ReviewPanel").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task The_learning_sorting_nearly_fills_1366_by_768_with_its_buttons_and_second_validation()
    {
        var (window, vm, link, client) = Open();
        var idle = Make.Stopped() with { Learning = new LearningInfo(true, 5, true, 400, 3, true) };
        client.Status = idle;
        link.Connect(client, idle);
        vm.User = new SignedInUser("sup", "Shift Supervisor", Role.Supervisor, true);
        link.Push(idle);
        Settle(window);
        Assert.True(window.FindDescendant<Control>("LearningNotice").IsEffectivelyVisible);
        client.ToSort.AddRange([
            new SortPicture("L01_C1_J007_261104-080000_000001", 0, "Top", false, ["Read code: no code found"], DateTimeOffset.UtcNow, false),
            new SortPicture("L01_C1_J007_261104-080000_000002", 0, "Top", true, [], DateTimeOffset.UtcNow, true)]);
        client.Unreadable.Add("L01_C1_J007_261104-080000_000001");
        vm.Run.OpenSortingCommand.Execute(null);
        var sort = (SortViewModel)vm.Overlay!;
        await sort.LoadAsync();
        Settle(window);
        var screen = new Rect(0, 0, 1366, 768);
        var frame = BoundsIn(window, window.FindDescendant<Control>("SortImageFrame"));
        Assert.True(frame.Width >= 900 && frame.Height >= 500, $"the picture is only {frame.Size}");
        foreach (var name in new[] { "SortAccept", "SortReject", "SortPrevious", "SortNext", "SortSkip", "SortClose", "SortLearn", "SortZoomIn", "SortIdle", "SortSaveSettings" })
        {
            var c = window.FindDescendant<Control>(name);
            Assert.True(c.IsEffectivelyVisible, $"{name} hidden");
            Assert.True(screen.Contains(BoundsIn(window, c)), $"{name} at {BoundsIn(window, c)} is outside 1366 x 768");
            Assert.True(c.Bounds.Width >= 32 && c.Bounds.Height >= 32, $"{name} is {c.Bounds.Size}");
        }
        SaveSnapshot(window, "17-learning-sorting-dark");
        await sort.AcceptCommand.ExecuteAsync(null);
        Settle(window);
        foreach (var name in new[] { "SortCheckPanel", "SortCheckBad", "SortCheckRecord", "SortCheckGood" })
        {
            var c = window.FindDescendant<Control>(name);
            Assert.True(c.IsEffectivelyVisible, $"{name} hidden");
            Assert.True(screen.Contains(BoundsIn(window, c)), $"{name} at {BoundsIn(window, c)} is outside 1366 x 768");
        }
        Assert.False(window.FindDescendant<Control>("SortAccept").IsEffectivelyVisible);
        SaveSnapshot(window, "18-learning-second-validation-dark");
    }

    private static void SaveSnapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("VS_SNAPSHOT_DIR") is not { } dir) return;
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public void Long_result_words_shrink_and_never_push_the_counters()
    {
        var (window, _, link, client) = Open();
        link.Connect(client, Make.Running(12480, 37));
        link.Drop();
        Settle(window);
        var block = BoundsIn(window, window.FindDescendant<Control>("ResultBlock"));
        var counters = window.FindDescendant<Control>("Counters");
        Assert.False(block.Intersects(BoundsIn(window, counters)), "result block overlaps the counters");
        var grid = counters.GetVisualDescendants().OfType<Grid>().First();
        Assert.True(grid.DesiredSize.Height <= counters.Bounds.Height, $"counters need {grid.DesiredSize.Height} px, have {counters.Bounds.Height}");
        var text = window.FindDescendant<Control>("ResultBlock").GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.Equal("Not connected", text.Text);
        Assert.True(text.TextLayout.TextLines.Count == 1, "result text broke into lines");
    }

    [AvaloniaFact]
    public void Touch_targets_are_at_least_32_px_and_toolbar_actions_are_40_px_with_icon_and_label()
    {
        var (window, _, link, client) = Open();
        link.Connect(client, Make.Stopped());
        Settle(window);

        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 32, $"{button.Name} is {button.Bounds.Size}");
        }
        var bar = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "ActionBar");
        var actions = bar.GetVisualDescendants().OfType<Button>().ToList();
        Assert.InRange(actions.Count, 1, 7);
        foreach (var a in actions)
        {
            Assert.Equal(40, a.Bounds.Height);
            Assert.True(BoundsIn(window, bar).Contains(BoundsIn(window, a)), $"{a.Name} outside the toolbar");
            Assert.NotEmpty(a.GetVisualDescendants().OfType<PathIcon>());
            Assert.Contains(a.GetVisualDescendants().OfType<TextBlock>(), t => !string.IsNullOrWhiteSpace(t.Text));
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(a) as string), $"{a.Name} has no tooltip");
        }
    }

    [AvaloniaFact]
    public void Shortcuts_work_on_the_Run_screen_and_never_behind_an_open_panel()
    {
        var (window, vm, link, client) = Open();
        link.Connect(client, Make.Running(10, 0));
        Settle(window);

        Assert.True(vm.ShortcutCommand.CanExecute("parts"));
        Assert.False(vm.ShortcutCommand.CanExecute("next"));   // a workspace key leaves Page Down to the Run screen's lists
        Assert.False(vm.ShortcutCommand.CanExecute("save"));
        vm.ShortcutCommand.Execute("stop");
        Assert.IsType<ConfirmViewModel>(vm.Overlay);           // Stop still asks first
        Assert.False(vm.ShortcutCommand.CanExecute("start"));
        window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        Settle(window);
        Assert.IsType<ConfirmViewModel>(vm.Overlay);           // the question is not replaced
        Assert.Equal(0, client.Calls.Count(c => c == "Start"));
        vm.Close();
        Assert.True(vm.ShortcutCommand.CanExecute("start"));
        window.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.None);
        Settle(window);
        Assert.IsType<PartsViewModel>(vm.Overlay);
    }

    [AvaloniaFact]
    public void This_picture_lists_failed_tools_first_and_clears_with_a_new_picture_or_a_drop()
    {
        var (window, vm, link, client) = Open();
        link.Connect(client, Make.Running(10, 1));
        var tools = Enumerable.Range(1, 7).Select(i => new ToolOutcome($"Tool {i}", i != 7)).ToList();
        link.Push(Synthetic.Frame(1, Verdict.Ng) with { Tools = tools, Codes = [] });
        Settle(window);
        var lines = vm.Run.LastPart;
        Assert.Equal(RunViewModel.LastPartLines, lines.Count);
        Assert.Equal("Tool 7", lines[0].Text);
        Assert.Equal(Tone.Ng, lines[0].Tone);
        Assert.Equal("+2 more", lines[^1].Text);   // 5 shown of 7

        link.Push(Synthetic.Frame(2, Verdict.Ok) with { Tools = [], Codes = [] });
        Settle(window);
        Assert.Empty(vm.Run.LastPart);

        link.Push(Synthetic.Frame(3, Verdict.Ok));
        Settle(window);
        Assert.NotEmpty(vm.Run.LastPart);
        link.Drop();
        Settle(window);
        Assert.Empty(vm.Run.LastPart);
    }

    [AvaloniaFact]
    public void Overview_charts_fill_from_the_parts_and_bad_parts_opens_its_tab()
    {
        var (window, vm, link, client) = Open();
        link.Connect(client, Make.Running(120, 6));
        var now = DateTimeOffset.UtcNow;
        for (ulong i = 1; i <= 120; i++)
            vm.Run.Parts.Add(new PartResult(i, now.AddSeconds(-(int)(120 - i) * 20), "Whisky", $"P{i}",
                i % 20 == 0 ? Verdict.Ng : Verdict.Ok, 40 + i % 30, i % 20 == 0 ? "Code not read" : ""));
        vm.Run.UpdateOverview(now);
        Settle(window);

        Assert.True(vm.Run.Overview.HasParts);
        Assert.Equal("Code not read", Assert.Single(vm.Run.Overview.Reasons).Reason);
        var minutes = window.FindDescendant<MinutesChart>("MinutesChart");
        Assert.Same(vm.Run.Overview, minutes.Data);
        var last = minutes.Bounds.Width - 10;
        Assert.NotNull(minutes.TipAt(new Point(last, minutes.Bounds.Height / 2)));
        var reasons = window.FindDescendant<ReasonsChart>("ReasonsChart");
        Assert.Contains("Code not read", reasons.TipAt(new Point(reasons.Bounds.Width / 2, 10)));

        vm.Run.ShowBadPartsCommand.Execute(null);
        Settle(window);
        Assert.True(window.FindDescendant<Control>("NgStrip").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void An_explicit_neutral_tone_gets_its_colour_too()
    {
        var (window, _, link, client) = Open();
        link.Connect(client, Make.Stopped());
        Settle(window);
        var frame = window.FindDescendant<Border>("ImageFrame");   // starts Neutral, the old default
        Assert.Contains("tone-neutral", frame.Classes);
        Assert.NotNull(frame.BorderBrush);
    }

    [AvaloniaFact]
    public void Status_colours_follow_the_theme()
    {
        var (window, _, link, client) = Open();
        link.Connect(client, Make.Running());
        link.Push(Synthetic.Frame(1, Verdict.Ng));
        Settle(window);
        var block = window.GetVisualDescendants().OfType<Border>().First(c => c.Name == "ResultBlock");
        var dark = ((ISolidColorBrush)block.Background!).Color;
        Application.Current!.RequestedThemeVariant = App.VariantFor(AppTheme.Light);
        Settle(window);
        var light = ((ISolidColorBrush)block.Background!).Color;
        var icon = block.GetVisualDescendants().OfType<PathIcon>().Single();
        Assert.Equal(Color.Parse("#FFFFFF"), ((ISolidColorBrush)icon.Foreground!).Color);   // light: white on red
        Assert.Equal(Color.Parse("#DC2626"), dark);
        Assert.Equal(Color.Parse("#991B1B"), light);
        Application.Current!.RequestedThemeVariant = App.VariantFor(AppTheme.Dark);
    }

    /// <summary>The job designer and a two-camera Run screen at 1366 x 768: canvas, cameras, tools, settings,
    /// Try and Save on screen; camera chips over the live image. Saved as PNG with VS_SNAPSHOT_DIR (a picture of
    /// VS_DESIGNER_JPEG is shown on the canvas when set).</summary>
    [AvaloniaFact]
    public async Task Designer_and_two_camera_run_fit_1366_by_768()
    {
        var (window, vm, link, client) = Open();
        var job = VisionSystem.Studio.ViewModels.JobDraft.New("Tablets QR + batch", 2);
        job.Number = 21;
        job.Cameras[0].Name = "Carton QR";
        job.Cameras[1].Name = "Label and batch";
        job.AddTool(VisionSystem.Studio.ViewModels.ToolKind.ReadCode, 0);
        var ean = job.AddTool(VisionSystem.Studio.ViewModels.ToolKind.ReadCode, 1);
        ean.Name = "EAN";
        ean.Format = "Ean13";
        ean.Expected = "8901234567890";
        var text = job.AddTool(VisionSystem.Studio.ViewModels.ToolKind.CheckText, 1);
        text.Name = "Batch print";
        text.Polarity = "LightOnDark";
        var plc = text.AddLine();
        plc.Source = "Plc";
        plc.SourceName = "Text";
        foreach (var l in new[] { "MFG {MFG}", "EXP {EXP}", "MRP RS.{MRP}" }) text.AddLine(l);
        text.Roi = (470, 259, 701, 310);
        client.JobFiles["Tablets QR + batch"] = job.ToJson();
        client.Products = [new("Tablets QR + batch", "")];
        var jpeg = Environment.GetEnvironmentVariable("VS_DESIGNER_JPEG") is { } f && File.Exists(f) ? File.ReadAllBytes(f) : Synthetic.Frame(1, Verdict.Ok).Jpeg;
        client.TryDefault = new TryOutcome(true, true, 31, jpeg, 1280, 960, 1, 1280, 960,
            [new ToolTried("EAN", "barcode", true, "8901234567890", 2), new ToolTried("Batch print", "ocr", true, "Text OK", 29, 92)],
            [], [new ResultGraphic(GraphicKind.Polygon, [new(500, 280), new(1150, 280), new(1150, 520), new(500, 520)], Verdict.Ok, "Text")],
            [new TextLineRead("Batch print", "B.NO A2610307", "B.NO A2610307", true, 96), new TextLineRead("Batch print", "MFG 10/2026", "MFG 10/2026", true, 97)]);
        var running = Make.Running(183, 17) with { Cameras = ["Carton QR", "Label and batch"], Batch = Make.Batch, Plc = Make.Plc,
            LineSpeedPpm = 225, CycleBudgetMs = 267, Warning = "Image is getting darker. Clean the lens or check the light." };
        link.Connect(client, running);
        vm.User = new SignedInUser("eng", "Vision Engineer", Role.Engineer, true, MayDesignJobs: true);
        SeedHour(vm.Run);
        link.Push(Synthetic.Frame(1, Verdict.Ok) with { Camera = "Carton QR", Tools = [new ToolOutcome("Read code 1", true), new ToolOutcome("Locate carton", true)] });
        link.Push(Synthetic.Frame(2, Verdict.Ng) with { Camera = "Label and batch", Tools = [new ToolOutcome("EAN", true), new ToolOutcome("Batch print", false, 41)] });
        Settle(window);
        // one tile per camera, side by side, each with its own verdict frame, all within 1366 x 768
        var tiles = window.GetVisualDescendants().OfType<Border>().Where(c => c.Name == "ImageFrame").ToList();
        Assert.Equal(2, tiles.Count);
        var (left, right) = (BoundsIn(window, tiles[0]), BoundsIn(window, tiles[1]));
        Assert.True(left.Right <= right.Left && Math.Abs(left.Width - right.Width) < 1 && left.Height >= 280, $"tiles at {left} and {right}");
        Assert.DoesNotContain("tone-ok", tiles[0].Classes);   // the part is NG: Carton QR's tile is not green
        Assert.Contains("tone-ng", tiles[1].Classes);
        var screen1 = new Rect(0, 0, 1366, 768);
        foreach (var name in new[] { "ImageArea", "LastPartPanel", "BatchPlcPanel", "PlcLast", "BottomPanel", "Banner" })
            Assert.True(screen1.Contains(BoundsIn(window, window.FindDescendant<Control>(name))), $"{name} is outside 1366 x 768");
        var dir = Environment.GetEnvironmentVariable("VS_SNAPSHOT_DIR");
        void Save(string name)
        {
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();   // a fresh frame, not the last one drawn
            if (dir is not null) window.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
        }
        Save("10-run-two-cameras-dark");

        Assert.True(window.FindDescendant<Button>("DesignerButton").IsEffectivelyVisible);
        await vm.OpenDesignerCommand.ExecuteAsync(null);
        Assert.True(vm.ShortcutCommand.CanExecute("next"));      // in the workspace the keys are its own
        Assert.False(vm.ShortcutCommand.CanExecute("change"));
        var designer = vm.Designer!;
        designer.SelectedJob = "Tablets QR + batch";
        await Task.Delay(50);
        designer.CameraIndex = 1;
        designer.SelectedTool = designer.CameraTools.Last();
        await designer.TryCommand.ExecuteAsync(null);
        Settle(window);
        var screen = new Rect(0, 0, 1366, 768);
        foreach (var name in new[] { "JobPicker", "CameraList", "JobTree", "DesignCanvas", "TryButton", "RunAllButton", "SaveJob", "TeachButton", "PropertiesScroll", "ToolResultPanel", "BottomDocks", "StatusBar" })
        {
            var control = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
            Assert.True(control.IsEffectivelyVisible, $"{name} hidden");
            var bounds = BoundsIn(window, control);
            Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name} squeezed to nothing");
            Assert.True(screen.Intersects(bounds), $"{name} at {bounds} is off the screen");
            if (name is "SaveJob" or "TryButton" or "TeachButton" or "JobPicker") Assert.True(screen.Contains(bounds), $"{name} at {bounds} is cut by the screen's edge");
        }
        Save("11-designer-check-text-dark");
        // the property search and Essential / All leave only matching rows (Check text selected)
        int Rows() => window.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("prow") && g.IsEffectivelyVisible);
        var all = Rows();
        designer.PropertySearch = "letter";
        Settle(window);
        Assert.Equal(1, Rows());
        designer.ShowEssentialCommand.Execute(null);   // letter height is an advanced setting
        Settle(window);
        Assert.Equal(0, Rows());
        Assert.True(window.FindDescendant<Control>("NoSetting").IsEffectivelyVisible);
        designer.PropertySearch = "";
        Settle(window);
        Assert.InRange(Rows(), 1, all - 1);
        designer.ShowAllCommand.Execute(null);
        Settle(window);
        Assert.Equal(all, Rows());
        Assert.False(window.FindDescendant<Control>("NoSetting").IsEffectivelyVisible);
        var zoomBox = window.FindDescendant<ZoomBox>("Zoom");
        var fitted = zoomBox.Bounds.Size;
        window.FindDescendant<Button>("Zoom100Button").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        // 100 %: one camera pixel per screen pixel; the scroll bars and the overview lie over the picture
        Assert.Equal("100 %", designer.ZoomText);
        Assert.True(window.FindDescendant<Control>("ZoomScrollX").IsEffectivelyVisible);
        Assert.True(window.FindDescendant<Control>("Overview").IsEffectivelyVisible);
        Assert.Equal(fitted, zoomBox.Bounds.Size);
        Save("11a-workspace-zoom-dark");
        window.FindDescendant<ZoomBox>("Zoom").Fit();

        await designer.RunAllCommand.ExecuteAsync(null);
        Settle(window);
        Assert.True(window.FindDescendant<Control>("ImageSet").IsEffectivelyVisible);
        Assert.True(window.FindDescendant<Control>("MarginChart").IsEffectivelyVisible);
        Save("11b-workspace-image-set-dark");
        // the picture keeps most of the height beside the docked panels (UI-UX-SPEC.md 4.19)
        var canvas = BoundsIn(window, window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "DesignCanvas"));
        Assert.True(canvas.Height >= 340 && canvas.Width >= 600, $"picture only {canvas.Size}");

        // an operator signing in closes the designer: the Run screen is back
        vm.User = new SignedInUser("op", "Line Operator", Role.Operator, true);
        Assert.Null(vm.Designer);
        Assert.True(vm.ShowRun);
        vm.Run.BatchCommand.Execute(null);   // running: Press Stop first
        link.Push(Make.Stopped() with { Cameras = ["Carton QR", "Label and batch"],
            Batch = [new BatchEntry("MFG", "MFG", "10/2026", ""), new BatchEntry("EXP", "EXP", "09/2028", ""), new BatchEntry("MRP", "MRP", "145.00", "")] });
        vm.Run.BatchCommand.Execute(null);
        Assert.IsType<BatchViewModel>(vm.Overlay);
        Save("12-start-batch-dark");
        window.Close();
    }

    /// <summary>An hour of a line at about 220 parts a minute, with a slow patch, for the Overview charts.</summary>
    private static void SeedHour(RunViewModel run)
    {
        var now = DateTimeOffset.UtcNow;
        var random = new Random(7);
        string[] reasons = ["Code not read", "Line 4 cut short", "1D code missing", "Camera 2 image dark"];
        ulong id = 0;
        for (var minute = 59; minute >= 0; minute--)
        {
            var slow = minute is >= 17 and <= 20;
            var n = 208 + random.Next(16) - (slow ? 70 : 0);
            for (var i = 0; i < n; i++)
            {
                var at = now.AddMinutes(-minute).AddSeconds(-59 + i * 58.0 / n);
                var bad = random.NextDouble() < (slow ? 0.03 : 0.006);
                var verdict = bad ? (random.NextDouble() < 0.2 ? Verdict.Duplicate : Verdict.Ng) : Verdict.Ok;
                var ms = 46 + random.NextDouble() * 38 + (random.NextDouble() < 0.03 ? 70 : 0);
                run.Parts.Add(new PartResult(++id, at, "Whisky 750 ml", $"P{id}", verdict, ms,
                    verdict == Verdict.Ng ? reasons[Math.Min(3, (int)(random.NextDouble() * random.NextDouble() * 4.5))] : ""));
            }
        }
        run.UpdateOverview(now);
    }

    /// <summary>Saves the screens for the pull request when VS_SNAPSHOT_DIR is set (with VS_STUB=1, one against the stub runtime too).</summary>
    [AvaloniaFact]
    public async Task Snapshots()
    {
        var dir = Environment.GetEnvironmentVariable("VS_SNAPSHOT_DIR");
        if (dir is null) return;   // the layout tests above already render the screens
        void Save(Window w, string name)
        {
            Settle(w);
            var frame = w.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }

        var (w1, vm1, link1, client1) = Open();
        link1.Connect(client1, Make.Running(12480, 37));
        SeedHour(vm1.Run);
        for (ulong i = 1; i <= 9; i++) link1.Push(Synthetic.Frame(i, i % 3 == 0 ? Verdict.Ng : i == 7 ? Verdict.Duplicate : Verdict.Ok));
        link1.Push(Synthetic.Frame(10, Verdict.Ok));
        await Task.Delay(1100);
        vm1.Run.Tick();
        Save(w1, "1-run-ok-dark");
        link1.Push(Synthetic.Frame(11, Verdict.Ng));
        Save(w1, "2-run-ng-dark");
        link1.Drop();
        Save(w1, "3-not-connected-dark");
        w1.Close();

        var (w2, _, link2, client2) = Open(AppTheme.Light);
        link2.Connect(client2, Make.Stopped() with { Counters = new Counters(5120, 14) });
        link2.Push(Synthetic.Frame(1, Verdict.Ok));
        link2.Push(Make.Stopped() with { Counters = new Counters(5120, 14) });
        Save(w2, "4-stopped-light");
        await w2.FindDescendant<Button>("UserButton").Command!.ExecuteAsyncCompat();
        var signIn = (SignInViewModel)((MainViewModel)w2.DataContext!).Overlay!;
        signIn.PickUserCommand.Execute(signIn.Users[0]);
        foreach (var d in "12") signIn.DigitCommand.Execute(d.ToString());
        Save(w2, "5-sign-in-light");
        w2.Close();

        var (w3, vm3, link3, client3) = Open(AppTheme.HighContrast);
        link3.Connect(client3, Make.Running(820, 9) with { Warning = "Image is getting darker. Clean the lens or check the light." });
        link3.Push(Synthetic.Frame(1, Verdict.Ok));
        Save(w3, "6-warning-high-contrast");
        vm3.Run.StopCommand.Execute(null);
        Save(w3, "7-stop-question-high-contrast");
        vm3.Close();
        vm3.Run.ChangeProductCommand.Execute(null);
        Save(w3, "8-change-product-high-contrast");
        w3.Close();

        // Run follow-ups (PR #110): yield ring and cycle time, a tooltip, the NG image viewer,
        // "What do I do?" and product pictures, in dark and light.
        foreach (var (theme, tag) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
        {
            var (w, rvm, rlink, client) = Open(theme);
            client.Counters = new RunCounters(12517, 12480, 37, 99.7, 48, 91);
            client.Products = [new("Whisky 750 ml", "LIQ-750-48", "picture-1"), new("Tablets 10x10", "PH-1010-24", "picture-2"),
                new("Tablets 10x10", "PH-1010-12", "picture-2"), new("Label check", "LBL-A", "picture-3")];
            for (var j = 1; j <= 3; j++) client.Images[$"picture-{j}"] = new FullImage(VisionStudio.Remote.Stub.StubPictures.Product(j), 320, 240);
            rlink.Connect(client, Make.Running(12480, 37) with
            {
                Warning = "Codes are getting harder to read. Check the printer.",
                WarningHelp = "Look at the last bad parts: is the print faint or smudged?\nClean the print head as shown on the printer.\nIf the print stays faint, call maintenance.",
            });
            SeedHour(rvm.Run);
            for (ulong i = 1; i <= 8; i++)
            {
                var f = Synthetic.Frame(i, i % 3 == 0 ? Verdict.Ng : i == 7 ? Verdict.Duplicate : Verdict.Ok);
                rlink.Push(f with { Graphics = f.Graphics.Select(g => g with { Tooltip = g.Verdict switch
                {
                    Verdict.Ng => g.Label + " not read: print smudged",
                    Verdict.Duplicate => "Same code seen before: RJ0187477790",
                    _ => g.Label + " read",
                } }).ToList(), ImageId = $"img-{i}" });
                client.Images[$"img-{i}"] = new FullImage(f.Jpeg, f.Width, f.Height);
            }
            rvm.Run.Tick();
            var last = rvm.Run.Tiles[0].Graphics[0];
            rvm.Run.TapImage(new Point2(last.Points.Average(p => p.X), last.Points.Average(p => p.Y)), 4);
            Save(w, $"10-run-ring-tooltip-help-{tag}");
            rvm.Run.OpenNgCommand.Execute(rvm.Run.NgStrip.First(t => t.Tone == Tone.Ng));
            var viewer = (NgViewerViewModel)rvm.Overlay!;
            await viewer.LoadAsync();
            var bad = viewer.Graphics.First(g => g.Verdict == Verdict.Ng);
            viewer.TapImage(new Point2(bad.Points.Average(p => p.X), bad.Points.Average(p => p.Y)), 4);
            Save(w, $"11-ng-viewer-{tag}");
            rvm.Close();
            rvm.Run.WhatToDoCommand.Execute(null);
            var steps = (AlarmHelpViewModel)rvm.Overlay!;
            steps.DoneCommand.Execute(steps.Steps[0]);
            Save(w, $"12-what-do-i-do-{tag}");
            rvm.Close();
            rlink.Push(Make.Stopped());
            rvm.Run.ChangeProductCommand.Execute(null);
            var picker = (ProductPickerViewModel)rvm.Overlay!;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !picker.Products.All(p => p.HasPicture)) await Task.Delay(20);
            Save(w, $"13-change-product-pictures-{tag}");
            rvm.Close();

            // Parts (owner, 2026-10-30): every part's result, with a gap where parts are missing.
            for (ulong i = 1; i <= 40; i++)
            {
                if (i is 17 or 18) continue;
                var failed = i % 9 == 0;
                rlink.Push(new PartResult(i, DateTimeOffset.Now.AddSeconds(i * 0.27 - 20), "Whisky 750 ml", $"(00)0890123400000{i:000}",
                    failed ? Verdict.Ng : i == 25 ? Verdict.Duplicate : Verdict.Ok, 380 + i % 7 * 9,
                    failed ? "Codes missing in case" : i == 25 ? "Same code seen before" : "", failed ? 46 : 48, failed ? 2 : 0, i == 25 ? 1 : 0));
            }
            rvm.Run.OpenPartsCommand.Execute(null);
            Save(w, $"14-parts-{tag}");
            w.Close();
        }

        // The real view-models and gRPC link against a running stub runtime (VS_STUB=1).
        if (Environment.GetEnvironmentVariable("VS_STUB") != "1") return;
        Application.Current!.RequestedThemeVariant = App.VariantFor(AppTheme.Dark);
        await using var link = new RuntimeLink(GrpcRuntimeConnector.ForDefaultEndpoint(() => "en"));
        var vm = new MainViewModel(Make.Strings, link, new DispatcherUi(), TimeProvider.System, App.Decode, AppTheme.Dark, _ => { });
        var w4 = new MainWindow { DataContext = vm, Width = 1366, Height = 768 };
        w4.Show();
        link.Start();
        var end = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < end && vm.LinkState != LinkState.Connected)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        // Sign in as the stub's operator and start the line, as an operator would.
        var calls = new RuntimeCalls(link);
        var (_, signedIn) = await calls.TryAsync((c, ct) => c.SignInWithPinAsync("operator", "1234", ct));
        vm.User = signedIn?.User;
        await calls.TryAsync((c, ct) => c.StartAsync(ct));
        while (DateTime.UtcNow < end && (vm.LinkState != LinkState.Connected || vm.Run.Tiles[0].Graphics.Count == 0 || vm.Run.NgStrip.Count == 0))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.Equal(LinkState.Connected, vm.LinkState);
        Assert.NotEmpty(vm.Run.Tiles[0].Graphics);
        Save(w4, "9-stub-runtime-dark");
        w4.Close();
    }
}

internal sealed class DispatcherUi : IUi
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

internal static class HeadlessExtensions
{
    public static T FindDescendant<T>(this Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    public static Task ExecuteAsyncCompat(this System.Windows.Input.ICommand command)
    {
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand async) return async.ExecuteAsync(null);
        command.Execute(null);
        return Task.CompletedTask;
    }
}

/// <summary>Made-up live frames (a carton label with a 2D code and a lot line); no customer image.</summary>
internal static class Synthetic
{
    public static LiveFrame Frame(ulong seq, Verdict verdict)
    {
        const int w = 800, h = 600;
        var random = new Random((int)seq);
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(52, 56, 62));
        var matrix = SKMatrix.CreateRotationDegrees((float)(random.NextDouble() * 6 - 3), w / 2f, h / 2f);
        canvas.SetMatrix(matrix);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(214, 196, 160) };
        canvas.DrawRoundRect(new SKRect(140, 80, 660, 520), 18, 18, paint);
        paint.Color = new SKColor(236, 232, 222);
        canvas.DrawRect(new SKRect(200, 140, 600, 460), paint);

        var code = new SKRect(230, 180, 390, 340);
        var missing = verdict == Verdict.Ng && seq % 2 == 1;
        if (!missing)
        {
            const int n = 16;
            var cell = code.Width / n;
            paint.Color = new SKColor(16, 16, 16);
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    if (x == 0 || y == n - 1 || (y == 0 && x % 2 == 0) || (x == n - 1 && y % 2 == 1) || random.Next(2) == 0)
                        canvas.DrawRect(code.Left + x * cell, code.Top + y * cell, cell, cell, paint);
        }
        var text = new SKRect(420, 200, 580, 320);
        using var font = new SKFont(SKTypeface.Default, 26);
        paint.Color = new SKColor(30, 30, 30);
        canvas.DrawText("LOT A23", text.Left, text.Top + 34, font, paint);
        canvas.DrawText("EXP 12/27", text.Left, text.Top + 74, font, paint);
        var smudged = verdict == Verdict.Ng && !missing;
        if (smudged)
        {
            paint.Color = new SKColor(60, 60, 60, 220);
            canvas.DrawOval(new SKRect(text.Left + 40, text.Top + 44, text.Left + 130, text.Top + 84), paint);
        }
        canvas.ResetMatrix();
        using var image = surface.Snapshot();
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 80);

        Point2[] Corners(SKRect r) =>
            new SKPoint[] { new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom) }
                .Select(p => matrix.MapPoint(p)).Select(p => new Point2(p.X, p.Y)).ToArray();
        var codeVerdict = missing ? Verdict.Ng : verdict == Verdict.Duplicate ? Verdict.Duplicate : Verdict.Ok;
        return new LiveFrame(seq, DateTimeOffset.Now.AddSeconds(seq * 7), jpeg.ToArray(), w, h, verdict,
        [
            new(GraphicKind.Polygon, Corners(code), codeVerdict, "Code"),
            new(GraphicKind.Polygon, Corners(text), smudged ? Verdict.Ng : Verdict.Ok, "Text"),
        ], Tools: [new("Read code 1", !missing), new("Check text 1", !smudged, smudged ? 41 : 78)],
        Codes: missing ? [] : [new($"(01)08901234567894(21)7Q2M{seq:D2}", "DataMatrix")]);
    }
}
