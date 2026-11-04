using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

public class ResultHoldTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_bad_part_stays_up_for_a_second_even_when_good_parts_follow()
    {
        var hold = new ResultHold();
        hold.Add(Verdict.Ng, T0);
        hold.Add(Verdict.Ok, T0.AddMilliseconds(300));
        hold.Add(Verdict.Ok, T0.AddMilliseconds(900));
        Assert.Equal(Verdict.Ng, hold.Current(T0.AddMilliseconds(950)));
        Assert.Equal(Verdict.Ok, hold.Current(T0.AddMilliseconds(1000)));
    }

    [Fact]
    public void At_high_rates_the_worst_of_the_last_200_ms_shows()
    {
        var hold = new ResultHold();
        hold.Add(Verdict.Warn, T0);
        hold.Add(Verdict.Ok, T0.AddMilliseconds(100));
        Assert.Equal(Verdict.Warn, hold.Current(T0.AddMilliseconds(150)));
        Assert.Equal(Verdict.Ok, hold.Current(T0.AddMilliseconds(400)));   // window passed: the last part
    }

    [Fact]
    public void An_NG_is_not_replaced_by_a_later_duplicate_within_the_hold()
    {
        var hold = new ResultHold();
        hold.Add(Verdict.Ng, T0);
        hold.Add(Verdict.Duplicate, T0.AddMilliseconds(500));
        Assert.Equal(Verdict.Ng, hold.Current(T0.AddMilliseconds(600)));
        Assert.Equal(Verdict.Duplicate, hold.Current(T0.AddMilliseconds(1100)));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var hold = new ResultHold();
        hold.Add(Verdict.Ng, T0);
        hold.Clear();
        Assert.Equal(Verdict.None, hold.Current(T0));
    }
}

public class PillStateTests
{
    [Fact]
    public void Highest_state_wins_and_every_state_has_a_shape()
    {
        var all = Make.Running() with
        {
            LicenceOk = false, PlcOk = false, CameraOk = false, Alarm = "x", Warning = "y",
        };
        Assert.Equal("status.connecting", PillState.From(LinkState.Connecting, null).TextKey);
        Assert.Equal("status.not_connected", PillState.From(LinkState.NotConnected, all).TextKey);
        Assert.Equal("status.locked", PillState.From(LinkState.Connected, all).TextKey);
        Assert.Equal("status.plc_lost", PillState.From(LinkState.Connected, all with { LicenceOk = true }).TextKey);
        Assert.Equal("status.no_camera", PillState.From(LinkState.Connected, all with { LicenceOk = true, PlcOk = true }).TextKey);
        Assert.Equal("status.alarm", PillState.From(LinkState.Connected, all with { LicenceOk = true, PlcOk = true, CameraOk = true }).TextKey);
        var warn = PillState.From(LinkState.Connected, Make.Running() with { Warning = "y" });
        Assert.Equal(("status.check", Tone.Warn, Icons.Bang), (warn.TextKey, warn.Tone, warn.Icon));
        var run = PillState.From(LinkState.Connected, Make.Running());
        Assert.Equal(("status.running", Tone.Ok, Icons.Play), (run.TextKey, run.Tone, run.Icon));
        var stop = PillState.From(LinkState.Connected, Make.Stopped());
        Assert.Equal(("status.stopped", Tone.Stopped, Icons.Pause), (stop.TextKey, stop.Tone, stop.Icon));
    }
}

public class RunViewModelTests
{
    private sealed class Overlays : IOverlayHost
    {
        public object? Shown { get; private set; }
        public void Show(object overlay) => Shown = overlay;
        public void Close() => Shown = null;
        public void CloseIf(object overlay)
        {
            if (ReferenceEquals(Shown, overlay)) Shown = null;
        }
    }

    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly ManualTime _time = new();
    private readonly Overlays _overlays = new();
    private readonly RunViewModel _vm;

    public RunViewModelTests()
    {
        _vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, _time, _ => new FakeImage());
        _link.StateChanged += s => _vm.Apply(s, null);   // as MainViewModel: the status comes on its own
        _link.StatusReceived += s => _vm.Apply(_link.State, s);
        _link.FrameReceived += _vm.OnFrame;
    }

    private void Connect(RuntimeStatus status)
    {
        _client.Status = status;
        _link.Connect(_client, status);
    }

    [Fact]
    public void Not_connected_shows_no_result_and_refuses_every_action_with_a_reason()
    {
        _link.Drop();
        Assert.Equal(Tone.Ng, _vm.ResultTone);
        Assert.Equal("Not connected", _vm.ResultText);
        Assert.True(_vm.HasBanner);
        Assert.Equal("Please wait. Reconnecting to the inspection.", _vm.BannerText);
        Assert.False(_vm.StartAvailable);
        Assert.False(_vm.ChangeAvailable);
        Assert.True(_vm.ImageDimmed);
    }

    [Fact]
    public void A_good_part_shows_OK_and_counts_from_the_runtime()
    {
        Connect(Make.Running(good: 12480, bad: 37));
        Assert.Equal("Waiting for a part", _vm.ResultText);
        _link.Push(Make.Frame(Verdict.Ok));
        Assert.Equal((Tone.Ok, Icons.Check, "OK"), (_vm.ResultTone, _vm.ResultIcon, _vm.ResultText));
        Assert.Equal(Tone.Ok, _vm.Tiles[0].FrameTone);
        Assert.False(_vm.ImageDimmed);
        Assert.Equal(12517.ToString("N0"), _vm.TotalText);
        Assert.Equal((99.7).ToString("0.0") + " %", _vm.GoodPercentText);
        Assert.Empty(_vm.NgStrip);
    }

    [Fact]
    public void A_bad_part_is_held_for_a_second_and_goes_to_the_strip()
    {
        Connect(Make.Running());
        _link.Push(Make.Frame(Verdict.Ng, 1));
        _time.Advance(TimeSpan.FromMilliseconds(400));
        _link.Push(Make.Frame(Verdict.Ok, 2));
        Assert.Equal(("NG", Tone.Ng, Icons.Cross), (_vm.ResultText, _vm.ResultTone, _vm.ResultIcon));
        _time.Advance(TimeSpan.FromMilliseconds(700));
        _vm.Tick();
        Assert.Equal("OK", _vm.ResultText);
        Assert.Single(_vm.NgStrip);
        Assert.Equal(Tone.Ng, _vm.NgStrip[0].Tone);
    }

    [Fact]
    public void No_picture_text_hides_once_an_image_or_graphics_arrive()
    {
        Connect(Make.Running());
        Assert.True(_vm.Tiles[0].ShowNoPicture);
        var noImage = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, _time, _ => null);
        noImage.OnFrame(Make.Frame(Verdict.Ok) with { Jpeg = [] });
        Assert.False(noImage.Tiles[0].ShowNoPicture);                     // graphics drawn on our own background
        noImage.OnFrame(Make.Frame(Verdict.Ok) with { Jpeg = [], Graphics = [] });
        Assert.True(noImage.Tiles[0].ShowNoPicture);
        _link.Push(Make.Frame(Verdict.Ok));
        Assert.False(_vm.Tiles[0].ShowNoPicture);
    }

    [Fact]
    public void Stop_then_Start_never_shows_the_last_OK_as_the_new_run()
    {
        Connect(Make.Running());
        _link.Push(Make.Frame(Verdict.Ok));
        Assert.Equal("OK", _vm.ResultText);
        _link.Push(Make.Stopped());
        _link.Push(Make.Running());
        Assert.Equal("Waiting for a part", _vm.ResultText);
        Assert.True(_vm.ImageDimmed);                        // the old picture until a new part comes
        _link.Push(Make.Frame(Verdict.Ok, 2));
        Assert.False(_vm.ImageDimmed);
    }

    [Fact]
    public void A_product_change_or_an_alarm_clears_the_shown_result()
    {
        Connect(Make.Running());
        _link.Push(Make.Frame(Verdict.Ok));
        _link.Push(Make.Running() with { Product = new ProductRef("Rum 375 ml", "") });
        Assert.Equal("Waiting for a part", _vm.ResultText);
        _link.Push(Make.Frame(Verdict.Ok, 2));
        _link.Push(Make.Running() with { Product = new ProductRef("Rum 375 ml", ""), Alarm = "Bin full." });
        _link.Push(Make.Running() with { Product = new ProductRef("Rum 375 ml", "") });
        Assert.Equal("Waiting for a part", _vm.ResultText);
    }

    [Fact]
    public void A_bad_part_whose_frame_was_dropped_still_shows_NG()
    {
        Connect(Make.Running(good: 10, bad: 0));
        _link.Push(Make.Frame(Verdict.Ok));
        _link.Push(Make.Running(good: 11, bad: 1));      // the NG part's frame never came
        Assert.Equal(("NG", Tone.Ng), (_vm.ResultText, _vm.ResultTone));
        _time.Advance(TimeSpan.FromMilliseconds(1100));
        _vm.Tick();
        Assert.Equal("NG", _vm.ResultText);                // it was the last part
        _link.Push(Make.Frame(Verdict.Ok, 2));
        Assert.Equal("OK", _vm.ResultText);
    }

    [Fact]
    public void Without_new_parts_the_last_result_expires()
    {
        Connect(Make.Running());
        _link.Push(Make.Frame(Verdict.Ok));
        _time.Advance(ResultHold.LastFor);
        _vm.Tick();
        Assert.Equal("Waiting for a part", _vm.ResultText);
    }

    [Fact]
    public void A_preview_frame_is_not_a_part()
    {
        Connect(Make.Running());
        _link.Push(Make.Frame(Verdict.None));
        Assert.Equal("Waiting for a part", _vm.ResultText);
        Assert.Empty(_vm.NgStrip);
        Assert.False(_vm.ImageDimmed);
    }

    [Fact]
    public void Reconnecting_waits_for_the_new_status_before_showing_the_line()
    {
        Connect(Make.Running());
        _link.Drop();
        _client.Status = Make.Stopped();
        _link.Connect(_client, Make.Stopped());
        Assert.Equal("Stopped", _vm.ResultText);
    }

    [Fact]
    public async Task A_command_that_gets_no_answer_in_time_is_not_a_drop()
    {
        Connect(Make.Stopped());
        _client.StartResult = new CommandResult(false, "run.reason.no_answer");
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(0, _link.Broken);
        Assert.StartsWith("No answer from the inspection yet.", _vm.NoticeText);
    }

    [Fact]
    public void The_strip_keeps_the_last_twenty_bad_parts_newest_first()
    {
        Connect(Make.Running());
        for (ulong i = 1; i <= 25; i++) _link.Push(Make.Frame(i % 2 == 0 ? Verdict.Duplicate : Verdict.Ng, i));
        Assert.Equal(RunViewModel.StripLength, _vm.NgStrip.Count);
        Assert.Equal(Tone.Ng, _vm.NgStrip[0].Tone);          // part 25
        Assert.Equal(Tone.Duplicate, _vm.NgStrip[1].Tone);   // part 24
    }

    [Fact]
    public void After_a_drop_an_old_result_is_never_shown_as_current()
    {
        Connect(Make.Running());
        _link.Push(Make.Frame(Verdict.Ok));
        _link.Drop();
        Assert.Equal("Not connected", _vm.ResultText);
        Connect(Make.Running());
        Assert.Equal("Waiting for a part", _vm.ResultText);
    }

    [Fact]
    public void Stopped_line_dims_the_image_and_offers_Start()
    {
        Connect(Make.Stopped());
        Assert.Equal(("Stopped", Tone.Stopped, Icons.Pause), (_vm.ResultText, _vm.ResultTone, _vm.ResultIcon));
        Assert.True(_vm.ImageDimmed);
        Assert.True(_vm.StartAvailable);
        Assert.False(_vm.StopAvailable);
    }

    [Fact]
    public async Task Start_when_not_possible_says_why_and_does_not_call_the_runtime()
    {
        Connect(Make.Stopped() with { CameraOk = false });
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Start", _client.Calls);
        Assert.True(_vm.HasNotice);
        Assert.Equal("No camera. Check the camera cable and power.", _vm.NoticeText);
        Assert.Equal("Camera not found. Check the camera cable and power.", _vm.BannerText);
        _time.Advance(TimeSpan.FromSeconds(6));
        _vm.Tick();
        Assert.False(_vm.HasNotice);
    }

    [Fact]
    public async Task A_refusal_shows_our_words_for_a_known_key_else_the_runtime_text()
    {
        Connect(Make.Stopped());
        _client.StartResult = new CommandResult(false, "runtime.refused.stopFirst", "Stop the line first.");
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Press Stop first.", _vm.NoticeText);

        _client.StartResult = new CommandResult(false, "runtime.refused.somethingNew", "Das Band ist gesperrt.");
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Das Band ist gesperrt.", _vm.NoticeText);
    }

    [Fact]
    public async Task A_call_that_fails_reports_the_link_broken()
    {
        Connect(Make.Stopped());
        _client.FailWith = new IOException("gone");
        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, _link.Broken);
        Assert.Equal("Not connected to the inspection. Please wait.", _vm.NoticeText);
    }

    [Fact]
    public async Task PLC_test_signal_buttons_are_an_engineers_in_Teach_and_ask_whether_the_machine_is_safe()
    {
        var mayTest = false;
        _vm.MayTestPlc = () => mayTest;
        var plc = new PlcSignals(true, true, false);
        Connect(Make.Stopped() with { Plc = plc });
        Assert.False(_vm.PlcTestShown);   // an operator: no buttons
        _vm.PlcTestOkCommand.Execute(null);
        Assert.Null(_overlays.Shown);

        mayTest = true;
        _vm.UserChanged();
        Assert.True(_vm.PlcTestShown);
        Assert.True(_vm.PlcTestAvailable);

        // asks once; No sends nothing
        _vm.PlcTestNgCommand.Execute(null);
        var confirm = Assert.IsType<ConfirmViewModel>(_overlays.Shown);
        Assert.Equal("This will send a signal to the machine. Is the machine safe?", confirm.Question);
        confirm.NoCommand.Execute(null);
        Assert.DoesNotContain("PlcTest NG", _client.Calls);

        _vm.PlcTestNgCommand.Execute(null);
        await ((ConfirmViewModel)_overlays.Shown!).YesCommand.ExecuteAsync(null);
        Assert.Contains("PlcTest NG", _client.Calls);
        Assert.Equal("Test NG sent to the PLC for 2 s. It is not counted as a part.", _vm.NoticeText);
        Assert.Equal("0", _vm.TotalText);   // a test signal is no part

        // the runtime's refusal is shown in our words
        _client.PlcTestResult = new CommandResult(false, "runtime.refused.plcLatchedNg", "raw");
        _vm.PlcTestOkCommand.Execute(null);
        await ((ConfirmViewModel)_overlays.Shown!).YesCommand.ExecuteAsync(null);
        Assert.Equal("An NG signal is on: the OK test waits until it ends.", _vm.NoticeText);

        // RunEnable on, the link lost, or the line running: not usable, the reason instead of the question
        Connect(Make.Stopped() with { Plc = plc with { RunEnable = true } });
        Assert.False(_vm.PlcTestAvailable);
        _vm.PlcTestOkCommand.Execute(null);
        Assert.Null(_overlays.Shown);
        Assert.Equal("The PLC has RunEnable on: switch the line to set-up on the PLC first.", _vm.NoticeText);
        Connect(Make.Stopped() with { Plc = plc with { HeartbeatOk = false } });
        Assert.False(_vm.PlcTestAvailable);
        Connect(Make.Running() with { Plc = plc });
        Assert.False(_vm.PlcTestAvailable);
        _vm.PlcTestOkCommand.Execute(null);
        Assert.Null(_overlays.Shown);
        Assert.Equal("Press Stop first.", _vm.NoticeText);

        // no PLC set up: no buttons
        Connect(Make.Stopped());
        Assert.False(_vm.PlcTestShown);
    }

    [Fact]
    public async Task Stop_asks_once_and_only_Yes_stops()
    {
        Connect(Make.Running());
        _vm.StopCommand.Execute(null);
        var confirm = Assert.IsType<ConfirmViewModel>(_overlays.Shown);
        Assert.Equal("Stop checking parts? The machine will treat parts as bad.", confirm.Question);
        confirm.NoCommand.Execute(null);
        Assert.Null(_overlays.Shown);
        Assert.DoesNotContain("Stop", _client.Calls);

        _vm.StopCommand.Execute(null);
        await ((ConfirmViewModel)_overlays.Shown!).YesCommand.ExecuteAsync(null);
        Assert.Contains("Stop", _client.Calls);
        Assert.Null(_overlays.Shown);
    }

    [Fact]
    public async Task Change_product_lists_the_runtime_products_and_changes()
    {
        Connect(Make.Stopped());
        _vm.ChangeProductCommand.Execute(null);
        var picker = Assert.IsType<ProductPickerViewModel>(_overlays.Shown);
        // The scripted runtime answers at once, so the list is already loaded.
        var current = picker.Products.First(p => p.IsCurrent);
        Assert.Equal("Whisky 750 ml", current.Name);

        var other = picker.Products.First(p => p.Name == "Rum 375 ml");
        await picker.PickCommand.ExecuteAsync(other);
        Assert.Contains("Change Rum 375 ml", _client.Calls);
        Assert.Null(_overlays.Shown);
        Assert.Equal("Product changed: Rum 375 ml", _vm.NoticeText);
    }

    [Fact]
    public async Task A_refused_product_change_keeps_the_picker_open_with_the_reason()
    {
        Connect(Make.Running());
        _client.ChangeResult = new CommandResult(false, "runtime.refused.stopFirst", "Stop the line before changing the product.");
        var picker = new ProductPickerViewModel(Make.Strings, new RuntimeCalls(_link), _client.Status.Product, _overlays, (_, _) => { });
        _overlays.Show(picker);
        await picker.LoadAsync();
        await picker.PickCommand.ExecuteAsync(picker.Products.First(p => !p.IsCurrent));
        Assert.Same(picker, _overlays.Shown);
        Assert.Equal("Press Stop first.", picker.Message);
    }

    [Fact]
    public void Alarm_and_warning_texts_come_from_the_runtime()
    {
        Connect(Make.Running() with { Warning = "Image is getting darker. Clean the lens." });
        Assert.Equal((Tone.Warn, "Image is getting darker. Clean the lens."), (_vm.BannerTone, _vm.BannerText));
        _link.Push(Make.Running() with { Alarm = "Reject bin full. Empty the bin." });
        Assert.Equal((Tone.Ng, "Reject bin full. Empty the bin."), (_vm.BannerTone, _vm.BannerText));
        Assert.Equal("Alarm", _vm.ResultText);
    }
}
