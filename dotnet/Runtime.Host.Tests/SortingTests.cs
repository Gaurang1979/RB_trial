using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using StationGuard;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The learning session (owner, 2026-11-04): good pictures kept, the setting, the pictures to sort, the
/// second validation of an unreadable part and the possible escape.</summary>
public sealed class SortingTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!, _sup = null!;

    /// <summary>Good pictures kept for learning by an earlier run of the line (the store finds them as it starts):
    /// one near a limit, one not.</summary>
    string _nearGood = "", _plainGood = "";

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            new AccessStore(Path.Combine(f.Folder, "data")).SaveUser("sup", "Shift Supervisor", [Role.Supervisor], "sup-pass-1", null);
            _nearGood = GoodPicture(f, 990_001, near: true);
            _plainGood = GoodPicture(f, 990_002, near: false);
        });
        _op = await _f.SignInOperator();
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "sup", Password = "sup-pass-1", Client = ClientKind.Studio });
        Assert.True(r.Ok, r.ErrorText);
        _sup = RuntimeFixture.Bearer(r.Token);
    }

    /// <summary>A good picture of the Labels job as the station keeps one (its BMP and .json in data/learning-ok).</summary>
    static string GoodPicture(RuntimeFixture f, int serial, bool near)
    {
        var name = new ImageName(1, 1, 7, DateTime.Now, serial);
        var dir = name.Folder(Path.Combine(f.Folder, "data", "learning-ok"), ok: true);
        Directory.CreateDirectory(dir);
        var px = new byte[320 * 240];
        Array.Fill(px, (byte)120);
        BmpGray.Write(Path.Combine(dir, name + ".bmp"), px, 320, 240);
        File.WriteAllText(Path.Combine(dir, name + ".json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["product"] = "Labels", ["time"] = DateTimeOffset.Now, ["near_limit"] = near, ["ok"] = true, ["image_id"] = name.ToString(),
            ["camera"] = "cam", ["sample"] = "learning",
        }));
        return name.ToString();
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    /// <summary>Runs the "Labels" job (every part NG) until n bad parts came and their pictures are saved.</summary>
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
    public async Task Bad_and_good_pictures_wait_to_be_sorted_until_a_supervisor_sorts_them()
    {
        var bad = await BadParts(2);
        var good = _nearGood;
        var denied = await Assert.ThrowsAsync<RpcException>(() => _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _op).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        var list = await _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _sup);
        Assert.Contains(list.Items, i => i.ImageId == good && i.Ok && i.NearLimit);
        var b = Assert.Single(list.Items, i => i.ImageId == bad[0]);
        Assert.False(b.Ok);
        Assert.NotEmpty(b.Faults);   // what the software found is kept with the saved picture
        Assert.Equal((uint)list.Items.Count, list.Total);

        // a good picture accepted is learned as good, and leaves the list
        var g = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = good, Decision = ReviewDecision.Accepted }, _sup);
        Assert.True(g.Ok, g.ErrorText);
        Assert.True(g.Learned);
        Assert.False(g.PossibleEscape);
        var again = await _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _sup);
        Assert.DoesNotContain(again.Items, i => i.ImageId == good);
        Assert.Equal(list.Total - 1, again.Total);
    }

    [Fact]
    public async Task A_good_picture_rejected_is_a_possible_escape_and_teaches_the_job_to_be_stricter()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var good = _plainGood;
        var r = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = good, Decision = ReviewDecision.ConfirmedNg }, _sup);
        Assert.True(r.Ok, r.ErrorText);
        Assert.True(r.PossibleEscape);
        Assert.True(r.Learned);
        Assert.Equal(1u, Assert.Single(r.Summary.Cameras).ConfirmedNg);
    }

    [Fact]
    public async Task An_unreadable_part_is_validated_again_and_may_be_kept_as_a_record_only()
    {
        var ids = await BadParts(1);   // a QR not found: unreadable, not a defect the tool saw
        var ask = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = ids[0], Decision = ReviewDecision.Accepted }, _sup);
        Assert.True(ask.Unreadable);
        Assert.False(ask.Ok);
        Assert.NotEmpty(ask.Faults);
        var kept = await _f.Runtime().ReviewPartAsync(new ReviewPartRequest { ImageId = ids[0], Decision = ReviewDecision.Accepted, KeepAsRecord = true }, _sup);
        Assert.True(kept.Ok, kept.ErrorText);
        Assert.False(kept.Learned);
        Assert.Equal((0u, 1u), (Assert.Single(kept.Summary.Cameras).Accepted, kept.Summary.Cameras[0].AcceptedRecordOnly));
        Assert.True(ReviewDesk.Unreadable(["QR|explain.code.none"]));
        // a defect the tool saw as well: the clearly-bad path (record only when accepted), not the second validation
        Assert.False(ReviewDesk.Unreadable(["Part|explain.locate.not_found", "Date|explain.ocv.wrong"]));
        Assert.False(ReviewDesk.Unreadable(["Date|explain.ocv.wrong", "Grade|explain.verify.grade_low"]));
        // decided on the safe side: a reason not known as a defect, or no fault at all, is validated twice too
        Assert.True(ReviewDesk.Unreadable(["Grade|explain.verify.grade_low"]));
        Assert.True(ReviewDesk.Unreadable(["Edge|explain.caliper.weak_edge"]));
        Assert.True(ReviewDesk.Unreadable([]));
    }

    [Fact]
    public async Task A_bad_picture_saved_without_its_faults_is_never_taught_as_good()
    {
        // an NG picture saved before 2026-11-04 (or one the PLC rejected): only the failed tool's name is known
        var name = new ImageName(1, 1, 7, DateTime.Now, 900);
        var dir = name.Folder(Path.Combine(_f.Folder, "legacy"), ok: false);
        Directory.CreateDirectory(dir);
        var bmp = Path.Combine(dir, name + ".bmp");
        BmpGray.Write(bmp, new byte[320 * 240], 320, 240);
        File.WriteAllText(Path.ChangeExtension(bmp, ".json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["product"] = "Labels", ["ok"] = false, ["reasons"] = new[] { "Check text 1" }, ["image_id"] = name.ToString(),
        }));
        var p = SortQueue.Read(bmp)!;
        Assert.False(p.Ok);
        Assert.Equal(["Check text 1|" + SortQueue.UnknownCause], p.Faults);
        Assert.True(ReviewDesk.ClearlyBad(p.Faults).Clearly);   // accepted all the same: a record only
        Assert.Equal(ReviewStore.Kind.Record, ReviewDesk.KindFor(ReviewDecision.Accepted, ReviewDesk.ClearlyBad(p.Faults).Clearly));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_setting_is_a_supervisors_is_kept_and_turns_good_pictures_off()
    {
        var get = await _f.Runtime().GetLearningSettingsAsync(new GetLearningSettingsRequest(), _op);
        Assert.Equal((true, 5u), (get.Enabled, get.IdleMinutes));
        var denied = await Assert.ThrowsAsync<RpcException>(() => _f.Runtime().SetLearningSettingsAsync(new LearningSettings { Enabled = false, IdleMinutes = 5 }, _op).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        var tooShort = await _f.Runtime().SetLearningSettingsAsync(new LearningSettings { Enabled = true, IdleMinutes = 1 }, _sup);
        Assert.Equal("runtime.refused.idleMinutes", tooShort.ErrorKey);

        var set = await _f.Runtime().SetLearningSettingsAsync(new LearningSettings { Enabled = false, IdleMinutes = 7 }, _sup);
        Assert.True(set.Ok, set.ErrorText);
        Assert.Equal((false, 7u, "sup"), (set.Settings.Enabled, set.Settings.IdleMinutes, set.Settings.ChangedBy));
        Assert.False(_f.App.Services.GetRequiredService<OkSampler>().Enabled);
        // kept across a restart
        var again = new LearningSettingsStore(Path.Combine(_f.Folder, "data"));
        Assert.Equal((false, 7), (again.Enabled, again.IdleMinutes));

        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var status = await _f.Runtime().GetStatusAsync(new GetStatusRequest(), _op);
        Assert.False(status.Learning.Enabled);
        Assert.Equal(7u, status.Learning.IdleMinutes);
        Assert.True(status.Learning.MayLearn);   // the line is stopped
        Assert.False(status.Learning.Idle);      // not yet 7 minutes

        // off: nothing is offered for sorting
        Assert.Empty((await _f.Runtime().ListToSortAsync(new ListToSortRequest { Camera = -1 }, _sup)).Items);
    }

    [Fact]
    public void One_good_part_in_fifty_is_kept_and_near_limit_ones_at_most_thirty_an_hour()
    {
        var s = new OkSampler(_f.App.Services.GetRequiredService<OkSampler>().Store);
        var kept = Enumerable.Range(0, 200).Count(_ => s.Decide("Job-Top", nearLimit: false).Keep);
        Assert.Equal(4, kept);
        var near = Enumerable.Range(0, 100).Count(_ => s.Decide("Job-Side", nearLimit: true).Keep);
        Assert.Equal(OkSampler.NearLimitPerHour + 2, near);   // the cap, plus the 50th and 100th part kept anyway
    }
}
