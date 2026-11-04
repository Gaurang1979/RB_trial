using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using SkiaSharp;
using VisionCore;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>The job designer's calls (GetJob, SaveJob, ListPictures, TryJob), batch fields in the
/// status, and the two-camera live view: who may call them, and the refusals that keep the line safe
/// (a job that can never pass is not saved, the running job is not changed, nothing is tried while
/// the line runs).</summary>
public sealed class JobDesignerTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!, _eng = null!;

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            // a second camera with pictures of its own size, so its frames are told apart
            var second = Path.Combine(f.Folder, "frames2");
            Directory.CreateDirectory(second);
            for (var i = 0; i < 2; i++)
                BarcodeReading.BmpImage.SaveGray(Path.Combine(second, $"side{i}.bmp"), new byte[200 * 100], 200, 100);
            new JobStore(Path.Combine(f.Folder, "jobs")).Save(new Job
            {
                Name = "Two cameras",
                Number = 9,
                Cameras = [new CameraConfig { Name = "Cap", TestFolder = f.FramesFolder }, new CameraConfig { Name = "Label", TestFolder = second }],
                Tools =
                [
                    new CodeToolConfig { Name = "QR", Camera = 0, Format = BarcodeReading.CodeFormat.QrCode },
                    new TextToolConfig { Name = "Batch", Camera = 1, Mode = TextMode.Check, Lines = [new TextLineSpec { Template = "B.NO {LOT}" }] },
                ],
            }, "test");
        });
        _op = await _f.SignInOperator();
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(r.Ok, r.ErrorText);
        _eng = RuntimeFixture.Bearer(r.Token);
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    static async Task<StatusCode> Code(Func<Task> call)
    {
        try { await call(); return StatusCode.OK; }
        catch (RpcException e) { return e.StatusCode; }
    }

    async Task<string> JobJson(string name) => (await _f.Runtime().GetJobAsync(new GetJobRequest { Name = name }, _eng)).Json;

    [Fact]
    public async Task Only_an_engineer_in_Studio_designs_jobs()
    {
        Assert.True((await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), _eng)).MayDesignJobs);
        Assert.False((await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), _op)).MayDesignJobs);
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().GetJobAsync(new GetJobRequest { Name = "Labels" }).ResponseAsync));
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime().GetJobAsync(new GetJobRequest { Name = "Labels" }, _op).ResponseAsync));
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = "{}" }, _op).ResponseAsync));
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime().TryJobAsync(new TryJobRequest(), _op).ResponseAsync));
        // the engineer signed in over the network is a browser session: it only reads
        var net = await _f.Sessions(network: true).SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime(network: true).GetJobAsync(new GetJobRequest { Name = "Labels" }, RuntimeFixture.Bearer(net.Token)).ResponseAsync));
    }

    [Fact]
    public async Task A_saved_copy_is_a_new_job_and_a_job_that_can_never_pass_is_refused()
    {
        var got = await _f.Runtime().GetJobAsync(new GetJobRequest { Name = "Labels" }, _eng);
        Assert.True(got.Ok, got.ErrorText);
        var job = JsonSerializer.Deserialize<Job>(got.Json, JobStore.Json)!;
        Assert.Equal("Labels", job.Name);

        job.Name = "Labels copy";
        var taken = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = JsonSerializer.Serialize(job, JobStore.Json) }, _eng);
        Assert.False(taken.Ok);   // number 7 is Labels's: the PLC could not tell them apart
        Assert.Equal("runtime.refused.jobNumberTaken", taken.ErrorKey);

        job.Number = 17;
        var saved = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = JsonSerializer.Serialize(job, JobStore.Json) }, _eng);
        Assert.True(saved.Ok, saved.ErrorText);
        Assert.Equal(1, saved.Version);
        Assert.Contains((await _f.Runtime().ListJobsAsync(new ListJobsRequest())).Jobs, j => j.Name == "Labels copy" && j.Number == 17);
        var back = JsonSerializer.Deserialize<Job>((await _f.Runtime().GetJobAsync(new GetJobRequest { Name = "Labels copy" }, _eng)).Json, JobStore.Json)!;
        Assert.Equal("eng", back.UpdatedBy);

        // Check text with nothing to check fails every part: never stored
        job.Tools.Add(new TextToolConfig { Name = "Text", Mode = TextMode.Check });
        var never = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = JsonSerializer.Serialize(job, JobStore.Json) }, _eng);
        Assert.False(never.Ok);
        Assert.Equal("runtime.refused.jobInvalid", never.ErrorKey);
        Assert.Contains("lines to check", never.ErrorText);
        Assert.False((await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = "{ not json" }, _eng)).Ok);
    }

    [Fact]
    public async Task A_draft_is_read_as_strictly_as_a_job_file_so_no_setting_is_left_at_its_default_unseen()
    {
        var json = await JobJson("Labels");
        async Task<SaveJobResponse> Save(Action<System.Text.Json.Nodes.JsonObject> edit)
        {
            var draft = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            edit(draft);
            return await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = draft.ToJsonString() }, _eng);
        }

        var unknown = await Save(d => d["Descripton"] = "typo");
        Assert.False(unknown.Ok);
        Assert.Equal("runtime.refused.jobInvalid", unknown.ErrorKey);
        Assert.Contains("Descripton", unknown.ErrorText);

        var number = await Save(d => d["Tools"]![0]!["Format"] = 1);   // a named value written as its integer
        Assert.False(number.Ok);
        Assert.Contains("Format", number.ErrorText);

        var newer = await Save(d => d["SchemaVersion"] = "0.9");
        Assert.False(newer.Ok);
        Assert.Contains("newer version", newer.ErrorText);

        var twice = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = json.Insert(json.IndexOf('{') + 1, "\"Number\": 70, ") }, _eng);
        Assert.False(twice.Ok);

        // a try reads the draft the same way: what is tried is what would be saved
        var draft = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        draft["Descripton"] = "typo";
        var tryIt = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = draft.ToJsonString(), Camera = 0, Stored = "frame1.bmp" }, _eng);
        Assert.False(tryIt.Ok);
        Assert.Equal("runtime.refused.jobInvalid", tryIt.ErrorKey);

        // the draft as GetJob gave it saves as before
        var same = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = json }, _eng);
        Assert.True(same.Ok, same.ErrorText);
        Assert.Equal(7, (await _f.Runtime().ListJobsAsync(new ListJobsRequest())).Jobs.Single(j => j.Name == "Labels").Number);
    }

    [Fact]
    public async Task Accept_as_is_on_too_few_pixels_is_recorded_as_the_signed_in_engineer_at_the_runtimes_time()
    {
        var job = JsonSerializer.Deserialize<Job>(await JobJson("Two cameras"), JobStore.Json)!;
        var before = DateTimeOffset.Now.AddSeconds(-1);
        job.Tools[0].PixelsAccepted = new PixelAcceptance("typed by the client", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), 2.1f);
        var saved = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = JsonSerializer.Serialize(job, JobStore.Json) }, _eng);
        Assert.True(saved.Ok, saved.ErrorText);
        var back = JsonSerializer.Deserialize<Job>(await JobJson("Two cameras"), JobStore.Json)!.Tools[0].PixelsAccepted!;
        Assert.Equal(("eng", 2.1f), (back.By, back.FoundPx));
        Assert.InRange(back.At, before, DateTimeOffset.Now);

        // saved again unchanged: who and when stay as recorded, whatever the client sends
        job.Tools[0].PixelsAccepted = back with { By = "someone else" };
        Assert.True((await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = JsonSerializer.Serialize(job, JobStore.Json) }, _eng)).Ok);
        Assert.Equal(back, JsonSerializer.Deserialize<Job>(await JobJson("Two cameras"), JobStore.Json)!.Tools[0].PixelsAccepted);
    }

    [Fact]
    public async Task A_draft_is_tried_on_a_stored_picture_and_a_path_is_never_read()
    {
        var json = await JobJson("Labels");
        var list = await _f.Runtime().ListPicturesAsync(new ListPicturesRequest { JobJson = json, Camera = 0 }, _eng);
        Assert.True(list.Ok, list.ErrorText);
        Assert.Equal(["frame0.bmp", "frame1.bmp", "frame2.bmp"], list.Names);

        var r = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Stored = "frame1.bmp" }, _eng);
        Assert.True(r.Ok, r.ErrorText);
        Assert.False(r.Pass);   // no code on the frame: NG
        Assert.Equal(320, r.ImageWidth);
        Assert.Equal(240, r.ImageHeight);
        Assert.Equal(0xFF, r.Jpeg[0]);
        var tool = Assert.Single(r.Tools);
        Assert.Equal("Read code", tool.Name);
        Assert.False(tool.Pass);
        Assert.NotEmpty(tool.ReasonText);
        Assert.False(tool.ReportOnly);
        Assert.False(tool.HasCodeGrade);   // not graded: no grade, not a grade of 0
        // the picture check (AS-13) comes with every try: the four corners and the centre, advice only
        Assert.Equal(["top_left", "top_right", "bottom_left", "bottom_right", "centre"], r.PictureCheck.Zones.Select(z => z.Zone));
        Assert.Equal(new { X = 0, Y = 0, W = 106, H = 80 }, r.PictureCheck.Zones.Select(z => new { z.X, z.Y, W = z.Width, H = z.Height }).First());
        Assert.All(r.PictureCheck.Zones, z => Assert.Contains(z.State, new[] { "good", "close", "bad", "no_detail" }));
        var skipped = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Stored = "frame1.bmp", SkipPictureCheck = true }, _eng);
        Assert.True(skipped.Ok, skipped.ErrorText);
        Assert.Null(skipped.PictureCheck);

        var path = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Stored = "../jobs/Labels.json" }, _eng);
        Assert.False(path.Ok);
        Assert.Equal("runtime.refused.picture", path.ErrorKey);
        Assert.False((await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 3, Stored = "frame1.bmp" }, _eng)).Ok);
    }

    [Fact]
    public async Task An_uploaded_PNG_is_tried_like_a_stored_picture()
    {
        using var bmp = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Gray8, SKAlphaType.Opaque));
        bmp.Erase(new SKColor(128, 128, 128));
        using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        var r = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = await JobJson("Labels"), Camera = 0, Upload = ByteString.CopyFrom(png.ToArray()) }, _eng);
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal(64, r.ImageWidth);
        Assert.False(r.Pass);
        var junk = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = await JobJson("Labels"), Camera = 0, Upload = ByteString.CopyFromUtf8("not a picture") }, _eng);
        Assert.False(junk.Ok);
    }

    [Fact]
    public async Task The_tried_picture_comes_back_at_full_resolution_without_loss_by_its_image_id()
    {
        // requirements AS-17: zoomed in, the engineer judges clarity on the camera's own pixels
        using var bmp = new SKBitmap(new SKImageInfo(1500, 40, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = new byte[1500 * 40];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7 % 251);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        var json = await JobJson("Labels");
        var r = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Upload = ByteString.CopyFrom(png.ToArray()) }, _eng);
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal(1280, r.Width);   // the try's JPEG is reduced
        Assert.StartsWith("try-", r.ImageId);

        async Task<(byte[] Data, int Width, int Height, string Format)> Get(string id, GetImageRequest.Types.Encoding encoding)
        {
            using var data = new MemoryStream();
            int w = 0, h = 0;
            var format = "";
            await foreach (var c in _f.Runtime().GetImage(new GetImageRequest { ImageId = id, Encoding = encoding }, _eng).ResponseStream.ReadAllAsync())
            {
                if (w == 0) (w, h, format) = (c.Width, c.Height, c.Format);
                c.Data.WriteTo(data);
            }
            return (data.ToArray(), w, h, format);
        }
        var full = await Get(r.ImageId, GetImageRequest.Types.Encoding.Png);
        Assert.Equal((1500, 40, "png"), (full.Width, full.Height, full.Format));
        using (var decoded = SKBitmap.Decode(full.Data).Copy(SKColorType.Gray8))
            Assert.Equal(pixels, decoded.GetPixelSpan().ToArray());   // every pixel as the camera gave it
        var raw = await Get(r.ImageId, GetImageRequest.Types.Encoding.Raw);
        Assert.Equal(pixels, raw.Data);

        var again = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Stored = "frame1.bmp" }, _eng);
        Assert.NotEqual(r.ImageId, again.ImageId);
        var second = await Get(again.ImageId, GetImageRequest.Types.Encoding.Png);
        Assert.Equal((320, 240), (second.Width, second.Height));
        Assert.Equal(pixels, (await Get(r.ImageId, GetImageRequest.Types.Encoding.Raw)).Data);   // the earlier one is still kept
        Assert.Equal(StatusCode.NotFound, await Code(() => Get("try-0000000000000000", GetImageRequest.Types.Encoding.Png)));

        // a tried picture may be an engineer's upload: not for an operator
        Assert.Equal(StatusCode.PermissionDenied, await Code(async () =>
        {
            await foreach (var _ in _f.Runtime().GetImage(new GetImageRequest { ImageId = r.ImageId }, _op).ResponseStream.ReadAllAsync()) { }
        }));
        // "Try all pictures": not shown, so not kept
        var notShown = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Stored = "frame2.bmp", NotShown = true }, _eng);
        Assert.True(notShown.Ok, notShown.ErrorText);
        Assert.Equal("", notShown.ImageId);

        // while the line runs no tried picture is sent (encoding one takes the CPU a part needs), nor kept after
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        try { Assert.Equal(StatusCode.NotFound, await Code(() => Get(r.ImageId, GetImageRequest.Types.Encoding.Png))); }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
        Assert.Equal(StatusCode.NotFound, await Code(() => Get(again.ImageId, GetImageRequest.Types.Encoding.Png)));
    }

    [Fact]
    public async Task A_line_taking_its_text_from_the_PLC_is_tried_with_the_text_block_given()
    {
        var job = JsonSerializer.Deserialize<Job>(await JobJson("Two cameras"), JobStore.Json)!;
        var text = job.Tools.OfType<TextToolConfig>().Single();
        text.Lines = [new TextLineSpec { Source = ExpectedSource.Plc, SourceName = "Text", PlcText = new VisionCore.Plc.PlcTextSpec { Register = 510, Length = 20 } }];
        var json = JsonSerializer.Serialize(job, JobStore.Json);
        var without = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 1, Stored = "side0.bmp" }, _eng);
        Assert.True(without.Ok, without.ErrorText);
        Assert.Contains("PLC", Assert.Single(without.Tools).ReasonText);   // no text block: NG, never guessed
        var with = new TryJobRequest { JobJson = json, Camera = 1, Stored = "side0.bmp" };
        with.PlcText[510] = "B.NO A2610307";
        var r = await _f.Runtime().TryJobAsync(with, _eng);
        Assert.True(r.Ok, r.ErrorText);
        Assert.False(r.Pass);   // nothing printed on the picture
        Assert.DoesNotContain("PLC", Assert.Single(r.Tools).ReasonText);
    }

    [Fact]
    public async Task While_the_line_runs_its_job_is_not_saved_and_nothing_is_tried()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var json = await JobJson("Labels");
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        try
        {
            var save = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = json }, _eng);
            Assert.False(save.Ok);
            Assert.Equal("runtime.refused.stopFirst", save.ErrorKey);
            var tryIt = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = json, Camera = 0, Stored = "frame0.bmp" }, _eng);
            Assert.False(tryIt.Ok);
            Assert.Equal("runtime.refused.stopFirst", tryIt.ErrorKey);
        }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
        // stopped: the open job is saved as a new version and the line runs the new one
        var again = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = json }, _eng);
        Assert.True(again.Ok, again.ErrorText);
        Assert.Equal(2, again.Version);
        Assert.True((await _f.Runtime().GetJobAsync(new GetJobRequest(), _eng)).Open);
    }

    [Fact]
    public async Task The_status_lists_the_batch_to_enter_and_what_was_entered()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Two cameras" }, _op)).Ok);
        var field = Assert.Single((await _f.Runtime().GetStatusAsync(new GetStatusRequest())).Batch);
        Assert.Equal("LOT", field.Key);
        Assert.Equal("", field.Value);
        Assert.False((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);   // batch not entered
        Assert.True((await _f.Runtime().SetInputAsync(new SetInputRequest { Name = "batch.LOT", Value = "A123" }, _op)).Ok);
        Assert.Equal("A123", Assert.Single((await _f.Runtime().GetStatusAsync(new GetStatusRequest())).Batch).Value);
    }

    [Fact]
    public async Task Each_simulated_camera_plays_its_own_pictures_and_the_live_view_shows_both()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Two cameras" }, _op)).Ok);
        Assert.True((await _f.Runtime().SetInputAsync(new SetInputRequest { Name = "batch.LOT", Value = "A123" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = _f.Runtime().WatchFrames(new WatchFramesRequest { AllCameras = true, MaxFps = 30 }, cancellationToken: cts.Token);
        await Task.Delay(100);
        var start = await _f.Runtime().StartAsync(new StartRequest { Source = Source.Camera }, _op);
        Assert.True(start.Ok, start.ErrorText);
        var widths = new Dictionary<string, int>();
        try
        {
            await foreach (var f in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                widths[f.Camera] = (int)Math.Round(f.Width / f.Scale);
                Assert.False(f.Pass);
                if (widths.Count == 2) break;
            }
        }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
        Assert.Equal(320, widths["Cap"]);
        Assert.Equal(200, widths["Label"]);
    }

    [Fact]
    public async Task A_bad_parts_frame_is_never_held_back_by_the_frame_rate()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var call = _f.Runtime().WatchFrames(new WatchFramesRequest { MaxFps = 0.5f }, cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var frames = 0;
        try
        {
            await foreach (var f in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                Assert.False(f.Pass);   // no code on the frames: every part is NG
                if (++frames == 4) break;
            }
        }
        catch (RpcException) { }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
        Assert.Equal(4, frames);   // at 0.5 frames a second only one OK frame would come in 3 s
    }

    [Fact]
    public async Task A_job_that_cannot_run_opens_for_editing_but_is_never_run_and_a_damaged_file_is_refused()
    {
        var folder = Path.Combine(_f.Folder, "jobs");
        // a find-label tool whose taught pattern is gone, and a file cut short while it was written
        File.WriteAllText(Path.Combine(folder, "Untaught.json"), """
            {"SchemaVersion":"0.1","Name":"Untaught","Number":21,"Cameras":[{"Name":"Top"}],
             "Tools":[{"kind":"locate","Name":"Find label","PatternFile":"Untaught/Find label.v1.bmp"}]}
            """);
        File.WriteAllText(Path.Combine(folder, "Damaged.json"), """{"SchemaVersion":"0.1","Name":"Damaged","Cam""");

        var open = await _f.Runtime().GetJobAsync(new GetJobRequest { Name = "Untaught" }, _eng);
        Assert.True(open.Ok, open.ErrorText);
        var damaged = await _f.Runtime().GetJobAsync(new GetJobRequest { Name = "Damaged" }, _eng);
        Assert.False(damaged.Ok);
        Assert.Equal("runtime.refused.loadFailed", damaged.ErrorKey);

        var change = await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Untaught" }, _op);
        Assert.False(change.Ok);
        Assert.Contains("Teach the pattern again", change.ErrorText);
        Assert.False((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Number = 21 }, _op)).Ok);
        Assert.False((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Damaged" }, _op)).Ok);
        // the PLC's job number still finds a good job past the broken files
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Number = 9 }, _op)).Ok);

        // the number of a job that cannot run yet is still its own
        var copy = JsonSerializer.Deserialize<Job>(await JobJson("Two cameras"), JobStore.Json)!;
        copy.Name = "Copy";
        copy.Number = 21;
        var save = await _f.Runtime().SaveJobAsync(new SaveJobRequest { Json = JsonSerializer.Serialize(copy, JobStore.Json) }, _eng);
        Assert.Equal("runtime.refused.jobNumberTaken", save.ErrorKey);
    }
}
