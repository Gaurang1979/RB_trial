using Grpc.Core;
using VisionCore;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;
using Xunit;
using Status = VisionStudio.Remote.V1.Status;
using PictureCheck = VisionCore.PictureCheck;

/// <summary>The picture check during a run (requirements AS-13a): one picture in N is compared with the camera's
/// taught picture; pictures clearly worse, several in a row, warn the line through its status (code 940); advice
/// only, so every part's result stays as the tools decided.</summary>
public sealed class PictureDriftTests
{
    const int W = 320, H = 240;

    // squares of 16 px, grey 40 and 200: sharp edges in every zone, nothing saturated
    static byte[] Sharp()
    {
        var px = new byte[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                px[y * W + x] = (byte)(((x / 16) + (y / 16)) % 2 == 0 ? 40 : 200);
        return px;
    }

    // the same squares out of focus: a box blur of 13 px, twice
    static byte[] Blurred() => Box(Box(Sharp(), 6), 6);

    static byte[] Box(byte[] src, int r)
    {
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                int sum = 0, n = 0;
                for (var d = -r; d <= r; d++) { var xx = Math.Clamp(x + d, 0, W - 1); sum += src[y * W + xx]; n++; }
                tmp[y * W + x] = (byte)(sum / n);
            }
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                int sum = 0, n = 0;
                for (var d = -r; d <= r; d++) { var yy = Math.Clamp(y + d, 0, H - 1); sum += tmp[yy * W + x]; n++; }
                dst[y * W + x] = (byte)(sum / n);
            }
        return dst;
    }

    static GrayImage Image(byte[] px) => new(px, W, H);

    static Job Job() => new()
    {
        Name = "Caps",
        Cameras = [new CameraConfig { Name = "Top", ReferenceImage = "taught.bmp" }],
        Tools = [new CodeToolConfig { Name = "Read code", Format = BarcodeReading.CodeFormat.QrCode }],
    };

    [Fact]
    public void A_blurred_picture_is_worse_than_a_sharp_taught_one_and_a_sharp_one_is_not()
    {
        var taught = PictureCheck.Measure(Image(Sharp()));
        Assert.Empty(PictureDrift.Worse(taught, PictureCheck.Measure(Image(Sharp()))));
        var worse = PictureDrift.Worse(taught, PictureCheck.Measure(Image(Blurred())));
        Assert.Contains(PictureZoneName.Centre, worse);
        Assert.Equal(5, worse.Count);
        // no taught picture: only the limits judge, and a blurred picture is red there too
        Assert.NotEmpty(PictureDrift.Worse(null, PictureCheck.Measure(Image(Blurred()))));
        Assert.Empty(PictureDrift.Worse(null, PictureCheck.Measure(Image(Sharp()))));
    }

    [Fact]
    public void Light_falling_well_under_the_taught_picture_is_worse()
    {
        var dim = Sharp().Select(v => (byte)(v / 3)).ToArray();
        var worse = PictureDrift.Worse(PictureCheck.Measure(Image(Sharp())), PictureCheck.Measure(Image(dim)));
        Assert.Equal(5, worse.Count);
    }

    [Fact]
    public async Task Three_worse_pictures_in_a_row_warn_and_three_good_ones_clear_it()
    {
        var drift = new PictureDrift(1, load: _ => Image(Sharp()));
        var job = Job();
        var changes = 0;
        drift.Changed += () => Interlocked.Increment(ref changes);
        async Task Offer(byte[] px) { drift.Offer(job, 0, Image(px)); await drift.Last; }

        await Offer(Blurred());
        await Offer(Blurred());
        Assert.Empty(drift.Warnings);   // one or two odd pictures are no drift
        await Offer(Sharp());
        await Offer(Blurred());
        await Offer(Blurred());
        Assert.Empty(drift.Warnings);   // not in a row
        await Offer(Blurred());
        var w = Assert.Single(drift.Warnings);
        Assert.Equal("Top", w.CameraName);
        Assert.Contains("Top", w.Text);
        Assert.Contains(PictureAdviceKind.Focus, w.Advice);
        Assert.Contains("focus the lens", w.Help);
        Assert.Equal(1, changes);

        await Offer(Sharp());
        await Offer(Sharp());
        Assert.Single(drift.Warnings);
        await Offer(Sharp());
        Assert.Empty(drift.Warnings);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task One_picture_in_N_is_checked_and_Stop_or_a_job_change_clears_the_warning()
    {
        var loads = 0;
        var drift = new PictureDrift(4, load: _ => { Interlocked.Increment(ref loads); return Image(Sharp()); });
        var job = Job();
        // 11 pictures: the 4th and 8th are checked, so no warning yet
        for (var i = 0; i < 11; i++) { drift.Offer(job, 0, Image(Blurred())); await drift.Last; }
        Assert.Empty(drift.Warnings);
        drift.Offer(job, 0, Image(Blurred()));   // the 12th: the third checked
        await drift.Last;
        Assert.Single(drift.Warnings);
        Assert.Equal(1, loads);   // the taught picture is read once per job load

        drift.Clear();
        Assert.Empty(drift.Warnings);
        for (var i = 0; i < 12; i++) { drift.Offer(job, 0, Image(Blurred())); await drift.Last; }
        Assert.Single(drift.Warnings);
        // another job: its cameras start afresh
        drift.Offer(Job(), 0, Image(Blurred()));
        Assert.Empty(drift.Warnings);
    }

    [Fact]
    public void Off_checks_nothing_and_a_camera_out_of_range_is_ignored()
    {
        var drift = new PictureDrift(0, load: _ => throw new InvalidOperationException());
        drift.Offer(Job(), 0, Image(Blurred()));
        Assert.Same(Task.CompletedTask, drift.Last);
        var on = new PictureDrift(1, load: _ => Image(Sharp()));
        on.Offer(Job(), 3, Image(Blurred()));
        Assert.Same(Task.CompletedTask, on.Last);
    }

    // --- on the line ----------------------------------------------------------------------------

    static async Task<RuntimeFixture> Line(bool blurred) => await RuntimeFixture.StartAsync(f =>
    {
        var folder = Path.Combine(f.Folder, "caps");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 3; i++)
            BarcodeReading.BmpImage.SaveGray(Path.Combine(folder, $"cap{i}.bmp"), blurred ? Blurred() : Sharp(), W, H);
        var taught = Path.Combine(f.Folder, "taught.bmp");
        BarcodeReading.BmpImage.SaveGray(taught, Sharp(), W, H);
        new JobStore(Path.Combine(f.Folder, "jobs")).Save(new Job
        {
            Name = "Caps",
            Number = 9,
            Cameras = [new CameraConfig { Name = "Top", TestFolder = folder, ReferenceImage = taught }],
            Tools = [new CodeToolConfig { Name = "Read code", Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 }],
        }, "test");
    }, "--Runtime:PictureCheckEvery=1");

    static async Task<(List<InspectionResult> Results, Status Status)> Run(RuntimeFixture f, Func<Status, bool> until)
    {
        var op = await f.SignInOperator();
        Assert.True((await f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Caps" }, op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var call = f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.True((await f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, op)).Ok);
        var results = new List<InspectionResult>();
        Status status;
        await foreach (var r in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            results.Add(r);
            if (results.Count < 12) continue;
            status = await f.Runtime().GetStatusAsync(new GetStatusRequest());
            if (until(status)) break;
        }
        status = await f.Runtime().GetStatusAsync(new GetStatusRequest());
        await f.Runtime().StopAsync(new StopRequest(), op);
        return (results, status);
    }

    [Fact]
    public async Task Blurred_pictures_during_a_run_warn_the_line_and_every_result_stays_the_tools_own()
    {
        await using var f = await Line(blurred: true);
        var (results, status) = await Run(f, s => s.Alarms.Any(a => a.Code == PictureDrift.AlarmCode));
        var alarm = Assert.Single(status.Alarms, a => a.Code == PictureDrift.AlarmCode);
        Assert.Equal(Alarm.Types.Level.Warning, alarm.Level);
        Assert.Contains("Top", alarm.Text);
        Assert.NotEqual(PictureDrift.AlarmCode, status.ErrorCode);   // a warning, never the line's error
        // advice only: each part is decided by its tools alone, none is added or changed
        Assert.All(results, r =>
        {
            Assert.Equal(["Read code"], r.Tools.Select(t => t.Name));
            Assert.Equal(r.Tools.All(t => t.Pass), r.Pass);
            Assert.False(r.Pass);   // no code on the pictures: NG as before
        });
        // Stop clears it
        var stopped = await f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.DoesNotContain(stopped.Alarms, a => a.Code == PictureDrift.AlarmCode);
    }

    [Fact]
    public async Task Pictures_as_sharp_as_the_taught_one_raise_no_warning()
    {
        await using var f = await Line(blurred: false);
        var line = (LineRuntime)f.App.Services.GetService(typeof(LineRuntime))!;
        var (results, status) = await Run(f, _ => true);
        await line.Pictures.Last;
        Assert.True(results.Count >= 12);
        Assert.DoesNotContain(status.Alarms, a => a.Code == PictureDrift.AlarmCode);
        Assert.Empty(line.Pictures.Warnings);
    }
}
