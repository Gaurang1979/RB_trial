using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using HealthSample = PlantEvents.HealthSample;
using SkiaSharp;
using StationAgent;
using StationGuard;
using VisionCore;
using VisionCore.Plc;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>
/// The line as the runtime contract shows it: the station of VisionStudio (one job, its cameras,
/// the batch, the PLC link, NG images, the plant link) behind the contract's calls. The station
/// itself is used as it is until draft #70 merges; then it moves into the runtime (Pending).
/// Callers have checked access (<see cref="Sessions"/>). Line safety (requirements section 12):
/// a part is OK only when the station says so; a trigger while the line is not running, or a
/// job that checks nothing, is refused rather than passed.
/// </summary>
public sealed class LineRuntime : IDisposable
{
    public const string Version = "1.0.0";
    const int KeepImages = 64, KeepCycles = 1000;
    /// <summary>Bad parts' picture sources kept for the supervisor's NG review: a review may come minutes after the part.</summary>
    const int KeepNgSources = 500;

    readonly Station _station;
    readonly JobStore _jobs;
    readonly PlcService _plc;
    readonly LicenceService _licence;
    readonly RemoteServiceBadge _remote;
    readonly ImageCache _images;
    readonly ResourceGuard _guard;
    readonly StationPublisher _plant;
    readonly FrameStats _frames;
    readonly RuntimeOptions _opt;
    readonly StudioOptions _studio;
    readonly SetupCheck _setup;
    readonly PictureDrift _pictures;
    readonly object _lock = new();
    readonly List<Channel<V1.Status>> _statusWatchers = [];
    readonly List<Channel<InspectionResult>> _resultWatchers = [];
    readonly List<Channel<LiveFrame>> _frameWatchers = [];
    readonly LinkedList<(string Id, Render.Frame? Live, string? File)> _recentImages = new();
    readonly LinkedList<(string Id, string? File, string Job, int Camera, string CameraName, string[] Faults)> _ngSources = new();
    readonly LinkedList<InspectionResult> _recentResults = new();
    readonly List<double> _cycles = [];
    readonly Dictionary<string, ulong> _ngReasons = [];
    CamState[] _seenCams = [];
    int[] _seenVersion = [];
    ulong _nextInspection = 1, _total, _ok, _ng;
    uint _nextTrigger = 1;
    Timestamp? _batchStarted;
    // the last part, start or stop: the line is idle when nothing happened for the set idle time (learning session)
    DateTimeOffset _activeAt = DateTimeOffset.UtcNow;
    bool _wasRunning;

    readonly Micro850Bridge? _micro850;

    public LineRuntime(Station station, JobStore jobs, PlcService plc, LicenceService licence, ImageCache images,
        ResourceGuard guard, StationPublisher plant, FrameStats frames, IOptions<RuntimeOptions> opt, IOptions<StudioOptions> studio,
        SetupCheck setup, RemoteServiceBadge remote, Micro850Bridge? micro850 = null)
    {
        _micro850 = micro850;
        _remote = remote;
        remote.Changed += PublishStatus;   // a remote session or request shows on the line screen at once
        _station = station;
        _jobs = jobs;
        _plc = plc;
        _licence = licence;
        _images = images;
        _guard = guard;
        _plant = plant;
        _frames = frames;
        _opt = opt.Value;
        _studio = studio.Value;
        _setup = setup;
        Grades = new GradeTrail(Path.Combine(_studio.DataFolder, "grades.jsonl"));
        Seen(station.Cams);
        // health.sample with the whole PC's CPU (PR #97) and the frames the cameras missed; the
        // station's own sample leaves cpu_pct out.
        plant.Health = PlantHealth;
        station.Changed += OnStationChanged;
        station.PartCounted += OnPart;
        _pictures = new PictureDrift(_opt.PictureCheckEvery);
        _pictures.Changed += PublishStatus;
        station.Pictures = _pictures.Offer;
        plc.Changed += PublishStatus;
        guard.Sampled += _ => PublishStatus();
        setup.Changed += PublishStatus;
    }

    // --- state --------------------------------------------------------------------------------

    public V1.Status Status()
    {
        var job = _station.Job;
        var running = _station.Running;
        var s = new V1.Status
        {
            State = running ? V1.Status.Types.State.Busy
                : _station.LastError is not null || job is null ? V1.Status.Types.State.Error
                : V1.Status.Types.State.Stopped,
            ActiveJob = job?.Name ?? "",
            ActiveJobNumber = job?.Number ?? 0,
            PlcConnected = _plc.Runtime?.Connected == true,
            Version = Version,
            Source = _station.Mode is RunMode.Folder or RunMode.PlcSimulation ? V1.Source.Replay : V1.Source.Camera,
            LicenceOk = !_licence.Required || _licence.Status.Licensed,
            Health = Health(),
            SetupCheck = _setup.Current,
            LineSpeedPpm = (float)Math.Max(0, _opt.LineSpeedPpm),
            CycleBudgetMs = (float)Math.Max(0, _opt.CycleBudget()),
        };
        InspectionResult? last;
        lock (_lock)
        {
            s.Inspections = _total;
            s.Ng = _ng;
            s.BatchStarted = _batchStarted;
            last = _recentResults.First?.Value;
        }
        if (!string.Equals(_plc.Options.Protocol, "Off", StringComparison.OrdinalIgnoreCase))
        {
            // read without the PLC's lock: the status never waits for the PLC thread. A PLC set up
            // whose link could not start is a lost link, not "no PLC".
            var plc = _plc.Runtime;
            s.Plc = new PlcState
            {
                LinkUp = plc?.LinkUp == true,
                HeartbeatOk = plc?.HeartbeatOk == true,
                RunEnable = plc?.RunEnable == true,
                LastTriggerId = plc?.LastTriggerId ?? 0,
                Link = $"{_plc.Options.Protocol} :{_plc.Options.Port}",
            };
            if (last is not null)
            {
                s.Plc.LastResultTime = last.Time;
                s.Plc.LastResultPass = last.Pass;
            }
        }
        if (job is null) { s.ErrorText = "No job is open. Choose a product."; s.ErrorCode = PlcCodes.JobNotFound; }
        else if (!running && _station.LastError is { } e) s.ErrorText = e;
        var co = _station.Changeover.Status();
        s.Changeover = new V1.Changeover
        {
            Required = co.Required,
            State = co.State switch
            {
                ChangeoverState.NotNeeded => V1.Changeover.Types.State.NotNeeded,
                ChangeoverState.Accepted => V1.Changeover.Types.State.Accepted,
                ChangeoverState.Refused => V1.Changeover.Types.State.Refused,
                ChangeoverState.NoServer => V1.Changeover.Types.State.NoServer,
                _ => V1.Changeover.Types.State.Waiting,
            },
            Code = co.ShortCode,
            By = co.By ?? "",
            At = co.At is { } at ? Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(at)) : null,
            Note = co.Note ?? "",
            AtLine = co.AtLine,
        };
        if (job is not null)
        {
            try { s.Learning = Learning(job); } catch (Exception) { /* advice only: the status never fails for it */ }
            foreach (var f in job.BatchFields())
                s.Batch.Add(new BatchField { Key = f.Key, Label = f.Label, Example = f.Example ?? "", Value = _station.Batch.Get(f.Key) ?? "" });
            var cams = _station.Cams;
            for (var i = 0; i < job.Cameras.Count; i++)
            {
                var err = i < cams.Length ? cams[i].Error : null;
                s.Cameras.Add(new CameraState
                {
                    Name = job.Cameras[i].Name,
                    Serial = job.Cameras[i].Serial,
                    Connected = running && s.Source == V1.Source.Camera && err is null,
                });
            }
        }
        s.Alarms.AddRange(Alarms().OrderByDescending(a => a.Level));
        if (s.ErrorCode == 0 && s.Alarms.FirstOrDefault(a => a.Level == Alarm.Types.Level.Alarm) is { } top) s.ErrorCode = top.Code;
        // the line screen's neutral "Remote service active" badge (REQ-11, PROT-30): that a session is open, never who
        var (active, since) = _remote.Read();
        var requests = _remote.Requests();
        if (active || requests.Count > 0)
        {
            s.RemoteService = new RemoteService { Active = active };
            if (since is { } t) s.RemoteService.Since = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(t);
            s.RemoteService.Requests.AddRange(requests);   // what changes, never who asks (REQ-08)
        }
        return s;
    }

    IEnumerable<Alarm> Alarms()
    {
        static Alarm A(uint code, Alarm.Types.Level level, string text, string help) => new() { Code = code, Level = level, Text = text, Help = help };
        if (!_station.Running && _station.LastError is { } e && _station.Job is not null)
            yield return A(900, Alarm.Types.Level.Alarm, $"The line stopped: {e}", "Put right what the message says, then press Start.");
        foreach (var c in _station.Cams)
            if (c.HasTools && c.Error is { } ce)
                yield return A(PlcCodes.CameraNotFound, Alarm.Types.Level.Alarm, $"{c.Name}: {ce}. Its parts are NG.",
                    "Check the camera's cable and power. If it stays, call the engineer.");
        if (!string.Equals(_plc.Options.Protocol, "Off", StringComparison.OrdinalIgnoreCase))
        {
            if (_plc.Runtime is null)
                yield return A(PlcCodes.HeartbeatLost, Alarm.Types.Level.Alarm, $"The PLC link could not start: {_plc.LastError}", "Check the PLC settings with the engineer.");
            else if (!_plc.Runtime.Connected)
                yield return A(PlcCodes.HeartbeatLost, Alarm.Types.Level.Alarm, "The PLC is not connected. The PLC treats every part as NG.",
                    "Check the network cable between this PC and the PLC.");
        }
        var g = _guard.Status;
        if (g.Level == Pressure.Critical)
            yield return A(911, Alarm.Types.Level.Alarm, "This PC is almost out of memory or disk.", "Finish the batch, then restart the PC. Call the engineer if it comes back.");
        else if (g.Level == Pressure.High)
            yield return A(910, Alarm.Types.Level.Warning, "This PC is short of memory or disk; the live picture is shown less often.", "Inspection goes on. Tell the engineer.");
        if (g.RestartRecommended)
            yield return A(912, Alarm.Types.Level.Warning, "The runtime should be restarted.", "Restart it between batches.");
        // advice only (AS-13a): the tools still decide each part
        foreach (var w in _pictures.Warnings)
            yield return A(PictureDrift.AlarmCode, Alarm.Types.Level.Warning, w.Text, w.Help);
        if (_plant.Enabled && !_plant.Status.Connected)
            yield return A(920, Alarm.Types.Level.Info, "The plant server cannot be reached. Results wait on this PC and are sent later.", "Nothing to do on the line. Tell IT if it stays.");
        if (_licence.Required && !_licence.Status.Licensed)
            yield return A(930, Alarm.Types.Level.Alarm, "This PC is not licensed for production.", "Ask the engineer to check the licence.");
        if (!_station.IntegrityOk)
            yield return A(931, Alarm.Types.Level.Alarm, "The software has failed its integrity check and will not run production.",
                "Reinstall the software from the signed package. Call the engineer.");
        if (_station.LedgerFailure is { } lf)
            yield return A(LedgerGate.AlarmCode, Alarm.Types.Level.Alarm, $"The results log failed its tamper check and production will not start. {lf}",
                "The line will not run. Call the engineer to check results.db.");
    }

    PcHealth Health()
    {
        var g = _guard.Status;
        var o = _guard.Options;
        var h = new PcHealth
        {
            MemoryUsedPct = (float)g.MemoryUsedPct,
            Pressure = g.Level switch
            {
                Pressure.Normal => PcHealth.Types.Pressure.Normal,
                Pressure.Elevated => PcHealth.Types.Pressure.Elevated,
                Pressure.High => PcHealth.Types.Pressure.High,
                _ => PcHealth.Types.Pressure.Critical,
            },
            PressureReason = g.Reason ?? "",
            RestartRecommended = g.RestartRecommended,
            FramesMissed = (ulong)Math.Max(0, _frames.Missed),
            Limits = new GuardLimits
            {
                BudgetBytes = (ulong)Math.Max(0, g.BudgetBytes),
                ElevatedPct = (float)o.ElevatedPct,
                HighPct = (float)o.HighPct,
                CriticalPct = (float)o.CriticalPct,
                SystemHighPct = (float)o.SystemHighPct,
                SystemCriticalPct = (float)o.SystemCriticalPct,
                DiskHighFreePct = (float)o.DiskHighFreePct,
                DiskCriticalFreePct = (float)o.DiskCriticalFreePct,
            },
        };
        if (g.Last is { } s)
        {
            h.CpuPct = (float)(s.SystemCpuPct ?? 0);
            if (s.SystemTotalBytes > 0) h.RamPct = (float)Math.Clamp(100.0 * s.SystemLoadBytes / s.SystemTotalBytes, 0, 100);
            if (s.DiskTotalBytes > 0) h.DiskPct = (float)Math.Clamp(100.0 - 100.0 * s.DiskFreeBytes / s.DiskTotalBytes, 0, 100);
            h.Sampled = Timestamp.FromDateTimeOffset(s.At);
        }
        return h;
    }

    HealthSample? PlantHealth()
    {
        if (_guard.Status.Last is not { } s) return null;
        static double Pct(double v) => Math.Clamp(double.IsFinite(v) ? v : 0, 0, 100);
        return new HealthSample
        {
            CpuPct = s.SystemCpuPct is { } c ? Pct(c) : null,
            RamPct = s.SystemTotalBytes > 0 ? Pct(100.0 * s.SystemLoadBytes / s.SystemTotalBytes) : null,
            DiskPct = s.DiskTotalBytes > 0 ? Pct(100.0 - 100.0 * s.DiskFreeBytes / s.DiskTotalBytes) : null,
            FrameDrops = _frames.Missed,
        };
    }

    public ListJobsResponse ListJobs()
    {
        var r = new ListJobsResponse();
        foreach (var name in _jobs.Names())
        {
            Job? j;
            try { j = _jobs.Load(name); } catch (Exception) { continue; }   // a broken job file is not listed
            if (j is null) continue;
            r.Jobs.Add(new JobInfo { Name = j.Name, Number = j.Number, Version = j.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        return r;
    }

    public Counters Counters()
    {
        lock (_lock)
        {
            var sorted = _cycles.Order().ToList();
            var c = new Counters
            {
                Total = _total,
                Ok = _ok,
                Ng = _ng,
                YieldPercent = _total == 0 ? 0 : (float)(100.0 * _ok / _total),
                MedianCycleMs = sorted.Count == 0 ? 0 : (float)sorted[sorted.Count / 2],
                MaxCycleMs = sorted.Count == 0 ? 0 : (float)sorted[^1],
                Since = _batchStarted,
            };
            foreach (var (k, v) in _ngReasons) c.NgReasons[k] = v;
            return c;
        }
    }

    /// <summary>The last parts' results, newest first (at most 32), for the browser dashboard's NG strip.</summary>
    public IReadOnlyList<InspectionResult> RecentResults()
    {
        lock (_lock) return [.. _recentResults];
    }

    // --- control ------------------------------------------------------------------------------

    /// <summary><paramref name="user"/> is who changed the product, when known: they may not accept their own changeover.</summary>
    public ChangeJobResponse ChangeJob(ChangeJobRequest req, string user = "")
    {
        if (_station.Running) return new ChangeJobResponse { ErrorCode = PlcCodes.JobChangeWhileBusy, ErrorText = "Stop the line before changing the product.", ErrorKey = "runtime.refused.stopFirst" };
        var name = req.JobCase switch
        {
            ChangeJobRequest.JobOneofCase.Name => _jobs.Names().FirstOrDefault(n => n == req.Name),
            ChangeJobRequest.JobOneofCase.Number => _jobs.FindByNumber(req.Number),
            _ => _station.Job?.Name,
        };
        if (name is null) return new ChangeJobResponse { ErrorCode = PlcCodes.JobNotFound, ErrorText = "No such job.", ErrorKey = "runtime.refused.noJob" };
        // jobs have no variants yet: only "" or the job's own name is a product of it
        if (!string.IsNullOrEmpty(req.Variant) && req.Variant != name)
            return new ChangeJobResponse { ErrorCode = PlcCodes.JobNotFound, ErrorText = "No such product in this job.", ErrorKey = "runtime.refused.noProduct" };
        try { _station.Load(name, user); }
        catch (InvalidOperationException e) { return new ChangeJobResponse { ErrorCode = PlcCodes.JobNotFound, ErrorText = e.Message, ErrorKey = "runtime.refused.loadFailed" }; }
        ResetCounts();
        PublishStatus();
        return new ChangeJobResponse { Ok = true };
    }

    public StartResponse Start(StartRequest req, string user = "")
    {
        if (!string.IsNullOrEmpty(req.Job) && req.Job != _station.Job?.Name)
        {
            var change = ChangeJob(new ChangeJobRequest { Name = req.Job, Variant = req.Variant }, user);
            if (!change.Ok) return new StartResponse { ErrorCode = change.ErrorCode, ErrorText = change.ErrorText, ErrorKey = change.ErrorKey };
        }
        if (_station.Running) return new StartResponse { Ok = true };
        if (_station.Job is { } job && !job.Tools.Any(t => t.Enabled))
            return new StartResponse { ErrorCode = PlcCodes.ToolFailed, ErrorText = "This job checks nothing: add a tool in Teach before running the line.", ErrorKey = "runtime.refused.noTools" };
        var source = req.Source == V1.Source.Unspecified ? _opt.DefaultSource() : req.Source;
        var plc = _plc.Runtime is not null || _micro850 is { Enabled: true };
        var mode = source == V1.Source.Replay ? (plc ? RunMode.PlcSimulation : RunMode.Folder) : (plc ? RunMode.Plc : RunMode.FreeRun);
        // counts start afresh before the first part: the station inspects as soon as it starts, and a
        // reset after that would drop parts that were already streamed from the batch's counters
        var before = SaveCounts();
        ResetCounts();
        lock (_lock) _batchStarted = Timestamp.FromDateTime(DateTime.UtcNow);
        // the setup check at job load never runs into the batch: one still going stops here, its pictures dropped
        _setup.Stop();
        try { _station.Start(mode); }
        catch (ChangeoverException e)
        {
            RestoreCounts(before);   // a refused start keeps the last batch's counters
            return new StartResponse { ErrorCode = 409, ErrorText = e.Message, ErrorKey = "runtime.refused.changeover" };
        }
        catch (InvalidOperationException e)
        {
            RestoreCounts(before);   // a refused start keeps the last batch's counters
            _setup.Retry();          // and the line is still stopped: the setup check it stopped runs again
            return new StartResponse { ErrorCode = 409, ErrorText = e.Message, ErrorKey = "runtime.refused.start" };
        }
        // a possible escape found in the sorting stays raised until the line is started again
        _plant.Alarm(ReviewDesk.PossibleEscapeAlarm, false);
        PublishStatus();
        return new StartResponse { Ok = true };
    }

    public StopResponse Stop()
    {
        _station.Stop();
        PublishStatus();
        return new StopResponse { Ok = true };
    }

    /// <summary>The PLC screen's test signal (PLC-INTERFACE 7): OK or NG with Done for 2 s. Only in
    /// Teach (the line stopped) and with the PLC connected; the PLC runtime refuses it while RunEnable
    /// is 1, while a part is handled, and OK over a latched NG. The caller is an engineer (Sessions).</summary>
    public PlcTestSignalResponse PlcTestSignal(PlcTestSignalRequest req)
    {
        static PlcTestSignalResponse No(ushort code, string text, string key) =>
            new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (_station.Running)
            return No(PlcCodes.JobChangeWhileBusy, "Stop the line first: test signals are sent only in Teach.", "runtime.refused.stopFirst");
        if (_plc.Runtime is not { } rt || !rt.Connected)
            return No(PlcCodes.HeartbeatLost, "The PLC is not connected: it would not see the test signal.", "runtime.refused.plcOff");
        return rt.TestResult(req.Ok) switch
        {
            PlcTestRefusal.None => new PlcTestSignalResponse { Ok = true, PulseMs = PlcRuntime.TestPulseMs },
            PlcTestRefusal.LineRunning => No(PlcCodes.JobChangeWhileBusy, "Stop the line first: test signals are sent only in Teach.", "runtime.refused.stopFirst"),
            PlcTestRefusal.RunEnable => No(PlcCodes.TriggerNotReady, "The PLC has RunEnable on: switch the line to set-up on the PLC first.", "runtime.refused.plcRunEnable"),
            PlcTestRefusal.Busy => No(PlcCodes.TriggerWhileBusy, "A part is being handled with the PLC: wait until it is done.", "runtime.refused.plcBusy"),
            _ => No(PlcCodes.TriggerWhileBusy, "An NG signal is on: the OK test waits until it ends.", "runtime.refused.plcLatchedNg"),
        };
    }

    /// <summary>A software trigger from Vision Studio or plant software: one part now. Refused
    /// while the line is not running (there is nothing to inspect with) and on a PLC-triggered
    /// line (the PLC owns the part count).</summary>
    public async Task<TriggerResponse> TriggerAsync(TriggerRequest req, CancellationToken ct)
    {
        if (!_station.Running) return new TriggerResponse { ErrorCode = PlcCodes.TriggerNotReady };
        if (_station.Mode is RunMode.Plc or RunMode.PlcSimulation) return new TriggerResponse { ErrorCode = PlcCodes.TriggerWhileBusy };
        uint id;
        lock (_lock) id = _nextTrigger++;
        var partId = string.IsNullOrEmpty(req.PartId) ? $"T{id}" : req.PartId;
        await Task.Run(() => _station.Inspect(new PlcTrigger((ushort)id, partId, 0, (ushort)req.CameraMask, false)), ct).ConfigureAwait(false);
        InspectionResult? result;
        lock (_lock) result = _recentResults.FirstOrDefault(r => r.PartId == partId);
        if (result is not null) result.TriggerId = id;
        return new TriggerResponse { TriggerId = id, Result = req.WaitForResult ? result : null };
    }

    /// <summary>Inputs by name. "batch.&lt;field&gt;" sets a batch value the job's tools check (the
    /// line must be stopped: a value changed mid-run would judge parts against two batches).</summary>
    /// <summary>A second person's acceptance at the line PC; the caller has checked who they are and their role.</summary>
    public AcceptChangeoverResponse AcceptChangeoverAtLine(string code, string user, string fullName)
    {
        if (_station.Running) return new AcceptChangeoverResponse { ErrorCode = PlcCodes.JobChangeWhileBusy, ErrorText = "Stop the line first.", ErrorKey = "runtime.refused.stopFirst" };
        if (_station.Changeover.AcceptAtLine(code, user, fullName) is { } why)
            return new AcceptChangeoverResponse { ErrorCode = 409, ErrorText = why, ErrorKey = "runtime.changeover.notNow" };
        PublishStatus();
        return new AcceptChangeoverResponse { Ok = true };
    }

    public SetInputResponse SetInput(SetInputRequest req, string user = "")
    {
        // After a supervisor refused the changeover and the values are right: the same values as a new request.
        if (req.Name.Equals("changeover.ask", StringComparison.OrdinalIgnoreCase))
        {
            if (!_station.Changeover.AskAgain(user)) return new SetInputResponse { ErrorCode = 409 };
            PublishStatus();
            return new SetInputResponse { Ok = true };
        }
        if (req.Name.StartsWith("batch.", StringComparison.OrdinalIgnoreCase) && req.Name.Length > 6)
        {
            if (_station.Job is null) return new SetInputResponse { ErrorCode = PlcCodes.JobNotFound };
            if (_station.Running) return new SetInputResponse { ErrorCode = PlcCodes.JobChangeWhileBusy };
            var b = new BatchFields();
            foreach (var (k, v) in _station.Batch.Values) b.Values[k] = v;
            b.Values[req.Name[6..]] = req.Value;
            try { _station.SetBatch(b, user); }
            catch (InvalidOperationException) { return new SetInputResponse { ErrorCode = PlcCodes.JobChangeWhileBusy }; }
            PublishStatus();
            return new SetInputResponse { Ok = true };
        }
        return new SetInputResponse { ErrorCode = 404 };
    }

    // Under _lock or before any watcher: the live picture versions of a job's cameras.
    void Seen(CamState[] cams)
    {
        _seenCams = cams;
        _seenVersion = cams.Select(c => c.Version).ToArray();
    }

    void ResetCounts()
    {
        lock (_lock)
        {
            _total = _ok = _ng = 0;
            _cycles.Clear();
            _ngReasons.Clear();
            _batchStarted = null;
        }
    }

    sealed record SavedCounts(ulong Total, ulong Ok, ulong Ng, double[] Cycles, KeyValuePair<string, ulong>[] NgReasons, Timestamp? BatchStarted);

    SavedCounts SaveCounts()
    {
        lock (_lock) return new(_total, _ok, _ng, [.. _cycles], [.. _ngReasons], _batchStarted);
    }

    void RestoreCounts(SavedCounts s)
    {
        lock (_lock)
        {
            (_total, _ok, _ng, _batchStarted) = (s.Total, s.Ok, s.Ng, s.BatchStarted);
            _cycles.Clear();
            _cycles.AddRange(s.Cycles);
            _ngReasons.Clear();
            foreach (var (k, v) in s.NgReasons) _ngReasons[k] = v;
        }
    }

    // --- results from the station -------------------------------------------------------------

    // Raised on the station's thread on load, start, stop and after each part.
    void OnStationChanged()
    {
        // Stop or a job change ends the run: its picture warnings clear (AS-13a)
        if (!_station.Running) _pictures.Clear();
        PublishStatus();
    }

    // Raised once per part, on the inspecting thread under the station's lock. Kept short: it builds
    // the result from the part (never from the station's last-cycle values: with camera channels
    // two cameras finish at once, each its own part) and hands it to the watchers' queues.
    void OnPart(StationPart part)
    {
        try { CollectPart(part); }
        catch (Exception) { /* the contract's view must never stop inspection */ }
    }

    void CollectPart(StationPart part)
    {
        // the job the part was inspected with: a PLC part is counted when the PLC took it, maybe after a job change
        var job = part.Job ?? _station.Job;
        if (job is null) return;
        var cams = _station.Cams;
        bool current = ReferenceEquals(job, _station.Job);
        InspectionResult result;
        var frames = new List<LiveFrame>();
        lock (_lock)
        {
            // a job was loaded: its cameras start afresh
            if (!ReferenceEquals(cams, _seenCams)) { Seen(cams); Array.Clear(_seenVersion); }   // its first picture is new
            _activeAt = DateTimeOffset.UtcNow;
            result = new InspectionResult
            {
                InspectionId = _nextInspection++,
                Time = Timestamp.FromDateTime(DateTime.UtcNow),
                Job = job.Name,
                PartId = part.PartId ?? "",
                Pass = part.Ok,
                CycleMs = (float)part.Ms,
            };
            var reasons = new List<string>();
            for (var k = 0; k < part.Cameras.Length; k++)
            {
                var i = part.Cameras[k];
                if (i >= cams.Length || !cams[i].HasTools) continue;
                var c = cams[i];
                var drawn = current && c.Version != _seenVersion[i];   // the live picture is the current job's
                _seenVersion[i] = c.Version;
                // the camera's image name (owner, 2026-10-31): the same as its saved file, results.db and the plant events
                var imageId = part.Name is { } name ? name.WithCamera(i + 1).ToString() : $"{result.InspectionId}-{i}";
                if ((part.Errors[k] ?? (part.Results[k] is null ? "No picture from the camera" : null)) is { } err)
                {
                    result.Tools.Add(new V1.ToolResult { Name = c.Name, Kind = "Camera", Pass = false, ReasonText = err });
                    reasons.Add(err.StartsWith("No picture", StringComparison.Ordinal) ? "no_image" : "error");
                    continue;
                }
                var r = part.Results[k]!;
                var camPass = r.Pass;
                var camTools = new List<V1.ToolResult>();
                var camCodes = new List<V1.CodeRead>();
                foreach (var t in r.Tools)
                {
                    var cfg = job.Tools.FirstOrDefault(x => x.Name == t.ToolName);
                    var tool = new V1.ToolResult
                    {
                        Name = t.ToolName,
                        Kind = cfg is null ? "" : Station.LicenceToolOf(cfg),
                        Pass = t.Pass,
                        Confidence = t.Pass ? 1 : 0,
                        ReasonText = t.Pass ? t.Summary : t.Explain ?? t.Summary,
                        TimeMs = (float)t.TimeMs,
                    };
                    AddMeasures(tool, t);
                    RecordGrade(t, cfg);
                    camTools.Add(tool);
                    if (!t.Pass) reasons.Add(t.ToolName);
                    foreach (var code in t.Codes ?? [])
                    {
                        var read = new V1.CodeRead { Text = code.Text, Format = code.Format.ToString(), State = V1.CodeRead.Types.State.Read, AddOn = code.AddOn };
                        read.Corners.AddRange(code.Corners.Select(p => new V1.Point { X = p.X, Y = p.Y }));
                        camCodes.Add(read);
                    }
                }
                result.Tools.AddRange(camTools);
                result.Codes.AddRange(camCodes);
                result.ImageIds.Add(imageId);
                var live = drawn ? _images.Get("live" + i) : null;
                var file = r.ImageName is { } n && job.Cameras[i].TestFolder is { Length: > 0 } folder ? Path.Combine(folder, n) : null;
                _recentImages.AddFirst((imageId, live, file is not null && File.Exists(file) ? file : null));
                while (_recentImages.Count > KeepImages) _recentImages.RemoveLast();
                if (!camPass)
                {
                    // what each failed tool found ("<tool>|<reason key or text>"), for the review's double-check
                    var faults = camTools.Where(t => !t.Pass).Select(t => $"{t.Name}|{t.ReasonText}").ToArray();
                    _ngSources.AddFirst((imageId, file is not null && File.Exists(file) ? file : null, job.Name, i, job.Cameras[i].Name, faults));
                    while (_ngSources.Count > KeepNgSources) _ngSources.RemoveLast();
                }
                if (live is not null)
                {
                    var frame = Frame(result.InspectionId, job.Cameras[i].Name, imageId, live, camPass, r);
                    frame.Tools.AddRange(camTools.Select(t => t.Clone()));
                    frame.Codes.AddRange(camCodes.Select(c => c.Clone()));
                    frames.Add(frame);
                }
            }
            // the inspection said OK but the PLC got NG (e.g. NG 100: link or heartbeat lost before it took the result)
            if (part.Plc is { } plc)
            {
                result.Tools.Add(new V1.ToolResult { Name = "PLC link", Kind = "Plc", Pass = false, ReasonText = plc.Text });
                reasons.Add(plc.Reason);
            }
            _total++;
            if (part.Ok) _ok++;
            else
            {
                _ng++;
                if (reasons.Count == 0) reasons.Add("failed");
                foreach (var k in reasons.Distinct()) _ngReasons[k] = _ngReasons.GetValueOrDefault(k) + 1;
            }
            _cycles.Add(result.CycleMs);
            if (_cycles.Count > KeepCycles) _cycles.RemoveAt(0);
            _recentResults.AddFirst(result);
            while (_recentResults.Count > 32) _recentResults.RemoveLast();
        }
        Publish(_resultWatchers, result);
        foreach (var f in frames) Publish(_frameWatchers, f);
    }

    /// <summary>What a tool measured beside its pass, for the line's results and TryJob alike: Check text's print
    /// quality, report only (shown as information, never as OK) and a graded Read code's lowest grade.</summary>
    public static void AddMeasures(V1.ToolResult tool, VisionCore.ToolResult t)
    {
        if (t.PrintQuality is { } quality) tool.PrintQuality = quality;
        tool.ReportOnly = t.ReportOnly;
        // a code that could not be graded fails the tool: then no grade, so the margin never calls the picture safe
        if (t.Values.TryGetValue("grade", out var grade) && !t.Values.ContainsKey("grade.ungraded")) tool.CodeGrade = (float)grade;
    }

    /// <summary>The grades of the graded parts for the browser dashboard's print-quality trend (WEB-05); a part
    /// with a code that could not be graded is kept as ungraded, never as a grade. Kept in the data folder's
    /// grades.jsonl so a restart keeps the trend (WEB-06).</summary>
    public GradeTrail Grades { get; }

    /// <summary>The picture check during a run (requirements AS-13a): its warnings are in the status.</summary>
    public PictureDrift Pictures => _pictures;

    // --- learning session (owner, 2026-11-04) ----------------------------------------------------

    /// <summary>The learning session's setting; null: no learning session (tests, older set-ups).</summary>
    public LearningSettingsStore? LearningSettings { get; set; }
    /// <summary>The pictures waiting to be sorted; null: none are listed.</summary>
    public SortQueue? Sorts { get; set; }
    /// <summary>The good pictures kept for learning, so GetImage finds them; null: none are kept.</summary>
    public OkSampler? OkSamples { get; set; }
    /// <summary>The reviewed pictures, so GetImage shows one in the reviewed list (AS-04j) after its saved picture is
    /// gone; null: not shown.</summary>
    public ReviewStore? Reviews { get; set; }

    /// <summary>Parts seen since the runtime started (learning stops when one comes).</summary>
    public ulong Parts { get { lock (_lock) return _nextInspection; } }

    /// <summary>
    /// Whether the line is idle: stopped, or running without a part, for the set idle time (default 5 minutes). Learning
    /// may start on a stopped line, or on an idle running line whose PLC (when one is set up) has RunEnable off with its
    /// link up and heartbeat good (a lost link may hide RunEnable coming on), and while the PC is not short of memory
    /// or disk: with RunEnable on, a part may come at any moment and the inspection needs the PC.
    /// </summary>
    public (bool Idle, int Seconds, bool MayLearn) Idleness()
    {
        var running = _station.Running;
        DateTimeOffset since;
        lock (_lock)
        {
            if (running != _wasRunning) { _wasRunning = running; _activeAt = DateTimeOffset.UtcNow; }
            since = _activeAt;
        }
        var seconds = (int)Math.Clamp((DateTimeOffset.UtcNow - since).TotalSeconds, 0, int.MaxValue);
        var idle = seconds >= 60 * (LearningSettings?.IdleMinutes ?? LearningSettingsStore.DefaultIdleMinutes);
        var plcOff = string.Equals(_plc.Options.Protocol, "Off", StringComparison.OrdinalIgnoreCase);
        var rt = _plc.Runtime;
        var runEnableOff = plcOff || rt is { LinkUp: true, HeartbeatOk: true, RunEnable: false };
        return (idle, seconds, !running || (idle && runEnableOff && _guard.Level == Pressure.Normal));
    }

    V1.LearningState Learning(Job job)
    {
        var (idle, seconds, may) = Idleness();
        var on = LearningSettings?.Enabled == true;
        var waiting = Sorts?.WaitingCounts(job.Name);
        return new V1.LearningState
        {
            Enabled = on,
            IdleMinutes = (uint)(LearningSettings?.IdleMinutes ?? LearningSettingsStore.DefaultIdleMinutes),
            Idle = idle,
            IdleSeconds = (uint)seconds,
            MayLearn = may,
            ToSort = on && waiting is { } w ? (uint)w.All : 0,
            // the NG review stays whatever the setting (AS-04i)
            BadToReview = (uint)(waiting?.Bad ?? 0),
            FlaggedOk = (uint)(waiting?.Flagged ?? 0),
        };
    }

    void RecordGrade(VisionCore.ToolResult t, ToolConfig? cfg)
    {
        var ungraded = t.Values.ContainsKey("grade.ungraded");
        if (!ungraded && !t.Values.ContainsKey("grade")) return;
        double? grade = ungraded ? null : t.Values["grade"];
        Grades.Add(t.ToolName, DateTime.UtcNow, grade, (cfg as CodeToolConfig)?.MinGrade);
    }

    static LiveFrame Frame(ulong inspection, string camera, string imageId, Render.Frame live, bool pass, JobResult r)
    {
        var f = new LiveFrame
        {
            InspectionId = inspection,
            Camera = camera,
            Jpeg = ByteString.CopyFrom(live.Jpeg),
            Width = live.Width,
            Height = live.Height,
            Scale = (float)live.Scale,
            Pass = pass,
            ImageId = imageId,
        };
        foreach (var t in r.Tools)
            foreach (var g in t.Graphics)
            {
                var v = new V1.Graphic
                {
                    Kind = V1.Graphic.Types.Kind.Polygon,
                    Colour = g.Kind switch { GraphicKind.Ok => "status.ok", GraphicKind.Fail => "status.ng", _ => "status.info" },
                    Label = g.Label ?? "",
                    Tooltip = g.Label ?? t.ToolName,
                };
                v.Points.AddRange(g.Points.Select(p => new V1.Point { X = (float)(p.X * live.Scale), Y = (float)(p.Y * live.Scale) }));
                f.Graphics.Add(v);
            }
        return f;
    }

    // --- images -------------------------------------------------------------------------------

    /// <summary>Where a recent bad part's picture came from, for the NG review: its job and camera, the stored
    /// picture file (simulated cameras), and what each failed tool found. Null when no longer kept.</summary>
    public (string Job, int Camera, string CameraName, string? File, string[] Faults)? ImageSource(string imageId)
    {
        lock (_lock)
        {
            var n = _ngSources.FirstOrDefault(x => x.Id == imageId);
            return n.Id is null ? null : (n.Job, n.Camera, n.CameraName, n.File, n.Faults);
        }
    }

    /// <summary>The saved full picture of this image (the NG image store), for the NG review: Queued while it is
    /// still being written, else its file or null when none was saved.</summary>
    public (bool Queued, string? File) SavedPicture(string imageId) =>
        _station.NgImages.Find(imageId).State == NgImageState.Queued ? (true, null) : (false, Saved(imageId));

    /// <summary>An image of a recent part: full resolution from the stored picture it came from
    /// (replay), else the live picture as drawn (with its graphics). An older part's image by its name
    /// (L01_C2_J045_261031-104512_000123) comes from the saved NG images, without graphics. Null when it is no longer kept.</summary>
    public (byte[] Data, int Width, int Height, string Format)? Image(string imageId, GetImageRequest.Types.Encoding encoding, bool withGraphics)
    {
        (string Id, Render.Frame? Live, string? File) e;
        lock (_lock) e = _recentImages.FirstOrDefault(x => x.Id == imageId);
        if (e.Id is null)
        {
            if (Saved(imageId) is not { } saved) return null;
            e = (imageId, null, saved);
            withGraphics = false;
        }
        if (!withGraphics && e.File is not null && File.Exists(e.File))
        {
            return JobDesigner.Encode(GrayImage.Load(e.File), encoding);
        }
        return e.Live is { } live ? (live.Jpeg, live.Width, live.Height, "jpeg") : null;
    }

    // A saved image of this name, any form the store finds (old ids name the part, so their first camera's file).
    string? Saved(string imageId)
    {
        var files = _station.NgImages.Find(imageId).Files;
        // a good picture kept for learning (the sorting shows it)
        if (files.Count == 0 && OkSamples is { } ok) files = ok.Store.Find(imageId).Files;
        var found = ImageName.TryParse(imageId, out _)
            ? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Path) == imageId)?.Path
            : files.FirstOrDefault()?.Path;
        // a reviewed picture by its key, the store's copy kept with the decision
        return found ?? Reviews?.Find(imageId)?.File;
    }

    // --- watchers -----------------------------------------------------------------------------

    public ChannelReader<V1.Status> WatchStatus(CancellationToken ct)
    {
        var ch = Watch(_statusWatchers, 16, ct);
        ch.Writer.TryWrite(Status());   // the first message at once
        return ch.Reader;
    }

    public ChannelReader<InspectionResult> WatchResults(CancellationToken ct) => Watch(_resultWatchers, 256, ct).Reader;

    /// <summary>One frame per camera waits at most: a slow client gets the newest frames, never a queue.
    /// (The cameras of one part are published together; room for one frame would drop all but the last camera's.)</summary>
    public ChannelReader<LiveFrame> WatchFrames(CancellationToken ct) => Watch(_frameWatchers, Job.MaxCameras, ct).Reader;

    static Channel<T> Watch<T>(List<Channel<T>> list, int capacity, CancellationToken ct)
    {
        var ch = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (list) list.Add(ch);
        ct.Register(() =>
        {
            lock (list) list.Remove(ch);
            ch.Writer.TryComplete();
        });
        return ch;
    }

    void PublishStatus()
    {
        lock (_statusWatchers) if (_statusWatchers.Count == 0) return;
        V1.Status s;
        try { s = Status(); } catch (Exception) { return; }
        Publish(_statusWatchers, s);
    }

    static void Publish<T>(List<Channel<T>> list, T item)
    {
        lock (list) foreach (var ch in list) ch.Writer.TryWrite(item);
    }

    public void Dispose()
    {
        _station.Changed -= OnStationChanged;
        _station.PartCounted -= OnPart;
        if (_station.Pictures == _pictures.Offer) _station.Pictures = null;
        _pictures.Changed -= PublishStatus;
        _plc.Changed -= PublishStatus;
        _setup.Changed -= PublishStatus;
        Grades.Dispose();
        foreach (var l in new System.Collections.IList[] { _statusWatchers, _resultWatchers, _frameWatchers })
            lock (l) l.Clear();
    }
}
