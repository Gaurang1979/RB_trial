using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantEvents;
using StationAgent;
using StationGuard;
using VisionCore;
using VisionCore.Plc;

namespace VisionStudio.Services;

/// <summary>One counted part (<see cref="Station.PartCounted"/>): Results and Errors are in the order of Cameras.</summary>
public sealed record StationPart(int[] Cameras, bool Ok, double Ms, string? PartId, JobResult?[] Results, string?[] Errors)
{
    /// <summary>Set when the PLC decided the verdict, not the cameras: the part was inspected OK but the PLC
    /// got NG (e.g. NG 100, link or heartbeat lost before it took the result, PLC-INTERFACE 5.6).</summary>
    public PlcVerdict? Plc { get; init; }
    /// <summary>The job the part was inspected with (a PLC part is counted later, maybe after a job change).</summary>
    public Job? Job { get; init; }
    /// <summary>The part's image name (<see cref="ImageName"/>); camera i's image is <c>Name.WithCamera(i + 1)</c>.</summary>
    public ImageName? Name { get; init; }
}

/// <summary>Why the PLC got NG for a part inspected OK: the reason key for counters and logs, the plant
/// server's reason (tool "PLC"), and the text logged for each camera of the part.</summary>
public sealed record PlcVerdict(string Reason, string PlantKey, string Text)
{
    public static readonly PlcVerdict LinkLost = new("plc_link_lost", "link_lost", Station.PlcLinkLostError);
    public static readonly PlcVerdict NotTaken = new("plc_not_taken", "not_taken",
        "The PLC dropped its trigger before the result (its result timeout) and made the part NG (the inspection said OK)");
    public static readonly PlcVerdict Ng = new("plc_ng", "ng", "The PLC got NG for this part (the inspection said OK)");
}

/// <summary>One camera inspected one image (<see cref="Station.Reported"/>): the part number from the PLC
/// (null without), the PLC's part id as it sent it, the result (null when there was no picture), error text.</summary>
public sealed record CameraReport(int Camera, ushort? Part, string? PartId, JobResult? Result, string? Error)
{
    /// <summary>The job and batch when the image was inspected (a PLC part is logged later).</summary>
    public string? JobName { get; init; }
    public BatchFields? Batch { get; init; }
    /// <summary>The error is the PLC's verdict (<see cref="PlcVerdict"/>), not the camera's.</summary>
    public bool ByPlc { get; init; }
    /// <summary>This camera's image name (<see cref="StationGuard.ImageName"/>): its saved file and image id.</summary>
    public string? ImageId { get; init; }
}

public sealed class CamState
{
    public string Name { get; init; } = "";
    public JobResult? Last { get; set; }
    public int Good { get; set; }
    public int Bad { get; set; }
    public string? Error { get; set; }
    public int Version { get; set; }
    public bool HasTools { get; init; }
}

public enum RunMode
{
    /// <summary>Cameras fired by software, one cycle after another (bench testing).</summary>
    FreeRun,
    /// <summary>Stored pictures, one cycle after another.</summary>
    Folder,
    /// <summary>Each PLC trigger fires the cameras once (production).</summary>
    Plc,
    /// <summary>Each PLC trigger takes the next stored pictures (PLC wiring test; Simulation bit set).</summary>
    PlcSimulation,
}

/// <summary>
/// The running inspection: one job, its cameras (or their stored pictures), the batch values. All cameras
/// of a cycle are fired together and inspected in parallel; the cycle is OK when every camera is OK.
/// A cycle runs on a timer (free run, folder) or on each PLC trigger (PLC modes).
/// </summary>
public sealed class Station : IDisposable, IPlcHost
{
    private readonly JobStore _jobs;
    private readonly ModelPaths _models;
    private readonly ImageCache _images;
    private readonly StudioOptions _opt;
    private readonly CameraDrivers _drivers;
    private readonly ResourceGuard _guard;
    private readonly NgImageStore _ngImages;
    private readonly StationPublisher _plant;
    private readonly NgImageSource _imageSource;
    private readonly PartSerial _serials;
    private readonly object _lock = new();
    private readonly object _cycleLock = new();
    // The plant server's counters: parts from the camera since this program started, never reset by a
    // job load (the server keeps each station's latest snapshot, so a reset would zero the line's totals).
    private long _plantGood, _plantBad;
    private readonly long _plantSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private int _run;   // which run the loop belongs to: a late-ending old loop must not report the new one stopped
    private JobRunner? _runner;
    private CancellationTokenSource? _cts;
    private Thread? _loop;
    private ICamera?[] _cams = Array.Empty<ICamera?>();
    private IReadOnlyList<string>[] _folders = Array.Empty<IReadOnlyList<string>>();
    private int _next;
    private readonly int[] _nextOf = new int[Job.MaxCameras];
    private readonly object[] _camLocks = Enumerable.Range(0, Job.MaxCameras).Select(_ => new object()).ToArray();
    private bool _open;
    // The names the plant server shows for the cameras' images: clean, never empty, never twice the same.
    private string[] _camNames = Array.Empty<string>();
    // PLC parts inspected but not yet taken by the PLC (PlcTrigger.Held), by trigger source (-1 = the main
    // trigger, else the camera channel): counted, logged and reported when the PLC runtime says what the PLC got.
    private readonly Dictionary<int, HeldPart> _held = new();

    private sealed class HeldPart(int source, PlcTrigger trigger)
    {
        public readonly int Source = source;
        public readonly PlcTrigger Trigger = trigger;
        public readonly List<CameraReport> Reports = new();
        public Action<PlcVerdict?>? Commit;   // set: the PLC got NG for this part
        // the images of cameras judged OK and not kept: kept as NG images if the PLC gets NG for the part
        public readonly List<HeldImage> Images = new();
    }

    // One camera image of a held part: a copy in a pooled buffer, since a camera's frame buffer is reused for
    // its next frames; given back as soon as the PLC took the part OK, or when it is counted.
    private sealed record HeldImage(int Index, string Job, string Camera, ImageName Name, IReadOnlyDictionary<string, object?> Meta,
                                    byte[] Pixels, int Width, int Height);
    // deliveries are counted and logged here in order, off the PLC thread (the handshake never waits on the log)
    private readonly object _commitsLock = new();
    private Task _commits = Task.CompletedTask;

    /// <summary>The error text logged for a camera of a part the PLC got as NG 100 after its inspection said OK.</summary>
    public const string PlcLinkLostError = "PLC link or heartbeat lost before the PLC took the result: NG 100 (the inspection said OK)";

    private readonly LicenceService _licence;
    private readonly PackageIntegrityService _integrity;
    private readonly LedgerGate? _ledger;

    public Station(JobStore jobs, ModelPaths models, ImageCache images, IOptions<StudioOptions> opt, CameraDrivers drivers,
        LicenceService licence, ResourceGuard guard, NgImageStore ngImages, StationPublisher plant, PackageIntegrityService integrity,
        LedgerGate? ledger = null)
    {
        _ledger = ledger;
        _licence = licence;
        _integrity = integrity;
        _plant = plant;
        // What the plant server shows for this station: counters every 10 s, PC health every minute,
        // and an alarm while the PC is pressed. Publishing only queues; it never slows a part.
        _plant.Counters = () => (Interlocked.Read(ref _plantGood), Interlocked.Read(ref _plantBad), _plantSince);
        _plant.Health = PlantHealth;
        // The images the plant server asks for come from the saved NG images; none are made while the PC is pressed.
        // Slowly, on a thread of its own, and only between parts (never while one is being inspected).
        _plant.Images = _imageSource = new NgImageSource(ngImages, () => guard.Level >= Pressure.High || guard.PartInProgress);
        guard.LevelChanged += (_, to, _) => _plant.Alarm("pc_pressure", to >= Pressure.High,
            to == Pressure.Critical ? AlarmPriority.High : AlarmPriority.Medium);
        // A supervisor checks each changeover before the line runs (CHANGEOVER-VERIFY.md): the record goes in every
        // heartbeat and the answer comes back on the agent's thread.
        Changeover = new ChangeoverGate(opt.Value.DataFolder, plant.Configured, () => plant.Status.Connected);
        _plant.Changeover = () => Changeover.Current;
        _plant.ChangeoverAnswered += Changeover.OnAnswer;
        Changeover.Changed += RaiseChanged;
        _drivers = drivers;
        _guard = guard;
        _ngImages = ngImages;
        _jobs = jobs;
        _models = models;
        _images = images;
        _opt = opt.Value;
        _serials = new PartSerial(Path.Combine(_opt.DataFolder, "part-serial.json"));
        var last = Path.Combine(_opt.DataFolder, "last-job.txt");
        if (File.Exists(last))
            try { Load(File.ReadAllText(last).Trim()); } catch (Exception e) { LastError = e.Message; }
    }

    public Job? Job { get; private set; }
    public BatchFields Batch { get; private set; } = new();
    /// <summary>The supervisor's check of the changeover; Start on the cameras waits for it.</summary>
    public ChangeoverGate Changeover { get; }
    public bool Running => _open;
    public RunMode Mode { get; private set; }
    public CamState[] Cams { get; private set; } = Array.Empty<CamState>();
    public int Cycles { get; private set; }
    public int CyclesOk { get; private set; }
    public double LastCycleMs { get; private set; }
    public bool? LastCycleOk { get; private set; }
    public string? LastError { get; private set; }
    public string? LastPartId { get; private set; }
    public StationHistory History { get; } = new();
    public ResourceGuard Guard => _guard;
    public NgImageStore NgImages => _ngImages;

    /// <summary>Good pictures kept for learning (the runtime sets it; null: none are kept).</summary>
    public OkSampler? OkSamples { get; set; }
    /// <summary>Each camera's inspected picture, on the inspecting thread while its buffer is still the camera's (the
    /// runtime's picture check during a run samples it, requirements AS-13a). It must return at once; advice only.</summary>
    public Action<Job, int, GrayImage>? Pictures { get; set; }
    public PlantLinkStatus Plant => _plant.Status;
    public event Action? Changed;
    /// <summary>One camera inspected one image: camera, part number from the PLC (null without), result
    /// (null when there was no picture), error text. Raised on the inspecting thread.</summary>
    public event Action<int, ushort?, JobResult?, string?>? Inspected;
    /// <summary>As <see cref="Inspected"/>, with the PLC's part id as text. For a PLC part raised when the PLC took
    /// the result, so a part the PLC got as NG 100 carries <see cref="PlcLinkLostError"/>.</summary>
    public event Action<CameraReport>? Reported;
    /// <summary>One part was counted, once per part: the cameras that took part with each one's result
    /// (null when it had no picture or an error) and error, the verdict, the time and the PLC part id.
    /// With camera channels each camera's inspection is its own part, and two cameras can finish at
    /// once, so a listener takes the part from here, never from the station's last-cycle values.
    /// Raised on the inspecting thread under the station's lock: keep it short.</summary>
    public event Action<StationPart>? PartCounted;
    /// <summary>New batch values were entered (Start batch).</summary>
    public event Action<BatchFields>? BatchStarted;

    // Real cameras, not stored pictures: only these runs are production for the plant server.
    private bool Production => Mode is RunMode.FreeRun or RunMode.Plc;

    private void Report(int camera, ushort? part, JobResult? r, string? error, string? partId = null, HeldPart? held = null, ImageName? name = null)
    {
        var report = new CameraReport(camera, part, partId, r, error) { JobName = Job?.Name, Batch = Batch, ImageId = name?.WithCamera(camera + 1).ToString() };
        if (held is not null) { lock (held.Reports) held.Reports.Add(report); return; }
        Raise(report);
    }

    private void Raise(CameraReport c)
    {
        try { Inspected?.Invoke(c.Camera, c.Part, c.Result, c.Error); } catch { /* logging must never stop inspection */ }
        try { Reported?.Invoke(c); } catch { }
    }

    // Pages listen here, on the station's thread: one that throws (a page closing) must never
    // stop the inspection or the other listeners.
    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList())
            try { ((Action)h)(); } catch (Exception) { }
    }

    /// <summary>Opens a job; <paramref name="op"/> is who changed the product, when known (they may not accept their own changeover).</summary>
    public void Load(string name, string op = "")
    {
        lock (_lock)
        {
            if (Running) throw new InvalidOperationException("Stop the line before changing the job");
            LoadUnlocked(name, op);
        }
        RaiseChanged();
    }

    private void LoadUnlocked(string name, string op = "")
    {
        var job = _jobs.Load(name) ?? throw new InvalidOperationException($"No job called {name}");
        var runner = new JobRunner(job, _models);
        _runner?.Dispose();
        _runner = runner;
        Job = job;
        Batch = LoadBatch(name);
        Cams = job.Cameras.Select((c, i) => new CamState { Name = c.Name, HasTools = runner.HasTools(i) }).ToArray();
        _camNames = CameraNames.ForJob(job.Cameras.Select(c => (string?)c.Name).ToArray());
        Cycles = CyclesOk = 0;
        History.Clear();
        _ngImages.StartBatch(BatchLabel(job.Name, Batch));
        Changeover.Update(job, Batch, op: op);
        LastCycleOk = null;
        LastError = null;
        Directory.CreateDirectory(_opt.DataFolder);
        File.WriteAllText(Path.Combine(_opt.DataFolder, "last-job.txt"), name);
        _plant.Job(job.Name, job.Version);
    }

    /// <summary>Rebuild the tools after the job was saved again (same name).</summary>
    public void Reload()
    {
        if (Job is { } j && !Running) Load(j.Name);
    }

    /// <summary>New batch values; <paramref name="op"/> is who entered them, when known (they may not accept their own changeover).</summary>
    /// <remarks>Refused while the line runs: every part is judged against <see cref="Batch"/>, so new values mid-run would
    /// be used without the supervisor's check (line safety). Under the station's lock, so a Start cannot slip between.</remarks>
    public void SetBatch(BatchFields b, string op = "")
    {
        Job job;
        lock (_lock)
        {
            if (Job is null) return;
            if (Running) throw new InvalidOperationException("Stop the line before changing the batch");
            job = Job;
            Batch = b;
            Changeover.Update(job, b, op: op);
        }
        // A new batch starts its NG counts at zero; the images already kept stay.
        _ngImages.StartBatch(BatchLabel(job.Name, b));
        Directory.CreateDirectory(Path.Combine(_opt.DataFolder, "batch"));
        File.WriteAllText(Path.Combine(_opt.DataFolder, "batch", job.Name + ".json"), JsonSerializer.Serialize(b.Values, JobStore.Json));
        try { BatchStarted?.Invoke(b); } catch { }
        RaiseChanged();
    }

    // The batch as the operator knows it: the job and the values entered at batch start.
    private static string BatchLabel(string job, BatchFields b)
    {
        var values = string.Join(" ", b.Values.OrderBy(kv => kv.Key).Select(kv => kv.Value).Where(v => !string.IsNullOrWhiteSpace(v)));
        return values.Length == 0 ? $"{job} {DateTime.Now:yyyy-MM-dd HH:mm}" : $"{job} {values}";
    }

    private BatchFields LoadBatch(string job)
    {
        var p = Path.Combine(_opt.DataFolder, "batch", job + ".json");
        var b = new BatchFields();
        if (File.Exists(p))
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new())
                b.Values[k] = v;
        return b;
    }

    public void Start(RunMode mode)
    {
        lock (_lock)
        {
            if (Running) return;
            if (Job is null || _runner is null) throw new InvalidOperationException("Open a job first");
            var missing = Job.BatchFields().Where(f => Batch.Get(f.Key) is null).Select(f => f.Label).ToList();
            if (missing.Count > 0) throw new InvalidOperationException("Start the batch first: enter " + string.Join(", ", missing));
            // Line safety (requirements section 12): never run altered code. A failed integrity check
            // stops production here; the line is told through alarm 931 and parts stay NG.
            if (mode != RunMode.Folder) CheckIntegrity();
            // nor on a results log changed out of band (Req 17.10 REQ-16 item 5): alarm 801, the line does not start
            if (mode != RunMode.Folder && CheckLedger() is { } tampered)
                throw new InvalidOperationException($"ALM-{LedgerGate.AlarmCode}: {tampered} The line will not run until the engineer has checked the results log.");
            // CV-12: a job file swapped on disk while the job is loaded is not the job the supervisor checked.
            if (mode is RunMode.FreeRun or RunMode.Plc && _jobs.ChangedOnDisk(Job) is { } swapped) throw new ChangeoverException(swapped);
            // Production only: stored pictures are a try, never parts that leave the line.
            Changeover.Update(Job, Batch);
            if (mode is RunMode.FreeRun or RunMode.Plc && Changeover.Refusal() is { } why) throw new ChangeoverException(why);
            if (mode != RunMode.Folder) CheckLicence(Job);
            Mode = mode;
            LastError = null;
            OpenSources();
            var run = ++_run;
            // Stored pictures are not production: the plant server hears only camera runs.
            if (Production)
            {
                _plant.Alarm("station_error", false);
                _plant.State(LineStateValue.Running);
            }
            if (mode is RunMode.FreeRun or RunMode.Folder)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = new Thread(() => TimedLoop(token, run)) { IsBackground = true, Name = "station" };
                _loop.Start();
            }
        }
        RaiseChanged();
    }

    /// <summary>
    /// One picture from a job camera while the line is stopped, for teaching (UI-UX-SPEC.md 4.6 step 2): the
    /// camera is opened, fired once by software and closed again, under the station's lock so a Start cannot
    /// open it meanwhile. Null when no frame came in time.
    /// </summary>
    public GrayImage? GrabOne(CameraConfig camera, int timeoutMs = 3000)
    {
        lock (_lock)
        {
            if (Running) throw new InvalidOperationException("Stop the line to take a picture for teaching.");
            CheckCamera(camera);
            using var cam = _drivers.Open(camera);
            return cam.Grab(timeoutMs);
        }
    }

    public void Stop()
    {
        Thread? t;
        bool production;
        lock (_lock)
        {
            t = _loop;
            production = _open && Production;
            _cts?.Cancel();
        }
        t?.Join(5000);
        lock (_lock)
        {
            _loop = null;
            CloseSources();
        }
        // a camera fault belongs to the run: alarm 300 does not outlive Stop (Start finds the camera again).
        // After the part being inspected, which decides from its own results, never from these.
        lock (_cycleLock)
            foreach (var c in Cams) c.Error = null;
        if (production)
        {
            _plant.Alarm("camera_error", false, AlarmPriority.High);
            _plant.State(LineStateValue.Stopped, "operator");
        }
        RaiseChanged();
    }

    /// <summary>Licensed parent tool name of a tool (as in the licence record).</summary>
    public static string LicenceToolOf(ToolConfig t) => t switch
    {
        CodeToolConfig => "barcode",
        TextToolConfig => "ocr",
        LocateToolConfig => "locate",
        MeasureToolConfig => "measure",
        BlobToolConfig => "blob",
        InspectToolConfig => "inspect",
        AnomalyToolConfig => "anomaly",
        ThermalToolConfig => "thermal",
        InfraredToolConfig => "infrared",
        PolarToolConfig => "polar",
        PhotometricToolConfig => "photometric",
        WaveletToolConfig => "wavelet",
        InpaintToolConfig => "inpaint",
        RectifyToolConfig => "rectify",
        DriftToolConfig => "drift",
        DeformToolConfig => "deform",
        PhaseToolConfig => "phase",
        MetamorphicToolConfig => "metamorphic",
        AutotuneToolConfig => "autotune",
        _ => t.GetType().Name,
    };

    // production (cameras, PLC) runs only when the shipped software is unchanged (anti-tamper, line safety)
    private void CheckIntegrity()
    {
        if (!_integrity.Ok)
            throw new InvalidOperationException("Software integrity check failed: " + _integrity.Detail +
                " The line will not run until the software is reinstalled from the signed package.");
    }

    /// <summary>Checks the results ledger (Req 17.10 REQ-16 item 5) and tells the plant server; returns why it
    /// failed, or null. Start runs it before production; the runtime also runs it when it starts.</summary>
    public string? CheckLedger()
    {
        if (_ledger is null) return null;
        var why = _ledger.Check();
        _plant.Alarm("ledger_tampered", why is not null, AlarmPriority.High);
        return why;
    }

    /// <summary>Why the last results ledger check failed (alarm 801), or null.</summary>
    public string? LedgerFailure => _ledger?.Failure;

    /// <summary>Whether production may run as far as the integrity check is concerned (for the alarm and status).</summary>
    public bool IntegrityOk => _integrity.Ok;
    public string IntegrityDetail => _integrity.Detail;

    // production (cameras, PLC) needs a valid licence, licensed tools and licensed cameras
    private void CheckLicence(Job job)
    {
        if (!_licence.Required) return;
        var st = _licence.Status;
        if (!st.Licensed) throw new InvalidOperationException("Not licensed: " + st.Detail + " (Licence page)");
        var tools = job.Tools.Where(t => t.Enabled).Select(LicenceToolOf).Distinct().Where(t => !_licence.ToolLicensed(t)).ToList();
        if (tools.Count > 0) throw new InvalidOperationException("Tools not in the licence: " + string.Join(", ", tools));
        var parts = _licence.Unlicensed();
        if (parts.Count > 0)
            throw new InvalidOperationException("Parts not licensed: " + string.Join(", ", parts.Select(b => $"{b.Device.Kind} {b.Device.Serial} ({b.Reason})")) + " (Licence page)");
        if (st.Payload is { MaxCameras: > 0 } p && job.Cameras.Count(c => c is not null) > p.MaxCameras)
            throw new InvalidOperationException($"The licence covers {p.MaxCameras} camera(s); the job has {job.Cameras.Count}.");
    }

    private void CheckCamera(CameraConfig c)
    {
        if (!_licence.Required) return;
        string serial = c.Serial;
        if (serial.Length == 0) serial = _drivers.List().FirstOrDefault()?.Serial ?? "";
        if (serial.Length > 0 && !_licence.DeviceLicensed(serial))
            throw new InvalidOperationException($"{c.Name}: camera {serial} is not licensed for this installation (Licence page)");
    }

    private void OpenSources()
    {
        var job = Job!;
        bool cameras = Production;
        _cams = new ICamera?[job.Cameras.Count];
        _folders = job.Cameras.Select(c => cameras ? Array.Empty<string>() : FolderSource.List(c.TestFolder ?? "")).ToArray();
        _next = 0;
        Array.Clear(_nextOf);
        try
        {
            if (cameras)
            {
                for (int i = 0; i < _cams.Length; i++)
                    if (Cams[i].HasTools)
                    {
                        CheckCamera(job.Cameras[i]);
                        _cams[i] = _drivers.Open(job.Cameras[i]);
                        // Each image is inspected before the same camera grabs again: reuse frame buffers.
                        _cams[i]!.ReuseBuffers = true;
                    }
            }
            else if (_folders.All(f => f.Count == 0))
                throw new InvalidOperationException("No stored pictures: set a stored pictures folder for each camera in the job");
        }
        catch
        {
            CloseSources();
            throw;
        }
        _open = true;
    }

    private void CloseSources()
    {
        foreach (var c in _cams) c?.Dispose();
        _cams = Array.Empty<ICamera?>();
        _open = false;
    }

    private void TimedLoop(CancellationToken token, int run)
    {
        bool Current() { lock (_lock) return run == _run; }
        try
        {
            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                RunCycle(null);
                var wait = _opt.FolderIntervalMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) token.WaitHandle.WaitOne(wait);
            }
        }
        catch (Exception e)
        {
            LastError = e.Message;
            // An error while stopping is not a fault of the line.
            if (Production && !token.IsCancellationRequested && Current())
            {
                _plant.State(LineStateValue.Fault, "station_error");
                _plant.Alarm("station_error", true, AlarmPriority.High);
            }
        }
        finally
        {
            lock (_lock) { if (run == _run) { _loop = null; CloseSources(); } }
            RaiseChanged();
        }
    }

    /// <summary>The line number in image names (L01): the station setting LineNumber, 1-99.</summary>
    public int LineNumber => Math.Clamp(_opt.LineNumber, 1, 99);

    /// <summary>The name of a picture grabbed in Teach (owner, 2026-10-31): the line, camera index + 1 and the job's
    /// PLC number like a part's, with the day's count of grabs as its serial (its own count, not the parts').</summary>
    public ImageName GrabName(int jobNumber, int camera)
    {
        var (serial, at) = _serials.Next("grab");
        return new ImageName(LineNumber, Math.Clamp(camera + 1, 1, 9), Math.Clamp(jobNumber, 0, 999), at.DateTime, serial);
    }

    // A part's image name (owner, 2026-10-31), for its first camera: line, the job's PLC number (J000 when it has
    // none), local time and the day's serial. key: "" for a whole part, "C2" for a camera fired on its own.
    // Also the .json fields the name leaves out (milliseconds, time zone, PLC ids, batch values).
    private (ImageName Name, Dictionary<string, object?> Meta) NewPartName(Job job, int camera, string key, PlcTrigger? trigger, string? partId)
    {
        var (serial, at) = _serials.Next(key);
        var name = new ImageName(LineNumber, camera + 1, Math.Clamp(job.Number, 0, 999), at.DateTime, serial);
        var meta = new Dictionary<string, object?>
        {
            ["product"] = job.Name,
            ["line"] = name.Line,
            ["job_number"] = name.Job,
            ["serial"] = serial,
            ["time"] = at,
            ["time_zone"] = TimeZoneInfo.Local.Id,
            ["plc_trigger_id"] = trigger?.TriggerId,
            ["part_id"] = partId,
            ["batch_fields"] = new Dictionary<string, string>(Batch.Values),
        };
        return (name, meta);
    }

    // Under High pressure the live picture is drawn every 4th part only: inspection, counts and NG
    // images are unchanged, the screen is just less smooth.
    private bool DrawThisPart(int n) => _guard.Level < Pressure.High || n % 4 == 0;

    // Keeps one camera's image when the NG image store wants it, under the camera's image name (the part's
    // name with this camera), and draws the live picture. True when the image was kept.
    private bool KeepAndDraw(Job job, int i, GrayImage img, JobResult r, bool draw, ImageName part, Dictionary<string, object?> meta,
                             HeldPart? held = null)
    {
        // a bad picture keeps what each failed tool found ("<tool>|<reason key>"), so it can be sorted for learning
        // long after the part (the supervisor's double-check needs them)
        var m = r.Pass ? meta : new Dictionary<string, object?>(meta)
        {
            ["faults"] = r.Tools.Where(t => !t.Pass).Select(t => $"{t.ToolName}|{t.Explain ?? t.Summary}").ToArray(),
        };
        var kept = _ngImages.KeepImage($"{job.Name}-{job.Cameras[i].Name}", r.Pass,
            r.Pass ? Array.Empty<string>() : r.Tools.Where(t => !t.Pass).Select(t => t.ToolName).ToArray(),
            img.Pixels, img.Width, img.Height, img.Stride, null, _camNames[i], m, part.WithCamera(i + 1));
        if (r.Pass && !kept && OkSamples is { } ok) ok.Offer(job, i, _camNames[i], img, r, part.WithCamera(i + 1), meta);
        if (Pictures is { } pictures)
            try { pictures(job, i, img); } catch (Exception) { /* advice only: never stops the part */ }
        // a PLC part judged OK: the PLC may still get NG for it (NG 100, PLC-INTERFACE 5.6); hold its image
        // until the PLC's verdict, so such a part is kept as an NG image like any other (traceability)
        if (held is not null && r.Pass && !kept && _ngImages.KeepsNgImages)
            try { Hold(held, job, i, img, part.WithCamera(i + 1), meta); } catch (Exception) { /* an image copy must never fail the part */ }
        if (draw)
        {
            _images.Put("live" + i, Render.Draw(img, r.Tools.SelectMany(t => t.Graphics),
                job.Tools.Where(t => t.Camera == i && t.Roi is not null && t is not LocateToolConfig).Select(t => t.Roi!)));
            Cams[i].Version++;
        }
        return kept;
    }

    private void Hold(HeldPart held, Job job, int i, GrayImage img, ImageName name, Dictionary<string, object?> meta)
    {
        int w = img.Width, h = img.Height;
        var px = System.Buffers.ArrayPool<byte>.Shared.Rent(w * h);
        for (int y = 0; y < h; y++) img.Pixels.AsSpan(y * img.Stride, w).CopyTo(px.AsSpan(y * w));
        lock (held.Images) held.Images.Add(new HeldImage(i, $"{job.Name}-{job.Cameras[i].Name}", _camNames[i], name, meta, px, w, h));
    }

    // A held part's verdict is known: its images are kept as NG images when the PLC got NG for it (reason), and
    // the pooled copies go back, whatever happens. Returns the image name of the first camera whose image was
    // kept (any camera's name finds them all), as for a part judged NG when inspected.
    private string? KeepHeld(HeldPart held, PlcVerdict? plc)
    {
        HeldImage[] images;
        lock (held.Images) { images = held.Images.OrderBy(im => im.Index).ToArray(); held.Images.Clear(); }
        string? first = null;
        foreach (var im in images)
        {
            try
            {
                if (plc is not null && _ngImages.KeepImage(im.Job, false, [plc.Reason], im.Pixels.AsSpan(0, im.Width * im.Height),
                        im.Width, im.Height, im.Width, null, im.Camera, im.Meta, im.Name))
                    first ??= im.Name.ToString();
            }
            catch (Exception) { /* an image must never cost the part its count or report */ }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(im.Pixels); }
        }
        return first;
    }

    // One count per part over the cameras that took part: a camera without an image or with an error
    // makes the part NG with that reason, so it is never missing from the counts. Also the plant
    // server's result (with the image id when an image of the part was kept) and camera alarm, and the
    // history for the Run screen. Values: this part's measurements (the grades of a sampled part, the Check text
    // margin), sent with it.
    // Results and errors: this part's, by camera index (never the cameras' shared state, which Stop or the
    // next part may change).
    private void CountPart(Job job, bool ok, int[] cameras, GrayImage?[] images, JobResult?[] results, string?[] errors,
                           double ms, string? imageId, IReadOnlyDictionary<string, double>? values = null,
                           string? partId = null, HeldPart? held = null, ImageName? name = null)
    {
        var reasons = new List<string>();
        foreach (var i in cameras)
        {
            if (images[i] is null) reasons.Add("no_image");
            else if (errors[i] is not null) reasons.Add("error");
            else if (results[i] is { Pass: false } r) reasons.AddRange(r.Tools.Where(t => !t.Pass).Select(t => t.ToolName));
        }
        var plantReasons = ok ? Array.Empty<NgReason>() : PlantReasons(job, cameras, images, results, errors);
        var part = new StationPart(cameras, ok, ms, partId,
            cameras.Select(i => images[i] is null || errors[i] is not null ? null : results[i]).ToArray(),
            cameras.Select(i => errors[i] ?? (images[i] is null ? "No picture from the camera" : null)).ToArray()) { Job = job, Name = name };
        bool production = Production;

        // plc: the PLC got NG for this part (PLC-INTERFACE 5.6); an OK inspection is recorded as that NG
        void Commit(PlcVerdict? plc)
        {
            if (!ok) plc = null;
            if (held is not null)
                foreach (var c in held.Reports)
                    Raise(plc is not null && c.Error is null ? c with { Error = plc.Text, ByPlc = true } : c);
            bool final = ok && plc is null;
            if (plc is not null)
            {
                reasons = [plc.Reason];
                plantReasons = [new NgReason("PLC", plc.PlantKey)];
                CyclesOk = Math.Max(0, CyclesOk - 1);
                LastCycleOk = false;
            }
            _ngImages.CountPart(final, reasons);
            // the PLC got NG for an OK inspection: its images are kept as NG images now
            if (held is not null && KeepHeld(held, plc) is { } heldImage) imageId ??= heldImage;
            if (production)
            {
                Interlocked.Increment(ref final ? ref _plantGood : ref _plantBad);
                if (_open && Production) _plant.State(LineStateValue.Running);   // sent only when it changes; not after Stop
                _plant.Result(final, plantReasons, ms, partId: partId, values: values, imageId: imageId);
                _plant.Alarm("camera_error", Cams.Any(c => c.HasTools && c.Error is not null), AlarmPriority.High);
            }
            History.Add(DateTime.Now, final, ms);
            if (PartCounted is { } counted)
                try { counted(plc is not null ? part with { Ok = false, Plc = plc } : part); }
                catch { /* a listener must never stop inspection */ }
        }

        if (held is null) { Commit(null); return; }
        held.Commit = Commit;
        HeldPart? old;
        lock (_held)
        {
            _held.Remove(held.Source, out old);
            _held[held.Source] = held;
        }
        // never handed over (the PLC runtime closed before it ended): the PLC never took it, so NG
        if (old is not null) Queue(old, PlcVerdict.LinkLost);
    }

    /// <summary>The PLC runtime says what the PLC got for a held part (PLC-INTERFACE 5.6): the part is
    /// counted, logged and reported with that verdict, as NG when the PLC got NG for an OK inspection.
    /// Only queued here: the PLC thread never waits on the log or the plant report.</summary>
    public void Delivered(PlcDelivery d)
    {
        HeldPart? h;
        lock (_held)
        {
            if (!_held.TryGetValue(d.Camera, out h) || !ReferenceEquals(h.Trigger, d.Trigger)) return;
            _held.Remove(d.Camera);
        }
        // the PLC took it OK (the usual case): its image copies go back now, not when the log gets to it
        if (d.Result.Ok) KeepHeld(h, null);
        Queue(h, d.Result.Ok ? null : d.LinkLossNg ? PlcVerdict.LinkLost : d.NotTaken ? PlcVerdict.NotTaken : PlcVerdict.Ng);
    }

    private void Queue(HeldPart h, PlcVerdict? plc)
    {
        lock (_commitsLock)
            _commits = _commits.ContinueWith(_ =>
            {
                try { lock (_lock) h.Commit?.Invoke(plc); }
                catch { /* one part's log must never stop the next */ }
                RaiseChanged();
            }, TaskScheduler.Default);
    }

    /// <summary>Waits until every part handed over so far is counted and logged (tests, shutdown).</summary>
    public void WaitCounted(int timeoutMs = 5000)
    {
        Task t;
        lock (_commitsLock) t = _commits;
        t.Wait(timeoutMs);
    }

    /// <summary>Fires every camera of the job once (or takes the next stored pictures) and inspects.</summary>
    private (bool Ok, JobResult?[] Results, bool CameraFault) RunCycle(string? partId, PlcValues? plc = null, PlcTrigger? trigger = null)
    {
        lock (_cycleLock)
        {
            var held = trigger is { Held: true } ? new HeldPart(-1, trigger) : null;
            var job = Job!;
            var runner = _runner!;
            int n = _next++;
            var sw = Stopwatch.StartNew();
            var results = new JobResult?[_cams.Length];
            var errors = new string?[_cams.Length];
            var images = new GrayImage?[_cams.Length];
            var draw = DrawThisPart(n);
            // One image name per part: its cameras' names differ only in the camera. The part's image id is the
            // name of its first camera whose image was kept (any camera's name finds them all).
            var (partName, meta) = NewPartName(job, 0, "", trigger, partId);
            var kept = 0;
            _guard.PartStarted();
            try
            {
                Parallel.For(0, _cams.Length, i =>
                {
                    if (!Cams[i].HasTools) return;
                    try
                    {
                        var img = images[i] = _cams[i] is { } cam ? cam.Grab(cam.FrameWaitMs)
                            : _folders[i].Count > 0 ? GrayImage.Load(_folders[i][n % _folders[i].Count]) : null;
                        if (img is null) { Cams[i].Error = errors[i] = "No picture from the camera"; Report(i, null, null, errors[i], partId, held, partName); return; }
                        var r = runner.Run(i, img, Batch, plc);
                        results[i] = r;
                        Report(i, null, r, null, partId, held, partName);
                        var st = Cams[i];
                        st.Last = r;
                        st.Error = null;
                        if (r.Pass) st.Good++; else st.Bad++;
                        if (KeepAndDraw(job, i, img, r, draw, partName, meta, held)) Interlocked.Or(ref kept, 1 << i);
                    }
                    catch (Exception e) { Cams[i].Error = errors[i] = e.Message; Report(i, null, null, e.Message, partId, held, partName); }
                });
            }
            finally { _guard.PartFinished(); }
            // this part's own results: a camera with no picture or an error is NG even if Stop cleared its
            // error meanwhile or its last result was a pass (requirements section 12)
            var used = Enumerable.Range(0, Cams.Length).Where(i => Cams[i].HasTools).ToArray();
            bool ok = used.All(i => errors[i] is null && results[i]?.Pass == true);
            lock (_lock)
            {
                Cycles++;
                if (ok) CyclesOk++;
                LastCycleOk = ok;
                LastCycleMs = sw.Elapsed.TotalMilliseconds;
                LastPartId = partId;
                CountPart(job, ok, used, images, results, errors, LastCycleMs,
                    kept != 0 ? partName.WithCamera(System.Numerics.BitOperations.TrailingZeroCount(kept) + 1).ToString() : null,
                    PartValues(results), partId, held, partName);
            }
            RaiseChanged();
            return (ok, results, used.Any(i => errors[i] is not null));
        }
    }

    // Each tool's measurements of this part over its cameras (this cycle's results, never a previous part's). A Check
    // text margin met on more than one camera keeps the lowest: how close the part's weakest print is to failing.
    private static Dictionary<string, double> PartValues(IEnumerable<JobResult?> results) =>
        results.Where(r => r is not null).SelectMany(r => r!.Values)
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key,
                g => g.Key.EndsWith(".margin", StringComparison.Ordinal) ? g.Min(kv => kv.Value) : g.First().Value);

    // Why a part was NG, for the plant server: the failing tool and its reason key (explain.* without
    // the prefix), or the camera with no_image / error. Never empty for an NG.
    private static NgReason[] PlantReasons(Job job, int[] cameras, GrayImage?[] images, JobResult?[] results, string?[] errors)
    {
        var list = new List<NgReason>();
        foreach (var i in cameras)
        {
            if (images[i] is null) list.Add(new NgReason(job.Cameras[i].Name, "no_image"));
            else if (errors[i] is not null) list.Add(new NgReason(job.Cameras[i].Name, "error"));
            else if (results[i] is { Pass: false } r)
                list.AddRange(r.Tools.Where(t => !t.Pass).Select(t => new NgReason(t.ToolName,
                    t.Explain is { Length: > 0 } x ? (x.StartsWith("explain.") ? x["explain.".Length..] : x) : "failed")));
        }
        if (list.Count == 0) list.Add(new NgReason("station", "failed"));
        return list.ToArray();
    }

    private HealthSample? PlantHealth()
    {
        var s = _guard.Status.Last;
        if (s is null) return null;
        static double Pct(double v) => Math.Clamp(double.IsFinite(v) ? v : 0, 0, 100);
        return new HealthSample
        {
            // cpu_pct is the whole PC's; the guard samples only this program's CPU, so it is left out.
            RamPct = s.SystemTotalBytes > 0 ? Pct(100.0 * s.SystemLoadBytes / s.SystemTotalBytes) : null,
            DiskPct = s.DiskTotalBytes > 0 ? Pct(100.0 - 100.0 * s.DiskFreeBytes / s.DiskTotalBytes) : null,
        };
    }

    // ---- PLC (IPlcHost)

    public bool CanInspect => _open && Mode is RunMode.Plc or RunMode.PlcSimulation && Job is not null;
    public ushort ActiveJobNumber => (ushort)Math.Clamp(Job?.Number ?? 0, 0, 999);
    public bool Simulation => Mode == RunMode.PlcSimulation;
    public int CameraCount => Job?.Cameras.Count ?? 0;

    /// <summary>One camera, fired when the part reaches it (camera channels): only that camera's tools run.</summary>
    public PlcInspection InspectCamera(int camera, PlcTrigger trigger)
    {
        var job = Job!;
        var runner = _runner!;
        lock (_camLocks[camera])
        {
            var st = Cams[camera];
            if (!st.HasTools) return new PlcInspection(true, 0, 0, "", "", Array.Empty<float>());
            var images = new GrayImage?[Cams.Length];
            var sw = Stopwatch.StartNew();
            var (partName, meta) = NewPartName(job, camera, $"C{camera + 1}", trigger, trigger.PartId);
            var kept = false;
            var held = trigger.Held ? new HeldPart(camera, trigger) : null;
            JobResult r;
            _guard.PartStarted();
            try
            {
                try
                {
                    images[camera] = _cams.ElementAtOrDefault(camera) is { } cam ? cam.Grab(cam.FrameWaitMs)
                        : _folders.ElementAtOrDefault(camera) is { Count: > 0 } f ? GrayImage.Load(f[_nextOf[camera]++ % f.Count]) : null;
                }
                catch (Exception e) { st.Error = e.Message; }
                if (images[camera] is not { } img)
                {
                    st.Error ??= "No picture from the camera";
                    st.Bad++;
                    Report(camera, trigger.TriggerId, null, st.Error, trigger.PartId, held, partName);
                    var noErrors = new string?[Cams.Length];
                    noErrors[camera] = st.Error;
                    lock (_lock) CountPart(job, false, new[] { camera }, images, new JobResult?[Cams.Length], noErrors,
                        sw.Elapsed.TotalMilliseconds, null, partId: trigger.PartId, held: held, name: partName);
                    RaiseChanged();
                    return new PlcInspection(false, 0, 1, "", "", Array.Empty<float>(), PlcCodes.NoFrame);
                }
                r = runner.Run(camera, img, Batch, PlcValues.Of(trigger));
                Report(camera, trigger.TriggerId, r, null, trigger.PartId, held, partName);
                st.Last = r;
                st.Error = null;
                if (r.Pass) st.Good++; else st.Bad++;
                kept = KeepAndDraw(job, camera, img, r, DrawThisPart(st.Good + st.Bad), partName, meta, held);
            }
            finally { _guard.PartFinished(); }
            lock (_lock)
            {
                Cycles++;
                if (r.Pass) CyclesOk++;
                LastCycleOk = r.Pass;
                LastCycleMs = r.TotalMs;
                LastPartId = trigger.PartId;
                var one = new JobResult?[Cams.Length];
                one[camera] = r;
                CountPart(job, r.Pass, new[] { camera }, images, one, new string?[Cams.Length], r.TotalMs,
                    kept ? partName.ToString() : null, r.Values, trigger.PartId, held, partName);
            }
            RaiseChanged();
            // first failing tool of this camera, numbered in job order
            var enabled = job.Tools.Where(t => t.Enabled).ToList();
            ushort first = 0;
            string text1 = "", text2 = "";
            foreach (var tr in r.Tools)
            {
                int k = enabled.FindIndex(t => t.Name == tr.ToolName);
                if (!tr.Pass && first == 0 && k >= 0) first = (ushort)(k + 1);
                if (k >= 0 && enabled[k] is CodeToolConfig && tr.Pass && text1.Length == 0) text1 = tr.Summary;
                if (k >= 0 && enabled[k] is TextToolConfig && tr.Lines is { Count: > 0 } lines && text2.Length == 0)
                    text2 = lines.FirstOrDefault(l => l.Checked)?.Text ?? lines[0].Text;
            }
            return new PlcInspection(r.Pass, 0, first, text1, text2, Array.Empty<float>());
        }
    }

    public PlcInspection Inspect(PlcTrigger trigger)
    {
        var (ok, results, cameraFault) = RunCycle(trigger.PartId, PlcValues.Of(trigger), trigger);
        var job = Job!;
        // tool bits and the first failing tool, in job order over all cameras
        var enabled = job.Tools.Where(t => t.Enabled).ToList();
        uint pass = 0;
        ushort first = 0;
        string text1 = "", text2 = "";
        for (int k = 0; k < enabled.Count; k++)
        {
            var r = results.ElementAtOrDefault(enabled[k].Camera)?.Tools.FirstOrDefault(t => t.ToolName == enabled[k].Name);
            bool p = r?.Pass == true;
            if (p && k < 32) pass |= 1u << k;
            if (!p && first == 0) first = (ushort)(k + 1);
            if (r is null) continue;
            if (enabled[k] is CodeToolConfig && text1.Length == 0 && r.Pass) text1 = r.Summary;
            if (enabled[k] is TextToolConfig && text2.Length == 0 && r.Lines is { Count: > 0 } lines)
                text2 = lines.FirstOrDefault(l => l.Checked)?.Text ?? lines[0].Text;
        }
        ushort err = cameraFault ? PlcCodes.NoFrame : (ushort)0;
        return new PlcInspection(ok, pass, first, text1, text2, Array.Empty<float>(), err);
    }

    /// <remarks>A job that cannot be loaded answers JobNotFound with the reason in <see cref="LastError"/>;
    /// CameraNotFound is only for cameras of a loaded job that do not open. Either way the line is not ready.</remarks>
    public ushort LoadJob(ushort number)
    {
        ushort err = 0;
        var name = _jobs.FindByNumber(number);
        if (name is null)
        {
            LastError = NoJobReason(number);
            err = PlcCodes.JobNotFound;
        }
        else
            lock (_lock)
            {
                var mode = Mode;
                bool was = _open;
                CloseSources();
                try
                {
                    LoadUnlocked(name);
                    try
                    {
                        if (was) OpenSources();
                        Mode = mode;
                    }
                    catch (Exception e) { LastError = e.Message; err = PlcCodes.CameraNotFound; }
                }
                catch (Exception e) { LastError = e.Message; err = PlcCodes.JobNotFound; }
            }
        RaiseChanged();
        return err;
    }

    // FindByNumber passes over a job that cannot run: name it and say why, so the line is told more than "not found"
    private string NoJobReason(ushort number)
    {
        if (number == 0) return "The PLC selected job number 0; jobs are numbered from 1.";
        var unread = new List<string>();
        foreach (var n in _jobs.Names())
        {
            Job? j;
            try { j = _jobs.LoadForEdit(n); }
            catch (JobLoadException e) { unread.Add(e.Message); continue; }
            if (j?.Number != number) continue;
            try { _jobs.Load(n); }
            catch (JobLoadException e) { return e.Message; }
        }
        return unread.Count == 0
            ? $"No job has number {number}."
            : $"No job has number {number}, or it is in a file that cannot be read: {string.Join("; ", unread)}.";
    }

    public void Dispose()
    {
        Stop();
        _runner?.Dispose();
        _imageSource.Dispose();
        _serials.Dispose();
    }
}
