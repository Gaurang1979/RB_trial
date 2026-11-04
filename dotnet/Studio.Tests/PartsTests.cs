using Google.Protobuf.WellKnownTypes;
using VisionSystem.Studio.Runtime;
using VisionSystem.Studio.ViewModels;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Studio.Tests;

/// <summary>
/// Every part's own result listed (owner, 2026-10-30: "yes as report is required"): the contract's
/// InspectionResult in the app's words, the log with its gaps, and the Parts screen.
/// </summary>
public class PartsTests
{
    private static V1.InspectionResult Result(bool pass, params V1.CodeRead.Types.State[] codes)
    {
        var r = new V1.InspectionResult
        {
            InspectionId = 7, Job = "Tablets 10x10", Variant = "PH-1010-24", PartId = "CASE7", Pass = pass, CycleMs = 412,
            Time = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.Zero)),
        };
        r.ImageIds.Add("7-0");
        foreach (var s in codes) r.Codes.Add(new V1.CodeRead { State = s, Reason = s == V1.CodeRead.Types.State.Missed ? "Too faint" : "" });
        return r;
    }

    [Fact]
    public void A_passed_part_maps_to_OK_with_its_product_codes_and_image()
    {
        var p = GrpcRuntimeClient.Map(Result(true, V1.CodeRead.Types.State.Read, V1.CodeRead.Types.State.Read));
        Assert.Equal(Verdict.Ok, p.Result);
        Assert.Equal("Tablets 10x10 · PH-1010-24", p.Product);
        Assert.Equal(("CASE7", 7UL, 412.0), (p.PartId, p.Id, p.CycleMs));
        Assert.Equal((2, 0, 0), (p.CodesRead, p.CodesMissed, p.CodesDuplicate));
        Assert.Equal("", p.Reason);
        Assert.Equal("7-0", p.ImageId);
    }

    [Fact]
    public void Only_a_part_whose_one_fault_is_a_code_seen_before_is_same_code()
    {
        var dup = GrpcRuntimeClient.Map(Result(false, V1.CodeRead.Types.State.Read, V1.CodeRead.Types.State.Duplicate));
        Assert.Equal(Verdict.Duplicate, dup.Result);

        var missed = GrpcRuntimeClient.Map(Result(false, V1.CodeRead.Types.State.Duplicate, V1.CodeRead.Types.State.Missed));
        Assert.Equal(Verdict.Ng, missed.Result);
        Assert.Equal("Too faint", missed.Reason);

        var tool = Result(false, V1.CodeRead.Types.State.Duplicate);
        tool.Tools.Add(new V1.ToolResult { Name = "Check text", Pass = false, ReasonText = "Text is wrong" });
        var byTool = GrpcRuntimeClient.Map(tool);
        Assert.Equal(Verdict.Ng, byTool.Result);
        Assert.Equal("Text is wrong", byTool.Reason);

        // A failed part with no reason given is still NG, never OK.
        Assert.Equal(Verdict.Ng, GrpcRuntimeClient.Map(Result(false)).Result);
    }

    [Fact]
    public void The_log_counts_parts_and_marks_where_parts_are_missing()
    {
        var log = new PartLog();
        log.Add(Make.Part(1));
        log.Add(Make.Part(2, Verdict.Ng, "Code could not be read"));
        log.Add(Make.Part(5, Verdict.Duplicate));            // 3 and 4 never came: the stream fell behind
        Assert.Equal((1L, 1L, 1L, 3L), (log.Good, log.Bad, log.SameCode, log.Listed));
        Assert.Equal(4, log.Entries.Count);
        Assert.True(log.Entries[2].IsGap);
        Assert.Equal(2, log.Entries[2].Missed);

        log.NoteLinkLost(DateTimeOffset.Now);
        log.NoteLinkLost(DateTimeOffset.Now);                // said once per drop
        Assert.Equal(5, log.Entries.Count);
        Assert.True(log.Entries[4].LinkLost);

        log.Add(Make.Part(1));                               // the runtime restarted: ids start again, no gap
        Assert.Equal(6, log.Entries.Count);
        Assert.False(log.Entries[5].IsGap);
    }

    [Fact]
    public void A_drop_before_any_part_adds_no_gap_and_the_log_keeps_its_newest()
    {
        var log = new PartLog();
        log.NoteLinkLost(DateTimeOffset.Now);
        Assert.Empty(log.Entries);
        for (ulong i = 1; i <= PartLog.Capacity + 10; i++) log.Add(Make.Part(i));
        Assert.Equal(PartLog.Capacity, log.Entries.Count);
        Assert.Equal((ulong)PartLog.Capacity + 10, log.Entries[^1].Part!.Id);
        Assert.Equal(PartLog.Capacity + 10L, log.Listed);    // the counts are of every part seen
    }

    [Fact]
    public void Parts_screen_lists_newest_first_filters_bad_and_holds()
    {
        var time = new ManualTime();
        var log = new PartLog();
        log.Add(Make.Part(1));
        log.Add(Make.Part(2, Verdict.Ng, "Code could not be read"));
        log.Add(Make.Part(3));
        var closed = false;
        var vm = new PartsViewModel(Make.Strings, log, time, () => closed = true);

        Assert.Equal(["CASE003", "CASE002", "CASE001"], vm.Rows.Select(r => r.PartId));
        Assert.Equal(Tone.Ng, vm.Rows[1].Tone);
        Assert.Equal("Code could not be read", vm.Rows[1].Why);
        Assert.Contains("3", vm.SummaryText);

        vm.ToggleBadOnlyCommand.Execute(null);
        Assert.Equal(["CASE002"], vm.Rows.Select(r => r.PartId));
        vm.ToggleBadOnlyCommand.Execute(null);

        // New parts are drawn at most once a second, and not while the list is held.
        log.Add(Make.Part(4));
        vm.Tick();
        Assert.Equal(3, vm.Rows.Count);
        time.Advance(PartsViewModel.RedrawEvery);
        vm.ToggleHoldCommand.Execute(null);
        vm.Tick();
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(Make.Strings["parts.follow"], vm.HoldText);
        vm.ToggleHoldCommand.Execute(null);
        Assert.Equal("CASE004", vm.Rows[0].PartId);

        vm.BackCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public void Each_part_row_shows_its_image_name_and_a_gap_or_a_part_without_one_shows_none()
    {
        var log = new PartLog();
        log.Add(Make.Part(1) with { ImageId = "L01_C1_J045_261031-104512_000001" });
        log.Add(Make.Part(3));                               // 2 never came; 3 has no image kept
        var vm = new PartsViewModel(Make.Strings, log, new ManualTime(), () => { });

        Assert.Equal(["", "", "L01_C1_J045_261031-104512_000001"], vm.Rows.Select(r => r.Image));
        Assert.True(vm.Rows[1].IsGap);
    }

    [Fact]
    public void The_app_collects_every_part_and_notes_a_lost_link()
    {
        var link = new ManualLink();
        var vm = new MainViewModel(Make.Strings, link, new ImmediateUi(), new ManualTime(), _ => null, AppTheme.Dark, _ => { });
        link.Connect(new ScriptedClient(), Make.Running());
        link.Push(Make.Part(1));
        link.Push(Make.Part(2, Verdict.Ng));
        Assert.Equal(2L, vm.Run.Parts.Listed);

        vm.Run.OpenPartsCommand.Execute(null);
        var parts = Assert.IsType<PartsViewModel>(vm.Overlay);
        Assert.Equal(2, parts.Rows.Count);

        link.Drop();
        Assert.True(vm.Run.Parts.Entries[^1].LinkLost);
        Assert.Same(parts, vm.Overlay);                      // the list stays open; it is history
    }

    [Fact]
    public async Task A_runtime_without_the_results_stream_keeps_the_link_up()
    {
        var client = new ScriptedClient { NoResults = true };
        var connector = new OneClient(client);
        await using var link = new RuntimeLink(connector, probeEvery: TimeSpan.FromMilliseconds(50));
        link.Start();
        for (var i = 0; i < 200 && link.State != LinkState.Connected; i++) await Task.Delay(10);
        await Task.Delay(300);
        Assert.Equal(LinkState.Connected, link.State);
        Assert.Equal(1, connector.Attempts);
    }

    [Fact]
    public async Task Results_arrive_through_the_link()
    {
        var client = new ScriptedClient();
        await using var link = new RuntimeLink(new OneClient(client));
        var got = new List<PartResult>();
        link.ResultReceived += r => { lock (got) got.Add(r); };
        link.Start();
        await client.ResultStream.Writer.WriteAsync(Make.Part(1));
        await client.ResultStream.Writer.WriteAsync(Make.Part(2));
        for (var i = 0; i < 200 && got.Count < 2; i++) await Task.Delay(10);
        lock (got) Assert.Equal([1UL, 2UL], got.Select(p => p.Id));
    }

    private sealed class OneClient(IRuntimeClient client) : IRuntimeConnector
    {
        public int Attempts { get; private set; }

        public Task<IRuntimeClient> ConnectAsync(CancellationToken ct)
        {
            Attempts++;
            if (Attempts > 1) throw new IOException("only one connection in this test");
            return Task.FromResult(client);
        }
    }
}
