using BarcodeReading;
using VisionCore;
using VisionStudio.Services;
using Xunit;

public sealed class NgTeachLinkTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ngteach-" + Guid.NewGuid().ToString("N")[..8]);

    public NgTeachLinkTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string Saved(string rel, string? label = null)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, [0]);
        if (label is not null) File.WriteAllText(Path.ChangeExtension(p, ".json"), $"{{\"job\":\"{label}\",\"reasons\":[\"code\"]}}");
        return p;
    }

    [Fact]
    public void ALinkNamesThePictureUnderTheNgFolderAndResolvesBackToIt()
    {
        var p = Saved("2026-10-30/Strip_A-Top/101500_123_code.bmp");
        var href = NgTeachLink.Href(_root, p);
        Assert.Equal("teach?ng=2026-10-30%2FStrip_A-Top%2F101500_123_code.bmp", href);
        Assert.Equal(Path.GetFullPath(p), NgTeachLink.Resolve(_root, Uri.UnescapeDataString(href!["teach?ng=".Length..])));
        Assert.Null(NgTeachLink.Href(_root, Path.Combine(Path.GetTempPath(), "other.bmp")));
    }

    [Fact]
    public void ALinkOpensNothingOutsideTheNgFolderNorAnythingButAKeptBmp()
    {
        Saved("2026-10-30/J-C/a.bmp");
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-x.bmp");
        File.WriteAllBytes(outside, [0]);
        try
        {
            Assert.Null(NgTeachLink.Resolve(_root, "../" + Path.GetFileName(outside)));
            Assert.Null(NgTeachLink.Resolve(_root, "2026-10-30/../../" + Path.GetFileName(outside)));
            Assert.Null(NgTeachLink.Resolve(_root, outside));                       // rooted
            Assert.Null(NgTeachLink.Resolve(_root, "2026-10-30\\..\\..\\x.bmp"));
            Assert.Null(NgTeachLink.Resolve(_root, "2026-10-30/J-C/a.json"));      // not a picture
            Assert.Null(NgTeachLink.Resolve(_root, "2026-10-30/J-C/gone.bmp"));    // trimmed away
            Assert.Null(NgTeachLink.Resolve(_root, ""));
            Assert.Null(NgTeachLink.Resolve(_root, null));
            Assert.NotNull(NgTeachLink.Resolve(_root, "2026-10-30/J-C/a.bmp"));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void ThePictureOpensInTheJobAndCameraThatSavedIt()
    {
        // the line labels images "<job>-<camera>"; a job named with a dash must still match its own camera
        var a = new Job { Name = "Strip" };
        a.Cameras[0].Name = "A-Top";
        var b = new Job { Name = "Strip-A" };
        b.Cameras[0].Name = "Side";
        b.Cameras.Add(new CameraConfig { Name = "Top" });
        Job[] jobs = [a, b];

        var p = Saved("2026-10-30/Strip-A-Top/x.bmp", "Strip-A-Top");
        var label = NgTeachLink.Label(p);
        Assert.Equal("Strip-A-Top", label);
        var owner = NgTeachLink.Owner(label, jobs);
        Assert.NotNull(owner);
        Assert.Same(a, owner.Value.Job);   // first exact match in job order
        Assert.Equal(0, owner.Value.Camera);

        Assert.Equal((b, 1), NgTeachLink.Owner("Strip-A-Top", [b]));
        Assert.Null(NgTeachLink.Owner("Gone-Top", jobs));
        Assert.Null(NgTeachLink.Owner(NgTeachLink.Label(Saved("2026-10-30/J/no-meta.bmp")), jobs));
    }
}
