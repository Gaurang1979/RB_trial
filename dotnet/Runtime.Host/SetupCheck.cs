using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using VisionCore;
using VisionStudio.Services;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>What the setup check needs of the line: the loaded job, whether the line runs, and pictures
/// outside a run.</summary>
public interface ISetupLine
{
    Job? Job { get; }
    bool Running { get; }
    BatchFields Batch { get; }
    /// <summary>Raised on job load, start, stop and after parts.</summary>
    event Action? Changed;
    /// <summary>"camera" (grabbed by software) or "stored" (the camera's stored pictures, a replay line).</summary>
    string Source { get; }
    /// <summary>Picture <paramref name="shot"/> (0, 1) of a job camera while the line is stopped; null with the
    /// reason in plain words when none came.</summary>
    (GrayImage? Image, string? Error) Picture(CameraConfig camera, int shot);
}

/// <summary>
/// The setup checks at job load (requirements AS-13, AS-14, section 16 "When the picture and pixel checks run";
/// owner, 2026-11-01: "each job load requires to capture 1-2 images before run so lens, pixels etc checks can be
/// done", "in running job we can not disturb", "advice only"). Once per job load (a product change, the PLC's job
/// select, the runtime starting with a job, a job saved again), while the line is stopped, each camera with tools
/// gives one picture, or two when the first came to nothing or a Read code or text tool found nothing to measure on
/// it. Each picture gets the picture check per corner and centre, and the job's tools run on it with the setup-only
/// size measure on for the pixel check per tool. Advice only: it never stops Run and never decides a part; a camera
/// with no picture is said plainly. Never in a running batch: Start stops a check still going and drops its pictures,
/// and no picture is taken or measured once the line runs.
/// </summary>
public sealed class SetupCheck : IDisposable
{
    public const int MaxPictures = 2;
    readonly ISetupLine _line;
    readonly Func<Job, JobRunner> _tools;
    readonly object _lock = new();
    // The job object checked last (or being checked): every load makes a new one, so the same job loaded again is
    // checked again, and start, stop and parts (the same object) are not.
    Job? _checked;
    CancellationTokenSource? _cts;
    int _run;
    V1.SetupCheck? _current;
    Task _last = Task.CompletedTask;

    /// <summary><paramref name="tools"/> builds the job's tools for the check, with the size measure on.</summary>
    public SetupCheck(ISetupLine line, Func<Job, JobRunner> tools)
    {
        _line = line;
        _tools = tools;
        line.Changed += OnLineChanged;
        OnLineChanged();   // the runtime started with a job
    }

    /// <summary>The check of the loaded job as the status carries it; null when nothing is checked. Never changed
    /// once given out: each step is a new message.</summary>
    public V1.SetupCheck? Current { get { lock (_lock) return _current; } }

    /// <summary>The check started last, for tests: done when it ended or was stopped.</summary>
    public Task Last { get { lock (_lock) return _last; } }

    /// <summary>Raised when <see cref="Current"/> changed, on the check's thread or the line's.</summary>
    public event Action? Changed;

    /// <summary>The line is about to start: a check still going stops now and its pictures are dropped.</summary>
    public void Stop()
    {
        bool changed;
        lock (_lock) changed = StopUnlocked();
        if (changed) Raise();
    }

    /// <summary>Start was refused (a batch value missing, the licence): a check it stopped runs again, since the line
    /// is still stopped and Run has not happened.</summary>
    public void Retry()
    {
        lock (_lock)
            if (_current is { State: V1.SetupCheck.Types.State.Stopped }) _checked = null;
        OnLineChanged();
    }

    void OnLineChanged()
    {
        var job = _line.Job;
        var changed = false;
        lock (_lock)
        {
            if (_line.Running) changed = StopUnlocked();   // a job loaded while the line runs is checked after Stop
            else if (job is null)
            {
                changed = StopUnlocked() | _current is not null;
                _checked = null;
                _current = null;
            }
            else if (!ReferenceEquals(job, _checked))
            {
                StopUnlocked();
                _checked = job;
                var cameras = CamerasWithTools(job);
                if (cameras.Count == 0) _current = null;   // nothing to check: Start refuses a job that checks nothing
                else
                {
                    _current = new V1.SetupCheck { State = V1.SetupCheck.Types.State.Checking, Job = job.Name, Time = Now() };
                    var cts = _cts = new CancellationTokenSource();
                    var run = ++_run;
                    _last = Task.Factory.StartNew(() => Work(job, cameras, run, cts.Token), CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                changed = true;
            }
        }
        if (changed) Raise();
    }

    // Under _lock: a check still going stops and says so. True when the state changed.
    bool StopUnlocked()
    {
        _cts?.Cancel();
        _cts = null;
        if (_current is not { State: V1.SetupCheck.Types.State.Checking } c) return false;
        _current = new V1.SetupCheck { State = V1.SetupCheck.Types.State.Stopped, Job = c.Job, Time = Now() };
        return true;
    }

    static List<int> CamerasWithTools(Job job) =>
        Enumerable.Range(0, job.Cameras.Count).Where(i => job.Tools.Any(t => t.Enabled && t.Camera == i)).ToList();

    void Work(Job job, List<int> cameras, int run, CancellationToken ct)
    {
        // setup work: the PC's other work (the engineer's screen, the PLC link) comes first
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
        bool Stopped() => ct.IsCancellationRequested || _line.Running;
        var result = new List<V1.CameraSetupCheck>();
        JobRunner? runner = null;
        string? toolsError = null;
        try
        {
            if (Stopped()) return;
            try { runner = _tools(job); }
            catch (Exception e) { toolsError = $"The tools cannot be built: {e.Message}"; }
            foreach (var i in cameras)
            {
                var camera = job.Cameras[i];
                var check = new V1.CameraSetupCheck { Camera = camera.Name, Source = _line.Source };
                var sized = job.Tools.Where(t => t.Enabled && t.Camera == i && t is CodeToolConfig or TextToolConfig).ToList();
                var measured = new Dictionary<string, V1.ToolResult>();
                PictureCheckResult? best = null;
                var bestDetail = -1;
                string? error = null;
                for (var shot = 0; shot < MaxPictures; shot++)
                {
                    if (Stopped()) return;
                    GrayImage? img;
                    try { (img, error) = _line.Picture(camera, shot); }
                    catch (Exception e) { (img, error) = (null, e.Message); }
                    if (Stopped()) return;
                    if (img is null) continue;
                    check.Pictures++;
                    var picture = PictureCheck.Measure(img);
                    var detail = picture.Zones.Count(z => z.State != ZoneState.NoDetail);
                    if (detail > bestDetail) (best, bestDetail) = (picture, detail);
                    if (runner is not null && sized.Count > 0)
                    {
                        var r = runner.Run(i, img, _line.Batch);
                        if (Stopped()) return;
                        foreach (var t in r.Tools)
                        {
                            if (measured.ContainsKey(t.ToolName) || sized.FirstOrDefault(x => x.Name == t.ToolName) is not { } cfg) continue;
                            if (VisionCore.PixelCheck.Check(cfg, t, camera) is { } pixels)
                                measured[t.ToolName] = new V1.ToolResult { Name = t.ToolName, Kind = Station.LicenceToolOf(cfg), Pixels = JobDesigner.Pixels(pixels) };
                        }
                    }
                    // a second picture only when this one left a tool with nothing to measure
                    if (sized.All(t => measured.ContainsKey(t.Name)) || runner is null) break;
                }
                check.Error = check.Pictures == 0 ? error ?? "No picture came from the camera. Check its cable and power." : toolsError ?? "";
                if (best is not null) check.PictureCheck = JobDesigner.Check(best);
                check.Tools.AddRange(sized.Where(t => measured.ContainsKey(t.Name)).Select(t => measured[t.Name]));
                if (check.Pictures > 0 && runner is not null) check.Unmeasured.AddRange(sized.Where(t => !measured.ContainsKey(t.Name)).Select(t => t.Name));
                result.Add(check);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // advice only: a check that fails says so and never touches the line
            result.Clear();
            foreach (var i in cameras)
                result.Add(new V1.CameraSetupCheck { Camera = job.Cameras[i].Name, Source = _line.Source, Error = $"The setup check failed: {e.Message}" });
        }
        finally { runner?.Dispose(); }
        lock (_lock)
        {
            // a newer load, or Start, took over: these pictures are dropped
            if (run != _run || ct.IsCancellationRequested || _line.Running) return;
            var done = new V1.SetupCheck { State = V1.SetupCheck.Types.State.Done, Job = job.Name, Time = Now() };
            done.Cameras.AddRange(result);
            _current = done;
        }
        Raise();
    }

    static Timestamp Now() => Timestamp.FromDateTime(DateTime.UtcNow);

    void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception) { /* a listener never stops the check or the line */ }
    }

    /// <summary>The job's tools for the check, on a copy of the job so the line's job is never touched, with the
    /// setup-only size measure on (never on the line).</summary>
    public static Func<Job, JobRunner> ToolsOf(ModelPaths models) => job =>
    {
        var copy = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, JobStore.Json), JobStore.Json)!;
        return new JobRunner(copy, models) { MeasurePixels = true };
    };

    public void Dispose()
    {
        _line.Changed -= OnLineChanged;
        lock (_lock) StopUnlocked();
    }
}

/// <summary>The station as the setup check sees it. A camera line grabs one picture as Grab in job setup does
/// (<see cref="Station.GrabOne"/>: opened, fired once by software, closed, under the station's lock so Start cannot
/// open the camera meanwhile); outside a run the PLC may keep the light off, so a dark picture can mean only that.
/// A replay line takes the camera's stored pictures.</summary>
public sealed class StationSetupLine(Station station, RuntimeOptions options) : ISetupLine
{
    /// <summary>Short: Start waits for a picture already being taken, at most this long.</summary>
    public const int GrabTimeoutMs = 1000;

    public Job? Job => station.Job;
    public bool Running => station.Running;
    public BatchFields Batch => station.Batch;
    public event Action? Changed { add => station.Changed += value; remove => station.Changed -= value; }
    public string Source => options.DefaultSource() == V1.Source.Replay ? "stored" : "camera";

    public (GrayImage? Image, string? Error) Picture(CameraConfig camera, int shot)
    {
        if (Source == "stored")
        {
            var files = FolderSource.List(camera.TestFolder ?? "");
            return files.Count == 0 ? (null, "This camera has no stored pictures to check.") : (GrayImage.Load(files[shot % files.Count]), null);
        }
        try
        {
            return station.GrabOne(camera, GrabTimeoutMs) is { } img ? (img, null) : (null, "No picture came from the camera. Check its cable and power.");
        }
        catch (InvalidOperationException e) { return (null, e.Message); }
    }
}
