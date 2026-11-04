using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using StationGuard;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The review follow-ups (requirements AS-04i..k): the bad parts waiting, the reviewed list with a decision
/// changed or removed (audited, and flowing into what Learn uses), and the operator's "Was OK" flag.</summary>
public sealed class ReviewListTests : IAsyncLifetime
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

    NgReviews.NgReviewsClient Reviews() => new(_f.Local);

    ReviewStore Store() => _f.App.Services.GetRequiredService<ReviewStore>();

    string CameraName() => _f.App.Services.GetRequiredService<VisionCore.JobStore>().Load("Labels")!.Cameras[0].Name;

    List<string> AuditKinds() => _f.App.Services.GetRequiredService<VisionReports.ResultLog>()
        .Events(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Select(e => e.Kind).ToList();

    /// <summary>Runs the "Labels" job (every part NG: no QR found) until n bad parts came and their pictures are saved.</summary>
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
                ids.AddRange(r.ImageIds);
                if (ids.Count >= n) break;
            }
        }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
        var ng = _f.App.Services.GetRequiredService<NgImageStore>();
        for (var i = 0; i < 100 && ids.Any(id => ng.Find(id).State != NgImageState.Found); i++) await Task.Delay(50);
        return ids;
    }

    [Fact]
    public async Task A_decision_is_listed_changed_and_removed_audited_and_learning_uses_only_what_is_kept_now()
    {
        var ids = await BadParts(1);
        var id = ids[0];
        var denied = await Assert.ThrowsAsync<RpcException>(() => Reviews().ListReviewedAsync(new ListReviewedRequest { Camera = -1 }, _op).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        var ng = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = id, Decision = ReviewDecision.ConfirmedNg }, _sup);
        Assert.True(ng.Ok, ng.ErrorText);
        var listed = Assert.Single((await Reviews().ListReviewedAsync(new ListReviewedRequest { Camera = -1 }, _sup)).Items);
        Assert.Equal((id, ReviewDecision.ConfirmedNg, true, "sup", false), (listed.ImageId, listed.Decision, listed.Learned, listed.User, listed.SoftwareOk));
        Assert.False(Assert.Single(Store().Learning("Labels", 0, CameraName())).Good);
        Assert.DoesNotContain((await _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _sup)).Items, i => i.ImageId == id);

        // changed to good by the same rules: a QR not found is validated a second time first
        var ask = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = listed.ImageId, Decision = ReviewDecision.Accepted }, _sup);
        Assert.True(ask.Unreadable);
        Assert.False(ask.Ok);
        var good = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = listed.ImageId, Decision = ReviewDecision.Accepted, ConfirmUnreadable = true }, _sup);
        Assert.True(good.Ok, good.ErrorText);
        Assert.Equal((1u, 0u), (good.Summary.Cameras[0].Accepted, good.Summary.Cameras[0].ConfirmedNg));
        Assert.Equal(ReviewDecision.Accepted, Assert.Single((await Reviews().ListReviewedAsync(new ListReviewedRequest { Camera = -1 }, _sup)).Items).Decision);
        Assert.True(Assert.Single(Store().Learning("Labels", 0, CameraName())).Good);
        Assert.Contains("review_changed", AuditKinds());

        // removed: no longer learned from, waits for a review again, audited
        var opRemove = await Assert.ThrowsAsync<RpcException>(() => Reviews().RemoveReviewAsync(new RemoveReviewRequest { ImageId = id }, _op).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, opRemove.StatusCode);
        var removed = await Reviews().RemoveReviewAsync(new RemoveReviewRequest { ImageId = id }, _sup);
        Assert.True(removed.Ok, removed.ErrorText);
        Assert.Equal(0u, removed.Summary.Cameras[0].Accepted);
        Assert.Empty(Store().Learning("Labels", 0, CameraName()));
        Assert.Empty((await Reviews().ListReviewedAsync(new ListReviewedRequest { Camera = -1 }, _sup)).Items);
        Assert.Contains((await _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _sup)).Items, i => i.ImageId == id);
        Assert.Contains("review_removed", AuditKinds());
        Assert.Equal("runtime.refused.notReviewed", (await Reviews().RemoveReviewAsync(new RemoveReviewRequest { ImageId = id }, _sup)).ErrorKey);
    }

    [Fact]
    public async Task A_change_after_the_saved_picture_is_gone_keeps_the_rules_and_GetImage_shows_the_kept_copy()
    {
        var id = (await BadParts(1))[0];
        Assert.True((await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = id, Decision = ReviewDecision.ConfirmedNg }, _sup)).Ok);
        // the NG store dropped the picture (its clean-up): the review's copy is what is left
        foreach (var f in _f.App.Services.GetRequiredService<NgImageStore>().Find(id).Files) File.Delete(f.Path);
        var ask = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = id, Decision = ReviewDecision.Accepted }, _sup);
        Assert.True(ask.Unreadable);   // what the software found was kept with the decision

        // a review kept before 2026-11-04 has no fault keys: accepted, it is a record only, never learned from
        var legacy = new ImageName(1, 1, 7, DateTime.Now, 4242).ToString();
        var dir = Path.Combine(_f.Folder, "data", "reviews", "Labels", "cam0", "bad");
        BmpGray.Write(Path.Combine(dir, legacy + ".bmp"), new byte[320 * 240], 320, 240);
        File.WriteAllText(Path.Combine(dir, legacy + ".bmp.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            key = legacy, camera = CameraName(), decision = "confirmed_ng", user = "sup", note = "", at = DateTimeOffset.UtcNow.ToString("O"),
            faults = new[] { "QR: explain.code.none" },
        }));
        // GetImage shows a reviewed picture by its key, from the copy kept with the decision
        Assert.NotNull(_f.App.Services.GetRequiredService<LineRuntime>().Image(legacy, GetImageRequest.Types.Encoding.Png, withGraphics: false));
        var looks = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = legacy, Decision = ReviewDecision.Accepted }, _sup);
        Assert.True(looks.LooksBad);
        var record = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = legacy, Decision = ReviewDecision.Accepted, ConfirmLooksBad = true, ConfirmUnreadable = true }, _sup);
        Assert.True(record.Ok, record.ErrorText);
        Assert.False(record.Learned);
        Assert.DoesNotContain(Store().Learning("Labels", 0, CameraName()), e => e.Name.StartsWith(legacy, StringComparison.Ordinal));

        // an older review accepted as good does not say whether the software passed it: rejected, a possible escape
        var passed = new ImageName(1, 1, 7, DateTime.Now, 4343).ToString();
        var goodDir = Path.Combine(_f.Folder, "data", "reviews", "Labels", "cam0", "good");
        Directory.CreateDirectory(goodDir);
        BmpGray.Write(Path.Combine(goodDir, passed + ".bmp"), new byte[320 * 240], 320, 240);
        File.WriteAllText(Path.Combine(goodDir, passed + ".bmp.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            key = passed, camera = CameraName(), decision = "accepted", user = "sup", note = "", at = DateTimeOffset.UtcNow.ToString("O"),
            faults = Array.Empty<string>(),
        }));
        var escape = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = passed, Decision = ReviewDecision.ConfirmedNg }, _sup);
        Assert.True(escape.Ok, escape.ErrorText);
        Assert.True(escape.PossibleEscape);
    }

    [Fact]
    public async Task An_operators_Was_OK_is_shown_to_the_supervisor_counted_and_cleared_by_the_decision_never_learned()
    {
        var ids = await BadParts(2);
        var flag = await Reviews().FlagWasOkAsync(new FlagWasOkRequest { ImageId = ids[0] }, _op);
        Assert.True(flag.Ok, flag.ErrorText);
        Assert.Empty(Store().Learning("Labels", 0, CameraName()));   // a flag teaches nothing

        var list = await _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _sup);
        var flagged = Assert.Single(list.Items, i => i.ImageId == ids[0]);
        Assert.Equal("op", flagged.FlaggedOkBy);
        Assert.NotNull(flagged.FlaggedOkAt);
        Assert.Equal("", Assert.Single(list.Items, i => i.ImageId == ids[1]).FlaggedOkBy);
        Assert.Equal(ids[0], Assert.Single((await Reviews().ListReviewedAsync(new ListReviewedRequest { Camera = -1 }, _sup)).Flags).ImageId);
        // the flags are kept apart from the decisions: the browser's Operator flags tab lists decisions only
        Assert.DoesNotContain(Store().Recent(200), d => d.Key == ids[0]);
        Assert.Equal("runtime.refused.imageNotSaved", (await Reviews().FlagWasOkAsync(new FlagWasOkRequest { ImageId = "17-0" }, _op)).ErrorKey);
        var counts = _f.App.Services.GetRequiredService<SortQueue>().Count("Labels");
        Assert.Equal(1, counts.Flagged);
        Assert.True(counts.Bad >= 2);

        // the status carries the counts, whatever the setting (worked out in the background)
        Assert.True((await _f.Runtime().SetLearningSettingsAsync(new LearningSettings { Enabled = false, IdleMinutes = 5 }, _sup)).Ok);
        LearningState? state = null;
        for (var i = 0; i < 100 && state is not { BadToReview: > 0 }; i++)
        {
            state = (await _f.Runtime().GetStatusAsync(new GetStatusRequest(), _op)).Learning;
            if (state is not { BadToReview: > 0 }) await Task.Delay(50);
        }
        Assert.Equal((0u, 1u), (state!.ToSort, state.FlaggedOk));
        Assert.True(state.BadToReview >= 2);

        // the supervisor's decision clears it; a decided part cannot be flagged again
        Assert.True((await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = ids[0], Decision = ReviewDecision.ConfirmedNg }, _sup)).Ok);
        Assert.Empty((await Reviews().ListReviewedAsync(new ListReviewedRequest { Camera = -1 }, _sup)).Flags);
        Assert.Equal("runtime.refused.alreadyReviewed", (await Reviews().FlagWasOkAsync(new FlagWasOkRequest { ImageId = ids[0] }, _op)).ErrorKey);
        Assert.Equal("runtime.refused.imageGone", (await Reviews().FlagWasOkAsync(new FlagWasOkRequest { ImageId = new ImageName(1, 1, 7, DateTime.Now, 777_777).ToString() }, _op)).ErrorKey);
        Assert.Contains("was_ok_flag", AuditKinds());
    }
}
