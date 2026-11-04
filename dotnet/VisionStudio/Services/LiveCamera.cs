using BarcodeReading.Hik;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>
/// Camera driver "c1" (this build's GigE / USB3 SDK). Kept apart from the rest so the x64-only SDK
/// assembly loads only when a camera is listed or opened. No maker or model name leaves this class.
/// </summary>
public sealed class CameraDriverC1 : ICameraDriver
{
    public string Key => "c1";

    public IReadOnlyList<CameraInfo> List() =>
        HikCodeCamera.ListDevices()
            .Select(d => new CameraInfo(Key, d.Serial, string.IsNullOrWhiteSpace(d.UserName) ? "Camera " + d.Serial : d.UserName))
            .ToList();

    public ICamera Open(CameraConfig cfg) => new Cam(cfg);

    private sealed class Cam : ICamera
    {
        private readonly HikCodeCamera _cam;
        private readonly SemaphoreSlim _arrived = new(0);
        private GrayImage? _frame;
        // Three buffers in turn when ReuseBuffers is on: the frame being filled, the one being
        // inspected and one spare, so a buffer is never overwritten while still in use.
        private readonly byte[]?[] _ring = new byte[3][];
        private int _next;

        public bool ReuseBuffers { get; set; }

        public Cam(CameraConfig cfg)
        {
            _cam = new HikCodeCamera(new CameraSetup
            {
                SerialNumber = string.IsNullOrWhiteSpace(cfg.Serial) ? null : cfg.Serial,
                ExposureUs = cfg.ExposureUs,
                GainDb = cfg.GainDb,
                TriggerSource = "Software",   // no camera I/O lines: the PLC triggers through the PC later
                StrobeOnLine1 = false,
            }, null);
            _cam.FrameArrived += f =>
            {
                var size = f.Width * f.Height;
                byte[] px;
                if (ReuseBuffers)
                {
                    var i = _next++ % _ring.Length;
                    px = _ring[i] is { } b && b.Length == size ? b : (_ring[i] = new byte[size]);
                }
                else px = new byte[size];
                unsafe
                {
                    for (int y = 0; y < f.Height; y++)
                        new ReadOnlySpan<byte>((byte*)f.Pixels + (long)y * f.Stride, f.Width).CopyTo(px.AsSpan(y * f.Width));
                }
                _frame = new GrayImage(px, f.Width, f.Height) { Name = $"{cfg.Name} #{f.FrameNumber}", TakenAt = DateTime.Now };
                _arrived.Release();
            };
            try
            {
                _cam.Open();
                _cam.Start();
            }
            catch (Exception e)
            {
                // Whatever failed, the device and the semaphore are released.
                try { _cam.Dispose(); } catch { }
                _arrived.Dispose();
                throw new InvalidOperationException($"{cfg.Name}: {Neutral(e.Message)}", e);
            }
        }

        // the SDK's messages may carry its name: keep only what the user needs
        private static string Neutral(string m) =>
            m.Contains("not found", StringComparison.OrdinalIgnoreCase) ? "camera not found, check the cable and serial number" : "camera could not be opened";

        public GrayImage? Grab(int timeoutMs = 2000)
        {
            while (_arrived.CurrentCount > 0) _arrived.Wait(0);
            _cam.SoftwareTrigger();
            return _arrived.Wait(timeoutMs) ? _frame : null;
        }

        public void Dispose()
        {
            try { _cam.Stop(); } catch { }
            _cam.Dispose();
            _arrived.Dispose();
        }
    }
}
