using BarcodeReading.Hik;
using StationGuard;
using VisionCore;

namespace VisionSystem.Runtime;

/// <summary>Copies one frame from the driver's memory into the intake's buffer (Width × Height bytes).</summary>
public delegate void CopyFrame(Span<byte> into);

/// <summary>A camera fired by software that hands its frames over on its own thread, with the
/// camera's frame number so lost frames can be found.</summary>
public interface ITriggeredSource : IDisposable
{
    event Action<ulong, int, int, CopyFrame>? Frame;
    void Trigger();
    /// <summary>Fired by the PLC's output on the camera's trigger line (Micro850 trial): <see cref="Trigger"/> does
    /// nothing and each frame that arrives is a product.</summary>
    bool Hardware => false;
}

/// <summary>
/// A camera for the station that takes its frames through <see cref="FrameIntake"/> (PR #97)
/// instead of VisionStudio LiveCamera's round-robin ring, which can overwrite a buffer that is
/// still being inspected. The station inspects each image before the same camera grabs again, so
/// with <see cref="ReuseBuffers"/> the frame stays in its pooled buffer until the next grab and
/// is handed back then. A frame the camera numbered but never delivered, or one dropped because
/// every buffer was in use, is a missed part: <see cref="Grab"/> returns null and the station makes
/// the part NG ("No picture from the camera") and tells the line. The station waits
/// <see cref="FrameWaitMs"/> for a frame, what the job needs rather than a fixed 2 s (RT-FW-01).
/// </summary>
public sealed class IntakeCamera : ICamera
{
    readonly ITriggeredSource _source;
    readonly FrameIntake _intake;
    readonly string _name;
    readonly float _exposureUs;
    readonly double[] _latencyMs = new double[16];
    int _latencyNext, _lastBytes;
    long _lateUntil;
    ulong _lastNumber;
    ulong? _lateFrame;
    CapturedFrame? _held;

    /// <summary>The longest frame wait, and the wait before the camera's first frame (its size is not known yet).</summary>
    public const int MaxFrameWaitMs = 2000;
    /// <summary>Added to a frame's exposure and transfer time, for the PC's and the network's hiccups.</summary>
    public const int FrameMarginMs = 100;
    /// <summary>Transfer rate assumed for a frame: GigE, the slower of the two links (about 100 MB/s).</summary>
    public const double LinkBytesPerMs = CameraLink.GigEBytesPerMs;

    /// <param name="exposureUs">The camera's exposure from the job, part of <see cref="FrameWaitMs"/>.</param>
    public IntakeCamera(ITriggeredSource source, string name, int buffers = 3, float exposureUs = 0, HardwareFrames? hardware = null)
    {
        _source = source;
        _name = name;
        _exposureUs = Math.Max(0, exposureUs);
        _intake = new FrameIntake(buffers);
        _intake.PartMissed += (_, _) => MissedChanged?.Invoke();
        if (source.Hardware && hardware is not null)
        {
            _hardware = true;
            // every frame the camera sends (kept or dropped for want of a buffer) is one product: the bridge is told
            // after the intake holds it, so the Grab it asks for takes exactly this frame
            _source.Frame += (n, w, h, copy) =>
            {
                var at = System.Diagnostics.Stopwatch.GetTimestamp();
                try { _intake.Arrived(n, w, h, b => copy(b)); }
                finally { hardware.RaiseArrived(_name, n, at); }
            };
            hardware.RaiseOpened(_name);
        }
        else _source.Frame += (n, w, h, copy) => _intake.Arrived(n, w, h, b => copy(b));
    }

    readonly bool _hardware;

    public bool ReuseBuffers { get; set; }
    public FrameIntake Intake => _intake;
    /// <summary>Raised on the camera thread when a part is missed.</summary>
    public event Action? MissedChanged;

    /// <summary>
    /// How long a part waits for its frame (RT-FW-01): the job's exposure, the last frame's transfer over GigE and
    /// <see cref="FrameMarginMs"/>, never less than the slowest of the camera's last 16 frames plus the margin, at
    /// most <see cref="MaxFrameWaitMs"/>. Before the first frame it is <see cref="MaxFrameWaitMs"/>.
    /// </summary>
    public int FrameWaitMs => _lastBytes == 0 ? MaxFrameWaitMs : EstimateFrameWaitMs(_exposureUs, _lastBytes, _latencyMs.Max());

    /// <summary>The frame wait for this exposure, frame size and slowest recent trigger-to-frame time.</summary>
    public static int EstimateFrameWaitMs(float exposureUs, int frameBytes, double slowestMs)
    {
        var need = Math.Max(exposureUs / 1000.0 + frameBytes / LinkBytesPerMs, slowestMs) + FrameMarginMs;
        return (int)Math.Min(MaxFrameWaitMs, Math.Ceiling(need));
    }

    public GrayImage? Grab(int timeoutMs = 2000)
    {
        // The station is done with the last image: its buffer goes back for a later frame.
        Interlocked.Exchange(ref _held, null)?.Dispose();
        if (_hardware) return GrabFired(timeoutMs);
        // The last part's frame did not come in time, but may still come: wait out its late window and throw
        // away whatever arrives, so a late frame never reaches this part (line safety).
        for (long left; (left = _lateUntil - Environment.TickCount64) > 0;)
            if (_intake.TryTake(TimeSpan.FromMilliseconds(left)) is { } late) { Seen(late); late.Dispose(); }
        _lateUntil = 0;
        // Anything already waiting belongs to an earlier part (a late frame, or a lost one whose
        // part was already NG): never hand it to this part.
        while (_intake.TryTake(TimeSpan.Zero) is { } stale) { Seen(stale); stale.Dispose(); }
        var sent = System.Diagnostics.Stopwatch.GetTimestamp();
        _source.Trigger();
        var triggered = Environment.TickCount64;
        var until = triggered + timeoutMs;
        CapturedFrame? f;
        var threwLate = false;
        while (true)
        {
            f = _intake.TryTake(TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64)));
            if (f is null)
            {
                // No frame in time: this part is NG. Its frame is looked for until twice the wait after the trigger,
                // and after that by its number, the one after the last frame seen. Not when this part already threw
                // away an earlier part's late frame: the camera may have skipped that earlier trigger and sent this
                // part's frame under that number, and then nothing more is coming.
                _lateUntil = triggered + Math.Min(2L * timeoutMs, timeoutMs + MaxFrameWaitMs);
                if (!threwLate) _lateFrame = _lastNumber + 1;
                return null;
            }
            // An earlier part's frame later than its late window: never this part's.
            if (Seen(f)) { f.Dispose(); threwLate = true; continue; }
            // A lost frame is found only when a later one arrives, so it belongs to an earlier part,
            // which already had no picture (NG). The frame behind it is this part's.
            if (f.MissedReason == "frame_lost") { f.Dispose(); continue; }
            break;
        }
        // Dropped for want of a free buffer: this part has no picture (NG).
        if (f.Missed) { f.Dispose(); return null; }
        _latencyMs[_latencyNext++ % _latencyMs.Length] = System.Diagnostics.Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
        _lastBytes = f.Width * f.Height;
        var name = $"{_name} #{f.FrameNumber}";
        if (!ReuseBuffers)
        {
            var copy = f.Pixels.ToArray();
            var img = new GrayImage(copy, f.Width, f.Height) { Name = name, TakenAt = f.At.LocalDateTime };
            f.Dispose();
            return img;
        }
        _held = f;
        return new GrayImage(f.Buffer!, f.Width, f.Height) { Name = name, TakenAt = f.At.LocalDateTime };
    }

    // Fired by the PLC (Micro850 trial): the oldest frame in the intake is this product's, nothing is thrown away as
    // stale and nothing is triggered. A frame number the camera skipped is a product whose frame never came: its own
    // product has no result for this camera (it is rejected), so the next entry is taken for this one.
    GrayImage? GrabFired(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        CapturedFrame? f;
        while (true)
        {
            f = _intake.TryTake(TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64)));
            if (f is null) return null;
            _lastNumber = f.FrameNumber;
            if (f.MissedReason == "frame_lost") { f.Dispose(); continue; }
            break;
        }
        if (f.Missed) { f.Dispose(); return null; }
        _lastBytes = f.Width * f.Height;
        var name = $"{_name} #{f.FrameNumber}";
        if (!ReuseBuffers)
        {
            var img = new GrayImage(f.Pixels.ToArray(), f.Width, f.Height) { Name = name, TakenAt = f.At.LocalDateTime };
            f.Dispose();
            return img;
        }
        _held = f;
        return new GrayImage(f.Buffer!, f.Width, f.Height) { Name = name, TakenAt = f.At.LocalDateTime };
    }

    // Notes the camera's number of a frame or missed part taken from the intake. True when it is the frame of an
    // earlier part that had none in time (by its number), which must be thrown away. A lost or dropped frame of that
    // number, or any other number (a later one, or a count started afresh), ends the search for it.
    bool Seen(CapturedFrame f)
    {
        _lastNumber = f.FrameNumber;
        if (_lateFrame is not { } late) return false;
        _lateFrame = null;
        return f.FrameNumber == late && !f.Missed;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _held, null)?.Dispose();
        _source.Dispose();
        _intake.Dispose();
    }
}

/// <summary>The two camera links of the line (GigE Vision and USB3 Vision) and the time a frame takes over them.</summary>
public static class CameraLink
{
    /// <summary>GigE: about 100 MB/s of image data.</summary>
    public const double GigEBytesPerMs = 100_000;
    /// <summary>USB3: about 380 MB/s of image data.</summary>
    public const double Usb3BytesPerMs = 380_000;

    /// <summary>The rate of a link named "GigE" or "USB3" (any case); empty is GigE.</summary>
    public static double BytesPerMs(string? link) => (link ?? "").Trim().ToUpperInvariant() switch
    {
        "" or "GIGE" => GigEBytesPerMs,
        "USB3" => Usb3BytesPerMs,
        _ => throw new InvalidOperationException($"Camera link \"{link}\": use GigE or USB3"),
    };

    /// <summary>From the trigger to the whole frame on the PC: the exposure, then the frame's bytes over the link.</summary>
    public static double FrameMs(float exposureUs, int frameBytes, double bytesPerMs) =>
        Math.Max(0, exposureUs) / 1000.0 + frameBytes / bytesPerMs;
}

/// <summary>Counts the parts the runtime's cameras missed, for health.sample frame_drops.</summary>
public sealed class FrameStats
{
    long _missed;
    public long Missed => Interlocked.Read(ref _missed);
    public void Track(IntakeCamera cam)
    {
        long seen = 0;
        cam.MissedChanged += () =>
        {
            var now = cam.Intake.Missed;
            Interlocked.Add(ref _missed, now - Interlocked.Exchange(ref seen, now));
        };
    }
}

/// <summary>
/// Camera driver "c1" (this build's GigE / USB3 SDK) for the runtime: frames through the intake.
/// The x64-only SDK assembly loads only when a camera is listed or opened. No maker or model name
/// leaves this class.
/// </summary>
public sealed class IntakeDriverC1(FrameStats stats, HardwareFrames? hardware = null, string? triggerLine = null, int debounceUs = 50) : ICameraDriver
{
    public string Key => "c1";

    public IReadOnlyList<CameraInfo> List() =>
        HikCodeCamera.ListDevices()
            .Select(d => new CameraInfo(Key, d.Serial, string.IsNullOrWhiteSpace(d.UserName) ? "Camera " + d.Serial : d.UserName))
            .ToList();

    public ICamera Open(CameraConfig cfg)
    {
        var cam = new IntakeCamera(new HikSource(cfg, hardware is null ? null : triggerLine, debounceUs), cfg.Name, exposureUs: cfg.ExposureUs, hardware: hardware);
        stats.Track(cam);
        return cam;
    }

    sealed class HikSource : ITriggeredSource
    {
        readonly HikCodeCamera _cam;
        public event Action<ulong, int, int, CopyFrame>? Frame;

        public bool Hardware { get; }

        /// <param name="line">The trigger line the PLC fires (Micro850 trial); null = fired by software.</param>
        public HikSource(CameraConfig cfg, string? line = null, int debounceUs = 50)
        {
            Hardware = !string.IsNullOrWhiteSpace(line);
            _cam = new HikCodeCamera(new CameraSetup
            {
                SerialNumber = string.IsNullOrWhiteSpace(cfg.Serial) ? null : cfg.Serial,
                ExposureUs = cfg.ExposureUs,
                GainDb = cfg.GainDb,
                TriggerSource = Hardware ? line!.Trim() : "Software",   // the PLC triggers through the PC, or fires the camera itself
                DebounceUs = debounceUs,
                StrobeOnLine1 = false,
            }, null);
            _cam.FrameArrived += f => Frame?.Invoke(f.FrameNumber, f.Width, f.Height, into => CopyRows(f, into));
            try
            {
                _cam.Open();
                _cam.Start();
            }
            catch (Exception e)
            {
                try { _cam.Dispose(); } catch { }
                throw new InvalidOperationException($"{cfg.Name}: " + (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? "camera not found, check the cable and serial number" : "camera could not be opened"), e);
            }
        }

        static unsafe void CopyRows(RawFrame f, Span<byte> into)
        {
            for (var y = 0; y < f.Height; y++)
                new ReadOnlySpan<byte>((byte*)f.Pixels + (long)y * f.Stride, f.Width).CopyTo(into.Slice(y * f.Width, f.Width));
        }

        public void Trigger() { if (!Hardware) _cam.SoftwareTrigger(); }

        public void Dispose()
        {
            try { _cam.Stop(); } catch { }
            _cam.Dispose();
        }
    }
}

/// <summary>
/// Camera driver "sim": plays a folder of 8-bit BMP frames as a camera through the intake, with
/// frame numbers, so the camera path (intake, missed parts, production counts, the plant link)
/// can be tried on a PC without cameras (ARCHITECTURE.md section 2.4). Every camera of the job
/// opens on it when it is the only driver. A camera with stored pictures of its own (its test
/// folder) plays those, so each camera of a two-camera job sees its own part. Each frame comes as late
/// as the line's camera would send it, from the job camera's exposure, the frame's size and
/// <paramref name="link"/> (RT-SF-01); <paramref name="frameMs"/> sets a fixed delay instead.
/// </summary>
public sealed class SimulatorDriver(string folder, FrameStats stats, int? frameMs = null, string link = "GigE") : ICameraDriver
{
    public string Key => "sim";

    public IReadOnlyList<CameraInfo> List() => [new CameraInfo(Key, "SIM-1", "Simulator")];

    public ICamera Open(CameraConfig cfg)
    {
        var files = FolderSource.List(cfg.TestFolder ?? "");
        if (files.Count == 0) files = FolderSource.List(folder);
        if (files.Count == 0) throw new InvalidOperationException($"{cfg.Name}: the simulator folder has no BMP frames");
        var source = new SimSource(files, frameMs, exposureUs: cfg.ExposureUs, linkBytesPerMs: _bytesPerMs);
        var cam = new IntakeCamera(source, cfg.Name, exposureUs: cfg.ExposureUs);
        stats.Track(cam);
        return cam;
    }

    readonly double _bytesPerMs = CameraLink.BytesPerMs(link);

    /// <summary>Delivers the next file on its own thread after each trigger; <see cref="Lose"/> skips
    /// frame numbers, as a camera whose frames never reached the PC. Each file is decoded once and played
    /// from memory after that, up to <paramref name="cacheBytes"/> of pixels (RT-FP-01: a new frame-size
    /// array per frame lands on the large-object heap and makes almost every collection a full one);
    /// files beyond that are decoded each time they are played. A frame comes <see cref="FrameDelayMs"/> after its
    /// trigger: <paramref name="frameMs"/> when set, else the exposure and the frame's transfer over the link (RT-SF-01).</summary>
    public sealed class SimSource(IReadOnlyList<string> files, int? frameMs, long cacheBytes = SimSource.DefaultCacheBytes,
        float exposureUs = 0, double linkBytesPerMs = CameraLink.GigEBytesPerMs) : ITriggeredSource
    {
        /// <summary>Decoded pixels kept per simulated camera: 256 MB, a hundred 1920 × 1200 frames.</summary>
        public const long DefaultCacheBytes = 256L << 20;

        readonly GrayImage?[] _decoded = new GrayImage?[files.Count];
        long _cached;
        ulong _number;
        int _next, _lose;
        public event Action<ulong, int, int, CopyFrame>? Frame;

        /// <summary>From the trigger to the frame of this size: the fixed delay when one is set, else as the line's camera.</summary>
        public double FrameDelayMs(int width, int height) =>
            frameMs ?? CameraLink.FrameMs(exposureUs, width * height, linkBytesPerMs);

        /// <summary>The next <paramref name="frames"/> frames are lost on the way (tests).</summary>
        public void Lose(int frames) => Interlocked.Add(ref _lose, frames);

        public void Trigger()
        {
            var index = _next++ % files.Count;
            var number = ++_number;
            if (Volatile.Read(ref _lose) > 0) { Interlocked.Decrement(ref _lose); return; }
            var triggered = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = Task.Run(async () =>
            {
                // The frame's size is known once it is decoded; a first decode counts towards the delay.
                var img = Decoded(index);
                var left = FrameDelayMs(img.Width, img.Height) - System.Diagnostics.Stopwatch.GetElapsedTime(triggered).TotalMilliseconds;
                if (left > 0) await Task.Delay(TimeSpan.FromMilliseconds(left)).ConfigureAwait(false);
                Frame?.Invoke(number, img.Width, img.Height, into =>
                {
                    for (var y = 0; y < img.Height; y++)
                        img.Pixels.AsSpan(y * img.Stride, img.Width).CopyTo(into.Slice(y * img.Width, img.Width));
                });
            });
        }

        GrayImage Decoded(int index)
        {
            lock (_decoded)
                if (_decoded[index] is { } kept) return kept;
            var img = GrayImage.Load(files[index]);
            lock (_decoded)
            {
                if (_decoded[index] is { } kept) return kept;
                if (_cached + img.Pixels.Length <= cacheBytes)
                {
                    _decoded[index] = img;
                    _cached += img.Pixels.Length;
                }
            }
            return img;
        }

        public void Dispose() { }
    }
}
