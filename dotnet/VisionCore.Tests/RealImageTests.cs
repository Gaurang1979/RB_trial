using VisionCore;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Runs only when VS_IMAGES points at a folder of stored images (customer data is not in the repository):
/// the Harpic job with and without Find the part, text area drawn tight round the print on the first image.
///   VS_IMAGES="/path/Camera 1/Fail" VS_MODELS=../../ml/models dotnet test --filter RealImages
/// </summary>
public class RealImageTests
{
    private readonly ITestOutputHelper _out;
    public RealImageTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void RealImages()
    {
        var folder = Environment.GetEnvironmentVariable("VS_IMAGES");
        var models = Environment.GetEnvironmentVariable("VS_MODELS");
        if (folder is null || models is null) return;
        var files = FolderSource.List(folder);
        var first = GrayImage.Load(files[0]);
        TextToolConfig Text() => new()
        {
            Name = "Print", Roi = new RoiBox(470, 760, 1060, 330), CharHeightPx = 38,
            Lines = { new() { Template = "₹ {MRP}, {BATCH}" }, new() { Template = "₹ 0.23 per ml" }, new() { Template = "¥ 10/26, # 09/28 {TIME:HH:mm+-3}" } },
        };
        var batch = new BatchFields();
        batch.Values["MRP"] = "46.00";
        batch.Values["BATCH"] = "HWN881";
        var loc = new LocateToolConfig { Name = "Find", Roi = new RoiBox(500, 740, 1000, 360), MaxAngleDeg = 10 };
        loc.Teach(first);
        foreach (var (name, job) in new[]
        {
            ("tight area, no fixture", new Job { Name = "a", Tools = { Text() } }),
            ("tight area + Find the part", new Job { Name = "b", Tools = { loc, Text() } }),
        })
        {
            using var runner = new JobRunner(job, new ModelPaths(models));
            int ok = 0, n = 0, notFound = 0;
            var ms = new List<double>();
            foreach (var f in files)
            {
                GrayImage img;
                try { img = GrayImage.Load(f); } catch { continue; }
                var r = runner.Run(0, img, batch);
                n++;
                if (r.Pass) ok++;
                if (r.Tools.Any(t => t.Explain == "explain.locate.not_found")) notFound++;
                ms.Add(r.TotalMs);
            }
            ms.Sort();
            _out.WriteLine($"{name}: OK {ok} / {n} (part not found {notFound}), median {ms[ms.Count / 2]:0} ms, p95 {ms[(int)(ms.Count * 0.95)]:0} ms");
        }
    }
}
