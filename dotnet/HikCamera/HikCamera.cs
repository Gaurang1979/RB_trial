using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MvCameraControl;

namespace BarcodeReading.Hik;

/// <summary>Camera settings applied when the camera opens. Names follow the GenICam nodes.</summary>
public sealed class CameraSetup
{
    /// <summary>Camera serial number (printed on the camera). Null = first USB camera found.</summary>
    public string? SerialNumber { get; set; }
    public float ExposureUs { get; set; } = 2000;
    public float GainDb { get; set; } = 0;
    /// <summary>"Line0" (Hirose I/O, from the case sensor or PLC) or "Software".</summary>
    public string TriggerSource { get; set; } = "Line0";
    public string TriggerActivation { get; set; } = "RisingEdge";
    public float TriggerDelayUs { get; set; }
    public int DebounceUs { get; set; } = 50;
    /// <summary>Drive Line1 to fire the light (strobe); false when the PLC switches the light.</summary>
    public bool StrobeOnLine1 { get; set; } = true;
    /// <summary>Light on this long before the exposure (Line1 StrobeLinePreDelay).</summary>
    public int StrobePreDelayUs { get; set; }
    /// <summary>Light pulse length (Line1 StrobeLineDuration); 0 = as long as the exposure.</summary>
    public int StrobeDurationUs { get; set; }
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>Camera setup from a product master's stored camera settings.</summary>
    public static CameraSetup FromProduct(CameraSettings s, string? serialNumber, string triggerSource = "Line0", bool strobe = true)
    {
        // the light's timing around the exposure (calibration sets it); a PLC-driven light gets
        // the same numbers on the dashboard instead
        var t = LightTiming.For(s);
        return new()
        {
            SerialNumber = serialNumber,
            ExposureUs = s.ExposureUs,
            GainDb = s.GainDb,
            TriggerDelayUs = t.TriggerDelayUs,
            TriggerSource = triggerSource,
            StrobeOnLine1 = strobe && s.Light.Control == LightControl.CameraStrobe,
            StrobePreDelayUs = (int)MathF.Round(s.Light.PreOnUs),
            StrobeDurationUs = (int)MathF.Round(t.LightOnForUs),
        };
    }
}

/// <summary>
/// The top camera driven by the calibration search: software trigger, line stopped, the exposure,
/// gain and strobe pulse set for every trial, probe frames decimated when the camera can.
/// </summary>
public sealed class HikCalibrationCamera : ICalibrationCamera, IDisposable
{
    private readonly HikCodeCamera _cam;
    private readonly LightSettings _light;

    public HikCalibrationCamera(CameraSettings settings, string? serialNumber)
    {
        _light = settings.Light.Clone();
        _cam = new HikCodeCamera(CameraSetup.FromProduct(settings, serialNumber, "Software"), reader: null);
        _cam.Open();
    }

    public IReadOnlyList<CalibrationShot> Capture(float exposureUs, float gainDb, int count, int intervalMs, bool probe)
    {
        // the light is on for the whole exposure, PreOnUs before and PostOnUs after
        _cam.SetTrial(exposureUs, gainDb, (int)MathF.Round(_light.PreOnUs + exposureUs + _light.PostOnUs));
        int scale = probe ? _cam.BeginDecimation() : 1;
        try
        {
            var shots = new List<CalibrationShot>();
            foreach (var (px, w, h, t) in _cam.Capture(count, intervalMs))
                shots.Add(new CalibrationShot(px, w, h, scale, t));
            return shots;
        }
        finally
        {
            if (scale > 1) _cam.EndDecimation();
        }
    }

    public void Dispose() => _cam.Dispose();
}

/// <summary>
/// A raw camera frame. Pixels point into the SDK buffer and are valid only during the event
/// handler: copy or decode them there.
/// </summary>
public readonly record struct RawFrame(IntPtr Pixels, int Width, int Height, int Stride, ulong FrameNumber);

/// <summary>One decoded frame from a camera.</summary>
public sealed record CameraResult(ulong FrameNumber, DateTime HostTime, DecodeResult Decode, float CameraBrightness);

/// <summary>
/// Hikrobot MV-CS series USB3 camera (MV-CS200-10UM and others) feeding a <see cref="CodeReader"/>.
/// Frames go straight from the SDK buffer to the engine without a copy. One instance per camera.
/// </summary>
public sealed class HikCodeCamera : IDisposable
{
    private readonly CameraSetup _setup;
    private readonly CodeReader? _reader;
    private IDevice? _device;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly StationGuard.FrameGap _gap = new();

    /// <summary>Raised on the grab thread for every frame. Keep handlers short; queue heavy work.</summary>
    public event Action<CameraResult>? FrameDecoded;

    /// <summary>
    /// Raised for every frame before decoding. A camera created without a reader only raises
    /// this event, so the caller (e.g. the dashboard's station) decodes and keeps the image.
    /// </summary>
    public event Action<RawFrame>? FrameArrived;

    /// <summary>
    /// Raised on the grab thread, before the frame that follows them, with the number of frames
    /// the camera took that never reached the PC (its frame numbers jumped) and that next frame's
    /// number. With a hardware trigger each lost frame is a part that was not inspected: the
    /// station counts it NG. This happens when frames come faster than they are handled and the
    /// driver's buffers are all full.
    /// </summary>
    public event Action<int, ulong>? FramesMissed;

    /// <summary>Raised when the SDK reports an error the grab loop cannot recover from.</summary>
    public event Action<string>? Error;

    /// <summary>When set, frames that come up short are saved here as BMP for the learning service.</summary>
    public string? HardCaseFolder { get; set; }

    /// <param name="reader">Decodes each frame here; null = only raise <see cref="FrameArrived"/>.</param>
    public HikCodeCamera(CameraSetup setup, CodeReader? reader)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _reader = reader;
    }

    // GigE Vision and USB3 Vision cameras (docs/vision-system: no other interfaces)
    private const DeviceTLayerType Interfaces = DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice;

    /// <summary>Lists GigE and USB cameras: model and serial number.</summary>
    public static IReadOnlyList<(string Model, string Serial)> ListCameras()
    {
        SDKSystem.Initialize();
        Check(DeviceEnumerator.EnumDevices(Interfaces, out var list), "enumerate cameras");
        var outList = new List<(string, string)>();
        foreach (var d in list) outList.Add((d.ModelName, d.SerialNumber));
        return outList;
    }

    /// <summary>Lists GigE and USB cameras: serial number and the name set in the camera (no maker or model).</summary>
    public static IReadOnlyList<(string Serial, string UserName)> ListDevices()
    {
        SDKSystem.Initialize();
        Check(DeviceEnumerator.EnumDevices(Interfaces, out var list), "enumerate cameras");
        var outList = new List<(string, string)>();
        foreach (var d in list) outList.Add((d.SerialNumber, d.UserDefinedName ?? ""));
        return outList;
    }

    public void Open()
    {
        SDKSystem.Initialize();
        Check(DeviceEnumerator.EnumDevices(Interfaces, out var list), "enumerate cameras");
        IDeviceInfo? info = null;
        foreach (var d in list)
        {
            if (_setup.SerialNumber is null || d.SerialNumber == _setup.SerialNumber)
            {
                info = d;
                break;
            }
        }
        if (info is null)
            throw new InvalidOperationException(_setup.SerialNumber is null
                ? "no camera found"
                : $"camera {_setup.SerialNumber} not found");

        _device = DeviceFactory.CreateDevice(info);
        Check(_device.Open(), "open camera");
        // GigE: the largest packet the network path carries (jumbo frames when the NIC allows)
        if (_device is IGigEDevice gige && gige.GetOptimalPacketSize(out int packet) == MvError.MV_OK && packet > 0)
            Check(_device.Parameters.SetIntValue("GevSCPSPacketSize", packet), "GevSCPSPacketSize");
        Configure(_device.Parameters);
    }

    private void Configure(IParameters p)
    {
        // the engine reads 8-bit grey; Mono8 is also the least USB bandwidth
        Check(p.SetEnumValueByString("PixelFormat", "Mono8"), "PixelFormat");
        Check(p.SetEnumValueByString("ExposureAuto", "Off"), "ExposureAuto");
        Check(p.SetFloatValue("ExposureTime", _setup.ExposureUs), "ExposureTime");
        Check(p.SetEnumValueByString("GainAuto", "Off"), "GainAuto");
        Check(p.SetFloatValue("Gain", _setup.GainDb), "Gain");

        // one frame per trigger
        Check(p.SetEnumValueByString("TriggerMode", "On"), "TriggerMode");
        Check(p.SetEnumValueByString("TriggerSource", _setup.TriggerSource), "TriggerSource");
        if (_setup.TriggerSource != "Software")
        {
            Check(p.SetEnumValueByString("TriggerActivation", _setup.TriggerActivation), "TriggerActivation");
            Check(p.SetFloatValue("TriggerDelay", _setup.TriggerDelayUs), "TriggerDelay");
            Check(p.SetEnumValueByString("LineSelector", _setup.TriggerSource), "LineSelector");
            Check(p.SetIntValue("LineDebouncerTime", _setup.DebounceUs), "LineDebouncerTime");
        }

        if (_setup.StrobeOnLine1)
        {
            // Line1 high while the sensor exposes: wire it to the strobe controller
            Check(p.SetEnumValueByString("LineSelector", "Line1"), "LineSelector");
            Check(p.SetEnumValueByString("LineSource", "ExposureStartActive"), "LineSource");
            Check(p.SetBoolValue("StrobeEnable", true), "StrobeEnable");
            // timing: on before the exposure, for the whole pulse (older firmware may lack these)
            TrySet(p, "StrobeLineDelay", 0);
            TrySet(p, "StrobeLinePreDelay", _setup.StrobePreDelayUs);
            TrySet(p, "StrobeLineDuration", _setup.StrobeDurationUs);
        }
    }

    private void TrySet(IParameters p, string name, long value)
    {
        int ret = p.SetIntValue(name, value);
        if (ret != MvError.MV_OK)
            Error?.Invoke($"camera has no {name} (0x{ret:x8}): set the light timing in the light controller or PLC");
    }

    /// <summary>
    /// Takes <paramref name="count"/> frames by software trigger, <paramref name="intervalMs"/> apart,
    /// and returns copies with the time of each trigger (ms from the first). For calibration: the
    /// camera must be open with TriggerSource "Software" and not started.
    /// </summary>
    public IReadOnlyList<(byte[] Pixels, int Width, int Height, double TimeMs)> Capture(int count, int intervalMs) =>
        Capture(count, intervalMs, null, out _);

    /// <summary>
    /// The same, each frame with its own exposure and gain (a test burst, <see cref="TestBurst"/>);
    /// a camera-driven strobe gets each frame's light pulse, so the light stays on for the whole
    /// exposure. Where the camera refuses a setting the frame keeps the calibrated one (a longer
    /// exposure whose light pulse cannot be set is taken at the calibrated exposure, brighter by
    /// gain). <paramref name="applied"/>: what each frame was taken with. The calibrated setting
    /// is restored afterwards.
    /// </summary>
    public IReadOnlyList<(byte[] Pixels, int Width, int Height, double TimeMs)> Capture(
        IReadOnlyList<BurstShot> shots, int intervalMs, out IReadOnlyList<BurstShot> applied) =>
        Capture(shots.Count, intervalMs, shots, out applied);

    private IReadOnlyList<(byte[] Pixels, int Width, int Height, double TimeMs)> Capture(
        int count, int intervalMs, IReadOnlyList<BurstShot>? shots, out IReadOnlyList<BurstShot> applied)
    {
        var grabber = Device.StreamGrabber;
        var frames = new List<(byte[], int, int, double)>();
        var used = new List<BurstShot>();
        applied = used;
        Check(grabber.StartGrabbing(), "start grabbing");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (shots is not null) used.Add(Apply(shots[i]));
                double due = (double)i * intervalMs;
                while (clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(1);
                double at = clock.Elapsed.TotalMilliseconds;
                SoftwareTrigger();
                Check(grabber.GetImageBuffer((uint)_setup.TimeoutMs, out IFrameOut frame), "grab calibration frame");
                try
                {
                    var img = frame.Image;
                    int w = (int)img.Width, h = (int)img.Height;
                    var copy = new byte[w * h];
                    System.Runtime.InteropServices.Marshal.Copy(img.PixelDataPtr, copy, 0, copy.Length);
                    frames.Add((copy, w, h, at));
                }
                finally
                {
                    grabber.FreeImageBuffer(frame);
                }
            }
        }
        finally
        {
            grabber.StopGrabbing();
            if (shots is not null) Restore();
        }
        return frames;
    }

    // one burst frame's setting; returns what the camera took
    private BurstShot Apply(BurstShot s)
    {
        var p = Device.Parameters;
        float e0 = _setup.ExposureUs, g0 = _setup.GainDb;
        float e = s.ExposureUs, g = s.GainDb, light = s.LightOnForUs;
        if (_setup.StrobeOnLine1)
        {
            // the light pulse for this exposure (nothing is exposed until the trigger after both are set)
            bool pulseOk = p.SetEnumValueByString("LineSelector", "Line1") == MvError.MV_OK
                           && p.SetIntValue("StrobeLineDuration", (long)MathF.Round(light)) == MvError.MV_OK;
            if (!pulseOk && e > e0)
            {
                g = MathF.Min(g + 20f * MathF.Log10(e / e0), MathF.Max(g0, Calibrator.MaxGainDb));
                e = e0;
                light = _setup.StrobeDurationUs;
            }
        }
        if (p.SetFloatValue("ExposureTime", e) != MvError.MV_OK) e = e0;
        if (p.SetFloatValue("Gain", g) != MvError.MV_OK) g = g0;
        float got = e / e0 * MathF.Pow(10f, (g - g0) / 20f);
        return new BurstShot(e, g, light, got);
    }

    private void Restore()
    {
        var p = Device.Parameters;
        p.SetFloatValue("ExposureTime", _setup.ExposureUs);
        p.SetFloatValue("Gain", _setup.GainDb);
        if (_setup.StrobeOnLine1 && p.SetEnumValueByString("LineSelector", "Line1") == MvError.MV_OK)
            p.SetIntValue("StrobeLineDuration", _setup.StrobeDurationUs);
    }

    /// <summary>
    /// Calibration trial settings (camera open, not started): exposure, gain, and, when the camera
    /// strobes the light, a pulse that covers the new exposure (on PreDelay before, as long as the
    /// light timing says). A PLC-switched light gets the timing from the calibration result instead.
    /// </summary>
    public void SetTrial(float exposureUs, float gainDb, int strobeDurationUs)
    {
        var p = Device.Parameters;
        Check(p.SetFloatValue("ExposureTime", exposureUs), "ExposureTime");
        Check(p.SetFloatValue("Gain", gainDb), "Gain");
        _setup.ExposureUs = exposureUs;
        _setup.GainDb = gainDb;
        if (_setup.StrobeOnLine1)
        {
            Check(p.SetEnumValueByString("LineSelector", "Line1"), "LineSelector");
            TrySet(p, "StrobeLineDuration", strobeDurationUs);
            _setup.StrobeDurationUs = strobeDurationUs;
        }
    }

    /// <summary>
    /// Sets 2 x 2 decimation for a probe frame (one pixel in four is read out: a quarter of the
    /// data, the same brightness, unlike summing binning) and returns the factor the camera
    /// actually gives: 2, or 1 when it has no decimation. <see cref="EndDecimation"/> restores the
    /// full frame. Grabbing must be stopped.
    /// </summary>
    public int BeginDecimation()
    {
        var p = Device.Parameters;
        if (p.GetIntValue("Width", out IIntValue w0) != MvError.MV_OK) return 1;
        _fullWidth = w0.CurValue;
        _fullHeight = p.GetIntValue("Height", out IIntValue h0) == MvError.MV_OK ? h0.CurValue : 0;
        bool h = p.SetEnumValue("DecimationHorizontal", 2) == MvError.MV_OK || p.SetIntValue("DecimationHorizontal", 2) == MvError.MV_OK;
        bool v = p.SetEnumValue("DecimationVertical", 2) == MvError.MV_OK || p.SetIntValue("DecimationVertical", 2) == MvError.MV_OK;
        if (!h || !v || p.GetIntValue("Width", out IIntValue w1) != MvError.MV_OK || w1.CurValue * 2 > _fullWidth)
        {
            EndDecimation();
            return 1;
        }
        return 2;
    }

    /// <summary>Back to the full frame after <see cref="BeginDecimation"/>.</summary>
    public void EndDecimation()
    {
        if (_fullWidth == 0) return;
        var p = Device.Parameters;
        if (p.SetEnumValue("DecimationHorizontal", 1) != MvError.MV_OK) p.SetIntValue("DecimationHorizontal", 1);
        if (p.SetEnumValue("DecimationVertical", 1) != MvError.MV_OK) p.SetIntValue("DecimationVertical", 1);
        // the image size does not grow back by itself
        Check(p.SetIntValue("Width", _fullWidth), "Width");
        if (_fullHeight > 0) Check(p.SetIntValue("Height", _fullHeight), "Height");
        _fullWidth = _fullHeight = 0;
    }

    private long _fullWidth, _fullHeight;

    /// <summary>Changes exposure while running, for example from the dashboard or the tuner.</summary>
    public void SetExposure(float exposureUs)
    {
        Check(Device.Parameters.SetFloatValue("ExposureTime", exposureUs), "ExposureTime");
        _setup.ExposureUs = exposureUs;
    }

    /// <summary>Fires one software trigger (when TriggerSource is "Software").</summary>
    public void SoftwareTrigger() => Check(Device.Parameters.SetCommandValue("TriggerSoftware"), "TriggerSoftware");

    public void Start()
    {
        var grabber = Device.StreamGrabber;
        // Three driver buffers: the frame being handled, the next one arriving and one spare.
        // When all are full a new frame is dropped by the driver and shows as a frame-number gap
        // (FramesMissed); a buffer is never overwritten while it is handled.
        grabber.SetImageNodeNum(3);
        _gap.Reset();
        Check(grabber.StartGrabbing(), "start grabbing");
        _stop = false;
        _thread = new Thread(GrabLoop) { IsBackground = true, Name = "HikCodeCamera " + _setup.SerialNumber };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
        _device?.StreamGrabber.StopGrabbing();
    }

    private void GrabLoop()
    {
        var grabber = Device.StreamGrabber;
        while (!_stop)
        {
            int ret = grabber.GetImageBuffer((uint)_setup.TimeoutMs, out IFrameOut frame);
            if (ret != MvError.MV_OK) continue; // timeout: no trigger yet
            try
            {
                int lost = _gap.Next(frame.FrameNum);
                if (lost > 0) FramesMissed?.Invoke(lost, frame.FrameNum);
                var img = frame.Image;
                if (img.PixelType != MvGvspPixelType.PixelType_Gvsp_Mono8)
                {
                    Error?.Invoke($"unexpected pixel type {img.PixelType}; set the camera to Mono8");
                    continue;
                }
                int width = (int)img.Width, height = (int)img.Height;
                FrameArrived?.Invoke(new RawFrame(img.PixelDataPtr, width, height, width, frame.FrameNum));
                if (_reader is null) continue;
                DecodeResult result = _reader.Decode(img.PixelDataPtr, width, height, width);

                if (!result.Stats.Complete && HardCaseFolder is not null)
                    SaveHardCase(frame);

                FrameDecoded?.Invoke(new CameraResult(frame.FrameNum, DateTime.Now, result, result.Stats.LabelBrightness));
            }
            catch (Exception e)
            {
                Error?.Invoke(e.Message);
            }
            finally
            {
                grabber.FreeImageBuffer(frame);
            }
        }
    }

    private void SaveHardCase(IFrameOut frame)
    {
        Directory.CreateDirectory(HardCaseFolder!);
        string path = Path.Combine(HardCaseFolder!, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{frame.FrameNum}.bmp");
        var fmt = new ImageFormatInfo { FormatType = ImageFormatType.Bmp };
        int ret = Device.ImageSaver.SaveImageToFile(path, frame.Image, fmt, CFAMethod.Equilibrated);
        if (ret != MvError.MV_OK)
        {
            Error?.Invoke($"save hard case failed: {ret:x8}");
            return;
        }
        // sidecar for the retraining agent (ml/retrain.py): what this case should contain
        if (_reader is null) return;
        var s = _reader.Settings;
        string fmtName = s.Format switch
        {
            CodeFormat.QrCode => "qr", CodeFormat.DataMatrix => "dm", CodeFormat.Gs1_128 => "code128", CodeFormat.Ean13 => "ean13",
            CodeFormat.Ean8 => "ean8", CodeFormat.UpcA => "upca", CodeFormat.UpcE => "upce", CodeFormat.Code39 => "code39",
            CodeFormat.Code93 => "code93", CodeFormat.Itf => "itf", CodeFormat.Codabar => "codabar", CodeFormat.DataBar => "databar",
            CodeFormat.MicroQr => "microqr", CodeFormat.Pdf417 => "pdf417", CodeFormat.Aztec => "aztec",
            CodeFormat.Pharmacode => "pharmacode", CodeFormat.PharmacodeReverse => "pharmacode_rev", CodeFormat.DotCode => "dotcode",
            CodeFormat.RMqr => "rmqr",
            _ => ((int)s.Format).ToString(System.Globalization.CultureInfo.InvariantCulture), // several types: the flag value
        };
        File.WriteAllText(Path.ChangeExtension(path, ".json"),
            $"{{\"format\": \"{fmtName}\", \"expected\": {s.ExpectedCount}, \"exposure_us\": {_setup.ExposureUs}}}");
    }

    private IDevice Device => _device ?? throw new InvalidOperationException("camera is not open");

    private static void Check(int ret, string what)
    {
        if (ret != MvError.MV_OK)
            throw new InvalidOperationException($"camera: {what} failed (0x{ret:x8})");
    }

    public void Dispose()
    {
        Stop();
        if (_device is not null)
        {
            _device.Close();
            _device.Dispose();
            _device = null;
        }
    }
}
