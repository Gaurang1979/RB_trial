using StationGuard;
using VisionStudio.Services;
using Xunit;

/// <summary>The Run page's NG image list in the naming pattern's folders (owner, 2026-10-31) and the older day folders,
/// and Teach links to an image in the new folders.</summary>
public sealed class NgImageListTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "nglist-" + Guid.NewGuid().ToString("N")[..8]);

    public NgImageListTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string Saved(string dir, string stem, string? json = null)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, stem + ".bmp");
        File.WriteAllBytes(p, [0]);
        if (json is not null) File.WriteAllText(Path.ChangeExtension(p, ".json"), json);
        return p;
    }

    [Fact]
    public void BothLayoutsAreListedNewestFirstWithoutOkImages()
    {
        var c1 = new ImageName(1, 1, 45, new DateTime(2026, 10, 31, 10, 45, 12), 123);
        var newNg = Saved(c1.Folder(_root, false), c1.ToString(), """{"job":"Whisky-Top","reasons":["Read code"]}""");
        var c2 = c1.WithCamera(2);
        Saved(c2.Folder(_root, false), c2.ToString());   // no .json: named from the file name
        var ok = new ImageName(1, 1, 45, new DateTime(2026, 10, 31, 11, 0, 0), 124);
        Saved(ok.Folder(_root, true), ok.ToString());     // an OK image (save mode All): not an NG image
        var oldNoId = Saved(Path.Combine(_root, "2026-10-30", "Whisky-Top"), "101530_123_no_code");
        var oldId = Saved(Path.Combine(_root, "2026-10-30", "Whisky-Side"), "20261030T061530123Z-000042_Side", """{"job":"Whisky-Side","reasons":["smudge"]}""");

        var list = NgImageList.Newest(_root, 10);
        Assert.Equal(4, list.Count);
        Assert.DoesNotContain(list, f => f.Name.StartsWith(ok.ToString(), StringComparison.Ordinal));
        Assert.Equal(new DateTime(2026, 10, 31, 10, 45, 12), list[0].At);
        var n = list.Single(f => f.Path == newNg);
        Assert.Equal(("Read code", "Whisky-Top"), (n.Reason, n.Where));
        Assert.Equal("J045 C2", list.Single(f => f.Name == c2 + ".bmp").Where);
        var o = list.Single(f => f.Path == oldNoId);
        Assert.Equal(("no_code", "Whisky-Top", new DateTime(2026, 10, 30, 10, 15, 30, 123)), (o.Reason, o.Where, o.At));
        Assert.Equal("smudge", list.Single(f => f.Path == oldId).Reason);

        Assert.Equal(2, NgImageList.Newest(_root, 2).Count);   // stops once it has enough
    }

    [Fact]
    public void TeachOpensAnImageInTheNewFoldersWithItsJobAndCamera()
    {
        var name = new ImageName(2, 1, 7, new DateTime(2026, 10, 31, 9, 0, 0), 1);
        var p = Saved(name.Folder(_root, false), name.ToString(), """{"job":"Labels-Top","reasons":["x"]}""");
        var href = NgTeachLink.Href(_root, p)!;
        Assert.Equal("teach?ng=" + Uri.EscapeDataString($"L02/2026-10-31/J007/C1/NG/{name}.bmp"), href);
        var path = NgTeachLink.Resolve(_root, Uri.UnescapeDataString(href["teach?ng=".Length..]));
        Assert.Equal(Path.GetFullPath(p), path);
        var job = new VisionCore.Job { Name = "Labels", Cameras = [new VisionCore.CameraConfig { Name = "Top" }] };
        Assert.Equal(0, NgTeachLink.Owner(NgTeachLink.Label(path!), [job])!.Value.Camera);
    }
}
