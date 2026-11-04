namespace VisionCore;

/// <summary>
/// A camera as the user sees it: serial number and a label (the name set in the camera, e.g. the
/// white-label product name, or one the user typed). Never the maker or model: the UI must not show
/// which make of camera is fitted.
/// </summary>
public sealed record CameraInfo(string DriverKey, string Serial, string Label);

/// <summary>One open GigE / USB3 camera, fired by software (the PLC trigger comes later).</summary>
public interface ICamera : IDisposable
{
    /// <summary>Fires the camera and waits for its frame; null when no frame came in time.</summary>
    GrayImage? Grab(int timeoutMs = 2000);

    /// <summary>
    /// When true, the camera reuses a few frame buffers instead of a new one per frame (a 20 MP
    /// frame is 20 MB on the large-object heap; one per frame forces full collections). Only for an
    /// owner that is done with each image before the next grab but two, such as the station's run loop;
    /// Teach and Test keep images and leave it off.
    /// </summary>
    bool ReuseBuffers { get => false; set { } }

    /// <summary>
    /// How long a part waits for this camera's frame (requirements 12, RT-FW-01): what the job needs (exposure,
    /// transfer and a margin) for a camera that knows it, else 2 s. A part whose frame does not come in time is NG.
    /// </summary>
    int FrameWaitMs => 2000;
}

/// <summary>
/// A family of cameras behind one maker's SDK. Every setting the job needs (exposure, gain, pixel
/// format, packet size, trigger) is set by the driver: no maker software is needed on the PC.
/// Driver keys are neutral codes stored in job files, not maker names.
/// </summary>
public interface ICameraDriver
{
    string Key { get; }
    IReadOnlyList<CameraInfo> List();
    ICamera Open(CameraConfig config);
}

/// <summary>All camera drivers of this build. A job camera opens by driver key, or by serial on any driver.</summary>
public sealed class CameraDrivers
{
    private readonly List<ICameraDriver> _drivers = new();

    public void Add(ICameraDriver d) => _drivers.Add(d);

    public IReadOnlyList<CameraInfo> List()
    {
        var all = new List<CameraInfo>();
        foreach (var d in _drivers)
        {
            try { all.AddRange(d.List()); }
            catch { /* a driver whose runtime is missing on this PC lists nothing */ }
        }
        return all;
    }

    public ICamera Open(CameraConfig c)
    {
        if (_drivers.Count == 0) throw new InvalidOperationException("No camera driver in this build");
        var d = _drivers.FirstOrDefault(x => x.Key == c.Driver);
        if (d is null && !string.IsNullOrWhiteSpace(c.Serial))
            d = _drivers.FirstOrDefault(x => { try { return x.List().Any(i => i.Serial == c.Serial); } catch { return false; } });
        d ??= _drivers[0];
        try { return d.Open(c); }
        catch (Exception e) when (e is BadImageFormatException or DllNotFoundException or FileNotFoundException or TypeInitializationException)
        {
            throw new InvalidOperationException($"{c.Name}: the camera runtime is not installed on this PC");
        }
    }
}
