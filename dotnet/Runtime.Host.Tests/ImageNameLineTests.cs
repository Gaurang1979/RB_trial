using System.Text.Json;
using Grpc.Core;
using StationGuard;
using VisionStudio.Remote.V1;
using Xunit;

/// <summary>The image naming pattern on the line (owner, 2026-10-31): the runtime's image ids are the saved files'
/// names, in the line/date/job/camera/verdict folders, with the line number setting; GetImage opens a saved image by name.</summary>
public sealed class ImageNameLineTests : IAsyncLifetime
{
    // An NG image saved the day before this runtime started: GetImage finds it by name from the NG image folder.
    // Dated from today, so it never ages out of what the station keeps.
    static readonly ImageName Earlier = new(3, 1, 7, DateTime.Today.AddDays(-1).AddHours(8), 5);
    RuntimeFixture _f = null!;
    Metadata _op = null!;

    public async Task InitializeAsync()
    {
        _f = await RuntimeFixture.StartAsync(f =>
        {
            var dir = Earlier.Folder(Path.Combine(f.Folder, "data", "ng-images"), false);
            Directory.CreateDirectory(dir);
            var px = new byte[40 * 30];
            Array.Fill(px, (byte)77);
            BmpGray.Write(Path.Combine(dir, Earlier + ".bmp"), px, 40, 30);
            new VisionCore.JobStore(Path.Combine(f.Folder, "jobs")).Save(new VisionCore.Job
            {
                Name = "Two",
                Cameras = [new VisionCore.CameraConfig { Name = "Top", TestFolder = f.FramesFolder }, new VisionCore.CameraConfig { Name = "Side", TestFolder = f.FramesFolder }],
                Tools =
                [
                    new VisionCore.CodeToolConfig { Name = "Top code", Camera = 0, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 },
                    new VisionCore.CodeToolConfig { Name = "Side code", Camera = 1, Format = BarcodeReading.CodeFormat.QrCode, ExpectedCount = 1 },
                ],
            }, "test");
        }, "--Studio:LineNumber=3");
        _op = await _f.SignInOperator();
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task Image_ids_are_the_saved_files_names()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = _f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var results = new List<InspectionResult>();
        await foreach (var r in call.ResponseStream.ReadAllAsync(cts.Token)) { results.Add(r); if (results.Count == 3) break; }
        await _f.Runtime().StopAsync(new StopRequest(), _op);

        var names = results.Select(r => r.ImageIds.Single()).ToList();
        foreach (var id in names)
        {
            Assert.True(ImageName.TryParse(id, out var n), id);
            Assert.Equal((3, 1, 7), (n.Line, n.Camera, n.Job));   // line setting, camera 1, the job's PLC number 7
        }
        var serials = names.Select(id => { ImageName.TryParse(id, out var n); return n.Serial; }).ToList();
        Assert.Equal(serials.Order(), serials);
        Assert.Equal(serials.Count, serials.Distinct().Count());

        // every part is NG: its image is saved under its name in the line/date/job/camera/NG folder
        var root = Path.Combine(_f.Folder, "data", "ng-images");
        ImageName.TryParse(names[^1], out var last);
        var bmp = Path.Combine(last.Folder(root, false), names[^1] + ".bmp");
        for (var i = 0; i < 200 && !File.Exists(Path.ChangeExtension(bmp, ".json")); i++) await Task.Delay(20);
        Assert.True(File.Exists(bmp), bmp);
        var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp, ".json"))).RootElement;
        Assert.Equal(names[^1], meta.GetProperty("image_id").GetString());
        Assert.Equal("Labels", meta.GetProperty("product").GetString());
        Assert.Equal(7, meta.GetProperty("job_number").GetInt32());
        Assert.False(meta.GetProperty("ok").GetBoolean());
        Assert.Contains(".", meta.GetProperty("time").GetString());   // milliseconds and the offset are in the .json
        Assert.True(meta.TryGetProperty("time_zone", out _));
    }

    [Fact]
    public async Task Both_cameras_of_a_part_differ_only_in_the_camera_and_a_job_without_a_number_is_J000()
    {
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Two" }, _op)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = _f.Runtime().StreamResults(new StreamResultsRequest(), cancellationToken: cts.Token);
        await Task.Delay(100);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest { Source = Source.Replay }, _op)).Ok);
        var results = new List<InspectionResult>();
        await foreach (var r in call.ResponseStream.ReadAllAsync(cts.Token)) { results.Add(r); if (results.Count == 2) break; }
        await _f.Runtime().StopAsync(new StopRequest(), _op);
        foreach (var r in results)
        {
            Assert.Equal(2, r.ImageIds.Count);
            Assert.True(ImageName.TryParse(r.ImageIds[0], out var a));
            Assert.True(ImageName.TryParse(r.ImageIds[1], out var b));
            Assert.Equal((3, 1, 0), (a.Line, a.Camera, a.Job));
            Assert.Equal(a.WithCamera(2), b);
        }
    }

    [Fact]
    public async Task GetImage_opens_a_saved_image_by_its_name()
    {
        var chunks = new List<ImageChunk>();
        await foreach (var c in _f.Runtime().GetImage(new GetImageRequest { ImageId = Earlier.ToString(), Encoding = GetImageRequest.Types.Encoding.Raw }).ResponseStream.ReadAllAsync())
            chunks.Add(c);
        Assert.Equal((40, 30, "gray8"), (chunks[0].Width, chunks[0].Height, chunks[0].Format));
        Assert.All(chunks.SelectMany(c => c.Data), b => Assert.Equal(77, b));

        // the other camera of that part was never saved: not found, never camera 1's picture
        var e = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in _f.Runtime().GetImage(new GetImageRequest { ImageId = Earlier.WithCamera(2).ToString() }).ResponseStream.ReadAllAsync()) { }
        });
        Assert.Equal(StatusCode.NotFound, e.StatusCode);
    }
}
