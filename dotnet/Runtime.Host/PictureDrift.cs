using VisionCore;

namespace VisionSystem.Runtime;

/// <summary>
/// The picture check during a run (requirements AS-13a): the drift monitor's watch on the pictures. One picture in
/// <c>every</c> of each camera is measured with the picture check per corner and centre (AS-13) and compared with the
/// camera's taught picture by the setup-check limits (#219, <see cref="PictureCheckLimits"/>). After
/// <see cref="Confirm"/> checked pictures in a row are clearly worse, the line's status carries a warning
/// (<see cref="AlarmCode"/>); as many good ones in a row, a job change or Stop clear it. Advice only: it never sees or
/// changes a part's result. The cycle never waits for it: the picture is copied on the inspecting thread (its buffer
/// goes back to the camera) and measured on a background thread, and a picture that comes while a check is still
/// running is not checked.
/// </summary>
public sealed class PictureDrift
{
    /// <summary>The line status warning: a camera's pictures are clearly worse than its taught picture.</summary>
    public const uint AlarmCode = 940;
    /// <summary>Checked pictures in a row that raise the warning, and that clear it.</summary>
    public const int Confirm = 3;
    /// <summary>A zone whose light falls under this share of the taught zone's is worse.</summary>
    public const double LightDrop = 0.6;

    /// <summary>A camera's warning: the zones worse than taught and the AS-13 advice on the last worse picture.</summary>
    public sealed record Warning(int Camera, string CameraName, IReadOnlyList<PictureZoneName> Zones, IReadOnlyList<PictureAdviceKind> Advice)
    {
        public string Text => $"{CameraName}: the picture is clearly worse than the taught picture ({string.Join(", ", Zones.Select(ZoneText))}). " +
                              "Inspection goes on; the tools still decide each part.";

        public string Help => Advice.Count == 0
            ? "Compare the camera, lens and light with the taught picture. Tell the engineer."
            : string.Join(" ", Advice.Distinct().Select(AdviceText)) + " Tell the engineer.";
    }

    sealed class Camera
    {
        public long Seen;
        public bool TaughtRead;
        public PictureCheckResult? Taught;
        public int Worse, Fine;
        public Warning? Warning;
    }

    readonly int _every;
    readonly PictureCheckLimits _limits;
    readonly Func<string, GrayImage?> _load;
    readonly object _lock = new();
    Job? _job;
    Camera[] _cams = [];
    int _generation, _busy;
    Task _last = Task.CompletedTask;

    /// <param name="every">One picture in this many of each camera is checked; 0 or less: none.</param>
    /// <param name="load">Reads a taught picture file (null when it cannot); default <see cref="GrayImage.Load"/>.</param>
    public PictureDrift(int every, PictureCheckLimits? limits = null, Func<string, GrayImage?>? load = null)
    {
        _every = every;
        _limits = limits ?? new PictureCheckLimits();
        _load = load ?? (p => File.Exists(p) ? GrayImage.Load(p) : null);
    }

    /// <summary>Raised when a warning was raised or cleared, on the check's thread or the caller's.</summary>
    public event Action? Changed;

    /// <summary>The check started last, for tests.</summary>
    public Task Last { get { lock (_lock) return _last; } }

    /// <summary>The warnings now, by camera.</summary>
    public IReadOnlyList<Warning> Warnings
    {
        get { lock (_lock) return _cams.Select(c => c.Warning).OfType<Warning>().ToList(); }
    }

    /// <summary>One camera's inspected picture, on the inspecting thread. Returns at once; never throws.</summary>
    public void Offer(Job job, int camera, GrayImage img)
    {
        if (_every <= 0) return;
        int generation;
        lock (_lock)
        {
            if (!ReferenceEquals(job, _job)) ResetUnlocked(job);
            if (camera < 0 || camera >= _cams.Length) return;
            if (_cams[camera].Seen++ % _every != _every - 1) return;
            generation = _generation;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // a check still running: not this picture
        try
        {
            var copy = Copy(img);
            var task = Task.Run(() =>
            {
                try { Check(job, camera, copy, generation); }
                catch (Exception) { /* advice only: a failed check is no warning and never touches the line */ }
                finally { Volatile.Write(ref _busy, 0); }
            });
            lock (_lock) _last = task;
        }
        catch (Exception) { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>The line stopped (or a new run starts): every warning clears and the counts start again.</summary>
    public void Clear()
    {
        bool had;
        lock (_lock)
        {
            had = _cams.Any(c => c.Warning is not null);
            ResetUnlocked(_job);
        }
        if (had) Changed?.Invoke();
    }

    void ResetUnlocked(Job? job)
    {
        _job = job;
        _generation++;
        _cams = job is null ? [] : job.Cameras.Select(_ => new Camera()).ToArray();
    }

    void Check(Job job, int camera, GrayImage img, int generation)
    {
        var now = PictureCheck.Measure(img, _limits);
        var taught = Taught(job, camera, generation);
        var zones = Worse(taught, now, _limits);
        bool changed;
        lock (_lock)
        {
            if (generation != _generation) return;   // a job change or Stop since: this picture is dropped
            var c = _cams[camera];
            var before = c.Warning;
            if (zones.Count > 0)
            {
                c.Fine = 0;
                if (++c.Worse >= Confirm)
                    c.Warning = new Warning(camera, job.Cameras[camera].Name, zones, now.Advice.Select(a => a.Kind).ToList());
            }
            else
            {
                c.Worse = 0;
                if (++c.Fine >= Confirm) c.Warning = null;
            }
            changed = (before is null) != (c.Warning is null);
        }
        if (changed) Changed?.Invoke();
    }

    // the camera's taught picture measured once per job load; null without one (the limits alone then judge)
    PictureCheckResult? Taught(Job job, int camera, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return null;
            if (_cams[camera].TaughtRead) return _cams[camera].Taught;
        }
        PictureCheckResult? taught = null;
        try
        {
            if (job.Cameras[camera].ReferenceImage is { Length: > 0 } path && _load(path) is { } img)
                taught = PictureCheck.Measure(img, _limits);
        }
        catch (Exception) { /* an unreadable taught picture: judged by the limits alone */ }
        lock (_lock)
        {
            if (generation == _generation) (_cams[camera].Taught, _cams[camera].TaughtRead) = (taught, true);
        }
        return taught;
    }

    /// <summary>The zones of <paramref name="now"/> clearly worse than the taught picture: red now and not red when
    /// taught, sharpness under <see cref="PictureCheckLimits.RelativeBad"/> of the taught zone's (or no edge left
    /// where the taught zone had one), or light under <see cref="LightDrop"/> of the taught zone's. Without a taught
    /// picture, the zones red now.</summary>
    public static IReadOnlyList<PictureZoneName> Worse(PictureCheckResult? taught, PictureCheckResult now, PictureCheckLimits? limits = null)
    {
        var l = limits ?? new PictureCheckLimits();
        var worse = new List<PictureZoneName>();
        foreach (var z in now.Zones)
        {
            var t = taught?.Zones.FirstOrDefault(x => x.Zone == z.Zone);
            var bad = z.State == ZoneState.Bad && t?.State != ZoneState.Bad;
            if (t is not null && !double.IsNaN(t.Sharpness) && t.Sharpness > 0)
                bad |= double.IsNaN(z.Sharpness) || z.Sharpness < l.RelativeBad * t.Sharpness;
            if (t is not null && t.Light > 0) bad |= z.Light < LightDrop * t.Light;
            if (bad) worse.Add(z.Zone);
        }
        return worse;
    }

    // tight rows: the camera's buffer goes back for a later frame after this part
    static GrayImage Copy(GrayImage img)
    {
        var px = new byte[img.Width * img.Height];
        for (var y = 0; y < img.Height; y++) img.Pixels.AsSpan(y * img.Stride, img.Width).CopyTo(px.AsSpan(y * img.Width));
        return new GrayImage(px, img.Width, img.Height) { Name = img.Name, TakenAt = img.TakenAt };
    }

    static string ZoneText(PictureZoneName z) => z switch
    {
        PictureZoneName.TopLeft => "top left", PictureZoneName.TopRight => "top right",
        PictureZoneName.BottomLeft => "bottom left", PictureZoneName.BottomRight => "bottom right", _ => "centre",
    };

    // the plain advice of requirements AS-13
    static string AdviceText(PictureAdviceKind k) => k switch
    {
        PictureAdviceKind.Tilt => "A corner or side is blurred: the lens or camera may be tilted, or the part not flat.",
        PictureAdviceKind.Focus => "The picture is blurred: focus the lens, or check that the part does not move.",
        PictureAdviceKind.CleanLens => "The picture is soft and dull: clean the lens or the window in front of it.",
        PictureAdviceKind.Vignetting => "The corners are darker than the centre: open the aperture a little, move the light or use a wider light.",
        PictureAdviceKind.Dark => "Part of the picture is too dark: adjust the light.",
        _ => "Part of the picture is too bright: adjust the light, or remove the glare there.",
    };
}
