using System.Text.Json.Nodes;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Tests;

/// <summary>The job draft edits the job file in place: what it does not show is kept, new tools
/// carry their type first, and each setting is written as VisionCore reads it.</summary>
public class JobDraftTests
{
    private const string TwoCameras = """
        {
          "Name": "Tablets 10x10", "Number": 12, "Version": 3, "Description": "keep me",
          "Cameras": [ { "Name": "Cap", "Serial": "", "TestFolder": "/pics/cap", "ExposureUs": 900, "GainDb": 2 },
                       { "Name": "Label", "Serial": "S2" } ],
          "Tools": [
            { "kind": "code", "Name": "QR", "Camera": 0, "Enabled": true, "Format": "QrCode", "ExpectedCount": 1, "Expected": "",
              "TimeBudgetMs": 80, "DpmLighting": "Diffuse90" },
            { "kind": "text", "Name": "Batch", "Camera": 1, "Enabled": true, "Mode": "Check",
              "Lines": [ { "Entry": "Template", "Source": "Job", "SourceName": "", "Template": "B.NO {BATCH}", "PlcText": null } ],
              "Print": "Solid", "Polarity": "LightOnDark", "Dates": { "ExpiryMonths": 24 } }
          ]
        }
        """;

    [Fact]
    public void What_the_designer_does_not_show_is_kept_on_save()
    {
        var job = JobDraft.Parse(TwoCameras);
        job.Tools[0].Expected = "https://x.in/{SERIAL}";
        job.Cameras[0].Name = "Top";
        var back = JsonNode.Parse(job.ToJson())!.AsObject();
        Assert.Equal("keep me", back["Description"]!.ToString());
        Assert.Equal(900, back["Cameras"]![0]!["ExposureUs"]!.GetValue<int>());
        Assert.Equal("Top", back["Cameras"]![0]!["Name"]!.ToString());
        Assert.Equal(80, back["Tools"]![0]!["TimeBudgetMs"]!.GetValue<int>());
        Assert.Equal("https://x.in/{SERIAL}", back["Tools"]![0]!["Expected"]!.ToString());
        Assert.Equal(24, back["Tools"]![1]!["Dates"]!["ExpiryMonths"]!.GetValue<int>());
    }

    [Fact]
    public void Tools_belong_to_their_camera_and_a_new_tool_starts_with_its_kind()
    {
        var job = JobDraft.Parse(TwoCameras);
        Assert.Equal(["QR"], job.ToolsOf(0).Select(t => t.Name));
        Assert.Equal(["Batch"], job.ToolsOf(1).Select(t => t.Name));
        var code = job.AddTool(ToolKind.ReadCode, 1);
        var check = job.AddTool(ToolKind.CheckText, 1);
        Assert.Equal("Read code 1", code.Name);
        Assert.Equal("Check text 1", check.Name);
        Assert.Equal("kind", code.Node.First().Key);   // the job file's type comes first
        Assert.True(check.IsCheck);
        Assert.Equal(["Batch", "Read code 1", "Check text 1"], job.ToolsOf(1).Select(t => t.Name));
        job.RemoveTool(code);
        Assert.DoesNotContain("Read code 1", job.ToJson());
    }

    [Fact]
    public void Pharma_preset_fills_the_required_AIs()
    {
        var t = JobDraft.Parse(TwoCameras).Tools[0];
        t.Gs1Check = true;
        t.UseGs1PharmaCommand.Execute(null);
        Assert.Equal("01, 17, 10, 21", t.Gs1Required);
        Assert.Equal(["01", "17", "10", "21"], t.Node["Gs1"]!["Required"]!.AsArray().Select(x => x!.ToString()));
    }

    [Fact]
    public void Code_settings_are_written_as_the_job_file_holds_them()
    {
        var t = JobDraft.Parse(TwoCameras).Tools[0];
        t.Format = "Gs1_128";
        Assert.True(t.IsLinear);
        t.MinLength = 14;
        t.MaxLength = 14;
        t.CheckDigit = "Verify";
        t.MinGrade = "C";
        t.Gs1Check = true;
        t.Gs1Required = "(01), 17 10;21";
        t.Roi = (100, 50, 300, 200);
        var n = t.Node;
        Assert.Equal("Gs1_128", n["Format"]!.ToString());
        Assert.Equal(2, n["MinGrade"]!.GetValue<int>());
        Assert.Equal(["01", "17", "10", "21"], n["Gs1"]!["Required"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal(300, n["Roi"]!["Width"]!.GetValue<int>());
        Assert.Equal("C", t.MinGrade);
        t.MinGrade = "None";
        Assert.Equal(-1, n["MinGrade"]!.GetValue<int>());
        t.Gs1Check = false;
        Assert.Null(n["Gs1"]);
        t.Roi = null;
        Assert.Null(n["Roi"]);
        Assert.Equal("Whole picture", t.RoiText);
    }

    [Fact]
    public void Text_lines_take_their_expected_text_from_the_job_the_PLC_or_a_tool()
    {
        var t = JobDraft.Parse(TwoCameras).Tools[1];
        Assert.Equal("LightOnDark", t.Polarity);
        Assert.Equal("B.NO {BATCH}", Assert.Single(t.Lines).Template);
        var mrp = t.AddLine("MRP RS.{MRP}");
        var plc = t.AddLine("");
        plc.Source = "Plc";
        plc.SourceName = "UserIn1";
        Assert.True(plc.NeedsSourceName);
        var lines = t.Node["Lines"]!.AsArray();
        Assert.Equal(3, lines.Count);
        Assert.Equal("Template", lines[1]!["Entry"]!.ToString());
        Assert.Equal("Plc", lines[2]!["Source"]!.ToString());
        t.RemoveLine(mrp);
        Assert.Equal(2, t.Node["Lines"]!.AsArray().Count);

        // the PLC's text block: the line gets a place for its string (PLC-INTERFACE.md 3.7)
        plc.SourceName = "Text";
        Assert.True(plc.IsPlcText);
        Assert.Equal(501, plc.PlcRegister);
        plc.PlcLength = 20;
        Assert.Equal(20, plc.Node["PlcText"]!["Length"]!.GetValue<int>());
        Assert.Equal("HighFirst", plc.Node["PlcText"]!["ByteOrder"]!.ToString());
    }

    [Fact]
    public void A_new_job_has_two_cameras_and_at_most_four()
    {
        var job = JobDraft.New("Syrup 100 ml", 2);
        Assert.Equal(["Camera 1", "Camera 2"], job.Cameras.Select(c => c.Name));
        Assert.NotNull(job.AddCamera());
        Assert.NotNull(job.AddCamera());
        Assert.Null(job.AddCamera());
        Assert.Equal(4, JsonNode.Parse(job.ToJson())!["Cameras"]!.AsArray().Count);
    }

    [Fact]
    public void Batch_values_to_try_with_are_typed_as_key_equals_value()
    {
        var b = DesignerViewModel.ParseBatch("BATCH=A123; MRP = 45.00 ;junk")!;
        Assert.Equal("A123", b["batch"]);
        Assert.Equal("45.00", b["MRP"]);
        Assert.Equal(2, b.Count);
        Assert.Null(DesignerViewModel.ParseBatch(" "));
        var both = "BATCH=A1; PLC501=B.NO A1; PLCx=1";
        Assert.Equal(["BATCH", "PLCx"], DesignerViewModel.ParseBatch(both)!.Keys.Order());
        Assert.Equal("B.NO A1", DesignerViewModel.ParsePlcText(both)![501]);
    }
}

/// <summary>The designer screen against a scripted runtime: open, try, teach, try all, save, refusals.</summary>
public class DesignerViewModelTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();
    private bool _closed;

    private async Task<DesignerViewModel> Open(string json)
    {
        _client.JobFiles["Tablets"] = json;
        _client.Products = [new("Tablets", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => _closed = true);
        await vm.LoadAsync();
        await vm.OpenAsync("Tablets");
        return vm;
    }

    private static string Job() => JobDraft.New("Tablets", 2).ToJson();

    [Fact]
    public async Task Opening_a_job_shows_its_cameras_tools_and_pictures()
    {
        var draft = JobDraft.New("Tablets", 2);
        draft.AddTool(ToolKind.ReadCode, 0);
        draft.AddTool(ToolKind.CheckText, 1);
        var vm = await Open(draft.ToJson());
        Assert.Equal(["Tablets"], vm.Jobs);
        Assert.Equal(2, vm.Cameras.Count);
        Assert.Equal(0, vm.CameraIndex);
        Assert.Equal("Read code 1", Assert.Single(vm.CameraTools).Name);
        Assert.Equal(3, vm.Pictures.Count);
        vm.CameraIndex = 1;
        Assert.Equal("Check text 1", Assert.Single(vm.CameraTools).Name);
        Assert.Contains("good1.bmp", vm.PictureText);
    }

    [Fact]
    public async Task Try_shows_the_result_with_the_selected_tools_area_scaled_to_the_picture()
    {
        var vm = await Open(Job());
        vm.AddReadCodeCommand.Execute(null);
        vm.SelectedTool!.Roi = (100, 200, 400, 300);
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal("good1.bmp", _client.Tries[^1].Stored);
        Assert.Contains("\"Read code 1\"", _client.Tries[^1].Json);   // the draft as it is now, not as saved
        Assert.Equal(Tone.Ok, vm.FrameTone);
        Assert.Single(vm.Results);
        var area = Assert.Single(vm.Graphics, g => g.Verdict == Verdict.Info);
        Assert.Equal(50, area.Points[0].X);   // full-image pixels × the JPEG's scale (0.5)
        Assert.Equal(250, area.Points[2].X);

        // an area drawn on the picture (JPEG pixels) becomes the tool's area in full-image pixels
        vm.AreaDrawn(new Point2(20, 10), new Point2(220, 110));
        Assert.Equal((40, 20, 400, 200), vm.SelectedTool.Roi);
        vm.AreaDrawn(new Point2(20, 10), new Point2(22, 12));   // a tap is not an area
        Assert.Equal((40, 20, 400, 200), vm.SelectedTool.Roi);
    }

    [Fact]
    public async Task Teach_takes_the_taught_draft_and_keeps_the_tool_selected()
    {
        var vm = await Open(Job());
        vm.CameraIndex = 1;
        vm.AddCheckTextCommand.Execute(null);
        var taught = JobDraft.Parse(vm.Job!.ToJson());
        var t = taught.Tools.Single();
        t.AddLine("B.NO A12345");
        t.AddLine("MFG 10/2026");
        t.Roi = (10, 20, 300, 120);
        _client.TryDefault = _client.TryDefault with { TaughtJson = taught.ToJson(), TaughtText = "Found 2 line(s) of text" };
        await vm.TeachCommand.ExecuteAsync(null);
        Assert.Equal("Check text 1", _client.Tries[^1].TeachTool);
        Assert.Equal(1, vm.CameraIndex);
        Assert.Equal("Check text 1", vm.SelectedTool!.Name);
        Assert.Equal(["B.NO A12345", "MFG 10/2026"], vm.SelectedTool.Lines.Select(l => l.Template));
        Assert.Equal("Found 2 line(s) of text", vm.Message);
        Assert.True(vm.Dirty);

        // the pictures stay: Try all right after Teach tries every one, once
        Assert.Equal(3, vm.Pictures.Count);
        _client.Tries.Clear();
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(["good1.bmp", "good2.bmp", "bad1.bmp"], _client.Tries.Select(t => t.Stored));
    }

    [Fact]
    public async Task Try_all_counts_good_and_bad_pictures_and_names_the_bad_ones()
    {
        var vm = await Open(Job());
        vm.AddReadCodeCommand.Execute(null);
        _client.TryAnswers["bad1.bmp"] = _client.TryDefault with { Pass = false, TimeMs = 30 };
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(3, _client.Tries.Count);
        Assert.Contains("2 OK, 1 NG", vm.Summary);
        Assert.Contains("bad1.bmp", vm.Summary);
        Assert.Contains("30 ms", vm.Summary);
    }

    [Fact]
    public async Task A_save_the_runtime_refuses_shows_its_reason_and_stays_unsaved()
    {
        var vm = await Open(Job());
        vm.AddCheckTextCommand.Execute(null);
        _client.SaveAnswer = new SaveOutcome(false, Refused: new CommandResult(false, "runtime.refused.jobInvalid", "Check text 1: add the lines to check"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Check text 1: add the lines to check", vm.Message);
        Assert.Equal(Tone.Ng, vm.MessageTone);
        Assert.True(vm.Dirty);

        _client.SaveAnswer = null;
        vm.SelectedTool!.AddLine("B.NO {BATCH}");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Tone.Ok, vm.MessageTone);
        Assert.Contains("version 4", vm.Message);
        Assert.False(vm.Dirty);
        Assert.Contains("B.NO {BATCH}", _client.JobFiles["Tablets"]);
    }

    [Fact]
    public async Task Without_an_engineer_the_runtime_says_no_and_the_screen_says_who_may()
    {
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => null, () => _closed = true);
        _client.JobFiles.Clear();
        await vm.OpenAsync("Tablets");
        Assert.False(vm.HasJob);
        Assert.Equal("No such job.", vm.Message);
        vm.CloseCommand.Execute(null);
        Assert.True(_closed);
    }

    [Fact]
    public async Task A_new_job_needs_a_name_not_taken()
    {
        var vm = await Open(Job());
        vm.NewJobName = "tablets";
        await vm.NewJobCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["design.name_taken"], vm.Message);
        vm.NewJobName = "Syrup";
        await vm.NewJobCommand.ExecuteAsync(null);
        Assert.Equal("Syrup", vm.JobName);
        Assert.Equal(2, vm.Cameras.Count);
        vm.JobNumber = "21";
        Assert.Equal(21, vm.Job!.Number);
    }
}

/// <summary>The Job menu (UI-UX-SPEC.md 4.7): Save as, Reload, opening another job over unsaved changes,
/// and the designer starting on the line's job.</summary>
public class JobMenuTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private async Task<DesignerViewModel> Open()
    {
        var a = JobDraft.New("Tablets", 2);
        a.Number = 21;
        a.AddTool(ToolKind.ReadCode, 0);
        _client.JobFiles["Tablets"] = a.ToJson();
        _client.JobFiles["Syrup"] = JobDraft.New("Syrup", 1).ToJson();
        _client.JobFiles[""] = a.ToJson();   // GetJob("") is the line's open job
        _client.Products = [new("Tablets", ""), new("Syrup", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => null, () => { });
        await vm.LoadOpenJobAsync();
        await Task.Yield();
        return vm;
    }

    [Fact]
    public async Task The_designer_starts_on_the_lines_job_and_knows_what_changed()
    {
        var vm = await Open();
        Assert.Equal("Tablets", vm.JobName);
        Assert.False(vm.Dirty);
        vm.CameraTools[0].Expected = "ABC";   // a setting typed in a field is a change too
        Assert.True(vm.Dirty);
    }

    [Fact]
    public async Task Reload_asks_before_dropping_changes_and_brings_back_the_saved_job()
    {
        var vm = await Open();
        vm.CameraTools[0].Expected = "ABC";
        vm.ReloadCommand.Execute(null);
        Assert.Equal(Make.Strings["design.drop_changes"], vm.Question);
        vm.AnswerNoCommand.Execute(null);
        Assert.Null(vm.Question);
        Assert.Equal("ABC", vm.CameraTools[0].Expected);   // kept

        vm.ReloadCommand.Execute(null);
        await vm.AnswerYesCommand.ExecuteAsync(null);
        Assert.Equal("", vm.CameraTools[0].Expected);
        Assert.False(vm.Dirty);
        Assert.Contains("GetJob Tablets", _client.Calls);
    }

    [Fact]
    public async Task Opening_another_job_over_changes_asks_first()
    {
        var vm = await Open();
        vm.JobNumber = "22";
        vm.SelectedJob = "Syrup";
        Assert.NotNull(vm.Question);
        Assert.Equal("Tablets", vm.JobName);
        vm.AnswerNoCommand.Execute(null);
        Assert.Equal("Tablets", vm.SelectedJob);
        Assert.Null(vm.Question);   // putting the list back asks nothing

        vm.SelectedJob = "Syrup";
        await vm.AnswerYesCommand.ExecuteAsync(null);
        Assert.Equal("Syrup", vm.JobName);
        Assert.False(vm.Dirty);
    }

    [Fact]
    public async Task Save_as_makes_a_new_job_without_the_PLC_number_and_keeps_the_old_one()
    {
        var vm = await Open();
        vm.CameraTools[0].Expected = "ABC";
        vm.SaveAsCommand.Execute(null);
        Assert.True(vm.SavingAs);
        Assert.Equal("Tablets copy", vm.SaveAsName);
        vm.SaveAsName = "syrup";
        await vm.SaveAsConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["design.name_taken"], vm.Message);   // names are told apart without case
        vm.SaveAsName = "Tablets 2";
        await vm.SaveAsConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.SavingAs);
        Assert.Equal("Tablets 2", vm.JobName);
        Assert.Equal("", vm.JobNumber);   // two jobs with one PLC number would leave the choice to chance
        Assert.False(vm.Dirty);
        Assert.Contains("Tablets 2", vm.Jobs);
        Assert.Contains("ABC", _client.JobFiles["Tablets 2"]);
        Assert.DoesNotContain("ABC", _client.JobFiles["Tablets"]);   // the old job is as it was
    }

    [Fact]
    public async Task A_new_job_never_saved_has_nothing_to_reload()
    {
        var vm = await Open();
        vm.NewJobName = "Fresh";
        await vm.NewJobCommand.ExecuteAsync(null);
        Assert.True(vm.Dirty);
        vm.ReloadCommand.Execute(null);
        Assert.Equal(Make.Strings["design.never_saved"], vm.Message);
        Assert.Null(vm.Question);
    }
}

/// <summary>Two cameras on the Run screen, and Start batch.</summary>
public class TwoCameraRunTests
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
    private readonly ManualTime _time = new();
    private readonly RunViewModel _vm;

    public TwoCameraRunTests()
    {
        _vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), _overlays, _time, _ => new FakeImage());
    }

    private static LiveFrame Frame(string camera, Verdict v, ulong seq) => Make.Frame(v, seq) with { Camera = camera };

    private void Connect(RuntimeStatus status)
    {
        _link.Connect(_client, status);
        _vm.Apply(LinkState.Connected, status);
    }

    [Fact]
    public void Each_camera_has_its_own_tile_with_its_own_picture_and_verdict_frame()
    {
        Connect(Make.Running() with { Cameras = ["Cap", "Label"] });
        Assert.Equal(["Cap", "Label"], _vm.Tiles.Select(t => t.Name));
        Assert.All(_vm.Tiles, t => Assert.True(t.ShowHeader));
        Assert.All(_vm.Tiles, t => Assert.True(t.Dimmed));   // no picture yet

        _vm.OnFrame(Frame("Cap", Verdict.Ok, 1));
        var cap = _vm.Tiles[0].Graphics;
        Assert.Equal(Tone.Ok, _vm.Tiles[0].FrameTone);
        Assert.False(_vm.Tiles[0].Dimmed);
        Assert.Equal(Tone.Neutral, _vm.Tiles[1].FrameTone);   // never green without its own picture
        Assert.True(_vm.Tiles[1].Dimmed);

        _vm.OnFrame(Frame("Label", Verdict.Ng, 2));
        Assert.Same(cap, _vm.Tiles[0].Graphics);               // Cap's picture stays in its tile
        Assert.NotEmpty(_vm.Tiles[1].Graphics);
        Assert.Equal(Tone.Neutral, _vm.Tiles[0].FrameTone);     // the part is NG: no tile is green
        Assert.Equal(Tone.Ng, _vm.Tiles[1].FrameTone);
        Assert.Equal(Icons.Cross, _vm.Tiles[1].ResultIcon);
        Assert.Equal(Tone.Ng, _vm.ResultTone);                 // the part is NG
        Assert.StartsWith("C2 ", Assert.Single(_vm.NgStrip).Time);

        // a camera the job does not list: its result counts, its picture goes nowhere
        _vm.OnFrame(Frame("Side", Verdict.Ng, 3));
        Assert.Equal(2, _vm.NgStrip.Count);
        Assert.Same(cap, _vm.Tiles[0].Graphics);
    }

    [Fact]
    public void A_camera_whose_pictures_stop_coming_is_dimmed_and_never_green()
    {
        Connect(Make.Running() with { Cameras = ["Cap", "Label"] });
        _vm.OnFrame(Frame("Cap", Verdict.Ok, 1));
        _vm.OnFrame(Frame("Label", Verdict.Ok, 2));
        Assert.Equal(Tone.Ok, _vm.Tiles[1].FrameTone);
        for (ulong i = 3; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _vm.OnFrame(Frame("Cap", Verdict.Ok, i));   // only Cap's pictures come
        }
        Assert.False(_vm.Tiles[0].Dimmed);
        Assert.True(_vm.Tiles[1].Dimmed);
        Assert.Equal(Tone.Neutral, _vm.Tiles[1].FrameTone);
        Assert.Equal(Icons.Camera, _vm.Tiles[1].ResultIcon);

        _vm.Apply(LinkState.Connected, Make.Stopped() with { Cameras = ["Cap", "Label"] });
        Assert.All(_vm.Tiles, t => Assert.True(t.Dimmed));
        Assert.All(_vm.Tiles, t => Assert.Equal(Tone.Neutral, t.FrameTone));
    }

    [Fact]
    public void This_picture_has_a_heading_per_camera_and_each_cameras_lines()
    {
        Connect(Make.Running() with { Cameras = ["Cap", "Label"] });
        _vm.OnFrame(Frame("Cap", Verdict.Ok, 1) with { Tools = [new ToolOutcome("QR", true)], Codes = [] });
        var tools = Enumerable.Range(1, 5).Select(i => new ToolOutcome($"Text {i}", i != 5)).ToList();
        _vm.OnFrame(Frame("Label", Verdict.Ng, 2) with { Tools = tools, Codes = [] });
        var lines = _vm.LastPart.ToList();
        Assert.True(lines[0].Header);
        Assert.Equal("Cap", lines[0].Text);
        Assert.Equal("QR", lines[1].Text);
        var label = lines.FindIndex(l => l.Header && l.Text == "Label");
        Assert.Equal(Tone.Ng, lines[label].Tone);
        Assert.Equal("Text 5", lines[label + 1].Text);         // failed first
        Assert.Equal("+4 more", lines[^1].Text);               // two lines each with two cameras
        Assert.Equal(5, lines.Count);

        _link.Drop();
        _vm.Apply(LinkState.NotConnected, null);
        Assert.All(_vm.LastPart, l => Assert.True(l.Header)); // headings only: no part is current
    }

    [Fact]
    public void While_the_part_is_NG_no_cameras_tile_is_green()
    {
        Connect(Make.Running(10, 0) with { Cameras = ["Cap", "Label"] });
        _vm.OnFrame(Frame("Cap", Verdict.Ok, 1));
        _vm.OnFrame(Frame("Label", Verdict.Ok, 2));
        Assert.All(_vm.Tiles, t => Assert.Equal(Tone.Ok, t.FrameTone));
        // the runtime counts a bad part whose pictures all passed (the PLC link, a camera without a picture)
        _vm.Apply(LinkState.Connected, Make.Running(10, 1) with { Cameras = ["Cap", "Label"] });
        Assert.Equal(Tone.Ng, _vm.ResultTone);
        Assert.All(_vm.Tiles, t => Assert.Equal(Tone.Neutral, t.FrameTone));
        Assert.All(_vm.LastPart.Where(l => l.Header), l => Assert.NotEqual(Tone.Ok, l.Tone));
    }

    [Fact]
    public void The_PLCs_last_part_is_timed_by_this_screen_not_the_runtimes_clock()
    {
        // the runtime's PC is an hour ahead: its last OK is green for a few seconds only
        var plc = new PlcSignals(true, true, true, 7, _time.Now.AddHours(1), true);
        Connect(Make.Running() with { Plc = plc });
        Assert.Equal(Tone.Ok, _vm.PlcLastTone);
        _time.Advance(ResultHold.LastFor);
        _vm.Apply(LinkState.Connected, Make.Running() with { Plc = plc });
        Assert.Equal(Tone.Neutral, _vm.PlcLastTone);
        _vm.Apply(LinkState.Connected, Make.Running() with { Plc = plc with { LastResultTime = _time.Now.AddHours(1), LastResultPass = false } });
        Assert.Equal(Tone.Ng, _vm.PlcLastTone);   // the next part
    }

    [Fact]
    public void One_camera_has_one_tile_without_a_heading()
    {
        Connect(Make.Running() with { Cameras = ["Top"] });
        var tile = Assert.Single(_vm.Tiles);
        Assert.False(tile.ShowHeader);
        _vm.OnFrame(Frame("Top", Verdict.Ok, 1) with { Tools = [new ToolOutcome("QR", true)], Codes = [] });
        Assert.DoesNotContain(_vm.LastPart, l => l.Header);
        _vm.OnFrame(Frame("", Verdict.Ok, 2));                 // a frame that names no camera goes to the one tile
        Assert.Same(tile, Assert.Single(_vm.Tiles));
        Assert.False(tile.Dimmed);
    }

    [Fact]
    public void Line_speed_and_budget_reach_the_charts_only_while_connected()
    {
        Connect(Make.Running() with { LineSpeedPpm = 225, CycleBudgetMs = 267 });
        Assert.Equal(225, _vm.LineSpeedPpm);
        Assert.Equal(267, _vm.CycleBudgetMs);
        _vm.Apply(LinkState.NotConnected, null);
        Assert.Equal(0, _vm.LineSpeedPpm);
        Assert.Equal(0, _vm.CycleBudgetMs);
    }

    [Fact]
    public void Batch_and_PLC_card_shows_the_values_and_signals_and_nothing_good_without_a_live_link()
    {
        var at = _time.Now.AddSeconds(-1);
        var plc = new PlcSignals(true, true, true, 48213, at, true, "ModbusTcp :502");
        Connect(Make.Running() with
        {
            Batch = [new BatchEntry("MFG", "MFG", "10/2026", "10/2026"), new BatchEntry("MRP", "MRP", "145.00", "")],
            Plc = plc,
        });
        Assert.Equal([new BatchLine("MFG", "10/2026", false), new BatchLine("MRP", "not entered", true)], _vm.BatchLines);
        Assert.Equal([Tone.Ok, Tone.Ok, Tone.Ok], _vm.PlcLines.Select(l => l.Tone));
        Assert.Equal(["Link", "Heartbeat", "RunEnable"], _vm.PlcLines.Select(l => l.Label));
        Assert.True(_vm.HasPlcLast);
        Assert.Contains("OK", _vm.PlcLastText);
        Assert.Contains("last trigger 48213", _vm.PlcLastText);
        Assert.Equal(Tone.Ok, _vm.PlcLastTone);
        Assert.Equal("", _vm.PlcNote);

        // the heartbeat stops: RunEnable is not known and the last result is no longer green
        _vm.Apply(LinkState.Connected, Make.Running() with { Plc = plc with { HeartbeatOk = false }, PlcOk = false });
        Assert.Equal([Tone.Neutral, Tone.Ng, Tone.Neutral], _vm.PlcLines.Select(l => l.Tone));   // a link without heartbeat is not green
        Assert.Equal(Tone.Neutral, _vm.PlcLastTone);

        // the link is down: only the link is known, as lost
        _vm.Apply(LinkState.Connected, Make.Running() with { Plc = plc with { LinkUp = false, HeartbeatOk = false }, PlcOk = false });
        Assert.Equal([Tone.Ng, Tone.Neutral, Tone.Neutral], _vm.PlcLines.Select(l => l.Tone));

        // stopped: the last part is history, shown without colour
        _vm.Apply(LinkState.Connected, Make.Stopped() with { Plc = plc });
        Assert.Equal(Tone.Neutral, _vm.PlcLastTone);

        _vm.Apply(LinkState.Connected, Make.Stopped());
        Assert.Empty(_vm.PlcLines);
        Assert.False(_vm.HasPlcLast);
        Assert.Equal("No PLC set up", _vm.PlcNote);
        Assert.Equal("This product has no batch values", _vm.BatchNote);

        _vm.Apply(LinkState.NotConnected, null);
        Assert.Empty(_vm.PlcLines);
        Assert.Empty(_vm.BatchLines);
        Assert.Equal("Not known: the runtime is not connected", _vm.PlcNote);
    }

    [Fact]
    public async Task Start_with_the_batch_not_entered_asks_for_it_then_starts()
    {
        var status = Make.Stopped() with { Batch = [new BatchEntry("BATCH", "BATCH", "", ""), new BatchEntry("MRP", "MRP", "45.00", "")] };
        _link.Connect(_client, status);
        _vm.Apply(LinkState.Connected, status);
        await _vm.StartCommand.ExecuteAsync(null);
        var batch = Assert.IsType<BatchViewModel>(_overlays.Shown);
        Assert.DoesNotContain("Start", _client.Calls);

        batch.Fields[0].Value = "A12345";
        await batch.SaveCommand.ExecuteAsync(null);
        Assert.Contains("MRP", batch.Message);                    // every value is needed
        Assert.Empty(_client.BatchSet);

        batch.Fields[1].Value = " 45.00 ";
        await batch.SaveCommand.ExecuteAsync(null);
        Assert.Equal("A12345", _client.BatchSet["BATCH"]);
        Assert.Equal("45.00", _client.BatchSet["MRP"]);
        Assert.Null(_overlays.Shown);
        Assert.Equal("Start", _client.Calls[^1]);
    }

    [Fact]
    public async Task A_batch_the_runtime_refuses_stays_open_with_the_reason()
    {
        var status = Make.Stopped() with { Batch = [new BatchEntry("BATCH", "BATCH", "", "A1")] };
        _link.Connect(_client, status);
        _vm.Apply(LinkState.Connected, status);
        _vm.BatchCommand.Execute(null);
        var batch = Assert.IsType<BatchViewModel>(_overlays.Shown);
        _client.BatchResult = new CommandResult(false, "batch.reason.running");
        batch.Fields[0].Value = "A2";
        await batch.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["batch.reason.running"], batch.Message);
        Assert.Same(batch, _overlays.Shown);

        _vm.Apply(LinkState.Connected, Make.Running());
        _overlays.Close();
        _vm.BatchCommand.Execute(null);
        Assert.Null(_overlays.Shown);   // running: Press Stop first
        Assert.True(_vm.HasNotice);
    }
}
