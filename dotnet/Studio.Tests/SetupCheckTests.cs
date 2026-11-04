using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

/// <summary>The setup checks at job load on the Run screen (requirements AS-13, AS-14, section 16 "When the picture
/// and pixel checks run"): the runtime's advice reaches the Run screen's card while the line is stopped. Advice only:
/// Start stays available.</summary>
public class SetupCheckTests
{
    private sealed class Overlays : IOverlayHost
    {
        public void Show(object overlay) { }
        public void Close() { }
        public void CloseIf(object overlay) { }
    }

    private readonly ManualLink _link = new();
    private readonly ScriptedClient _client = new();
    private readonly RunViewModel _vm;

    public SetupCheckTests()
    {
        _vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), new Overlays(), new ManualTime(), _ => null);
        _link.StateChanged += s => _vm.Apply(s, null);
        _link.StatusReceived += s => _vm.Apply(_link.State, s);
    }

    private static PixelCheckTried TooFew(float found = 1.6f) =>
        new("module", 2.4f, 1.8f, found, 1.5f, 2.3f, 25, 38, false, "", null, []);

    private static SetupCheckOutcome Done(params CameraSetupOutcome[] cameras) => new(SetupCheckState.Done, "Tablets", cameras);

    private static CameraSetupOutcome Camera(string name, PictureCheckOutcome? check = null, ToolPixels[]? tools = null,
        string[]? unmeasured = null, int pictures = 1, string error = "", string source = "camera") =>
        new(name, pictures, source, error, check, tools ?? [], unmeasured ?? []);

    private static PictureCheckOutcome Advice(params PictureAdviceLine[] advice) => new([], advice);

    [Fact]
    public void The_status_carries_the_check_from_the_contract()
    {
        var s = new V1.Status { State = V1.Status.Types.State.Stopped, ActiveJob = "Tablets" };
        s.SetupCheck = new V1.SetupCheck { State = V1.SetupCheck.Types.State.Done, Job = "Tablets" };
        var cam = new V1.CameraSetupCheck { Camera = "Top", Pictures = 2, Source = "camera", PictureCheck = new V1.PictureCheck() };
        cam.PictureCheck.Advice.Add(new V1.PictureAdvice { Kind = "focus" });
        cam.Tools.Add(new V1.ToolResult { Name = "Read code 1", Kind = "barcode", Pixels = new V1.PixelCheck { Measure = "module", NeedPx = 2.4f, FoundPx = 1.6f, Factor = 1.5f } });
        cam.Unmeasured.Add("Read text 1");
        s.SetupCheck.Cameras.Add(cam);
        s.SetupCheck.Cameras.Add(new V1.CameraSetupCheck { Camera = "Side", Error = "No picture came from the camera. Check its cable and power." });

        var m = GrpcRuntimeClient.Map(s).SetupCheck!;
        Assert.Equal(SetupCheckState.Done, m.State);
        Assert.Equal("Tablets", m.Job);
        Assert.Equal(2, m.Cameras.Count);
        var top = m.Cameras[0];
        Assert.Equal(("Top", 2, "camera"), (top.Camera, top.Pictures, top.Source));
        Assert.Equal("focus", Assert.Single(top.PictureCheck!.Advice).Kind);
        var tool = Assert.Single(top.Tools);
        Assert.Equal("Read code 1", tool.Tool);
        Assert.True(tool.Pixels.TooFew);
        Assert.Equal(["Read text 1"], top.Unmeasured);
        Assert.Equal(0, m.Cameras[1].Pictures);
        Assert.StartsWith("No picture", m.Cameras[1].Error);

        // an older runtime sends none; a check still going has no cameras yet
        Assert.Null(GrpcRuntimeClient.Map(new V1.Status()).SetupCheck);
        var checking = new V1.Status { SetupCheck = new V1.SetupCheck { State = V1.SetupCheck.Types.State.Checking } };
        Assert.Equal(SetupCheckState.Checking, GrpcRuntimeClient.Map(checking).SetupCheck!.State);
    }

    [Fact]
    public void The_advice_reaches_the_Run_screen_while_stopped_and_Start_stays_available()
    {
        var stopped = Make.Stopped() with { Batch = Make.Batch };
        _client.Status = stopped;
        _link.Connect(_client, stopped);
        Assert.False(_vm.HasSetupCheck);

        _link.Push(stopped with { SetupCheck = new(SetupCheckState.Checking, "Tablets", []) });
        Assert.True(_vm.HasSetupCheck);
        Assert.Equal(Make.Strings["run.setup.checking"], Assert.Single(_vm.SetupLines).Text);

        var check = Done(Camera("Top", Advice(new PictureAdviceLine("tilt", ["top_left"])), [new("Read code 1", TooFew())]));
        _link.Push(stopped with { SetupCheck = check });
        Assert.Equal(2, _vm.SetupLines.Count);
        Assert.Equal(Make.Strings.Format("ws.pc.advice.tilt", Make.Strings["ws.pc.zone.top_left"]), _vm.SetupLines[0].Text);
        Assert.StartsWith("Read code 1: needs 1.5", _vm.SetupLines[1].Text);
        Assert.All(_vm.SetupLines, l => Assert.Equal(Tone.Warn, l.Tone));
        // advice only: Start is not refused for it
        Assert.True(_vm.StartAvailable);
        Assert.Null(_vm.StartReasonKey);

        // while the line runs the card is hidden (no check runs then)
        _link.Push(Make.Running() with { Batch = Make.Batch, SetupCheck = check });
        Assert.False(_vm.HasSetupCheck);
        Assert.Empty(_vm.SetupLines);
    }

    [Fact]
    public void Each_camera_says_what_it_found_or_that_all_is_good()
    {
        var lines = SetupAdvice.Lines(Make.Strings, Done(
            Camera("Top"),
            Camera("Side", pictures: 0, error: "No picture came from the camera. Check its cable and power."),
            Camera("Back", Advice(new PictureAdviceLine("dark", ["centre"])), unmeasured: ["Read text 1"]),
            Camera("Front", Advice(new PictureAdviceLine("dark", ["centre"])), source: "stored"),
            Camera("Label", tools: [new("Read code 2", TooFew() with { Accepted = true, AcceptedBy = "eng" })])));

        Assert.Equal(Make.Strings.Format("run.setup.camera", "Top", Make.Strings["run.setup.good"]), lines[0].Text);
        Assert.Equal(Tone.Ok, lines[0].Tone);
        Assert.Contains("Side: ", lines[1].Text);
        Assert.Contains("The line can still run.", lines[1].Text);
        // a dark grabbed picture: the PLC may keep the light off outside a run; not said for stored pictures
        Assert.Contains(Make.Strings["run.setup.dark_light"], lines[2].Text);
        Assert.Equal(Make.Strings.Format("run.setup.camera", "Back", Make.Strings.Format("run.setup.unmeasured", "Read text 1")), lines[3].Text);
        Assert.Equal(Tone.Info, lines[3].Tone);
        Assert.DoesNotContain(Make.Strings["run.setup.dark_light"], lines[4].Text);
        Assert.StartsWith("Front: ", lines[4].Text);
        // pixels accepted as they are: nothing more to say
        Assert.Equal(Make.Strings.Format("run.setup.camera", "Label", Make.Strings["run.setup.good"]), lines[5].Text);
        Assert.Equal(6, lines.Count);

        // one camera: not named; stopped by Start: said once
        Assert.Equal(Make.Strings["run.setup.good"], Assert.Single(SetupAdvice.Lines(Make.Strings, Done(Camera("Top")))).Text);
        Assert.Equal(Make.Strings["run.setup.stopped"],
            Assert.Single(SetupAdvice.Lines(Make.Strings, new(SetupCheckState.Stopped, "Tablets", []))).Text);
        Assert.Empty(SetupAdvice.Lines(Make.Strings, null));
    }
}
