using System.Text.Json;
using StationGuard;
using Xunit;

/// <summary>The image naming pattern (owner, 2026-10-31): build, parse, old forms, folders, the part serial and the store.</summary>
public sealed class ImageNameTests : IDisposable
{
    static readonly DateTime At = new(2026, 10, 31, 10, 45, 12, 345);
    readonly string _dir = Directory.CreateTempSubdirectory("img-name-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void TheOwnersExampleIsBuiltAndReadBack()
    {
        var n = new ImageName(1, 2, 45, At, 123);
        Assert.Equal("L01_C2_J045_261031-104512_000123", n.ToString());
        Assert.Equal(ImageName.Length, n.ToString().Length);
        Assert.Matches("^[A-Z0-9_-]{32}$", n.ToString());
        Assert.True(ImageName.TryParse(n.ToString(), out var back));
        Assert.Equal(n, back);
        Assert.Equal((1, 2, 45, 123), (back.Line, back.Camera, back.Job, back.Serial));
        Assert.Equal(new DateTime(2026, 10, 31, 10, 45, 12), back.Local);   // milliseconds go to the .json, not the name
    }

    [Fact]
    public void BothCamerasOfAPartDifferOnlyInTheCamera()
    {
        var c1 = new ImageName(3, 1, 0, At, 999999);
        var c2 = c1.WithCamera(2);
        Assert.Equal("L03_C1_J000_261031-104512_999999", c1.ToString());
        Assert.Equal("L03_C2_J000_261031-104512_999999", c2.ToString());
        Assert.Equal(c1.PartKey, c2.PartKey);
        Assert.Equal(c1.PartKey, ImageName.KeyOf(c2.ToString()));
    }

    [Fact]
    public void TheFolderIsLineDateJobCameraAndVerdict()
    {
        var n = new ImageName(1, 1, 45, At, 1);
        Assert.Equal(Path.Combine("root", "L01", "2026-10-31", "J045", "C1", "OK"), n.Folder("root", true));
        Assert.Equal(Path.Combine("root", "L01", "2026-10-31", "J045", "C1", "NG"), n.Folder("root", false));
    }

    [Theory]
    [InlineData(0, 1, 1, 1)] [InlineData(100, 1, 1, 1)] [InlineData(1, 0, 1, 1)] [InlineData(1, 10, 1, 1)]
    [InlineData(1, 1, -1, 1)] [InlineData(1, 1, 1000, 1)] [InlineData(1, 1, 1, 0)] [InlineData(1, 1, 1, 1_000_000)]
    public void ValuesOutsideTheFixedWidthAreRefused(int line, int camera, int job, int serial) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageName(line, camera, job, At, serial));

    [Theory]
    [InlineData("")]
    [InlineData("L01_C2_J045_261031-104512_00012")]      // short
    [InlineData("L01_C2_J045_261031-104512_0001234")]    // long
    [InlineData("l01_C2_J045_261031-104512_000123")]     // lower case
    [InlineData("L01_C2_J045_261331-104512_000123")]     // month 13
    [InlineData("L01_C2_J045_261031-104512_000000")]     // serial 0
    [InlineData("L00_C2_J045_261031-104512_000123")]     // line 0
    [InlineData("L01-C2_J045_261031-104512_000123")]
    [InlineData("L01_C2_J04A_261031-104512_000123")]
    public void OtherTextIsNotAName(string s) => Assert.False(ImageName.TryParse(s, out _));

    [Fact]
    public void OldFormsStillParse()
    {
        var id = ImageName.ParseAny("20261030T101530123Z-000042");
        Assert.Equal(ImageNameForm.OldStationId, id.Form);
        Assert.Equal("20261030T101530123Z-000042", id.Key);
        Assert.Equal(42, id.Serial);
        Assert.Equal(new DateTime(2026, 10, 30, 10, 15, 30, 123, DateTimeKind.Utc).ToLocalTime(), id.Time);

        var file = ImageName.ParseAny("20261030T101530123Z-000042_cam1");
        Assert.Equal((ImageNameForm.OldStationId, "20261030T101530123Z-000042", "cam1"), (file.Form, file.Key, file.Camera));

        var noId = ImageName.ParseAny("101530_123_no_code", new DateTime(2026, 10, 29));
        Assert.Equal((ImageNameForm.OldTimeReason, "no_code"), (noId.Form, noId.Reason));
        Assert.Equal(new DateTime(2026, 10, 29, 10, 15, 30, 123), noId.Time);

        var current = ImageName.ParseAny("L01_C2_J045_261031-104512_000123");
        Assert.Equal((ImageNameForm.Current, "C2", 123), (current.Form, current.Camera, current.Serial));
        Assert.Equal("L01_J045_261031-104512_000123", current.Key);

        var other = ImageName.ParseAny("12-0");
        Assert.Equal((ImageNameForm.Other, "12-0"), (other.Form, other.Key));
        Assert.Equal("12-0", ImageName.KeyOf("12-0"));
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
    }

    [Fact]
    public void TheSerialCountsTheDayAndStartsAgainAtLocalMidnight()
    {
        var ist = TimeSpan.FromHours(5.5);
        var clock = new Clock(new DateTimeOffset(2026, 10, 31, 23, 59, 58, ist));
        using var s = new PartSerial(null, clock);
        Assert.Equal(1, s.Next().Serial);
        Assert.Equal(2, s.Next().Serial);
        Assert.Equal(1, s.Next("C2").Serial);   // a camera fired on its own has its own count
        var (n, local) = s.Next();
        Assert.Equal(3, n);
        Assert.Equal(ist, local.Offset);
        clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 1, ist);   // 18:30 UTC: a new local day
        Assert.Equal(1, s.Next().Serial);
        Assert.Equal(1, s.Next("C2").Serial);
    }

    [Fact]
    public void TheSerialIsKeptAcrossARestartTheSameDayOnly()
    {
        var file = Path.Combine(_dir, "part-serial.json");
        var clock = new Clock(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.FromHours(5.5)));
        using (var s = new PartSerial(file, clock))
            for (var i = 0; i < 41; i++) s.Next();
        using (var s = new PartSerial(file, clock))
            Assert.Equal(42, s.Next().Serial);
        clock.Now = clock.Now.AddDays(1);
        using (var s = new PartSerial(file, clock))
            Assert.Equal(1, s.Next().Serial);
    }

    [Fact]
    public void ADamagedSerialFileStartsAt1()
    {
        var file = Path.Combine(_dir, "part-serial.json");
        File.WriteAllText(file, "{not json");
        using var s = new PartSerial(file);
        Assert.Equal(1, s.Next().Serial);
    }

    static byte[] Img(int w = 32, int h = 24) { var b = new byte[w * h]; for (var i = 0; i < b.Length; i++) b[i] = (byte)i; return b; }

    static NgImageLookup Wait(NgImageStore s, string id)
    {
        var f = s.Find(id);
        for (var i = 0; i < 500 && (f.State == NgImageState.Queued || s.Counts.QueuedBytes > 0); i++) { Thread.Sleep(10); f = s.Find(id); }
        return f;
    }

    [Fact]
    public void TheStoreSavesNamedImagesInTheirFoldersAndAnyCamerasNameFindsThePart()
    {
        var c1 = new ImageName(1, 1, 45, At, 123);
        var c2 = c1.WithCamera(2);
        using (var s = new NgImageStore(new NgImageOptions { Folder = _dir }))
        {
            Assert.True(s.KeepImage("Whisky-top", false, ["no_code"], Img(), 32, 24, camera: "top", name: c1,
                meta: new Dictionary<string, object?> { ["product"] = "Whisky" }));
            Assert.True(s.KeepImage("Whisky-side", false, ["smudge"], Img(), 32, 24, camera: "side", name: c2));
            var f = Wait(s, c2.ToString());
            Assert.Equal(NgImageState.Found, f.State);
            Assert.Equal(["side", "top"], f.Files.Select(x => x.Camera).Order().ToArray());
        }
        var bmp1 = Path.Combine(_dir, "L01", "2026-10-31", "J045", "C1", "NG", "L01_C1_J045_261031-104512_000123.bmp");
        var bmp2 = Path.Combine(_dir, "L01", "2026-10-31", "J045", "C2", "NG", "L01_C2_J045_261031-104512_000123.bmp");
        Assert.True(File.Exists(bmp1));
        Assert.True(File.Exists(bmp2));
        var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp2, ".json"))).RootElement;
        Assert.Equal(c2.ToString(), meta.GetProperty("image_id").GetString());   // the image id is its name
        Assert.False(meta.GetProperty("ok").GetBoolean());
        Assert.Equal("Whisky", JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(bmp1, ".json"))).RootElement.GetProperty("product").GetString());

        // after a restart the folder is indexed again, also a named file whose .json is gone
        File.Delete(Path.ChangeExtension(bmp2, ".json"));
        using var again = new NgImageStore(new NgImageOptions { Folder = _dir });
        Assert.Equal(2, again.Find(c1.ToString()).Files.Count);
        Assert.Equal(2, again.Find(c2.ToString()).Files.Count);
    }

    [Fact]
    public void OldFilesStayWhereTheyAreAndAreStillFound()
    {
        const string old = "20261030T101530123Z-000042";
        var dir = Path.Combine(_dir, "2026-10-30", "J-cam1");
        Directory.CreateDirectory(dir);
        BmpGray.Write(Path.Combine(dir, old + "_cam1.bmp"), Img(), 32, 24);
        File.WriteAllText(Path.Combine(dir, old + "_cam1.json"), JsonSerializer.Serialize(new { image_id = old, camera = "cam1", job = "J-cam1" }));
        using var s = new NgImageStore(new NgImageOptions { Folder = _dir });
        var f = s.Find(old);
        Assert.Equal(NgImageState.Found, f.State);
        Assert.Equal(Path.Combine(dir, old + "_cam1.bmp"), f.Files.Single().Path);
    }
}
