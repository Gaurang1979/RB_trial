using VisionCore;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;
using Xunit;
using V1 = VisionStudio.Remote.V1;
using SetupCheck = VisionSystem.Runtime.SetupCheck;

/// <summary>The setup checks at job load (requirements AS-13, AS-14, section 16 "When the picture and pixel checks
/// run"): once per load, before Run, one or two pictures per camera; never while the line runs; a check still going
/// when Run is pressed is stopped and its pictures dropped; a failed capture is said plainly; Run is never refused.</summary>
public sealed class SetupCheckTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    sealed class Line : ISetupLine
    {
        public Job? Job { get; set; }
        public bool Running { get; set; }
        public BatchFields Batch { get; } = new();
        public event Action? Changed;
        public string Source { get; set; } = "camera";
        public int Pictures;
        public ManualResetEventSlim? Hold;
        public Func<int, (GrayImage?, string?)> Next = _ => (Blank(), null);

        public void Raise() => Changed?.Invoke();

        public (GrayImage? Image, string? Error) Picture(CameraConfig camera, int shot)
        {
            Interlocked.Increment(ref Pictures);
            Hold?.Wait(Wait);
            return Next(shot);
        }
    }

    // a mid-grey picture with nothing on it: no code to measure, so the check takes a second picture
    static GrayImage Blank()
    {
        var px = new byte[320 * 240];
        Array.Fill(px, (byte)128);
        return new GrayImage(px, 320, 240);
    }

    static Job Labels(params ToolConfig[] tools) => new()
    {
        Name = "Labels",
        Cameras = [new CameraConfig { Name = "Top" }],
        Tools = tools.Length > 0 ? [.. tools] : [new CodeToolConfig { Name = "QR", Format = BarcodeReading.CodeFormat.QrCode }],
    };

    static readonly Func<Job, JobRunner> Tools = SetupCheck.ToolsOf(new ModelPaths(Path.Combine(AppContext.BaseDirectory, "models")));

    static async Task<V1.SetupCheck> Ended(SetupCheck check)
    {
        await check.Last.WaitAsync(Wait);
        return check.Current!;
    }

    [Fact]
    public async Task The_check_runs_once_after_a_load_with_one_or_two_pictures()
    {
        var line = new Line { Job = Labels() };
        using var check = new SetupCheck(line, Tools);
        Assert.Equal(V1.SetupCheck.Types.State.Checking, check.Current!.State);   // the runtime started with a job
        var done = await Ended(check);
        Assert.Equal(V1.SetupCheck.Types.State.Done, done.State);
        Assert.Equal("Labels", done.Job);
        var top = Assert.Single(done.Cameras);
        Assert.Equal(("Top", "camera", ""), (top.Camera, top.Source, top.Error));
        // the code tool found nothing on the first picture: a second one was taken, then no more
        Assert.Equal(2, top.Pictures);
        Assert.Equal(2, line.Pictures);
        Assert.Equal(["QR"], top.Unmeasured);
        Assert.Equal(5, top.PictureCheck.Zones.Count);

        // start, stop and parts raise Changed with the same job: not checked again
        line.Raise();
        line.Running = true;
        line.Raise();
        line.Running = false;
        line.Raise();
        Assert.Same(done, check.Current);
        Assert.Equal(2, line.Pictures);

        // the job loaded again (a product change, the PLC's job select): a new check
        line.Job = Labels();
        line.Raise();
        await Ended(check);
        Assert.Equal(4, line.Pictures);
    }

    [Fact]
    public async Task One_picture_is_enough_when_nothing_is_left_to_measure()
    {
        // a camera whose tools are not Read code or text (here a blob tool) needs no second picture
        var line = new Line { Job = Labels(new BlobToolConfig { Name = "Blob" }) };
        using var check = new SetupCheck(line, Tools);
        var top = Assert.Single((await Ended(check)).Cameras);
        Assert.Equal(1, top.Pictures);
        Assert.Empty(top.Unmeasured);
    }

    [Fact]
    public async Task Nothing_is_captured_while_the_line_runs()
    {
        var line = new Line { Job = Labels(), Running = true };
        using var check = new SetupCheck(line, Tools);
        line.Raise();   // parts of the running batch
        Assert.Null(check.Current);
        Assert.Equal(0, line.Pictures);

        // a job the PLC selected during the run is checked once the line is stopped, before the next Run
        line.Job = Labels();
        line.Raise();
        Assert.Equal(0, line.Pictures);
        line.Running = false;
        line.Raise();
        Assert.Equal(V1.SetupCheck.Types.State.Done, (await Ended(check)).State);
        Assert.Equal(2, line.Pictures);
    }

    [Fact]
    public async Task Run_stops_a_check_still_going_and_its_pictures_are_dropped()
    {
        using var hold = new ManualResetEventSlim(false);
        var line = new Line { Job = Labels(), Hold = hold };
        using var check = new SetupCheck(line, Tools);
        SpinWait.SpinUntil(() => line.Pictures == 1, Wait);

        // Start: the check is told first and the line starts at once, without waiting for the picture being taken
        check.Stop();
        line.Running = true;
        Assert.Equal(V1.SetupCheck.Types.State.Stopped, check.Current!.State);
        hold.Set();
        await check.Last.WaitAsync(Wait);
        // no second picture, no measure, and the first one's result is not shown
        Assert.Equal(1, line.Pictures);
        Assert.Equal(V1.SetupCheck.Types.State.Stopped, check.Current!.State);
        Assert.Empty(check.Current.Cameras);
    }

    [Fact]
    public async Task A_refused_Start_lets_the_check_run_again()
    {
        using var hold = new ManualResetEventSlim(false);
        var line = new Line { Job = Labels(), Hold = hold };
        using var check = new SetupCheck(line, Tools);
        SpinWait.SpinUntil(() => line.Pictures == 1, Wait);
        check.Stop();   // Start pressed; the station then refuses it (a batch value missing): the line stays stopped
        hold.Set();
        await check.Last.WaitAsync(Wait);
        Assert.Equal(V1.SetupCheck.Types.State.Stopped, check.Current!.State);
        check.Retry();
        Assert.Equal(V1.SetupCheck.Types.State.Done, (await Ended(check)).State);
        // a finished check is not run again by a later refused Start
        var done = check.Current;
        check.Retry();
        Assert.Same(done, check.Current);
    }

    [Fact]
    public async Task A_new_load_drops_the_check_of_the_job_before()
    {
        using var hold = new ManualResetEventSlim(false);
        var line = new Line { Job = Labels(), Hold = hold };
        using var check = new SetupCheck(line, Tools);
        SpinWait.SpinUntil(() => line.Pictures == 1, Wait);
        var first = check.Last;
        var cartons = Labels();
        cartons.Name = "Cartons";
        line.Job = cartons;
        line.Raise();
        hold.Set();
        await first.WaitAsync(Wait);
        var done = await Ended(check);
        Assert.Equal("Cartons", done.Job);
        Assert.Equal(V1.SetupCheck.Types.State.Done, done.State);
    }

    [Fact]
    public async Task A_failed_capture_is_said_plainly()
    {
        var line = new Line { Job = Labels(), Next = _ => (null, "No picture came from the camera. Check its cable and power.") };
        using var check = new SetupCheck(line, Tools);
        var top = Assert.Single((await Ended(check)).Cameras);
        Assert.Equal(0, top.Pictures);
        Assert.Equal(2, line.Pictures);   // tried twice
        Assert.Equal("No picture came from the camera. Check its cable and power.", top.Error);
        Assert.Null(top.PictureCheck);

        // a camera that throws is said the same way
        var throwing = new Line { Job = Labels(), Next = _ => throw new InvalidOperationException("Top: the camera runtime is not installed on this PC") };
        using var other = new SetupCheck(throwing, Tools);
        Assert.Equal("Top: the camera runtime is not installed on this PC", Assert.Single((await Ended(other)).Cameras).Error);
    }

    [Fact]
    public void A_job_with_no_tools_is_not_checked()
    {
        var line = new Line { Job = new Job { Name = "Empty", Cameras = [new CameraConfig { Name = "Top" }] } };
        using var check = new SetupCheck(line, Tools);
        Assert.Null(check.Current);
        Assert.Equal(0, line.Pictures);
    }

    [Fact]
    public async Task On_the_runtime_the_advice_comes_in_the_status_and_Run_is_never_refused()
    {
        await using var f = await RuntimeFixture.StartAsync();
        var op = await f.SignInOperator();
        var rt = f.Runtime();
        Assert.True((await rt.ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, op)).Ok);

        // replay: the camera's stored pictures are checked, two of them (the frames hold no code)
        var deadline = DateTime.UtcNow + Wait;
        var s = await rt.GetStatusAsync(new GetStatusRequest(), op);
        while (s.SetupCheck?.State != V1.SetupCheck.Types.State.Done && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            s = await rt.GetStatusAsync(new GetStatusRequest(), op);
        }
        Assert.Equal(V1.SetupCheck.Types.State.Done, s.SetupCheck?.State);
        var top = Assert.Single(s.SetupCheck!.Cameras);
        Assert.Equal(("Top", "stored", 2), (top.Camera, top.Source, top.Pictures));
        Assert.Equal(["Read code"], top.Unmeasured);

        // a product change and Start straight after: Start is not refused or held by the check
        Assert.True((await rt.ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, op)).Ok);
        var start = await rt.StartAsync(new StartRequest(), op);
        Assert.True(start.Ok, start.ErrorText);
        var running = await rt.GetStatusAsync(new GetStatusRequest(), op);
        Assert.NotEqual(V1.SetupCheck.Types.State.Checking, running.SetupCheck?.State ?? V1.SetupCheck.Types.State.Done);
        await rt.StopAsync(new StopRequest(), op);
    }
}
