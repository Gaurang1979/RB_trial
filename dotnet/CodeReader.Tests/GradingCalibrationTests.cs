using System;
using System.IO;
using BarcodeReading;
using Xunit;

public class GradingCalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grading-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripsEverythingItCalibrates()
    {
        var g = new GradingCalibration
        {
            WhiteCardFile = "P1_white.bmp", GreyDark = 31.5f, GreyLight = 214f, ReflDarkPct = 5f, ReflLightPct = 85f,
            Lens = new LensCalibration(2400, 2400, 960, 540, -0.1f, 0.02f, 0, 0, 0, 0.125f, 0.4f), UpdatedBy = "eng",
        };
        string path = Path.Combine(_dir, "P1.json");
        g.Save(path);
        var back = GradingCalibration.Load(path);
        Assert.True(back.HasFlatField && back.HasReflectance && back.HasScale);
        Assert.Equal(214f, back.GreyLight);
        Assert.Equal(0.125f, back.Lens!.MmPerPx);
        Assert.Equal("eng", back.UpdatedBy);
    }

    [Fact]
    public void MissingFileMeansNothingCalibrated()
    {
        var g = GradingCalibration.Load(Path.Combine(_dir, "none.json"));
        Assert.False(g.HasFlatField || g.HasReflectance || g.HasScale);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
