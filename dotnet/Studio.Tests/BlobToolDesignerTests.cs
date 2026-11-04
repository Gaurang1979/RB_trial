using System.Text.Json.Nodes;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

/// <summary>The Blob tool in the job designer (requirements 17.12 BLB-OUT-03..06): added from the toolbox or dropped,
/// its settings written to the job file, the outline unit (mm only with a calibration, limits converted), and Auto
/// tolerance: the runtime's suggestion shown with Accept / Not this, Accept one undo step.</summary>
public class BlobToolDesignerTests
{
    private readonly ScriptedClient _client = new();
    private readonly ManualLink _link = new();

    private async Task<DesignerViewModel> Open(double? mmPerPx = null)
    {
        var job = JobDraft.New("Lids", 1);
        if (mmPerPx is { } mm) job.Cameras[0].Node["Calibration"] = new JsonObject { ["Fx"] = 1200, ["MmPerPx"] = mm, ["RmsPx"] = 0.2 };
        _client.JobFiles["Lids"] = job.ToJson();
        _client.Products = [new("Lids", "")];
        _link.Connect(_client, Make.Stopped());
        var vm = new DesignerViewModel(Make.Strings, new RuntimeCalls(_link), _ => new FakeImage(), () => { });
        await vm.LoadAsync();
        await vm.OpenAsync("Lids");
        await vm.TryCommand.ExecuteAsync(null);
        _client.Tries.Clear();
        return vm;
    }

    /// <summary>A Blob tool with its outline taught (as the runtime's Teach leaves it).</summary>
    private static ToolDraft Taught(DesignerViewModel vm)
    {
        vm.AddBlobCommand.Execute(null);
        var t = vm.SelectedTool!;
        t.Node["TaughtOutline"] = new JsonArray(0, 0, 10, 0, 10, 10, 0, 10);
        t.CheckOutline = true;
        t.MaxDeviation = 2;
        vm.Watch();
        return t;
    }

    [Fact]
    public async Task A_new_blob_tool_asks_for_a_spot_and_its_settings_go_into_the_job_file()
    {
        var vm = await Open();
        vm.AddBlobCommand.Execute(null);
        var t = vm.SelectedTool!;
        Assert.True(t.IsBlob);
        Assert.Equal("Blob 1", t.Name);
        Assert.Equal("Blob", t.KindText);
        Assert.Equal("Present", t.Presence);   // a new tool never passes every picture
        Assert.Equal(Icons.Blob, Assert.Single(vm.Tree, n => n.IsTool).Icon);

        t.Threshold = "Band";
        Assert.True(t.HasLevel && t.IsBand && !t.IsLocal);
        t.Level = 300;   // kept to a grey level
        t.MinCount = 2;
        t.MaxCount = 2;
        t.BlobPolarity = "LightOnDark";
        var file = JsonNode.Parse(vm.Job!.ToJson())!["Tools"]![0]!;
        Assert.Equal("blob", (string?)file["kind"]);
        Assert.Equal(255, (int)file["Level"]!);
        Assert.Equal(2, (int)file["MinCount"]!);
        Assert.Equal("LightOnDark", (string?)file["Polarity"]);
        t.MaxCount = null;   // no limit: left out of the file
        Assert.Null(JsonNode.Parse(vm.Job.ToJson())!["Tools"]![0]!["MaxCount"]);

        vm.Watch();
        vm.UndoCommand.Execute(null);   // the settings typed
        vm.UndoCommand.Execute(null);   // the tool added
        Assert.Empty(vm.CameraTools);
    }

    [Fact]
    public async Task A_dropped_blob_tool_stays_where_it_is_dropped_and_is_tried_without_a_teach()
    {
        var vm = await Open();
        await vm.DropToolAsync(ToolKind.Blob, new Point2(320, 240));
        var t = vm.SelectedTool!;
        Assert.True(t.IsBlob);
        Assert.Equal(DesignerViewModel.DefaultArea(ToolKind.Blob, 640, 480, 1280, 960), t.Roi);
        var tried = Assert.Single(_client.Tries);
        Assert.Null(tried.TeachTool);
        vm.UndoCommand.Execute(null);   // the drop is one step
        Assert.Empty(vm.CameraTools);
    }

    [Fact]
    public async Task Mm_needs_a_calibration()
    {
        var vm = await Open();
        var t = Taught(vm);
        Assert.False(vm.HasCalibration);
        Assert.Equal(["px", Make.Strings["design.blob.mm_needs_calibration"]], vm.OutlineUnits);
        vm.BlobUnit = vm.OutlineUnits[1];
        Assert.Equal("Px", t.OutlineUnit);
        Assert.Equal("px", vm.BlobUnit);
        Assert.Equal(Make.Strings["design.blob.needs_calibration"], vm.Message);
    }

    [Fact]
    public async Task With_a_calibration_the_unit_changes_and_the_limits_keep_their_meaning()
    {
        var vm = await Open(mmPerPx: 0.05);
        var t = Taught(vm);
        t.MaxDentDepth = 4;
        vm.Watch();
        Assert.True(vm.HasCalibration);
        Assert.Equal("mm", vm.OutlineUnits[1]);
        vm.BlobUnit = "mm";
        Assert.Equal("Mm", t.OutlineUnit);
        Assert.Equal(0.1m, t.MaxDeviation);   // 2 px
        Assert.Equal(0.2m, t.MaxDentDepth);   // 4 px
        vm.UndoCommand.Execute(null);
        Assert.Equal("Px", vm.SelectedTool!.OutlineUnit);
        Assert.Equal(2m, vm.SelectedTool.MaxDeviation);

        // from mm with no calibration a limit cannot be converted: kept, never dropped (that would loosen the check)
        var loose = new ToolDraft(new JsonObject { ["kind"] = "blob", ["OutlineUnit"] = "Mm", ["MaxDeviation"] = 0.1 });
        Assert.False(loose.ChangeOutlineUnit("Px", null));
        Assert.Equal(0.1m, loose.MaxDeviation);
    }

    [Fact]
    public async Task Auto_tolerance_needs_a_taught_outline_and_three_pictures()
    {
        var vm = await Open();
        vm.AddBlobCommand.Execute(null);
        await vm.AutoToleranceCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["design.blob.teach_first"], vm.Message);
        Taught(vm);
        _client.PictureNames = ["good1.bmp", "good2.bmp"];
        await vm.LoadPicturesCommand.ExecuteAsync(null);
        await vm.AutoToleranceCommand.ExecuteAsync(null);
        Assert.Equal(Make.Strings["design.blob.three_pictures"], vm.Message);
        Assert.Empty(_client.Tolerances);
    }

    [Fact]
    public async Task Accept_sets_the_suggested_limits_as_one_undo_step_and_not_this_changes_nothing()
    {
        var vm = await Open();
        var t = Taught(vm);
        await vm.AutoToleranceCommand.ExecuteAsync(null);
        var (tool, pictures) = Assert.Single(_client.Tolerances);
        Assert.Equal("Blob 1", tool);
        Assert.Equal(["good1.bmp", "good2.bmp", "bad1.bmp"], pictures);   // no try-all run: every stored picture
        Assert.True(vm.HasToleranceSuggestion);
        Assert.Equal(2m, t.MaxDeviation);   // nothing changed yet

        vm.DismissToleranceCommand.Execute(null);
        Assert.False(vm.HasToleranceSuggestion);
        Assert.Equal(2m, t.MaxDeviation);

        await vm.AutoToleranceCommand.ExecuteAsync(null);
        vm.AcceptToleranceCommand.Execute(null);
        Assert.Equal(0.9m, vm.SelectedTool!.MaxDeviation);
        Assert.Equal(1.2m, vm.SelectedTool.MaxDentDepth);
        vm.UndoCommand.Execute(null);
        Assert.Equal(2m, vm.SelectedTool!.MaxDeviation);
        Assert.Null(vm.SelectedTool.MaxDentDepth);
    }

    [Fact]
    public async Task Auto_tolerance_measures_only_the_pictures_that_passed_try_all_and_shows_why_there_is_none()
    {
        var vm = await Open();
        Taught(vm);
        _client.PictureNames = ["good1.bmp", "good2.bmp", "good3.bmp", "bad1.bmp"];
        await vm.LoadPicturesCommand.ExecuteAsync(null);
        _client.TryAnswers["bad1.bmp"] = _client.TryDefault with { Pass = false };
        await vm.RunAllCommand.ExecuteAsync(null);
        _client.ToleranceAnswer = new BlobToleranceOutcome(true, Found: false, Reason: "No suggestion: good2.bmp shows two parts.");
        await vm.AutoToleranceCommand.ExecuteAsync(null);
        Assert.Equal(["good1.bmp", "good2.bmp", "good3.bmp"], Assert.Single(_client.Tolerances).Pictures);
        Assert.False(vm.HasToleranceSuggestion);
        Assert.Equal("No suggestion: good2.bmp shows two parts.", vm.Message);
    }

    [Fact]
    public void The_contract_answer_maps_with_its_calibration()
    {
        var m = GrpcRuntimeClient.Map(new V1.SuggestBlobToleranceResponse
        {
            Ok = true, Found = true, MaxDeviation = 0.05f, MaxDentDepth = 0.07f, Unit = "mm", Pictures = 5, WorstDeviation = 0.02f, WorstDent = 0.03f,
            Calibration = new V1.LensCalibration { Fx = 1200, MmPerPx = 0.05f, RmsPx = 0.2f },
        });
        Assert.True(m.Found);
        Assert.Equal(("mm", 5, 0.05f), (m.Unit, m.Pictures, m.MaxDeviation));
        Assert.Equal(0.05f, m.Calibration!.MmPerPx);
        Assert.Null(GrpcRuntimeClient.Map(new V1.SuggestBlobToleranceResponse { Ok = true }).Calibration);
        Assert.Equal("px", GrpcRuntimeClient.Map(new V1.SuggestBlobToleranceResponse { Ok = true }).Unit);
    }
}
