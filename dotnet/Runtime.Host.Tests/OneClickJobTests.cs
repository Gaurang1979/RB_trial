using System.Text.Json;
using Grpc.Core;
using VisionCore;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>The one-click new job's calls (GrabPicture, FindTools): a picture taken from a job camera and kept
/// with its stored pictures, and auto find's suggestions on a picture. Only an engineer in Vision Studio calls
/// them, nothing happens while the line runs, and a suggestion is never added to a job by the runtime.
/// Auto find loads the AI models and takes every core for a moment, so these run on their own, after the
/// tests that time camera frames (IntakeCameraTests waits 200 ms for a frame).</summary>
[Collection(nameof(AutoFindAlone))]
public sealed class OneClickJobTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    Metadata _op = null!, _eng = null!;

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync();
        _op = await _f.SignInOperator();
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(r.Ok, r.ErrorText);
        _eng = RuntimeFixture.Bearer(r.Token);
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    async Task<string> JobJson(string name) => (await _f.Runtime().GetJobAsync(new GetJobRequest { Name = name }, _eng)).Json;

    static async Task<StatusCode> Code(Func<Task> call)
    {
        try { await call(); return StatusCode.OK; }
        catch (RpcException e) { return e.StatusCode; }
    }

    [Fact]
    public async Task Only_an_engineer_grabs_and_finds()
    {
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime().GrabPictureAsync(new GrabPictureRequest(), _op).ResponseAsync));
        Assert.Equal(StatusCode.PermissionDenied, await Code(() => _f.Runtime().FindToolsAsync(new FindToolsRequest(), _op).ResponseAsync));
        Assert.Equal(StatusCode.Unauthenticated, await Code(() => _f.Runtime().GrabPictureAsync(new GrabPictureRequest()).ResponseAsync));
    }

    [Fact]
    public async Task A_grabbed_picture_is_kept_with_the_cameras_pictures_and_tried_like_them()
    {
        var json = await JobJson("Labels");
        var before = (await _f.Runtime().ListPicturesAsync(new ListPicturesRequest { JobJson = json, Camera = 0 }, _eng)).Names.Count;
        var g = await _f.Runtime().GrabPictureAsync(new GrabPictureRequest { JobJson = json, Camera = 0 }, _eng);
        Assert.True(g.Ok, g.ErrorText);
        Assert.StartsWith("grab-", g.Name);
        Assert.True(File.Exists(Path.Combine(_f.FramesFolder, g.Name)));
        var names = (await _f.Runtime().ListPicturesAsync(new ListPicturesRequest { JobJson = g.JobJson, Camera = 0 }, _eng)).Names;
        Assert.Equal(before + 1, names.Count);
        Assert.Contains(g.Name, names);
        var tried = await _f.Runtime().TryJobAsync(new TryJobRequest { JobJson = g.JobJson, Camera = 0, Stored = g.Name }, _eng);
        Assert.True(tried.Ok, tried.ErrorText);
        Assert.Equal((320, 240), (tried.ImageWidth, tried.ImageHeight));   // the simulator's frame, whole
        Assert.False(tried.Pass);                                           // no code on it: NG

        Assert.Equal("runtime.refused.noCamera", (await _f.Runtime().GrabPictureAsync(new GrabPictureRequest { JobJson = json, Camera = 3 }, _eng)).ErrorKey);
    }

    [Fact]
    public async Task A_camera_with_no_pictures_folder_gets_one_under_the_data_folder()
    {
        var job = new Job { Name = "New/label", Cameras = [new CameraConfig { Name = "Top" }] };
        var g = await _f.Runtime().GrabPictureAsync(new GrabPictureRequest { JobJson = JsonSerializer.Serialize(job, JobStore.Json), Camera = 0 }, _eng);
        Assert.True(g.Ok, g.ErrorText);
        var folder = JsonSerializer.Deserialize<Job>(g.JobJson, JobStore.Json)!.Cameras[0].TestFolder!;
        Assert.Equal(Path.Combine(_f.Folder, "data", "pictures", "New_label", "camera 1"), folder);
        Assert.True(File.Exists(Path.Combine(folder, g.Name)));
    }

    [Fact]
    public async Task Find_tools_answers_with_the_picture_and_never_changes_the_job()
    {
        var json = await JobJson("Labels");
        var f = await _f.Runtime().FindToolsAsync(new FindToolsRequest { JobJson = json, Camera = 0, Stored = "frame0.bmp" }, _eng);
        Assert.True(f.Ok, f.ErrorText);
        Assert.Empty(f.Suggestions);                 // a flat frame: nothing to check
        Assert.True(f.Jpeg.Length > 0);
        Assert.Equal((320, 240), (f.ImageWidth, f.ImageHeight));
        var inWindow = await _f.Runtime().FindToolsAsync(new FindToolsRequest { JobJson = json, Camera = 0, Stored = "frame0.bmp",
            Area = new Area { X = 10, Y = 10, Width = 100, Height = 80 } }, _eng);
        Assert.True(inWindow.Ok, inWindow.ErrorText);
        Assert.Equal(json, await JobJson("Labels"));

        var missing = await _f.Runtime().FindToolsAsync(new FindToolsRequest { JobJson = json, Camera = 0, Stored = "../jobs/Labels.json" }, _eng);
        Assert.Equal("runtime.refused.picture", missing.ErrorKey);   // a name from the list, never a path
    }

    [Fact]
    public async Task While_the_line_runs_nothing_is_grabbed_or_found()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        var json = await JobJson("Labels");
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        try
        {
            var g = await _f.Runtime().GrabPictureAsync(new GrabPictureRequest { JobJson = json, Camera = 0 }, _eng);
            Assert.Equal("runtime.refused.stopFirst", g.ErrorKey);
            var f = await _f.Runtime().FindToolsAsync(new FindToolsRequest { JobJson = json, Camera = 0, Stored = "frame0.bmp" }, _eng);
            Assert.Equal("runtime.refused.stopFirst", f.ErrorKey);
        }
        finally { await _f.Runtime().StopAsync(new StopRequest(), _op); }
    }
}

[CollectionDefinition(nameof(AutoFindAlone), DisableParallelization = true)]
public sealed class AutoFindAlone;
