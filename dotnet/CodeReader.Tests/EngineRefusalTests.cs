using System;
using System.IO;
using System.Runtime.InteropServices;
using BarcodeReading;
using Xunit;

namespace BarcodeReading.Tests;

/// <summary>P/Invoke against the built engine (engine/build): a run without the library built passes nothing
/// here, so the pull request's "Verified" must say the engine was built (Req 17.1 ENG-06).</summary>
public class EngineRefusalTests
{
    private static bool EngineBuilt()
    {
        string lib = OperatingSystem.IsWindows() ? "codereader.dll" : OperatingSystem.IsMacOS() ? "libcodereader.dylib" : "libcodereader.so";
        return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, lib), out var h) && Free(h);
    }
    private static bool Free(IntPtr h) { NativeLibrary.Free(h); return true; }

    private static LensCalibration Lens(float rms) => new(2400, 2400, 960, 540, -0.1f, 0.02f, 0, 0, 0, 0.125f, rms);

    [Fact]
    public void RefusalCountsAreReadFromTheEngine()
    {
        if (!EngineBuilt()) return;
        using var reader = new CodeReader(new ReaderSettings());
        var px = new byte[640 * 480];
        Array.Fill(px, (byte)128);
        var stats = reader.Decode(px, 640, 480, 640).Stats;
        // the grown struct_size is honoured, and nothing was refused on a blank picture
        Assert.Equal(0, stats.RefusedEan);
        Assert.Equal(0, stats.RefusedAddon);
        Assert.Equal(0, stats.Codes);
    }

    [Fact]
    public void ALensOverTheRmsLimitIsReportedNotIgnored()
    {
        if (!EngineBuilt()) return;
        using var reader = new CodeReader(new ReaderSettings());
        var e = Assert.Throws<InvalidOperationException>(() => reader.SetLens(Lens(0.9f)));
        Assert.Contains("0.5 px", e.Message);
        Assert.Throws<InvalidOperationException>(() => reader.SetLens(Lens(float.NaN)));
    }

    [Fact]
    public void AGoodLensAndAScaleOnlyLensAreAccepted()
    {
        if (!EngineBuilt()) return;
        using var reader = new CodeReader(new ReaderSettings());
        reader.SetLens(Lens(0.4f));
        reader.SetLens(new LensCalibration(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.125f, -1)); // scale only
    }

    [Fact]
    public void ApplyToPassesOnARefusedStoredLens()
    {
        if (!EngineBuilt()) return;
        using var reader = new CodeReader(new ReaderSettings());
        var folder = Path.Combine(Path.GetTempPath(), "grading-" + Guid.NewGuid().ToString("N"));
        Assert.Null(new GradingCalibration { Lens = Lens(0.3f) }.ApplyTo(reader, folder));
        var why = new GradingCalibration { Lens = Lens(0.8f) }.ApplyTo(reader, folder);
        Assert.NotNull(why);
        Assert.Contains("0.5 px", why);
        Assert.Null(new GradingCalibration().ApplyTo(reader, folder));
    }
}
