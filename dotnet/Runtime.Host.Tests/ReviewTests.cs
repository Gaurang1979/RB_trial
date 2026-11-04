using Grpc.Core;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Microsoft.Extensions.DependencyInjection;
using VisionCore;
using Xunit;

/// <summary>The supervisor's NG review and learning from it (owner, 2026-10-31).</summary>
public sealed class ReviewTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!, _sup = null!;

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
            new AccessStore(Path.Combine(f.Folder, "data")).SaveUser("sup", "Shift Supervisor", [Role.Supervisor], "sup-pass-1", null));
        _op = await _f.SignInOperator();
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "sup", Password = "sup-pass-1", Client = ClientKind.Studio });
        Assert.True(r.Ok, r.ErrorText);
        _sup = RuntimeFixture.Bearer(r.Token);
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    /// <summary>Runs the "Labels" job (a QR on blank pictures: every part NG) until n bad parts' image ids came.</summary>
    async Task<List<string>> BadParts(int n)
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = _f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var ids = new List<string>();
        try
        {
            await foreach (var r in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                Assert.False(r.Pass);
                ids.AddRange(r.ImageIds);
                if (ids.Count >= n) break;
            }
        }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
        return ids;
    }

    static ReviewPartRequest Review(string id, ReviewDecision d) => new() { ImageId = id, Decision = d };

    [Fact]
    public async Task Only_a_supervisor_or_above_reviews_and_each_decision_keeps_the_full_picture()
    {
        var ids = await BadParts(3);
        var denied = await Assert.ThrowsAsync<RpcException>(() => _f.Runtime().ReviewPartAsync(Review(ids[0], ReviewDecision.Accepted), _op).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        // "code not found" is not a defect the tool saw, but the software could not read it (owner, 2026-11-04): the
        // first Accept asks for a second validation; validated, the part is learned from
        var first = await _f.Runtime().ReviewPartAsync(Review(ids[0], ReviewDecision.Accepted), _sup);
        Assert.False(first.Ok);
        Assert.True(first.Unreadable);
        Assert.Equal("runtime.review.unreadable", first.ErrorKey);
        var a = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = ids[0], Decision = ReviewDecision.Accepted, ConfirmUnreadable = true }, _sup);
        Assert.True(a.Ok, a.ErrorText);
        Assert.True(a.Learned);
        Assert.False(a.LooksBad);
        var c = await _f.Runtime().ReviewPartAsync(Review(ids[1], ReviewDecision.ConfirmedNg), _sup);
        Assert.True(c.Ok, c.ErrorText);
        var cam = Assert.Single(c.Summary.Cameras);
        Assert.Equal((1u, 1u, 0u), (cam.Accepted, cam.ConfirmedNg, cam.AcceptedRecordOnly));

        // a decision given again replaces the first
        var again = await _f.Runtime().ReviewPartAsync(Review(ids[0], ReviewDecision.ConfirmedNg), _sup);
        cam = Assert.Single(again.Summary.Cameras);
        Assert.Equal((0u, 2u), (cam.Accepted, cam.ConfirmedNg));
        var files = Directory.GetFiles(Path.Combine(_f.Folder, "data", "reviews", "Labels", "cam0", "bad"), "*.bmp");
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 320 * 240));   // the full picture, not a preview

        var gone = await _f.Runtime().ReviewPartAsync(Review("999999-0", ReviewDecision.Accepted), _sup);
        Assert.False(gone.Ok);
        Assert.Equal("runtime.refused.imageGone", gone.ErrorKey);
        Assert.Equal("runtime.refused.reviewDecision",
            (await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = ids[2] }, _sup)).ErrorKey);
    }

    [Fact]
    public async Task Learning_waits_for_the_line_to_stop_and_says_what_it_needs()
    {
        var ids = await BadParts(2);
        Assert.True((await _f.Runtime().ReviewPartAsync(Review(ids[0], ReviewDecision.ConfirmedNg), _sup)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        try
        {
            var busy = await _f.Runtime().LearnFromReviewsAsync(new LearnRequest(), _sup);
            Assert.False(busy.Ok);
            Assert.Equal("runtime.refused.stopFirst", busy.ErrorKey);
        }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }

        var learn = await _f.Runtime().LearnFromReviewsAsync(new LearnRequest(), _sup);
        Assert.True(learn.Ok, learn.ErrorText);
        Assert.Contains("at least one accepted part", learn.Problem);
        Assert.Equal("", learn.ProposalId);
        Assert.Equal((0u, 1u), (learn.Accepted, learn.ConfirmedNg));

        var apply = await _f.Runtime().ApplyLearningAsync(new ApplyLearningRequest { ProposalId = "nothing" }, _sup);
        Assert.False(apply.Ok);
        Assert.Equal("runtime.refused.proposalGone", apply.ErrorKey);
    }

    [Fact]
    public void A_defect_the_tool_saw_is_clearly_bad_and_an_unsure_read_is_not()
    {
        var (clearly, words) = ReviewDesk.ClearlyBad(["Batch|explain.ocv.wrong", "QR|explain.code.none"]);
        Assert.True(clearly);
        Assert.Equal(["Batch: a character is wrong", "QR: explain.code.none"], words);
        Assert.False(ReviewDesk.ClearlyBad(["QR|explain.code.none", "Batch|explain.ocr.unsure", "Grade|explain.verify.grade_low"]).Clearly);
        Assert.True(ReviewDesk.ClearlyBad(["Code|explain.code.wrong"]).Clearly);
        Assert.False(ReviewDesk.ClearlyBad([]).Clearly);
    }

    [Fact]
    public void A_part_accepted_only_as_a_record_never_joins_the_learning_set_and_a_restart_never_overwrites()
    {
        var folder = Path.Combine(_f.Folder, "store-test");
        var store = new ReviewStore(folder);
        var pic = Path.Combine(_f.FramesFolder, "frame0.bmp");
        var now = DateTimeOffset.UtcNow;
        store.Review("Job", 0, "Top", "20261031T101500000Z-000001-c0", pic, ReviewStore.Kind.Bad, "sup", "", now, []);
        // the same runtime id after a restart is another part: its key holds the time, so the first stays
        store.Review("Job", 0, "Top", "20261031T111500000Z-000001-c0", pic, ReviewStore.Kind.Good, "sup", "", now, []);
        store.Review("Job", 0, "Top", "20261031T111600000Z-000002-c0", pic, ReviewStore.Kind.Record, "sup", "", now, ["Batch: a character is wrong"]);
        Assert.Equal((1, 1, 1), store.Count("Job", 0));
        var set = store.Learning("Job", 0, "Top");
        Assert.Equal([true, false], set.Select(e => e.Good));
        Assert.DoesNotContain(set, e => e.Name.Contains("000002", StringComparison.Ordinal));
        Assert.Empty(store.Learning("Job", 0, "Side"));   // the cameras were reordered: another view never teaches this one
        Assert.Contains("a character is wrong", File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "reviews", "Job", "cam0", "accepted-record-only"), "*.json").Single()));
        // the same part decided again moves
        store.Review("Job", 0, "Top", "20261031T101500000Z-000001-c0", pic, ReviewStore.Kind.Good, "sup", "", now, []);
        Assert.Equal((2, 0, 1), store.Count("Job", 0));
    }

    [Fact]
    public void A_clearly_bad_part_accepted_is_a_record_only_whatever_the_app_asks()
    {
        Assert.Equal(ReviewStore.Kind.Record, ReviewDesk.KindFor(ReviewDecision.Accepted, clearlyBad: true));
        Assert.Equal(ReviewStore.Kind.Good, ReviewDesk.KindFor(ReviewDecision.Accepted, clearlyBad: false));
        Assert.Equal(ReviewStore.Kind.Bad, ReviewDesk.KindFor(ReviewDecision.ConfirmedNg, clearlyBad: true));
        foreach (var key in new[] { "explain.gs1.wrong_value", "explain.gs1.missing_ai", "explain.code1d.addon_wrong", "explain.blob.present",
                     "explain.blob.area", "explain.ocv.broken", "explain.ocv.shift", "explain.line.straightness", "explain.code.short", "explain.code1d.check_digit" })
            Assert.True(ReviewDesk.ClearlyBad([$"T|{key}"]).Clearly, key);
    }

    static TuneSample Sample(bool good, bool pass) =>
        new(new TuneImage(good ? "g.bmp" : "b.bmp", good), new VisionCore.JobResult(pass, [], 1, null));

    [Fact]
    public void A_looser_setting_needs_its_own_bad_part_and_five_bad_parts_only_the_tuned_checks_reject()
    {
        var looser = new TuneKnob("Batch", TuneKnobKind.TextMinQuality, 60, 52, 70, 40);
        var p = new TuneProposal(0, [looser], new TuneCount(3, 3, 5, 5, 10), null);
        var otherCheckFails = Enumerable.Range(0, 9).Select(_ => Sample(false, pass: false)).ToList();
        Assert.NotNull(Learning.LooserNotProved(p, otherCheckFails));   // caught by another check: proves nothing about Batch
        var tunedRejects = Enumerable.Range(0, 5).Select(_ => Sample(false, pass: true)).ToList();
        Assert.Null(Learning.LooserNotProved(p, tunedRejects));
        var blind = p with { Knobs = [looser with { HighestBad = null }] };
        Assert.Contains("no confirmed bad part", Learning.LooserNotProved(blind, tunedRejects));
        var stricter = p with { Knobs = [new TuneKnob("Batch", TuneKnobKind.TextMinQuality, 52, 60, 70, 40)] };
        Assert.Null(Learning.LooserNotProved(stricter, []));
    }

    [Fact]
    public async Task Apply_saves_a_new_version_only_while_the_reviews_and_the_job_are_as_learned()
    {
        var learning = _f.App.Services.GetRequiredService<Learning>();
        var store = _f.App.Services.GetRequiredService<ReviewStore>();
        var jobs = new JobStore(Path.Combine(_f.Folder, "jobs"));
        var pic = Path.Combine(_f.FramesFolder, "frame0.bmp");
        store.Review("Labels", 0, "Top", "k1-c0", pic, ReviewStore.Kind.Bad, "sup", "", DateTimeOffset.UtcNow, []);
        var job = jobs.Load("Labels")!;
        var p = new TuneProposal(0, [], new TuneCount(0, 0, 1, 1, 1), null);

        // a part confirmed bad after the learning: not proved against
        var id = learning.Remember(job, 0, p, store.Learning("Labels", 0, "Top"));
        store.Review("Labels", 0, "Top", "k2-c0", pic, ReviewStore.Kind.Bad, "sup", "", DateTimeOffset.UtcNow, []);
        var changed = await _f.Runtime().ApplyLearningAsync(new ApplyLearningRequest { ProposalId = id }, _sup);
        Assert.Equal("runtime.refused.reviewsChanged", changed.ErrorKey);

        id = learning.Remember(job, 0, p, store.Learning("Labels", 0, "Top"));
        var ok = await _f.Runtime().ApplyLearningAsync(new ApplyLearningRequest { ProposalId = id }, _sup);
        Assert.True(ok.Ok, ok.ErrorText);
        Assert.Equal(job.Version + 1, ok.Version);
        Assert.Equal("runtime.refused.proposalGone", (await _f.Runtime().ApplyLearningAsync(new ApplyLearningRequest { ProposalId = id }, _sup)).ErrorKey);

        // learned on the version before the one just saved: refused
        id = learning.Remember(job, 0, p, store.Learning("Labels", 0, "Top"));
        Assert.Equal("runtime.refused.jobChanged", (await _f.Runtime().ApplyLearningAsync(new ApplyLearningRequest { ProposalId = id }, _sup)).ErrorKey);
    }
}
