using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

/// <summary>The runtime contract's report_only and code_grade in the app: a report-only tool is shown as
/// information, never as OK, and a graded Read code gets the margin chart over "Try all pictures".</summary>
public class ReportOnlyAndGradeTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private async Task<DesignerViewModel> Open(string minGrade)
    {
        var draft = JobDraft.New("Cartons", 1);
        draft.AddTool(ToolKind.ReadCode, 0).MinGrade = minGrade;
        _client.JobFiles["Cartons"] = draft.ToJson();
        _client.Products = [new("Cartons", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Cartons");
        return vm;
    }

    [Fact]
    public void The_contract_fields_map_and_an_older_runtime_reads_as_deciding_and_ungraded()
    {
        var f = new V1.LiveFrame { InspectionId = 3, Pass = true };
        f.Tools.Add(new V1.ToolResult { Name = "Lot", Pass = true, ReportOnly = true });
        f.Tools.Add(new V1.ToolResult { Name = "Code", Pass = true });
        Assert.Equal([new ToolOutcome("Lot", true, ReportOnly: true), new ToolOutcome("Code", true)], GrpcRuntimeClient.Map(f).Tools!);

        var t = new V1.TryJobResponse { Ok = true, Pass = true };
        t.Tools.Add(new V1.ToolResult { Name = "Lot", Kind = "ocr", Pass = true, ReportOnly = true });
        t.Tools.Add(new V1.ToolResult { Name = "Code", Kind = "barcode", Pass = true, CodeGrade = 3 });
        t.Tools.Add(new V1.ToolResult { Name = "Old", Kind = "barcode", Pass = true });
        var tried = GrpcRuntimeClient.Map(t).Tools!;
        Assert.True(tried[0].ReportOnly);
        Assert.Null(tried[0].CodeGrade);
        Assert.Equal(3f, tried[1].CodeGrade);
        Assert.False(tried[2].ReportOnly);
        Assert.Null(tried[2].CodeGrade);   // no grade, never a grade of F
    }

    [Fact]
    public void A_report_only_tool_is_information_and_a_failed_tool_is_NG_whatever_it_says()
    {
        Assert.Equal((Tone.Info, Icons.Info, "ⓘ", "result.info"),
            (ToolMark.Tone(true, true), ToolMark.Icon(true, true), ToolMark.Mark(true, true), ToolMark.Word(true, true)));
        Assert.Equal((Tone.Ok, "✓"), (ToolMark.Tone(true, false), ToolMark.Mark(true, false)));
        // a report-only tool still fails on a setup or tool error: never hidden as information
        Assert.Equal((Tone.Ng, "✗", "result.ng"), (ToolMark.Tone(false, true), ToolMark.Mark(false, true), ToolMark.Word(false, true)));
        Assert.Equal("Info", DesignerConverters.ToolText.Convert(new ToolTried("Lot", "ocr", true, "", 1, ReportOnly: true), typeof(string), null, null!));
        Assert.Equal("ⓘ", DesignerConverters.ToolMark.Convert(new ToolTried("Lot", "ocr", true, "", 1, ReportOnly: true), typeof(string), null, null!));
    }

    [Fact]
    public void The_Run_screens_last_part_shows_a_report_only_tool_as_information()
    {
        var vm = new RunViewModel(Make.Strings, new RuntimeCalls(_link), new NoOverlays(), new ManualTime(), _ => null);
        _link.FrameReceived += vm.OnFrame;
        _link.Connect(new ScriptedClient { Status = Make.Running() }, Make.Running());
        _link.Push(new LiveFrame(5, DateTimeOffset.Now, [9], 800, 600, Verdict.Ok, [], null,
            [new ToolOutcome("Code", true), new ToolOutcome("Lot", true, ReportOnly: true)], []));

        var lot = vm.LastPart.Single(l => l.Text == "Lot");
        Assert.Equal((Tone.Info, Icons.Info), (lot.Tone, lot.Icon));
        Assert.Equal(Tone.Ok, vm.LastPart.Single(l => l.Text == "Code").Tone);
    }

    [Fact]
    public async Task A_report_only_tool_is_marked_as_information_in_the_job_tree()
    {
        var vm = await Open("None");
        _client.TryDefault = _client.TryDefault with { Tools = [new ToolTried("Read code 1", "barcode", true, "", 3, ReportOnly: true)] };
        await vm.TryCommand.ExecuteAsync(null);
        var node = vm.Tree.Single(n => n.Name == "Read code 1");
        Assert.Equal((Tone.Info, "ⓘ"), (node.Tone, node.Mark));
    }

    [Fact]
    public async Task Try_all_gives_a_graded_code_tool_its_grade_margin()
    {
        var vm = await Open("C");
        foreach (var name in _client.PictureNames)
            _client.TryAnswers[name] = _client.TryDefault with
            {
                Tools = [new ToolTried("Read code 1", "barcode", true, "", 5, CodeGrade: name == "good1.bmp" ? 3 : 4)],
            };
        await vm.RunAllCommand.ExecuteAsync(null);

        var m = vm.SelectedMargin!;
        Assert.True(m.Grade);
        Assert.Equal([3.0, 4.0, 4.0], m.Values);
        Assert.Equal(2, m.Limit);
        Assert.True(m.Safe);   // the worst, B, is a whole grade above C
        Assert.Equal(Make.Strings.Format("ws.margin.grade", 3), vm.MarginTitle);
    }

    [Fact]
    public async Task A_picture_not_graded_makes_the_grade_margin_not_safe()
    {
        var vm = await Open("B");
        foreach (var name in _client.PictureNames)
            _client.TryAnswers[name] = _client.TryDefault with
            {
                Tools = [new ToolTried("Read code 1", "barcode", name != "bad1.bmp", "", 5, CodeGrade: name == "bad1.bmp" ? null : 4)],
            };
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.SelectedMargin!.Values.Count);
        Assert.False(vm.SelectedMargin.Safe);
    }

    [Fact]
    public async Task A_code_tool_without_a_lowest_grade_has_no_margin()
    {
        var vm = await Open("None");
        _client.TryDefault = _client.TryDefault with { Tools = [new ToolTried("Read code 1", "barcode", true, "", 5, CodeGrade: 4)] };
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.False(vm.HasMargin);   // nothing to be close to
    }

    [Fact]
    public void A_grade_margin_is_safe_a_whole_grade_above_the_limit_and_shows_letters()
    {
        Assert.False(new ToolMargin("t", [2, 4], 2, 0, 4, Grade: true).Safe);   // C at a C limit: close
        Assert.True(new ToolMargin("t", [3, 4], 2, 0, 4, Grade: true).Safe);
        Assert.False(new ToolMargin("t", [double.NaN, 4], 2, 0, 4, Grade: true).Safe);
        var m = new ToolMargin("t", [0, 3, 3, 4], 2, 0, 4, Grade: true);
        Assert.Equal(["F", "D", "C", "B", "A"], new[] { 0.0, 1, 2, 3, 4 }.Select(m.Text));
        Assert.Equal([1, 0, 0, 2, 1], m.Counts());   // one band per grade, F to A
        Assert.Equal(1.5, m.Edge);   // failing ends between D and C
        Assert.Equal(20, new ToolMargin("t", [75], 60, 0, 100).Counts().Length);
    }

    private sealed class NoOverlays : IOverlayHost
    {
        public void Show(object overlay) { }
        public void Close() { }
        public void CloseIf(object overlay) { }
    }
}
