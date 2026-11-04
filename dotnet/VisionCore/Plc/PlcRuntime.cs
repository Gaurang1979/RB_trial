using System.Diagnostics;

namespace VisionCore.Plc;

/// <summary>What the PLC runtime needs from the inspection side (the station).</summary>
public interface IPlcHost
{
    /// <summary>A job is loaded, the station is in PLC mode and its cameras (or stored images) are ready.</summary>
    bool CanInspect { get; }
    ushort ActiveJobNumber { get; }
    /// <summary>Images come from files, not cameras.</summary>
    bool Simulation { get; }
    /// <summary>Fires the cameras once and inspects. Called on a worker thread, one at a time.</summary>
    PlcInspection Inspect(PlcTrigger trigger);
    /// <summary>Loads the job with this number; returns 0 or an error code (section 9).</summary>
    ushort LoadJob(ushort number);
    /// <summary>Cameras of the loaded job.</summary>
    int CameraCount { get; }
    /// <summary>Fires one camera and runs its tools (camera channels, section 3.5). Called on a worker
    /// thread; calls for different cameras may run at the same time.</summary>
    PlcInspection InspectCamera(int camera, PlcTrigger trigger);
    /// <summary>The verdict the PLC got for a part whose trigger has <see cref="PlcTrigger.Held"/> set:
    /// raised once per such part, when the PLC drops its trigger after Done (it took the result), when
    /// the result comes after the PLC dropped its trigger (it gave up and made the part NG:
    /// <see cref="PlcDelivery.NotTaken"/>), at a link or heartbeat loss, or when the runtime closes.
    /// At a loss or close a held OK counts as NG 100 (<see cref="PlcDelivery.LinkLossNg"/>), even if the
    /// PLC had taken it a moment before: when in doubt, NG (section 5.6, requirements section 12).
    /// Raised on the PLC thread or an inspection thread: the host should only queue it.</summary>
    void Delivered(PlcDelivery delivery) { }
}

/// <summary>A part's result as the PLC got it. Camera: -1 for the main trigger (all cameras), else the camera channel.</summary>
public sealed record PlcDelivery(int Camera, PlcTrigger Trigger, PlcInspection Result, bool LinkLossNg)
{
    /// <summary>The PLC dropped its trigger before Done (its result timeout, section 7 state 10): it made the
    /// part NG itself, so Result is NG whatever the inspection said.</summary>
    public bool NotTaken { get; init; }
}

public sealed record PlcTrigger(ushort TriggerId, string PartId, uint ProductId, ushort CameraMask, bool SaveImage)
{
    /// <summary>UserIn1..4 of the input block at the trigger (empty when not from the PLC).</summary>
    public IReadOnlyList<int> UserIn { get; init; } = Array.Empty<int>();
    /// <summary>The text block taken with the trigger (section 3.7); null when not from the PLC.</summary>
    public PlcText? Text { get; init; }
    /// <summary>Set by the PLC runtime: the part is counted when the runtime calls
    /// <see cref="IPlcHost.Delivered"/> with the verdict the PLC got, not when inspected.</summary>
    public bool Held { get; init; }
}

/// <summary>Why <see cref="PlcRuntime.TestResult"/> sent no test signal (None: it was sent).</summary>
public enum PlcTestRefusal
{
    None,
    /// <summary>The line runs: test signals only in Teach, with the line stopped.</summary>
    LineRunning,
    /// <summary>The PLC sets RunEnable = 1.</summary>
    RunEnable,
    /// <summary>A part is being inspected, or its result has not been taken by the PLC.</summary>
    Busy,
    /// <summary>OK asked while an NG is latched.</summary>
    LatchedNg,
}

public sealed record PlcInspection(bool Ok, uint ToolPass, ushort NgReason, string Text1, string Text2,
                                   IReadOnlyList<float> Values, ushort ErrorCode = 0);

public sealed class PlcOptions
{
    /// <summary>Off, ModbusTcp.</summary>
    public string Protocol { get; set; } = "Off";
    /// <summary>PLC make and model (LineProfiles key): wiring diagram, I/O assignment, health rules.</summary>
    public string PlcModel { get; set; } = "delta-dvp12se-16sp";
    public int Port { get; set; } = 502;
    /// <summary>PLC heartbeat unchanged this long = connection lost (error 100). 200-5000 ms.</summary>
    public int WatchdogMs { get; set; } = 1000;
    /// <summary>How often the input block is checked (server protocols are also woken by every PLC write).</summary>
    public int PollMs { get; set; } = 2;
    /// <summary>The address of this PC's network card on the PLC network that the Modbus TCP server listens on;
    /// empty = every network card (as before).</summary>
    public string BindAddress { get; set; } = "";

    /// <summary>The address to listen on (<see cref="BindAddress"/>); refuses one that is not an IP address.</summary>
    public System.Net.IPAddress BindIp() =>
        string.IsNullOrWhiteSpace(BindAddress) ? System.Net.IPAddress.Any
        : System.Net.IPAddress.TryParse(BindAddress.Trim(), out var ip) ? ip
        : throw new FormatException($"PLC network card address '{BindAddress}' is not an IP address");
}

/// <summary>
/// The four-phase handshake of PLC-INTERFACE.md section 4.1, the job change, alarm reset and the
/// two-way heartbeat (section 5.6), over any <see cref="IPlcLink"/>. Results are written before the
/// status bits, so the PLC never sees Done with old values.
/// </summary>
public sealed class PlcRuntime : IDisposable
{
    private readonly IPlcLink _link;
    private readonly IPlcHost _host;
    private readonly PlcOptions _opt;
    private readonly PlcOutput _out;
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private byte[] _prev = new byte[PlcInput.Size];
    private Task? _inspection;
    private ushort _lastHb;
    private readonly Stopwatch _hbSeen = Stopwatch.StartNew();
    private int _hbChanges;
    private bool _hbLost = true;
    private bool _runEnable;
    private int _lastTriggerId;
    private int _hbLosses;
    private int _disconnectsSeen;
    private readonly Stopwatch _visionHb = Stopwatch.StartNew();
    private readonly PlcBlock _camOut;
    private byte[] _camPrev = new byte[PlcCameraChannels.InSize];
    private readonly bool[] _camBusy = new bool[PlcCameraChannels.Max];
    // the TriggerID each trigger source (0 = the main trigger, 1 + c = camera channel c) took the text block with last
    private readonly ushort?[] _textTaken = new ushort?[1 + PlcCameraChannels.Max];
    // per trigger source (as _textTaken): the result written that the PLC has not taken yet, and whether
    // it was an OK turned into NG 100; handed to the host (Delivered) outside the lock, on the PLC thread
    private readonly (PlcTrigger Trigger, PlcInspection Result, bool Turned)?[] _held = new (PlcTrigger, PlcInspection, bool)?[1 + PlcCameraChannels.Max];
    private readonly List<PlcDelivery> _deliver = new();
    // per trigger source: the PLC dropped its trigger while the inspection ran (it gave up, section 7 state 10)
    private readonly bool[] _gaveUp = new bool[1 + PlcCameraChannels.Max];
    private readonly Task?[] _camTasks = new Task?[PlcCameraChannels.Max];
    private bool _closed;

    public PlcRuntime(IPlcLink link, IPlcHost host, PlcOptions opt, bool restarted = false)
    {
        _link = link;
        _host = host;
        _opt = opt;
        _out = new PlcOutput(new PlcBlock(PlcOutput.Size, link.BigEndian));
        _camOut = new PlcBlock(PlcCameraChannels.OutSize, link.BigEndian);
        if (restarted) SetError(PlcCodes.Restarted);
        _link.InputChanged += () => _wake.Set();
        _thread = new Thread(Loop) { IsBackground = true, Name = "plc" };
        _thread.Start();
    }

    public string LinkName => _link.Name;
    public bool Connected => _link.Connected && !_hbLost;
    /// <summary>The link and the PLC's signals as the Run screen shows them; read without the PLC lock,
    /// so any thread may ask (the status is built under the station's lock).</summary>
    public bool LinkUp => _link.Connected;
    public bool HeartbeatOk => !Volatile.Read(ref _hbLost);
    public bool RunEnable => Volatile.Read(ref _runEnable);
    /// <summary>TriggerID of the last trigger taken (main or a camera channel); 0 before the first.</summary>
    public ushort LastTriggerId => (ushort)Volatile.Read(ref _lastTriggerId);
    public long Inspections { get; private set; }
    public event Action? Changed;
    /// <summary>A product left the line (part log, section 3.6). Raised on the PLC thread.</summary>
    public event Action<PartRecord>? PartLeft;

    private ushort _settingsVersion;
    private ushort _partSeq;
    private bool _partSeqKnown;

    // new records of the part log since the last look; the first look only takes the position
    private void PollPartLog()
    {
        var raw = _link.ReadPartLog();
        if (raw.Length < PlcPartLog.Size || PartLeft is null) return;
        var b = new PlcBlock(PlcPartLog.Size, _link.BigEndian);
        raw.AsSpan(0, PlcPartLog.Size).CopyTo(b.Bytes);
        var all = Enumerable.Range(0, PlcPartLog.Records).Select(i => PlcPartLog.Read(b, i)).OfType<PartRecord>().ToList();
        if (all.Count == 0) return;
        if (!_partSeqKnown)
        {
            _partSeq = PlcPartLog.Newer(b, (ushort)(all[0].Seq - 0x4000)).Last().Seq;   // newest present
            _partSeqKnown = true;
            return;
        }
        foreach (var r in PlcPartLog.Newer(b, _partSeq))
        {
            _partSeq = r.Seq;
            try { PartLeft?.Invoke(r); } catch { /* the log must not stop the PLC thread */ }
        }
    }

    /// <summary>Publishes the line settings; the PLC takes them over on the next read (new version number).</summary>
    public void PublishSettings(LineSettings s)
    {
        var b = new PlcBlock(LineSettings.Size, _link.BigEndian);
        s.Write(b, ++_settingsVersion == 0 ? ++_settingsVersion : _settingsVersion);
        _link.WriteSettings(b.Bytes);
    }

    /// <summary>The PLC's line diagnostics (null when the PLC sends none).</summary>
    public LineDiagnostics? Diagnostics()
    {
        var raw = _link.ReadDiagnostics();
        if (raw.Length < LineDiagnostics.Size) return null;
        var b = new PlcBlock(LineDiagnostics.Size, _link.BigEndian);
        raw.AsSpan(0, LineDiagnostics.Size).CopyTo(b.Bytes);
        var d = new LineDiagnostics(b);
        return d.LayoutVersion == 0 ? null : d;
    }

    /// <summary>Copies of the blocks for the PLC screen.</summary>
    public (PlcInput In, PlcOutput Out) Snapshot()
    {
        lock (_lock)
        {
            var i = new PlcBlock(PlcInput.Size, _link.BigEndian);
            _link.ReadInput().CopyTo(i.Bytes, 0);
            var o = new PlcBlock(PlcOutput.Size, _link.BigEndian);
            _out.B.Bytes.CopyTo(o.Bytes, 0);
            return (new PlcInput(i), new PlcOutput(o));
        }
    }

    /// <summary>How long a test signal stays on before Done, OK and NG go back to 0 (section 7).</summary>
    public const int TestPulseMs = 2000;
    // when the test signal's pulse ends (Environment.TickCount64); 0 = no test signal on
    private long _testUntil;

    /// <summary>Test signal from the PLC screen (section 7): OK or NOT OK with Done for
    /// <see cref="TestPulseMs"/>, as a real result would be, so the PLC side can be wired without parts.
    /// Only while the line is stopped (Teach), never while the PLC sets RunEnable or a part is being
    /// handled, and the OK signal never replaces a latched NG. The pulse ends early when RunEnable or a
    /// trigger comes up. The caller checks the user's role (engineer).</summary>
    public PlcTestRefusal TestResult(bool ok)
    {
        PlcTestRefusal no;
        lock (_lock)
        {
            var now = new PlcInput(InputNow());
            no = _host.CanInspect ? PlcTestRefusal.LineRunning
                : now.RunEnable ? PlcTestRefusal.RunEnable
                : _out.Busy || now.Trigger || _camBusy.Any(b => b) || _held.Any(h => h is not null) ? PlcTestRefusal.Busy
                : ok && _out.Done && _out.Ng ? PlcTestRefusal.LatchedNg
                : PlcTestRefusal.None;
            if (no == PlcTestRefusal.None)
            {
                _out.Ok = ok;
                _out.Ng = !ok;
                _out.Done = true;
                _testUntil = Environment.TickCount64 + TestPulseMs;
                _link.WriteOutput(_out.B.Bytes);
            }
        }
        if (no == PlcTestRefusal.None) Changed?.Invoke();
        return no;
    }

    private void SetError(ushort code)
    {
        _out.ErrorCode = code;
        _out.Error = code != 0;
    }

    // a refused trigger: error 100 (link or heartbeat lost) is the first cause and stays, so the PLC
    // learns why the line stopped and the error clears when the heartbeat is back
    private void Refuse(ushort code)
    {
        if (_out.ErrorCode != PlcCodes.HeartbeatLost) SetError(code);
    }

    private void Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            _wake.WaitOne(_opt.PollMs);
            try { Step(); }
            catch { /* keep the link running; errors show in the block */ }
        }
    }

    private void Step()
    {
        bool changed = false, camChangedByLoss = false;
        try
        {
            lock (_lock)
            {
                var raw = _link.ReadInput();
                var inB = new PlcBlock(PlcInput.Size, _link.BigEndian);
                raw.CopyTo(inB.Bytes, 0);
                var prevB = new PlcBlock(PlcInput.Size, _link.BigEndian);
                _prev.CopyTo(prevB.Bytes, 0);
                var now = new PlcInput(inB);
                var was = new PlcInput(prevB);
                bool Rise(Func<PlcInput, bool> f) => f(now) && !f(was);
                bool Fall(Func<PlcInput, bool> f) => !f(now) && f(was);
                Volatile.Write(ref _runEnable, now.RunEnable);

                // heartbeat both ways
                if (now.Heartbeat != _lastHb)
                {
                    _lastHb = now.Heartbeat;
                    _hbSeen.Restart();
                    _hbChanges++;
                    _out.HeartbeatEcho = now.Heartbeat;
                    changed = true;
                    if (_hbLost && _hbChanges >= 3)
                    {
                        _hbLost = false;
                        if (_out.ErrorCode == PlcCodes.HeartbeatLost) SetError(0);
                    }
                }
                else if (!_hbLost && _hbSeen.ElapsedMilliseconds > Math.Clamp(_opt.WatchdogMs, 200, 5000))
                {
                    _hbLost = true;
                    _hbLosses++;
                    _hbChanges = 0;
                    SetError(PlcCodes.HeartbeatLost);
                    changed = true;
                    if (TakeBackOk()) camChangedByLoss = true;
                }
                int disconnects = _link.Disconnects;
                if (disconnects != _disconnectsSeen)
                {
                    _disconnectsSeen = disconnects;
                    if (TakeBackOk()) camChangedByLoss = true;
                    changed = true;
                }
                if (_visionHb.ElapsedMilliseconds >= 100)
                {
                    _visionHb.Restart();
                    _out.VisionHeartbeat++;
                    changed = true;
                }

                // alarm reset: clears what is no longer true
                if (Rise(i => i.ResetAlarm))
                {
                    _out.TriggerRejected = false;
                    if (!(_hbLost && _out.ErrorCode == PlcCodes.HeartbeatLost)) SetError(0);
                    changed = true;
                }

                // the test signal's pulse ends after 2 s, or at once when the PLC starts the line or triggers:
                // a test OK never stands while RunEnable = 1 (section 7)
                if (_testUntil != 0 && (Environment.TickCount64 >= _testUntil || now.RunEnable || now.Trigger))
                {
                    _testUntil = 0;
                    if (!_out.Busy) _out.Done = _out.Ok = _out.Ng = false;
                    changed = true;
                }

                // job change by number
                if (Rise(i => i.JobChange))
                {
                    if (_out.Busy) SetError(PlcCodes.JobChangeWhileBusy);
                    else
                    {
                        var err = _host.LoadJob(now.JobSelect);
                        if (err != 0) SetError(err);
                    }
                    _out.JobChangeDone = true;
                    changed = true;
                }
                if (Fall(i => i.JobChange)) { _out.JobChangeDone = false; changed = true; }

                // trigger: four-phase handshake
                if (Rise(i => i.Trigger))
                {
                    if (_out.Busy) { _out.TriggerRejected = true; Refuse(PlcCodes.TriggerWhileBusy); }
                    else if (!ReadyNow(now)) { _out.TriggerRejected = true; Refuse(PlcCodes.TriggerNotReady); }
                    else
                    {
                        Release(0, false);          // dropped and raised again between two looks: it took the last result
                        _gaveUp[0] = false;
                        _out.TriggerRejected = false;
                        _out.Busy = true;
                        _out.Done = _out.Ok = _out.Ng = _out.ExposureDone = false;
                        _out.TriggerIdEcho = now.TriggerId;
                        Volatile.Write(ref _lastTriggerId, now.TriggerId);
                        var trig = new PlcTrigger(now.TriggerId, now.PartId, now.ProductId, now.CameraMask, now.SaveImage)
                            { UserIn = now.UserIns, Text = TakeText(0, now.TriggerId), Held = true };
                        var mark = LinkMark();
                        _inspection = Task.Run(() => Finish(trig, mark));
                    }
                    changed = true;
                }
                if (Fall(i => i.Trigger) && _out.Busy) _gaveUp[0] = true;
                if (Fall(i => i.Trigger) && !_out.Busy)
                {
                    _out.Done = _out.ExposureDone = _out.Ok = _out.Ng = false;
                    changed = true;
                }
                if (!now.Trigger && !_out.Busy) Release(0, false);

                bool ready = ReadyNow(now) && !_out.Busy;
                if (ready != _out.Ready) { _out.Ready = ready; changed = true; }
                var job = _host.ActiveJobNumber;
                // JobActive: a job is loaded and the line runs it (section 3.2), not while stopped
                bool jobActive = job != 0 && _host.CanInspect;
                if (job != _out.ActiveJob || _out.JobActive != jobActive) { _out.ActiveJob = job; _out.JobActive = jobActive; changed = true; }
                if (_out.Simulation != _host.Simulation) { _out.Simulation = _host.Simulation; changed = true; }

                PollPartLog();

                // camera channels: each camera's own four-phase handshake (section 3.5)
                var camRaw = _link.ReadCameraIn();
                var camNow = new PlcBlock(PlcCameraChannels.InSize, _link.BigEndian);
                camRaw.CopyTo(camNow.Bytes, 0);
                var camWas = new PlcBlock(PlcCameraChannels.InSize, _link.BigEndian);
                _camPrev.CopyTo(camWas.Bytes, 0);
                bool camChanged = camChangedByLoss;
                for (int c = 0; c < PlcCameraChannels.Max; c++)
                {
                    bool exists = c < _host.CameraCount;
                    bool t = PlcCameraChannels.Trigger(camNow, c), tw = PlcCameraChannels.Trigger(camWas, c);
                    bool done = PlcCameraChannels.Done(_camOut, c), ok = PlcCameraChannels.Ok(_camOut, c);
                    bool rejected = PlcCameraChannels.Rejected(_camOut, c);
                    if (t && !tw)
                    {
                        if (_camBusy[c] || !exists || !ReadyNow(now))
                        {
                            rejected = true;
                            Refuse(_camBusy[c] ? PlcCodes.TriggerWhileBusy : PlcCodes.TriggerNotReady);
                            changed = true;
                        }
                        else
                        {
                            Release(1 + c, false);
                            _gaveUp[1 + c] = false;
                            rejected = done = ok = false;
                            _camBusy[c] = true;
                            int cam = c;
                            var id = PlcCameraChannels.TriggerId(camNow, c);
                            PlcCameraChannels.SetResult(_camOut, c, id, 0, 0);
                            Volatile.Write(ref _lastTriggerId, id);
                            var trig = new PlcTrigger(id, now.PartId, now.ProductId, (ushort)(1 << c), now.SaveImage)
                                { UserIn = now.UserIns, Text = TakeText(1 + c, id), Held = true };
                            var mark = LinkMark();
                            _camTasks[c] = Task.Run(() => FinishCamera(cam, trig, mark));
                        }
                    }
                    if (!t && tw && _camBusy[c]) _gaveUp[1 + c] = true;
                    if (!t && tw && !_camBusy[c]) done = ok = false;
                    if (!t && !_camBusy[c]) Release(1 + c, false);
                    if (!t) rejected = false;          // shown until the PLC drops its trigger
                    bool camReady = exists && ReadyNow(now) && !_camBusy[c];
                    var before = (byte[])_camOut.Bytes.Clone();
                    PlcCameraChannels.SetStatus(_camOut, c, camReady, _camBusy[c], done, ok, rejected);
                    if (!before.AsSpan().SequenceEqual(_camOut.Bytes)) camChanged = true;
                }
                _camPrev = camRaw;
                if (camChanged) _link.WriteCameraOut(_camOut.Bytes);

                _prev = raw;
                if (changed) _link.WriteOutput(_out.B.Bytes);
                changed |= camChanged;
            }
        }
        finally { Deliver(); }   // even when a step throws after a release
        if (changed) Changed?.Invoke();
    }

    // Under the lock: hands a source's held result over (to Deliver). At a loss (or when the runtime
    // closes) a held OK the PLC has not taken becomes NG 100, as TakeBackOk writes it to the PLC.
    private void Release(int source, bool loss)
    {
        if (_held[source] is not { } h) return;
        _held[source] = null;
        bool turn = loss && h.Result.Ok;
        _deliver.Add(new PlcDelivery(source - 1, h.Trigger, turn ? LinkLossNg(h.Result) : h.Result, h.Turned || turn));
    }

    // Outside the lock, on the PLC thread (or in Dispose): the host counts and logs each part once.
    private void Deliver()
    {
        PlcDelivery[] list;
        lock (_lock)
        {
            if (_deliver.Count == 0) return;
            list = _deliver.ToArray();
            _deliver.Clear();
        }
        foreach (var d in list)
            try { _host.Delivered(d); } catch { /* the log must not stop the PLC thread */ }
    }

    private static PlcInspection LinkLossNg(PlcInspection r) =>
        r with { Ok = false, NgReason = PlcCodes.HeartbeatLost, ErrorCode = PlcCodes.HeartbeatLost };

    // the text block as it is now, for a trigger with this id; each id is taken once per trigger source,
    // so strings left from the part before are never checked against this one
    private PlcText TakeText(int source, ushort triggerId)
    {
        bool repeated = _textTaken[source] == triggerId;
        _textTaken[source] = triggerId;
        return new PlcText(_link.ReadText(), _link.BigEndian, triggerId, repeated);
    }

    private bool ReadyNow(PlcInput i) =>
        _host.CanInspect && i.RunEnable && !_hbLost && !(_out.Error && _out.ErrorCode is not 0 and not PlcCodes.Restarted);

    // heartbeat losses and ended PLC connections: taken when the trigger's write landed, compared with
    // the count when its result is written
    private (int HbLosses, int Disconnects) LinkMark() => (_hbLosses, _link.DisconnectsAtInputWrite);
    private (int HbLosses, int Disconnects) LinkNow() => (_hbLosses, _link.Disconnects);

    // section 5.6 on a loss noticed after the result went out (a pulled cable shows only when the
    // watchdog runs out): a latched OK the PLC may not have taken becomes NG 100. Returns whether a
    // camera channel changed.
    private bool TakeBackOk()
    {
        for (int s = 0; s < _held.Length; s++) Release(s, true);
        if (_out.Done && _out.Ok)
        {
            _out.Ok = false;
            _out.Ng = true;
            _out.NgReason = PlcCodes.HeartbeatLost;
            SetError(PlcCodes.HeartbeatLost);
        }
        bool cam = false;
        for (int c = 0; c < PlcCameraChannels.Max; c++)
        {
            if (!(PlcCameraChannels.Done(_camOut, c) && PlcCameraChannels.Ok(_camOut, c))) continue;
            PlcCameraChannels.SetResult(_camOut, c, PlcCameraChannels.IdEcho(_camOut, c), PlcCodes.HeartbeatLost,
                _camOut.U16(8 * c + 6));
            PlcCameraChannels.SetStatus(_camOut, c, PlcCameraChannels.Ready(_camOut, c), PlcCameraChannels.Busy(_camOut, c),
                true, false, PlcCameraChannels.Rejected(_camOut, c));
            SetError(PlcCodes.HeartbeatLost);
            cam = true;
        }
        return cam;
    }

    // section 5.6: the PLC never gets OK for a part whose trigger came before a link or heartbeat loss
    private PlcInspection AfterLinkLoss(PlcInspection r, (int, int) mark)
    {
        if (Connected && LinkNow() == mark) return r;
        return LinkLossNg(r);
    }

    private void Finish(PlcTrigger trig, (int, int) mark)
    {
        var sw = Stopwatch.StartNew();
        PlcInspection r;
        try { r = _host.Inspect(trig); }
        catch (Exception) { r = new PlcInspection(false, 0, 0, "", "", Array.Empty<float>(), PlcCodes.ToolFailed); }
        lock (_lock)
        {
            var inspected = r;
            r = AfterLinkLoss(r, mark);
            if (Hold(0, trig, r, inspected)) { _out.Busy = false; goto closed; }
            // data first ...
            for (int i = 0; i < 20; i++) _out.SetValue(i, i < r.Values.Count ? r.Values[i] : float.NaN);
            bool fit = _out.SetText1(r.Text1) & _out.SetText2(r.Text2);
            _out.WarningCode = fit ? (ushort)0 : PlcCodes.StringCut;
            _out.Warning = !fit;
            _out.ToolPass = r.ToolPass;
            _out.NgReason = r.Ok ? (ushort)0 : r.NgReason;
            _out.CycleTime = (float)sw.Elapsed.TotalMilliseconds;
            _out.InspectionCount = (uint)++Inspections;
            if (r.ErrorCode != 0) SetError(r.ErrorCode);
            _link.WriteOutput(_out.B.Bytes);
            // ... then the status bits
            _out.ExposureDone = true;
            _out.Ok = r.Ok;
            _out.Ng = !r.Ok;
            _out.Done = true;
            _out.Busy = false;
            _link.WriteOutput(_out.B.Bytes);
        }
        Changed?.Invoke();
        closed:
        Deliver();
        _wake.Set();
    }

    private void FinishCamera(int cam, PlcTrigger trig, (int, int) mark)
    {
        var sw = Stopwatch.StartNew();
        PlcInspection r;
        try { r = _host.InspectCamera(cam, trig); }
        catch (Exception) { r = new PlcInspection(false, 0, 0, "", "", Array.Empty<float>(), PlcCodes.ToolFailed); }
        lock (_lock)
        {
            var inspected = r;
            r = AfterLinkLoss(r, mark);
            if (Hold(1 + cam, trig, r, inspected)) { _camBusy[cam] = false; goto closed; }
            // result first, then the status bits
            PlcCameraChannels.SetResult(_camOut, cam, trig.TriggerId, r.Ok ? (ushort)0 : r.NgReason,
                (ushort)Math.Min(65535, sw.Elapsed.TotalMilliseconds));
            _link.WriteCameraOut(_camOut.Bytes);
            _camBusy[cam] = false;
            PlcCameraChannels.SetStatus(_camOut, cam, false, false, true, r.Ok, false);
            _link.WriteCameraOut(_camOut.Bytes);
            _out.InspectionCount = (uint)++Inspections;
            if (r.ErrorCode != 0) SetError(r.ErrorCode);
            _link.WriteOutput(_out.B.Bytes);
        }
        Changed?.Invoke();
        closed:
        Deliver();
        _wake.Set();
    }

    // Under the lock, when a source's inspection ends: holds the result until the PLC takes it. When the
    // PLC already dropped its trigger it gave up (result timeout) and made the part NG: handed over now as
    // NG. When the runtime has closed the PLC never gets it: handed over now as NG 100, and true (nothing
    // more to write; the link is closed).
    private bool Hold(int source, PlcTrigger trig, PlcInspection r, PlcInspection inspected)
    {
        _held[source] = (trig, r, inspected.Ok && !r.Ok);
        if (_closed) { Release(source, true); return true; }
        bool down = source == 0 ? !new PlcInput(InputNow()).Trigger
            : !PlcCameraChannels.Trigger(CameraInNow(), source - 1);
        if (_gaveUp[source] || down)
        {
            _held[source] = null;
            _deliver.Add(new PlcDelivery(source - 1, trig, r.Ok ? r with { Ok = false } : r, inspected.Ok && !r.Ok) { NotTaken = true });
        }
        return false;
    }

    private PlcBlock InputNow()
    {
        var b = new PlcBlock(PlcInput.Size, _link.BigEndian);
        _link.ReadInput().CopyTo(b.Bytes, 0);
        return b;
    }

    private PlcBlock CameraInNow()
    {
        var b = new PlcBlock(PlcCameraChannels.InSize, _link.BigEndian);
        _link.ReadCameraIn().CopyTo(b.Bytes, 0);
        return b;
    }

    /// <summary>Camera channel outputs for the PLC screen.</summary>
    public PlcBlock CameraOutSnapshot()
    {
        lock (_lock)
        {
            var b = new PlcBlock(PlcCameraChannels.OutSize, _link.BigEndian);
            _camOut.Bytes.CopyTo(b.Bytes, 0);
            return b;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        _thread.Join(2000);
        var running = _camTasks.Append(_inspection).OfType<Task>().ToArray();
        try { Task.WaitAll(running, 2000); } catch { /* an inspection's failure is its own */ }
        // the link is closed: the PLC treats a part it had not taken as NG (section 5.6); an inspection
        // still running hands its part over as NG 100 when it ends (Hold)
        lock (_lock)
        {
            _closed = true;
            for (int s = 0; s < _held.Length; s++) Release(s, true);
        }
        _link.Dispose();
        Deliver();
    }
}
