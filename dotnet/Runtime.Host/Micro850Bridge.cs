using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VisionCore.Plc;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// Settings of the Allen-Bradley Micro850 link (section "Micro850" of vs-runtime.json). TRIAL BUILD ONLY:
/// the line's existing CCW program (HARPIK08042026_Final_141026) is a Modbus TCP server with holding
/// registers 400001-400050 (mod_address[0..49]); this PC is the client. The PLC fires the cameras itself
/// (DO_00, DO_09, P1_DO_01 to the cameras' Line0) at encoder distances, opens a result window per product
/// and rejects every product that gets no PASS in that window. This PC writes PASS (register 3) or FAIL
/// (register 4) and toggles the heartbeat (register 43 bit 1; stuck 5 s = "software crash", line stops).
/// </summary>
public sealed class Micro850Options
{
    /// <summary>On: the station runs in PLC mode, the cameras wait for the PLC's hardware trigger.</summary>
    public bool Enabled { get; set; }
    public string PlcIp { get; set; } = "192.168.1.5";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;
    /// <summary>Modbus address of register 0: 0 for the Micro850 (400001 = mod_address[0]); 4096 for the factory's
    /// Delta DVP-SE running plc/delta-dvp/RBTrialDVP.st (D0 = 16#1000).</summary>
    public int RegisterBase { get; set; }
    /// <summary>One request's answer time; a link that misses it is reconnected.</summary>
    public int TimeoutMs { get; set; } = 300;
    /// <summary>Register 43 bit 1 is toggled this often; the PLC stops the line when it is unchanged for 5 s.</summary>
    public int HeartbeatMs { get; set; } = 1000;
    /// <summary>The PLC's status words (25-27, 35-37, 43) are read this often for the log.</summary>
    public int StatusMs { get; set; } = 500;
    /// <summary>Camera trigger input that the PLC output is wired to (Hikrobot "Line0", "Line2").</summary>
    public string CameraTrigger { get; set; } = "Line0";
    public int TriggerDebounceUs { get; set; } = 50;
    /// <summary>Per job camera (1, 2, 3, 4): when its frame arrives after the product's first camera frame, in ms.
    /// Cameras on the same PLC output have 0. Set at commissioning from the "offset" column of the product log.</summary>
    public double[] CameraDelayMs { get; set; } = [0, 0, 0, 0];
    /// <summary>A frame belongs to a product when its time, less its camera's delay, is this close to the product's.
    /// Must be well under half the product pitch (267 ms at 225/min).</summary>
    public double ToleranceMs { get; set; } = 40;
    /// <summary>The result is written no earlier than this after the product's time (the PLC's window opens at
    /// register 47's distance: a PASS before that is not taken and the product is rejected).</summary>
    public double ResultNotBeforeMs { get; set; } = 0;
    /// <summary>No result is written later than this after the product's time: the PLC rejects the product itself
    /// (no result in its window) and a late PASS could land in the next product's window.</summary>
    public double ResultDeadlineMs { get; set; } = 200;
    /// <summary>Writes <see cref="Setpoints"/> to the PLC on every connect. Off: the PLC keeps its own values.</summary>
    public bool WriteSetpointsOnConnect { get; set; }
    /// <summary>Register (0-49) to value, for example "0": 30 (camera trigger 1 distance, encoder pulses).</summary>
    public Dictionary<string, int> Setpoints { get; set; } = new();
    /// <summary>Product log folder (relative: under the data folder).</summary>
    public string LogFolder { get; set; } = "micro850";
}

/// <summary>A minimal Modbus TCP client: read holding registers (3), write one (6). One request at a time.</summary>
public sealed class ModbusTcpClient(string host, int port, byte unit, int timeoutMs) : IDisposable
{
    TcpClient? _tcp;
    NetworkStream? _s;
    ushort _tid;
    readonly object _lock = new();

    public bool Connected => _tcp?.Connected == true;

    public void Connect()
    {
        lock (_lock)
        {
            Close();
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
            if (!tcp.ConnectAsync(host, port).Wait(Math.Max(timeoutMs, 1000)))
            {
                tcp.Dispose();
                throw new IOException($"PLC {host}:{port} did not answer");
            }
            _tcp = tcp;
            _s = tcp.GetStream();
        }
    }

    public ushort[] Read(ushort address, ushort count)
    {
        var pdu = new byte[] { 3, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count };
        var r = Request(pdu);
        if (r.Length < 2 + 2 * count || r[1] != 2 * count) throw new IOException("Modbus: short answer");
        var v = new ushort[count];
        for (var i = 0; i < count; i++) v[i] = (ushort)(r[2 + 2 * i] << 8 | r[3 + 2 * i]);
        return v;
    }

    public void Write(ushort address, ushort value) =>
        Request(new byte[] { 6, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value });

    byte[] Request(byte[] pdu)
    {
        lock (_lock)
        {
            var s = _s ?? throw new IOException("PLC not connected");
            var id = ++_tid;
            var frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(id >> 8); frame[1] = (byte)id;
            frame[4] = (byte)((pdu.Length + 1) >> 8); frame[5] = (byte)(pdu.Length + 1);
            frame[6] = unit;
            pdu.CopyTo(frame, 7);
            try
            {
                s.Write(frame);
                var head = ReadExactly(s, 7);
                var len = head[4] << 8 | head[5];
                var body = ReadExactly(s, len - 1);
                if ((head[0] << 8 | head[1]) != id) throw new IOException("Modbus: answer to another request");
                if ((body[0] & 0x80) != 0) throw new IOException($"Modbus exception {body.ElementAtOrDefault(1)} for function {pdu[0]}");
                return body;
            }
            catch { Close(); throw; }
        }
    }

    static byte[] ReadExactly(NetworkStream s, int n)
    {
        var b = new byte[n];
        for (int got = 0, k; got < n; got += k)
            if ((k = s.Read(b, got, n - got)) == 0) throw new IOException("PLC closed the connection");
        return b;
    }

    void Close()
    {
        try { _tcp?.Dispose(); } catch { }
        _tcp = null;
        _s = null;
    }

    public void Dispose() { lock (_lock) Close(); }
}

/// <summary>
/// Puts each camera's frames together into products by time (the PLC tells this PC nothing about which product a
/// frame is of). A frame at time t of camera c is of the open product whose time is t - delay[c] within the
/// tolerance and that has no frame of c yet; else it starts a product. Line safety: a frame that fits two products,
/// or two products closer than twice the tolerance, makes them FAIL; a product with a camera missing at its deadline
/// gets no PASS.
/// </summary>
public sealed class ProductGrouper(int cameras, double[] delayMs, double toleranceMs)
{
    /// <summary>Cameras of the loaded job: a product is complete when each of them has a result.</summary>
    public int Cameras { get; set; } = cameras;

    public sealed class Product(long seq, double refMs, int cameras)
    {
        public long Seq { get; } = seq;
        public double RefMs { get; } = refMs;
        public double?[] ArrivedMs { get; } = new double?[cameras];
        public bool?[] Ok { get; } = new bool?[cameras];
        public string?[] Why { get; } = new string?[cameras];
        public bool Ambiguous { get; set; }
        public bool Decided { get; set; }
        public bool Complete => Ok.All(o => o.HasValue);
        public bool Pass => !Ambiguous && Ok.All(o => o == true);
    }

    readonly List<Product> _open = new();
    long _seq;

    public double Delay(int cam) => cam < delayMs.Length ? delayMs[cam] : 0;

    /// <summary>The product this frame (camera, arrival time in ms) belongs to.</summary>
    public Product Arrive(int cam, double tMs)
    {
        var r = tMs - Delay(cam);
        var fits = _open.Where(p => !p.ArrivedMs[cam].HasValue && Math.Abs(r - p.RefMs) <= toleranceMs).ToList();
        Product prod;
        if (fits.Count == 0)
        {
            prod = new Product(++_seq, r, Cameras);
            // two products this close cannot be told apart: neither passes
            foreach (var p in _open.Where(p => Math.Abs(r - p.RefMs) < 2 * toleranceMs)) { p.Ambiguous = true; prod.Ambiguous = true; }
            _open.Add(prod);
        }
        else
        {
            prod = fits.MinBy(p => Math.Abs(r - p.RefMs))!;
            if (fits.Count > 1) foreach (var p in fits) p.Ambiguous = true;
        }
        prod.ArrivedMs[cam] = tMs;
        return prod;
    }

    /// <summary>Products that are complete or past <paramref name="deadlineMs"/>; decided ones are dropped after a while.</summary>
    public List<Product> Due(double nowMs, double notBeforeMs, double deadlineMs)
    {
        var due = _open.Where(p => !p.Decided && nowMs >= p.RefMs + notBeforeMs && (p.Complete || nowMs > p.RefMs + deadlineMs)).ToList();
        _open.RemoveAll(p => p.Decided && nowMs > p.RefMs + deadlineMs + 4 * toleranceMs);
        return due;
    }

    public int Open => _open.Count;
}

/// <summary>Frames as they reach the PC from cameras fired by the PLC: the bridge's view of the open cameras.</summary>
public sealed class HardwareFrames
{
    /// <summary>Camera name, its frame number and the arrival time (Stopwatch ticks); raised on the camera thread.</summary>
    public event Action<string, ulong, long>? Arrived;
    /// <summary>A camera of this name was opened (its earlier frames are gone).</summary>
    public event Action<string>? Opened;
    public void RaiseArrived(string name, ulong number, long ticks) => Arrived?.Invoke(name, number, ticks);
    public void RaiseOpened(string name) => Opened?.Invoke(name);
}

/// <summary>
/// The Micro850 link (TRIAL BUILD). Each camera frame is inspected on its camera's own thread
/// (<see cref="Station.InspectCamera"/>), the frames are put together into products by <see cref="ProductGrouper"/>,
/// and the product's verdict is written to the PLC: PASS only when every camera passed in time, else FAIL, or nothing
/// after the deadline (the PLC then rejects the product itself).
/// </summary>
public sealed class Micro850Bridge : BackgroundService
{
    // holding register offsets (400001 = 0) of the PLC program HARPIK08042026_Final_141026
    public const ushort RegPass = 3, RegFail = 4, RegHeartbeat = 43, RegStatusFrom = 25, RegStatusCount = 19;

    readonly Micro850Options _o;
    readonly Station _station;
    readonly HardwareFrames _frames;
    readonly ILogger<Micro850Bridge> _log;
    readonly string _logDir;
    readonly ConcurrentQueue<Action> _todo = new();
    readonly AutoResetEvent _wake = new(false);
    readonly ProductGrouper _grouper;
    readonly BlockingCollection<(ProductGrouper.Product P, long Ticks)>[] _work;
    readonly long[] _generation;
    readonly object _groupLock = new();
    ushort[]? _lastStatus;
    long _lastPassTicks, _lastFailTicks;
    public long Passed, Failed, Unwritten;

    public Micro850Bridge(IOptions<Micro850Options> o, Station station, HardwareFrames frames, IOptions<StudioOptions> studio, ILogger<Micro850Bridge> log)
    {
        _o = o.Value;
        _station = station;
        _frames = frames;
        _log = log;
        _logDir = Path.IsPathRooted(_o.LogFolder) ? _o.LogFolder : Path.Combine(studio.Value.DataFolder, _o.LogFolder);
        _grouper = new ProductGrouper(VisionCore.Job.MaxCameras, _o.CameraDelayMs, _o.ToleranceMs);
        _work = Enumerable.Range(0, VisionCore.Job.MaxCameras).Select(_ => new BlockingCollection<(ProductGrouper.Product, long)>()).ToArray();
        _generation = new long[VisionCore.Job.MaxCameras];
        if (!_o.Enabled) return;
        if (_o.ToleranceMs <= 0 || _o.ResultDeadlineMs <= _o.ResultNotBeforeMs)
            throw new InvalidOperationException("Micro850: ToleranceMs must be > 0 and ResultDeadlineMs > ResultNotBeforeMs");
        _frames.Arrived += OnFrame;
        _frames.Opened += OnOpened;
    }

    public bool Enabled => _o.Enabled;
    public bool Connected { get; private set; }
    public string? LastError { get; private set; }

    ushort At(int register) => checked((ushort)(_o.RegisterBase + register));

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    int CameraIndex(string name)
    {
        var job = _station.Job;
        return job is null ? -1 : job.Cameras.FindIndex(c => c.Name == name);
    }

    void OnOpened(string name)
    {
        // a camera opened afresh: frames waiting for its old instance are gone with it
        var i = CameraIndex(name);
        if (i < 0) return;
        Interlocked.Increment(ref _generation[i]);
        while (_work[i].TryTake(out _)) { }
    }

    void OnFrame(string name, ulong number, long ticks)
    {
        var cam = CameraIndex(name);
        if (cam < 0 || cam >= _work.Length) return;
        ProductGrouper.Product p;
        lock (_groupLock)
        {
            _grouper.Cameras = _station.Job?.Cameras.Count ?? 0;
            if (cam >= _grouper.Cameras) return;
            p = _grouper.Arrive(cam, Ms(ticks));
        }
        _work[cam].Add((p, Interlocked.Read(ref _generation[cam])));
    }

    // one camera's inspections, in frame order: the frame this item stands for is the oldest in the camera's intake
    void CameraWorker(int cam, CancellationToken ct)
    {
        foreach (var (p, gen) in _work[cam].GetConsumingEnumerable(ct))
        {
            bool ok;
            string? why = null;
            try
            {
                if (gen != Interlocked.Read(ref _generation[cam]) || !_station.CanInspect) { ok = false; why = "line not running"; }
                else
                {
                    var r = _station.InspectCamera(cam, new PlcTrigger((ushort)p.Seq, $"P{p.Seq}", (uint)p.Seq, (ushort)(1 << cam), false));
                    ok = r.Ok;
                    if (!ok) why = r.ErrorCode != 0 ? $"error {r.ErrorCode}" : $"tool {r.NgReason}";
                }
            }
            catch (Exception e) { ok = false; why = e.Message; }
            lock (_groupLock) { p.Ok[cam] = ok; p.Why[cam] = why; }
            _wake.Set();
        }
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!_o.Enabled) return Task.CompletedTask;
        for (var i = 0; i < _work.Length; i++)
        {
            var cam = i;
            new Thread(() => { try { CameraWorker(cam, ct); } catch (OperationCanceledException) { } }) { IsBackground = true, Name = $"m850-cam{cam + 1}" }.Start();
        }
        return Task.Factory.StartNew(() => Loop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    void Loop(CancellationToken ct)
    {
        Directory.CreateDirectory(_logDir);
        using var plc = new ModbusTcpClient(_o.PlcIp, _o.Port, _o.UnitId, _o.TimeoutMs);
        var sw = Stopwatch.StartNew();
        long nextHb = 0, nextStatus = 0, nextConnect = 0;
        var hb = false;
        while (!ct.IsCancellationRequested)
        {
            _wake.WaitOne(2);
            var now = sw.ElapsedMilliseconds;
            if (!plc.Connected)
            {
                if (Connected) { Connected = false; _log.LogWarning("Micro850 {Ip}: link lost; products get no result and the PLC rejects them", _o.PlcIp); }
                if (now >= nextConnect)
                {
                    nextConnect = now + 1000;
                    try
                    {
                        plc.Connect();
                        Connected = true;
                        LastError = null;
                        _log.LogInformation("Micro850 {Ip}:{Port} connected", _o.PlcIp, _o.Port);
                        if (_o.WriteSetpointsOnConnect) WriteSetpoints(plc);
                    }
                    catch (Exception e) { LastError = e.Message; }
                }
            }
            try
            {
                Decide(plc);
                if (plc.Connected && now >= nextHb)
                {
                    // the heartbeat says this program is alive; with the line stopped the PLC still rejects every product
                    nextHb = now + _o.HeartbeatMs;
                    hb = !hb;
                    plc.Write(At(RegHeartbeat), (ushort)(hb ? 2 : 0));
                }
                if (plc.Connected && now >= nextStatus)
                {
                    nextStatus = now + _o.StatusMs;
                    Status(plc.Read(At(RegStatusFrom), RegStatusCount));
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                LastError = e.Message;
            }
        }
    }

    void WriteSetpoints(ModbusTcpClient plc)
    {
        foreach (var (k, v) in _o.Setpoints)
        {
            if (!ushort.TryParse(k, out var reg) || reg > 49 || reg is RegPass or RegFail or RegHeartbeat)
                throw new InvalidOperationException($"Micro850 setpoint register \"{k}\": use 0-49 except 3, 4 and 43");
            plc.Write(At(reg), (ushort)(short)v);
        }
        _log.LogInformation("Micro850: {Count} setpoints written", _o.Setpoints.Count);
    }

    void Decide(ModbusTcpClient plc)
    {
        List<ProductGrouper.Product> due;
        var nowTicks = Stopwatch.GetTimestamp();
        var now = Ms(nowTicks);
        lock (_groupLock)
        {
            due = _grouper.Due(now, _o.ResultNotBeforeMs, _o.ResultDeadlineMs);
            foreach (var p in due) p.Decided = true;
        }
        foreach (var p in due)
        {
            bool pass;
            lock (_groupLock) pass = p.Complete && p.Pass;
            var written = "none";
            // the PLC clears register 3 after 50 ms and 4 after 100 ms: a second write before that is not seen as a new
            // result. A PASS that would come too late is never written: the product then has no result and is rejected.
            var gap = pass ? 60 : 110;
            var last = pass ? _lastPassTicks : _lastFailTicks;
            var wait = gap - Ms(nowTicks - last);
            if (wait > 0 && now + wait <= p.RefMs + _o.ResultDeadlineMs) { Thread.Sleep((int)Math.Ceiling(wait)); now = Ms(Stopwatch.GetTimestamp()); wait = 0; }
            if (plc.Connected && wait <= 0 && now <= p.RefMs + _o.ResultDeadlineMs)
            {
                plc.Write(At(pass ? RegPass : RegFail), 1);
                if (pass) { _lastPassTicks = Stopwatch.GetTimestamp(); Interlocked.Increment(ref Passed); }
                else { _lastFailTicks = Stopwatch.GetTimestamp(); Interlocked.Increment(ref Failed); }
                written = pass ? "PASS" : "FAIL";
            }
            else Interlocked.Increment(ref Unwritten);
            LogProduct(p, written, now);
        }
    }

    void LogProduct(ProductGrouper.Product p, string written, double nowMs)
    {
        var file = Path.Combine(_logDir, $"products-{DateTime.Now:yyyyMMdd}.csv");
        var cams = _station.Job?.Cameras.Count ?? p.Ok.Length;
        var c = CultureInfo.InvariantCulture;
        string line;
        lock (_groupLock)
        {
            var cols = Enumerable.Range(0, cams).Select(i =>
                $"{(p.Ok[i] is null ? "missing" : p.Ok[i] == true ? "ok" : "ng")};{(p.ArrivedMs[i] is { } a ? (a - p.RefMs + _grouper.Delay(i)).ToString("0.0", c) : "")};{p.Why[i]?.Replace(',', ' ')}");
            line = string.Join(",", new[] { DateTime.Now.ToString("HH:mm:ss.fff", c), p.Seq.ToString(c), written, p.Ambiguous ? "ambiguous" : "",
                (nowMs - p.RefMs).ToString("0.0", c) }.Concat(cols));
        }
        try
        {
            var head = !File.Exists(file);
            using var w = File.AppendText(file);
            if (head) w.WriteLine("time,product,written,flag,result_ms_after_product," + string.Join(",", Enumerable.Range(1, cams).Select(i => $"cam{i} result;offset_ms;why")));
            w.WriteLine(line);
        }
        catch (IOException) { /* the log never holds up a result */ }
        if (written != "PASS") _log.LogInformation("Micro850 product {Seq}: {Written} {Line}", p.Seq, written, line);
    }

    // status words 25-43: encoder 25, counters 26-27, inputs 35, outputs 36, alarms 37, flags 43
    static readonly (int Bit, string Name)[] AlarmBits =
    [
        (0, "result window longer than the product pitch (Delta factory program)"), (8, "infeed jammed"), (9, "emergency stop / DI_13 off"), (10, "P1_DI_00 off"), (11, "reject verification failed"),
        (12, "outfeed jammed"), (13, "reject bin full"), (14, "alarm 37.14"), (15, "vision heartbeat lost (software crash)"),
        (2, "DI_05 off"), (3, "DI_06 off"), (4, "DI_07 off"), (5, "DI_08 off"), (6, "DI_09 off"), (7, "DI_10 off"),
    ];

    void Status(ushort[] s)
    {
        var before = _lastStatus;
        _lastStatus = s;
        if (before is null) return;
        int alarms = s[37 - RegStatusFrom], was = before[37 - RegStatusFrom];
        foreach (var (bit, name) in AlarmBits)
        {
            bool on = (alarms >> bit & 1) != 0, then = (was >> bit & 1) != 0;
            if (on != then) _log.LogWarning("Micro850 alarm {State}: {Name}", on ? "ON" : "cleared", name);
        }
    }

    /// <summary>Encoder (25), counters (26, 27), inputs (35), outputs (36), alarms (37) and flags (43) as last read.</summary>
    public ushort[]? LastStatus => _lastStatus;

    public override void Dispose()
    {
        foreach (var w in _work) w.CompleteAdding();
        base.Dispose();
    }
}
