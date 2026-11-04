using System.Security.Cryptography;
using System.Text;
using BarcodeReading;
using BarcodeReading.Dashboard.Services;
using BarcodeReading.Protection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StationGuard;
using VisionCore.Licensing;
using Xunit;

// Requirements 18.3 ENC-HOST-01: a station that needs a licence and has no content key does not start (NG, told).
public sealed class StationLicenceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("dash-licence-").FullName;
    readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    (Station Station, CaseLog Log) Make(HostLicence licence)
    {
        var opt = new StationOptions
        {
            ProductsFolder = Path.Combine(_dir, "products"), ModelsFolder = Path.Combine(_dir, "models"),
            DataFolder = Path.Combine(_dir, "data"), ReplayFolder = _dir,
        };
        Directory.CreateDirectory(opt.ProductsFolder);
        var log = new CaseLog(opt.DataFolder);
        var guard = new ResourceGuard(new ProcessProbe(opt.DataFolder));
        var images = new NgImageStore(new NgImageOptions { Folder = Path.Combine(opt.DataFolder, "hard-cases"), Mode = SaveMode.None }, () => Pressure.Normal);
        _owned.Add(guard);
        _owned.Add(images);
        var station = new Station(Options.Create(opt), new ProductStore(opt.ProductsFolder), log, new DuplicateGuard(log, opt.DataFolder),
            NullLogger<Station>.Instance, guard, images, licence);
        _owned.Add(station);
        return (station, log);
    }

    [Fact]
    public void WithoutTheKeyTheStationRefusesToStartAndSaysWhy()
    {
        // a PC set to need a licence, but no licence answer has been kept here
        var keys = new StoredLicenceKeySource(Path.Combine(_dir, "licence"), "-----BEGIN PUBLIC KEY-----", null);
        var (station, log) = Make(new HostLicence(keys));

        var e = Assert.Throws<InvalidOperationException>(() => station.Start("P-24", SourceKind.Replay, "op"));
        Assert.Contains("refused (NG)", e.Message);
        Assert.Contains("licence content key not available", e.Message);
        var snap = station.Snapshot();
        Assert.False(snap.Running);
        Assert.StartsWith("NG: ", snap.Error);
        Assert.Contains(log.AuditTrail(), r => r.Action == "start-refused" && r.Detail.StartsWith("P-24: NG"));
        Assert.Throws<InvalidOperationException>(() => station.Calibrate("P-24", SourceKind.Replay, "op", default, default));
        Assert.Contains(log.AuditTrail(), r => r.Action == "calibrate-refused");
    }

    [Fact]
    public void AnEncryptedProductWithNoKeySourceIsRefusedNotReadInTheClear()
    {
        var (station, log) = Make(new HostLicence(null));
        var box = ContentCipher.Encrypt(Encoding.UTF8.GetBytes("{\"ProductCode\":\"P-24\"}"), RandomNumberGenerator.GetBytes(ContentCipher.KeySize), "P-24.json");
        File.WriteAllBytes(Path.Combine(_dir, "products", "P-24.json"), box);

        var e = Assert.Throws<InvalidOperationException>(() => station.Start("P-24", SourceKind.Replay, "op"));
        Assert.Contains("refused (NG)", e.Message);
        Assert.False(station.Snapshot().Running);
        Assert.Contains(log.AuditTrail(), r => r.Action == "start-refused");
    }
}
