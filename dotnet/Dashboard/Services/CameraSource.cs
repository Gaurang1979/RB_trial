using BarcodeReading.Hik;

namespace BarcodeReading.Dashboard.Services;

/// <summary>
/// Hikrobot cameras for the station: the top camera hands raw frames to the station, the side
/// camera decodes its one parent code itself. Kept in its own class so the Hikrobot SDK is only
/// loaded when a camera is actually used (Replay mode never touches it).
/// </summary>
public sealed class CameraSource : IDisposable
{
    private HikCodeCamera? _child, _parent;
    private CodeReader? _parentReader;

    public bool HasParentCamera => _parent is not null;

    public static CameraSource Open(ProductMaster product, StationOptions opt, Action<IntPtr, int, int, int> onFrame,
                                    Action<string> onParentCode, Action<string> onError)
    {
        var src = new CameraSource();
        try
        {
            src._child = new HikCodeCamera(CameraSetup.FromProduct(product.ChildCamera, Blank(opt.ChildCameraSerial)), reader: null);
            src._child.FrameArrived += f => onFrame(f.Pixels, f.Width, f.Height, f.Stride);
            src._child.Error += onError;
            src._child.Open();

            if (!string.IsNullOrWhiteSpace(opt.ParentCameraSerial))
            {
                src._parentReader = new CodeReader(product.ParentReaderSettings());
                src._parent = new HikCodeCamera(CameraSetup.FromProduct(product.ParentCamera, opt.ParentCameraSerial, strobe: false),
                                                src._parentReader);
                src._parent.FrameDecoded += r =>
                {
                    var code = r.Decode.Codes.FirstOrDefault();
                    if (code is not null) onParentCode(code.Text);
                };
                src._parent.Error += onError;
                src._parent.Open();
            }

            src._child.Start();
            src._parent?.Start();
            return src;
        }
        catch
        {
            src.Dispose();
            throw;
        }
    }

    /// <summary>Frames from the top camera by software trigger, for calibration (line stopped).</summary>
    public static IReadOnlyList<(byte[] Pixels, int Width, int Height, double TimeMs)> CaptureForCalibration(
        ProductMaster product, StationOptions opt, int count, int intervalMs)
    {
        using var cam = new HikCodeCamera(CameraSetup.FromProduct(product.ChildCamera, Blank(opt.ChildCameraSerial), "Software"), reader: null);
        cam.Open();
        return cam.Capture(count, intervalMs);
    }

    /// <summary>A test burst from the top camera by software trigger (line stopped): each frame with its
    /// own exposure and gain; <paramref name="applied"/> says what each frame was taken with.</summary>
    public static IReadOnlyList<(byte[] Pixels, int Width, int Height, double TimeMs)> CaptureBurst(
        ProductMaster product, StationOptions opt, IReadOnlyList<BurstShot> shots, int intervalMs, out IReadOnlyList<BurstShot> applied)
    {
        using var cam = new HikCodeCamera(CameraSetup.FromProduct(product.ChildCamera, Blank(opt.ChildCameraSerial), "Software"), reader: null);
        cam.Open();
        return cam.Capture(shots, intervalMs, out applied);
    }

    /// <summary>
    /// Auto-calibration on the top camera (line stopped): last good settings first, a probe frame,
    /// then exposure and gain trials (see <see cref="CalibrationSearch"/>), then the plan.
    /// </summary>
    public static CalibrationResult Tune(ProductMaster product, StationOptions opt, Func<CalibrationShot, DecodeResult> decode, string user)
    {
        using var cam = new HikCalibrationCamera(product.ChildCamera, Blank(opt.ChildCameraSerial));
        return Calibrator.Tune(product, cam, decode, user);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose()
    {
        _child?.Dispose();
        _parent?.Dispose();
        _parentReader?.Dispose();
        _child = _parent = null;
    }
}
