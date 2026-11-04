using System;
using System.IO;
using System.Linq;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

public class ProductStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "products-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ProductMaster Sample(int count = 48) => new()
    {
        ProductCode = "LIQ-750-48",
        Name = "Whisky 750 ml",
        ChildFormat = CodeFormat.QrCode,
        CodesPerCase = count,
        Gtin = "08901234567890",
        ChildCamera = new CameraSettings { ExposureUs = 3500, GainDb = 2, Roi = new RoiSettings { X = 400, Y = 200, Width = 4600, Height = 3200 } },
    };

    [Fact]
    public void SaveAndReloadKeepsCameraSettings()
    {
        var store = new ProductStore(_dir);
        store.Save(Sample(), "op1");

        var p = new ProductStore(_dir).Get("LIQ-750-48")!;
        Assert.Equal(48, p.CodesPerCase);
        Assert.Equal(3500, p.ChildCamera.ExposureUs);
        Assert.Equal(4600, p.ChildCamera.Roi!.Width);
        Assert.Equal(1, p.Version);
        Assert.Equal("op1", p.UpdatedBy);
    }

    [Fact]
    public void OtherJsonInTheFolderIsNotAProduct()
    {
        var store = new ProductStore(_dir);
        store.Save(Sample(), "op1");
        File.WriteAllText(Path.Combine(_dir, "LIQ-750-48.tuning.json"), "{\"frames\": 3, \"arms\": {}}");
        Assert.Equal(new[] { "LIQ-750-48" }, store.List().Select(p => p.ProductCode));
    }

    [Fact]
    public void EverySaveIsKeptInHistory()
    {
        var store = new ProductStore(_dir);
        var p = store.Save(Sample(), "op1");
        p.ChildCamera.ExposureUs = 4000;
        store.Save(p, "op2");

        var history = store.History("LIQ-750-48");
        Assert.Equal(2, history.Count);
        Assert.Equal(3500, history[0].ChildCamera.ExposureUs);
        Assert.Equal(4000, history[1].ChildCamera.ExposureUs);
        Assert.Equal(2, store.Get("LIQ-750-48")!.Version);
    }

    [Fact]
    public void ReaderSettingsComeFromTheProduct()
    {
        var s = Sample(96).ChildReaderSettings();
        Assert.Equal(96, s.ExpectedCount);
        Assert.Equal(CodeFormat.QrCode, s.Format);
        Assert.Equal((400, 200, 4600, 3200), s.Roi);
        Assert.Equal(1, Sample().ParentReaderSettings().ExpectedCount);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(24, true)]
    [InlineData(45, true)]
    [InlineData(48, true)]
    [InlineData(96, true)]
    [InlineData(50, false)]
    public void StandardCaseSizes(int count, bool standard) => Assert.Equal(standard, Sample(count).IsStandardCaseSize);

    [Fact]
    public void RejectsInvalidProduct()
    {
        var p = Sample();
        p.Gtin = "08901234567891"; // bad check digit
        p.CodesPerCase = 0;
        Assert.Equal(2, p.Validate().Count);
        Assert.Throws<ArgumentException>(() => new ProductStore(_dir).Save(p, "op1"));
    }

    [Fact]
    public void LockedProductNeedsApproval()
    {
        var p = Sample();
        p.Mode = LearningMode.Locked;
        Assert.Throws<InvalidOperationException>(() => new ProductStore(_dir).Save(p, "op1"));
        p.ApprovedBy = "qa1";
        Assert.Equal(1, new ProductStore(_dir).Save(p, "op1").Version);
    }
}

public class BmpImageTests
{
    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bmp");
        try
        {
            int w = 7, h = 5; // odd width exercises row padding
            var px = new byte[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 7);
            BmpImage.SaveGray(path, px, w, h);
            var (back, bw, bh) = BmpImage.LoadGray(path);
            Assert.Equal((w, h), (bw, bh));
            Assert.Equal(px, back);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class TimeTargetTests
{
    [Theory]
    [InlineData(12, 100)]
    [InlineData(24, 275)]
    [InlineData(45, 500)]
    [InlineData(48, 500)]
    [InlineData(96, 600)]
    public void BudgetFollowsTheLineTargetForTheCaseSize(int codes, int ms)
    {
        var p = new ProductMaster { ProductCode = "T", CodesPerCase = codes };
        Assert.Equal(ms, p.ChildReaderSettings().TimeBudgetMs);
        Assert.True(p.ChildReaderSettings().MatchSerials);
    }

    [Fact]
    public void AnExplicitBudgetWins()
    {
        var p = new ProductMaster { ProductCode = "T", CodesPerCase = 12 };
        p.Reader.TimeBudgetMs = 250;
        Assert.Equal(250, p.ChildReaderSettings().TimeBudgetMs);
    }
}

public class BmpFormatTests
{
    [Fact]
    public void Reads32BitBmp()
    {
        // 2 x 1, 32-bit BGRA: white, black
        var data = new byte[54 + 8];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(2).CopyTo(data, 18);
        BitConverter.GetBytes(1).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)32).CopyTo(data, 28);
        new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }.CopyTo(data, 54);
        var (px, w, h) = BmpImage.DecodeGray(data);
        Assert.Equal((2, 1), (w, h));
        Assert.Equal(new byte[] { 255, 0 }, px);
    }

    [Fact]
    public void NamesAPngSavedAsBmp()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0 };
        var e = Assert.Throws<InvalidDataException>(() => BmpImage.DecodeGray(png));
        Assert.Contains("PNG", e.Message);
    }
}
