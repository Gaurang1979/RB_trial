using Grpc.Core;
using VisionStudio.Remote.V1;
using VisionCore;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The line behind the contract: replay and simulated camera runs, results, images,
/// counters, inputs, health, and the refusals that keep a bad part from passing.</summary>
public sealed class LineTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!;
    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync();
        _op = await _f.SignInOperator();
    }
    public async Task DisposeAsync() => await _f.DisposeAsync();

    async Task<List<InspectionResult>> Collect(int n, Func<Task> start)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = _f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        await start();
        var list = new List<InspectionResult>();
        await foreach (var r in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            list.Add(r);
            if (list.Count == n) break;
        }
        return list;
    }

    [Fact]
    public async Task Replay_run_streams_NG_results_with_full_resolution_images_and_counts_them()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var results = await Collect(3, async () => Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok));
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        Assert.All(results, r => Assert.False(r.Pass));   // no code on the frames: NG, never OK
        Assert.All(results, r => Assert.Equal("Labels", r.Job));
        Assert.Contains(results, r => r.Tools.Any(t => t.Name == "Read code" && !t.Pass));

        var id = results[^1].ImageIds.Single();
        var chunks = new List<ImageChunk>();
        await foreach (var c in _f.Runtime().GetImage(new GetImageRequest { ImageId = id, Encoding = GetImageRequest.Types.Encoding.Raw }).ResponseStream.ReadAllAsync())
            chunks.Add(c);
        Assert.Equal(320, chunks[0].Width);
        Assert.Equal(240, chunks[0].Height);
        Assert.Equal("gray8", chunks[0].Format);
        Assert.Equal(320 * 240, chunks.Sum(c => c.Data.Length));

        var png = new List<ImageChunk>();
        await foreach (var c in _f.Runtime().GetImage(new GetImageRequest { ImageId = id, Encoding = GetImageRequest.Types.Encoding.Png }).ResponseStream.ReadAllAsync())
            png.Add(c);
        Assert.Equal("png", png[0].Format);
        Assert.Equal(0x89, png[0].Data[0]);

        var counters = await _f.Runtime().GetCountersAsync(new GetCountersRequest());
        Assert.True(counters.Total >= 3);
        Assert.Equal(counters.Total, counters.Ng);
        Assert.Equal(0, counters.YieldPercent);
        Assert.True(counters.NgReasons["Read code"] >= 3);
        var status = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.Equal(VisionStudio.Remote.V1.Status.Types.State.Stopped, status.State);
        Assert.Equal(counters.Total, status.Inspections);
    }

    [Fact]
    public async Task Unknown_image_is_not_found()
    {
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in _f.Runtime().GetImage(new GetImageRequest { ImageId = "999-0" }).ResponseStream.ReadAllAsync()) { }
        });
        Assert.Equal(StatusCode.NotFound, e.StatusCode);
    }

    [Fact]
    public async Task Simulated_camera_takes_frames_through_the_intake()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var results = await Collect(2, async () =>
        {
            var r = await _f.Runtime().StartAsync(new StartRequest { Source = Source.Camera }, _op);
            Assert.True(r.Ok, r.ErrorText);
        });
        var status = await _f.Runtime().GetStatusAsync(new GetStatusRequest());
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        Assert.Equal(Source.Camera, status.Source);
        Assert.Contains(status.Cameras, c => c.Name == "Top" && c.Connected);
        Assert.All(results, r => Assert.False(r.Pass));
        Assert.All(results, r => Assert.DoesNotContain(r.Tools, t => t.Kind == "Camera"));   // every part had its frame
    }

    [Fact]
    public async Task Two_camera_channels_finishing_together_are_two_parts_each_with_its_own_verdict()
    {
        var station = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<VisionStudio.Services.Station>(_f.App.Services);
        new JobStore(Path.Combine(_f.Folder, "jobs")).Save(new Job
        {
            Name = "TwoCams",
            Number = 9,
            Cameras = [new CameraConfig { Name = "Top", TestFolder = _f.FramesFolder }, new CameraConfig { Name = "Side", TestFolder = _f.FramesFolder }],
            Tools =
            [
                new CodeToolConfig { Name = "Top code", Camera = 0, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 },
                new CodeToolConfig { Name = "Side code", Camera = 1, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 },
            ],
        }, "test");
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "TwoCams" }, _op)).Ok);
        const int parts = 20;
        var results = await Collect(2 * parts, () =>
        {
            station.Start(VisionStudio.Services.RunMode.PlcSimulation);
            // both channels fired in the same PLC scan, as two cameras on one sensor
            for (ushort p = 1; p <= parts; p++)
                Parallel.Invoke(
                    () => station.InspectCamera(0, new VisionCore.Plc.PlcTrigger(p, $"P{p}", 0, 1, false)),
                    () => station.InspectCamera(1, new VisionCore.Plc.PlcTrigger(p, $"P{p}", 0, 2, false)));
            return Task.CompletedTask;
        });
        station.Stop();
        Assert.All(results, r => Assert.Single(r.Tools));   // one camera's tools per result, never two parts merged
        Assert.All(results, r => Assert.Equal(r.Tools.All(t => t.Pass), r.Pass));
        Assert.Equal(parts, results.Count(r => r.Tools[0].Name == "Top code"));
        var counters = await _f.Runtime().GetCountersAsync(new GetCountersRequest());
        Assert.Equal((ulong)(2 * parts), counters.Total);
        Assert.Equal(counters.Total, counters.Ng);
    }

    [Fact]
    public async Task Trigger_while_stopped_is_refused_not_passed()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var r = await _f.Runtime().TriggerAsync(new TriggerRequest { WaitForResult = true }, _op);
        Assert.Equal(111u, r.ErrorCode);
        Assert.Null(r.Result);
    }

    [Fact]
    public async Task Trigger_while_running_returns_its_result()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var r = await _f.Runtime().TriggerAsync(new TriggerRequest { PartId = "P-42", WaitForResult = true }, _op);
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        Assert.Equal(0u, r.ErrorCode);
        Assert.Equal("P-42", r.Result.PartId);
        Assert.False(r.Result.Pass);
    }

    [Fact]
    public async Task Job_that_checks_nothing_does_not_start()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Empty" }, _op)).Ok);
        var r = await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op);
        Assert.False(r.Ok);
        Assert.Equal("runtime.refused.noTools", r.ErrorKey);
    }

    [Fact]
    public async Task Product_and_batch_change_only_while_stopped()
    {
        Assert.False((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Nope" }, _op)).Ok);
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        Assert.True((await _f.Runtime().SetInputAsync(new SetInputRequest { Name = "batch.LOT", Value = "A1" }, _op)).Ok);
        Assert.Equal(404u, (await _f.Runtime().SetInputAsync(new SetInputRequest { Name = "lamp", Value = "on" }, _op)).ErrorCode);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var change = await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Empty" }, _op);
        var input = await _f.Runtime().SetInputAsync(new SetInputRequest { Name = "batch.LOT", Value = "B2" }, _op);
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        Assert.Equal("runtime.refused.stopFirst", change.ErrorKey);
        Assert.False(input.Ok);
    }

    [Fact]
    public async Task Status_carries_the_guard_limits_and_watch_sends_at_once()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = _f.Runtime().WatchStatus(new WatchStatusRequest(), cancellationToken: cts.Token);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        var s = call.ResponseStream.Current;
        Assert.Equal(85, s.Health.Limits.HighPct);
        Assert.Equal(95, s.Health.Limits.CriticalPct);
        Assert.NotEqual(PcHealth.Types.Pressure.Unspecified, s.Health.Pressure);
        // no job open after a first start: the status says so and is not "ready"
        Assert.Equal(VisionStudio.Remote.V1.Status.Types.State.Error, s.State);
    }

    [Fact]
    public async Task Live_frames_carry_vector_graphics_in_jpeg_pixels()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = _f.Runtime().WatchFrames(new WatchFramesRequest { MaxFps = 30, MaxWidth = 160 }, cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        var f = call.ResponseStream.Current;
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        Assert.Equal("Top", f.Camera);
        Assert.Equal(160, f.Width);
        Assert.Equal(120, f.Height);
        Assert.Equal(0.5f, f.Scale, 3);
        Assert.False(f.Pass);
        Assert.Equal(0xFF, f.Jpeg[0]);
        // The camera's tool results come with the frame; print quality only from a Check text tool.
        var tool = Assert.Single(f.Tools);
        Assert.Equal("Read code", tool.Name);
        Assert.False(tool.HasPrintQuality);
        Assert.False(tool.HasCodeGrade);
        Assert.All(f.Codes, c => Assert.Equal("", c.AddOn));
    }
}

/// <summary>The frame intake camera on its own: a lost or late frame never reaches the wrong part.</summary>
public sealed class IntakeCameraTests
{
    // A frame that comes is handed over at once; the long wait is for a loaded machine, where the
    // simulated camera's thread-pool task can start late (a frame that never comes is still null).
    const int FrameWait = 10_000;

    static string[] Frames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsic-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Enumerable.Range(0, 3).Select(i =>
        {
            var p = Path.Combine(dir, $"f{i}.bmp");
            var px = new byte[64 * 48];
            Array.Fill(px, (byte)(10 * (i + 1)));
            BarcodeReading.BmpImage.SaveGray(p, px, 64, 48);
            return p;
        }).ToArray();
    }

    [Fact]
    public void Lost_frame_makes_its_part_missed_and_the_next_part_gets_its_own_frame()
    {
        var src = new SimulatorDriver.SimSource(Frames(), 1);
        using var cam = new IntakeCamera(src, "Top") { ReuseBuffers = true };
        var a = cam.Grab(FrameWait);
        Assert.NotNull(a);
        Assert.Equal(10, a!.Pixels[0]);
        src.Lose(1);
        Assert.Null(cam.Grab(200));                // its frame never came: NG
        var c = cam.Grab(FrameWait);
        Assert.NotNull(c);
        Assert.Equal(30, c!.Pixels[0]);            // the third file, not a stale or missed record
        Assert.EndsWith("#3", c.Name);
        Assert.Equal(1, cam.Intake.Missed);
    }

    [Fact]
    public void Simulator_reads_frames_by_content_and_plays_each_cameras_own_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "Side"));
        // a PNG saved with a .bmp name, as camera tools often do, in the camera's own folder
        using (var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(64, 48, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque)))
        {
            bmp.Erase(new SkiaSharp.SKColor(90, 120, 150));
            using var png = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, "Side", "f0.bmp"), png.ToArray());
        }
        var px = new byte[64 * 48];
        Array.Fill(px, (byte)7);
        BarcodeReading.BmpImage.SaveGray(Path.Combine(dir, "f0.bmp"), px, 64, 48);
        var driver = new SimulatorDriver(dir, new FrameStats(), 1);
        using var side = driver.Open(new CameraConfig { Name = "Side", TestFolder = Path.Combine(dir, "Side") });
        using var top = driver.Open(new CameraConfig { Name = "Top" });
        Assert.Equal(120, side.Grab(FrameWait)!.Pixels[0]);   // the mean of R, G and B
        Assert.Equal(7, top.Grab(FrameWait)!.Pixels[0]);      // no folder of its own: the simulator folder
    }

    [Fact]
    public void Buffers_are_reused_only_after_the_next_grab()
    {
        var src = new SimulatorDriver.SimSource(Frames(), 1);
        using var cam = new IntakeCamera(src, "Top", buffers: 1) { ReuseBuffers = true };
        var a = cam.Grab(FrameWait)!;
        Assert.Equal(1, cam.Intake.Pool.InUse);    // held while the station inspects it
        var b = cam.Grab(FrameWait)!;              // the buffer went back first, then took the new frame
        Assert.Equal(20, b.Pixels[0]);
        Assert.Same(a.Pixels, b.Pixels);
    }

    [Fact]
    public void Frame_wait_follows_the_job_and_starts_at_two_seconds()
    {
        // 1920 x 1200 grey over GigE (23 ms), 2 ms exposure and the margin: about 126 ms, not 2 s
        Assert.Equal(126, IntakeCamera.EstimateFrameWaitMs(2000, 1920 * 1200, 0));
        // a camera whose frames have been slower than that: its slowest recent frame plus the margin
        Assert.Equal(400, IntakeCamera.EstimateFrameWaitMs(2000, 1920 * 1200, 300));
        Assert.Equal(IntakeCamera.MaxFrameWaitMs, IntakeCamera.EstimateFrameWaitMs(5_000_000, 20_000_000, 0));

        var src = new SimulatorDriver.SimSource(Frames(), 1);
        using var cam = new IntakeCamera(src, "Top", exposureUs: 2000) { ReuseBuffers = true };
        Assert.Equal(IntakeCamera.MaxFrameWaitMs, cam.FrameWaitMs);   // no frame yet: its size is not known
        Assert.NotNull(cam.Grab(FrameWait));
        Assert.InRange(cam.FrameWaitMs, IntakeCamera.FrameMarginMs + 2, IntakeCamera.MaxFrameWaitMs - 1);
        Assert.Equal(cam.FrameWaitMs, ((ICamera)cam).FrameWaitMs);    // what the station waits
    }

    [Fact]
    public void Simulator_frame_comes_as_late_as_the_lines_camera_would_send_it()
    {
        // 1920 x 1200 grey at 2 ms exposure: 23 ms more over GigE, 6 ms more over USB3 (RT-SF-01)
        var files = Frames();
        Assert.Equal(25.04, new SimulatorDriver.SimSource(files, null, exposureUs: 2000).FrameDelayMs(1920, 1200), 2);
        Assert.Equal(8.06, new SimulatorDriver.SimSource(files, null, exposureUs: 2000,
            linkBytesPerMs: CameraLink.BytesPerMs("usb3")).FrameDelayMs(1920, 1200), 2);
        // an explicit delay still wins
        Assert.Equal(7, new SimulatorDriver.SimSource(files, 7, exposureUs: 2000).FrameDelayMs(1920, 1200));
        Assert.Equal(CameraLink.GigEBytesPerMs, CameraLink.BytesPerMs(""));
        Assert.Throws<InvalidOperationException>(() => new SimulatorDriver(".", new FrameStats(), link: "CoaXPress"));

        // through the driver: the job camera's 60 ms exposure holds the frame back at least that long
        var driver = new SimulatorDriver(Path.GetDirectoryName(files[0])!, new FrameStats(), link: "USB3");
        using var cam = driver.Open(new CameraConfig { Name = "Top", ExposureUs = 60_000 });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(cam.Grab(FrameWait));
        Assert.True(watch.Elapsed.TotalMilliseconds >= 59, $"frame after {watch.Elapsed.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public void A_frame_later_than_its_late_window_is_known_by_its_number()
    {
        // frame 2 comes 600 ms after its trigger, long after its part's 100 ms wait and 200 ms late window, in the
        // middle of part 3's wait; frame 3 takes 700 ms. Frame 2 is thrown away by its number; part 3 gets frame 3.
        var src = new ScriptedSource(new() { [2] = 600, [3] = 700 });
        using var cam = new IntakeCamera(src, "Top") { ReuseBuffers = true };
        Assert.Equal(1, cam.Grab(FrameWait)!.Pixels[0]);
        Assert.Null(cam.Grab(100));
        var c = cam.Grab(FrameWait);
        Assert.NotNull(c);
        Assert.Equal(3, c!.Pixels[0]);
        Assert.Equal(4, cam.Grab(FrameWait)!.Pixels[0]);
    }

    [Fact]
    public void A_frame_later_than_its_part_never_reaches_the_next_part()
    {
        // frame 2 comes 300 ms after its trigger, after its part's 200 ms wait; frame 3 takes 200 ms. Without the
        // late window, frame 2 would arrive first in part 3's wait and be inspected as part 3.
        var src = new ScriptedSource(new() { [2] = 300, [3] = 200 });
        using var cam = new IntakeCamera(src, "Top") { ReuseBuffers = true };
        Assert.Equal(1, cam.Grab(FrameWait)!.Pixels[0]);
        Assert.Null(cam.Grab(200));                         // NG: no picture in time
        var c = cam.Grab(FrameWait);
        Assert.NotNull(c);
        Assert.Equal(3, c!.Pixels[0]);
        Assert.EndsWith("#3", c.Name);
    }

    [Fact]
    public void A_frame_that_never_reaches_the_PC_costs_only_that_part()
    {
        var src = new SimulatorDriver.SimSource(Frames(), 1);
        using var cam = new IntakeCamera(src, "Top", exposureUs: 2000) { ReuseBuffers = true };
        Assert.NotNull(cam.Grab(FrameWait));
        var wait = cam.FrameWaitMs;
        Assert.True(wait < IntakeCamera.MaxFrameWaitMs, $"frame wait {wait} ms");
        src.Lose(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(cam.Grab(wait));                        // NG after the job's wait, not 2 s
        Assert.InRange(sw.ElapsedMilliseconds, wait - 20, IntakeCamera.MaxFrameWaitMs - 1);
        var c = cam.Grab(FrameWait);                        // the next part has its own picture
        Assert.NotNull(c);
        Assert.Equal(30, c!.Pixels[0]);
    }

    [Fact]
    public void A_camera_that_skips_a_trigger_costs_that_part_and_the_next_never_more()
    {
        var src = new ScriptedSource(new()) { Ignore = 2 };     // the camera never takes a frame for trigger 2
        using var cam = new IntakeCamera(src, "Top") { ReuseBuffers = true };
        Assert.Equal(1, cam.Grab(FrameWait)!.Pixels[0]);
        Assert.Null(cam.Grab(100));
        // its next frame carries the number a late frame 2 would: thrown away, so this part is NG too
        Assert.Null(cam.Grab(300));
        Assert.Equal(3, cam.Grab(FrameWait)!.Pixels[0]);   // then in step again
        Assert.Equal(4, cam.Grab(FrameWait)!.Pixels[0]);
    }

    [Fact]
    public void Simulator_decodes_each_picture_once_within_its_cache()
    {
        var files = Frames();
        // room for two of the three 64 x 48 pictures
        var src = new SimulatorDriver.SimSource(files, 1, cacheBytes: 2 * 64 * 48);
        using var cam = new IntakeCamera(src, "Top") { ReuseBuffers = true };
        for (var i = 0; i < 3; i++) Assert.NotNull(cam.Grab(FrameWait));
        foreach (var f in files) File.Delete(f);
        // the two kept pictures play from memory; the third would be read from its file again, which is gone
        Assert.Equal(10, cam.Grab(FrameWait)!.Pixels[0]);
        Assert.Equal(20, cam.Grab(FrameWait)!.Pixels[0]);
        Assert.Null(cam.Grab(300));
    }

    /// <summary>A camera whose frame n comes delays[n] ms after its trigger (1 ms otherwise), every pixel n, numbered
    /// as a camera numbers the frames it takes; the trigger numbered <see cref="Ignore"/> takes no frame at all.</summary>
    sealed class ScriptedSource(Dictionary<int, int> delays) : ITriggeredSource
    {
        int _triggers, _frames;
        public int Ignore { get; init; }
        public event Action<ulong, int, int, CopyFrame>? Frame;

        public void Trigger()
        {
            if (++_triggers == Ignore) return;
            var n = ++_frames;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delays.GetValueOrDefault(n, 1)).ConfigureAwait(false);
                Frame?.Invoke((ulong)n, 8, 8, into => into.Fill((byte)n));
            });
        }

        public void Dispose() { }
    }
}

/// <summary>The Run screen's Batch and PLC card (UI-UX-SPEC.md 4.1): no PLC set up sends no PLC state;
/// a PLC set up whose link could not start is a lost link, never "no PLC".</summary>
public sealed class PlcStateTests
{
    [Fact]
    public async Task A_PLC_set_up_whose_link_could_not_start_is_sent_as_a_lost_link()
    {
        await using var f = await RuntimeFixture.StartAsync(f =>
        {
            Directory.CreateDirectory(Path.Combine(f.Folder, "data"));
            File.WriteAllText(Path.Combine(f.Folder, "data", "plc.json"), """{ "Protocol": "NoSuchProtocol" }""");
        });
        var s = await f.Runtime().GetStatusAsync(new GetStatusRequest());
        Assert.NotNull(s.Plc);
        Assert.False(s.Plc.LinkUp || s.Plc.HeartbeatOk || s.Plc.RunEnable);
        Assert.False(s.PlcConnected);
    }

    [Fact]
    public async Task No_PLC_set_up_sends_no_PLC_state()
    {
        await using var f = await RuntimeFixture.StartAsync();
        Assert.Null((await f.Runtime().GetStatusAsync(new GetStatusRequest())).Plc);
    }
}
