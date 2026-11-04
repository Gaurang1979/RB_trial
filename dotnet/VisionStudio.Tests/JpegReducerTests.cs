using SkiaSharp;
using StationAgent;
using StationGuard;
using VisionStudio.Services;
using Xunit;

public sealed class JpegReducerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("jpeg-reducer-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static byte[] Noise(int w, int h, int seed = 7)
    {
        var b = new byte[w * h];
        new Random(seed).NextBytes(b);
        return b;
    }

    static (int W, int H) Size(byte[] jpeg)
    {
        using var bmp = SKBitmap.Decode(jpeg) ?? throw new InvalidDataException("not a JPEG");
        return (bmp.Width, bmp.Height);
    }

    [Fact]
    public void ALargeNoisyImageStaysUnder300KbAndKeepsItsAspect()
    {
        var jpeg = JpegReducer.Reduce(Noise(5472, 3648), 5472, 3648);
        Assert.True(jpeg.Length <= JpegReducer.MaxBytes, $"{jpeg.Length} bytes");
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, jpeg[..3]);
        var (w, h) = Size(jpeg);
        Assert.InRange(w, 1, JpegReducer.MaxLongSide);
        Assert.InRange((double)w / h, 1.5 - 0.02, 1.5 + 0.02);
    }

    [Fact]
    public void AnImageThatFitsKeepsItsSizeAndABigOneIsCutToTheLongSideLimit()
    {
        var small = new byte[640 * 480];
        for (var i = 0; i < small.Length; i++) small[i] = (byte)(i % 640 / 3);   // a smooth gradient
        Assert.Equal((640, 480), Size(JpegReducer.Reduce(small, 640, 480)));
        var tall = new byte[2000 * 4000];
        for (var i = 0; i < tall.Length; i++) tall[i] = (byte)(i / 2000 / 20);
        var (w, h) = Size(JpegReducer.Reduce(tall, 2000, 4000));
        Assert.Equal((800, 1600), (w, h));
    }

    [Fact]
    public void ATinyBudgetShrinksTheImageInsteadOfFailing()
    {
        var jpeg = JpegReducer.Reduce(Noise(800, 600), 800, 600, maxBytes: 20_000);
        Assert.True(jpeg.Length <= 20_000, $"{jpeg.Length} bytes");
        Assert.True(Size(jpeg).W < 800);
    }

    [Fact]
    public void PixelsThatDoNotFillTheImageAreRefused()
        => Assert.Throws<ArgumentException>(() => JpegReducer.Reduce(new byte[10], 64, 48));

    [Fact]
    public async Task TheSourceAnswersQueuedMissingAndFoundFromTheSavedBmp()
    {
        using var store = new NgImageStore(new NgImageOptions { Folder = _dir });
        var source = new NgImageSource(store);
        Assert.Equal(ImageState.Missing, (await source.GetAsync("nope", default)).State);
        var px = Noise(300, 200);
        Assert.True(store.KeepImage("J-cam1", false, ["x"], px, 300, 200, imageId: "20261030T101530123Z-000001", camera: "cam1"));
        Assert.True(store.KeepImage("J-cam2", false, ["x"], px, 300, 200, imageId: "20261030T101530123Z-000001", camera: "cam2"));
        ImageAnswer a = ImageAnswer.NotYet;
        for (var i = 0; i < 300 && a.State == ImageState.NotYet; i++) { a = await source.GetAsync("20261030T101530123Z-000001", default); if (a.State == ImageState.NotYet) await Task.Delay(10); }
        Assert.Equal(ImageState.Found, a.State);
        Assert.Equal(["cam1", "cam2"], a.Images.Select(x => x.Camera).Order().ToArray());
        Assert.All(a.Images, x => Assert.Equal((300, 200), Size(x.Jpeg)));
        // under pressure the source asks to be tried later and reads nothing
        Assert.Equal(ImageState.NotYet, (await new NgImageSource(store, () => true).GetAsync("20261030T101530123Z-000001", default)).State);
    }
}
