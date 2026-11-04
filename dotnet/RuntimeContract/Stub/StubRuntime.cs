using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using VisionStudio.Remote.V1;

namespace VisionStudio.Remote.Stub;

/// <summary>
/// An in-memory runtime that serves the whole contract with made-up inspections, so Vision Studio
/// and the dashboard can be built and checked on a Mac before the real runtime service exists
/// (Hybrid 1). It applies the same access rules as the real runtime (<see cref="RuntimeAccess"/>).
/// Nothing here touches a camera, a PLC or a file except the optional folder of JPEG frames.
/// </summary>
public sealed class StubRuntime : IDisposable
{
    public sealed record User(string Name, string FullName, string? Password, string? Pin, Role[] Roles);
    public sealed record Job(string Name, int Number, string[] Variants, string Format, int CodesPerCase);
    public sealed record SessionInfo(ClientKind Client, User? User, bool OperateToken)
    {
        public IReadOnlyCollection<Role> Roles => User?.Roles ?? [];
    }

    // Test values for the stub only; the real runtime reads its user store.
    public static readonly User[] DefaultUsers =
    [
        new("operator", "Line Operator", null, "1234", [Role.Operator]),
        new("supervisor", "Shift Supervisor", "supervisor", "2345", [Role.Supervisor]),
        new("engineer", "Vision Engineer", "engineer", null, [Role.Engineer]),
        new("admin", "Plant Admin", "admin", null, [Role.Admin, Role.Qa]),
    ];

    /// <summary>The stub's token for plant software with the operate right.</summary>
    public const string PlantOperateToken = "stub-plant-operate";
    /// <summary>The stub's token for plant software that may only read.</summary>
    public const string PlantReadToken = "stub-plant-read";

    public static readonly Job[] DefaultJobs =
    [
        new("Whisky 750 ml", 1, ["LIQ-750-48"], "QrCode", 48),
        new("Tablets 10x10", 2, ["PH-1010-24", "PH-1010-12"], "DataMatrix", 24),
        new("Label check", 3, ["LBL-A"], "QrCode", 12),
    ];

    readonly object _gate = new();
    readonly Random _random;
    readonly IReadOnlyList<User> _users;
    readonly IReadOnlyList<Job> _jobs;
    readonly IReadOnlyList<string> _frames;
    readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    readonly List<Channel<Status>> _statusWatchers = [];
    readonly List<Channel<InspectionResult>> _resultWatchers = [];
    readonly List<Channel<LiveFrame>> _frameWatchers = [];
    readonly List<double> _cycles = [];
    readonly Dictionary<string, ulong> _ngReasons = [];
    readonly HashSet<string> _seenCodes = [];
    /// <summary>The last full images by id, oldest first, for GetImage (as the runtime keeps its NG images).</summary>
    readonly Dictionary<string, StoredImage> _images = [];
    readonly Queue<string> _imageOrder = new();

    sealed record StoredImage(byte[] Jpeg, int Width, int Height, IReadOnlyList<(float, float, float, float, bool)> Codes);

    /// <summary>How many full images GetImage can still send.</summary>
    public const int KeptImages = 50;
    CancellationTokenSource? _loop;

    Job _job;
    string _variant;
    Source _source = Source.Replay;
    bool _running;
    ulong _nextInspection = 1, _total, _ok, _ng;
    uint _nextTrigger = 1;
    long _nextSerial = 187477797;
    Timestamp? _batchStarted;
    InspectionResult? _last;
    readonly List<Alarm> _alarms = [];

    public StubRuntime(string? framesFolder = null, int seed = 1,
        IReadOnlyList<User>? users = null, IReadOnlyList<Job>? jobs = null)
    {
        _random = new Random(seed);
        _users = users ?? DefaultUsers;
        _jobs = jobs ?? DefaultJobs;
        _job = _jobs[0];
        _variant = _job.Variants[0];
        _frames = framesFolder is not null && Directory.Exists(framesFolder)
            ? Directory.EnumerateFiles(framesFolder)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToList()
            : [];
        _sessions[PlantOperateToken] = new SessionInfo(ClientKind.PlantSoftware, null, true);
        _sessions[PlantReadToken] = new SessionInfo(ClientKind.PlantSoftware, null, false);
    }

    /// <summary>Time between made-up inspections while running.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Share of cases with a missed code (NG).</summary>
    public double NgRate { get; init; } = 0.08;

    public int FrameCount => _frames.Count;

    // --- sessions -----------------------------------------------------------------------------

    public SignInResponse SignIn(SignInRequest req)
    {
        var user = _users.FirstOrDefault(u => string.Equals(u.Name, req.User, StringComparison.OrdinalIgnoreCase));
        var ok = user is not null && req.SecretCase switch
        {
            SignInRequest.SecretOneofCase.Password => user.Password is not null && FixedEquals(user.Password, req.Password),
            SignInRequest.SecretOneofCase.Pin => user.Pin is not null && FixedEquals(user.Pin, req.Pin),
            _ => false,
        };
        if (!ok) return new SignInResponse { ErrorCode = 401, ErrorText = "Wrong name or password. Try again.", ErrorKey = "runtime.signIn.wrong" };
        var client = req.Client == ClientKind.Unspecified ? ClientKind.Studio : req.Client;
        if (client == ClientKind.PlantSoftware)
            return new SignInResponse { ErrorCode = 400, ErrorText = "Plant software signs in with a token, not a user.", ErrorKey = "runtime.signIn.useToken" };
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _sessions[token] = new SessionInfo(client, user, false);
        return new SignInResponse { Ok = true, Token = token, User = Info(user!) };
    }

    public void SignOut(string? token)
    {
        if (token is not null && token != PlantOperateToken && token != PlantReadToken) _sessions.TryRemove(token, out _);
    }

    public SessionInfo? Find(string? token) => token is not null && _sessions.TryGetValue(token, out var s) ? s : null;

    public WhoAmIResponse WhoAmI(SessionInfo s)
    {
        var r = new WhoAmIResponse
        {
            Client = s.Client,
            MayOperate = RuntimeAccess.MayOperate(s.Client, s.Roles, s.OperateToken),
            MayChangeSetup = RuntimeAccess.MayChangeSetup(s.Client, s.Roles),
            MayDesignJobs = RuntimeAccess.MayDesignJobs(s.Client, s.Roles),
            MayTestPlc = RuntimeAccess.MayTestPlc(s.Client, s.Roles),
        };
        if (s.User is not null) r.User = Info(s.User);
        return r;
    }

    public ListUsersResponse ListUsers()
    {
        var r = new ListUsersResponse();
        r.Users.AddRange(_users.Select(Info));
        return r;
    }

    static UserInfo Info(User u)
    {
        var i = new UserInfo { Name = u.Name, FullName = u.FullName, HasPin = u.Pin is not null, HasPassword = u.Password is not null };
        i.Roles.AddRange(u.Roles);
        return i;
    }

    static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    // --- state --------------------------------------------------------------------------------

    public Status Status()
    {
        lock (_gate) return StatusLocked();
    }

    Status StatusLocked()
    {
        var s = new Status
        {
            State = _running ? V1.Status.Types.State.Busy : V1.Status.Types.State.Stopped,
            ActiveJob = _job.Name,
            ActiveJobNumber = _job.Number,
            ActiveVariant = _variant,
            PlcConnected = true,
            Inspections = _total,
            Ng = _ng,
            Version = "stub-1.0",
            Source = _source,
            LicenceOk = true,
            BatchStarted = _batchStarted,
            // a line set to the stub's own pace: one part per Interval
            LineSpeedPpm = (float)(60000 / Interval.TotalMilliseconds),
            CycleBudgetMs = (float)Interval.TotalMilliseconds,
            Plc = new PlcState { LinkUp = true, HeartbeatOk = true, RunEnable = true, Link = "Stub" },
        };
        if (_last is not null)
        {
            s.Plc.LastTriggerId = _last.TriggerId;
            s.Plc.LastResultTime = _last.Time;
            s.Plc.LastResultPass = _last.Pass;
        }
        s.Cameras.Add(new CameraState { Name = "Top", Serial = "STUB-TOP", Connected = true });
        s.Alarms.AddRange(_alarms.OrderByDescending(a => a.Level));
        return s;
    }

    public ListJobsResponse ListJobs()
    {
        var r = new ListJobsResponse();
        foreach (var j in _jobs)
        {
            var info = new JobInfo { Name = j.Name, Number = j.Number, Version = "1", PictureId = PictureId(j) };
            info.Variants.AddRange(j.Variants);
            r.Jobs.Add(info);
        }
        return r;
    }

    static string PictureId(Job j) => $"picture-{j.Number}";

    /// <summary>
    /// A full image for GetImage: a kept inspection image ("img-n") or a product picture
    /// ("picture-n"), as (bytes, width, height, format); null when there is no such image (any more).
    /// With no frames folder the inspection image is a drawn picture of the case.
    /// </summary>
    public (byte[] Data, int Width, int Height, string Format)? Image(string id)
    {
        if (id.StartsWith("picture-", StringComparison.Ordinal))
        {
            var job = _jobs.FirstOrDefault(j => PictureId(j) == id);
            return job is null ? null : (StubPictures.Product(job.Number), 320, 240, "png");
        }
        StoredImage? stored;
        lock (_gate) _images.TryGetValue(id, out stored);
        if (stored is null) return null;
        return stored.Jpeg.Length > 0
            ? (stored.Jpeg, stored.Width, stored.Height, "jpeg")
            : (StubPictures.Case(stored.Width, stored.Height, stored.Codes), stored.Width, stored.Height, "png");
    }

    /// <summary>An alarm with its "What do I do?" steps, as the runtime's alarm catalogue will give it.</summary>
    public static Alarm SampleAlarm() => new()
    {
        Code = 310,
        Level = Alarm.Types.Level.Alarm,
        Text = "Image too dark. Check the light above the camera.",
        Help = "Look at the light above the camera: it should be on.\nIf it is off, check its cable is pushed in.\n"
            + "Wipe the camera window with a clean, dry cloth.\nStill dark? Call your supervisor.",
        Since = Timestamp.FromDateTime(DateTime.UtcNow),
    };

    /// <summary>A warning with steps: the line keeps running.</summary>
    public static Alarm SampleWarning() => new()
    {
        Code = 120,
        Level = Alarm.Types.Level.Warning,
        Text = "Codes are getting harder to read. Check the printer.",
        Help = "Look at the last bad parts: is the print faint or smudged?\nClean the print head as shown on the printer.\n"
            + "If the print stays faint, call maintenance.",
        Since = Timestamp.FromDateTime(DateTime.UtcNow),
    };

    public Counters Counters()
    {
        lock (_gate)
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

    // --- control (callers have checked access) -----------------------------------------------

    public ChangeJobResponse ChangeJob(ChangeJobRequest req)
    {
        lock (_gate)
        {
            if (_running) return new ChangeJobResponse { ErrorCode = 409, ErrorText = "Stop the line before changing the product.", ErrorKey = "runtime.refused.stopFirst" };
            var job = req.JobCase switch
            {
                ChangeJobRequest.JobOneofCase.Name => _jobs.FirstOrDefault(j => j.Name == req.Name),
                ChangeJobRequest.JobOneofCase.Number => _jobs.FirstOrDefault(j => j.Number == req.Number),
                _ => _job,
            };
            if (job is null) return new ChangeJobResponse { ErrorCode = 404, ErrorText = "No such job.", ErrorKey = "runtime.refused.noJob" };
            var variant = string.IsNullOrEmpty(req.Variant) ? job.Variants[0] : req.Variant;
            if (!job.Variants.Contains(variant)) return new ChangeJobResponse { ErrorCode = 404, ErrorText = "No such product in this job.", ErrorKey = "runtime.refused.noProduct" };
            _job = job;
            _variant = variant;
        }
        PublishStatus();
        return new ChangeJobResponse { Ok = true };
    }

    public StartResponse Start(StartRequest req)
    {
        if (!string.IsNullOrEmpty(req.Job) || !string.IsNullOrEmpty(req.Variant))
        {
            var change = ChangeJob(new ChangeJobRequest { Name = string.IsNullOrEmpty(req.Job) ? _job.Name : req.Job, Variant = req.Variant });
            if (!change.Ok && change.ErrorCode != 409) return new StartResponse { ErrorCode = change.ErrorCode, ErrorText = change.ErrorText, ErrorKey = change.ErrorKey };
        }
        lock (_gate)
        {
            if (_running) return new StartResponse { Ok = true };
            _running = true;
            if (req.Source != Source.Unspecified) _source = req.Source;
            _total = _ok = _ng = 0;
            _cycles.Clear();
            _ngReasons.Clear();
            _batchStarted = Timestamp.FromDateTime(DateTime.UtcNow);
            _loop = new CancellationTokenSource();
            _ = RunLoop(_loop.Token);
        }
        PublishStatus();
        return new StartResponse { Ok = true };
    }

    public StopResponse Stop()
    {
        lock (_gate)
        {
            _running = false;
            _loop?.Cancel();
            _loop = null;
        }
        PublishStatus();
        return new StopResponse { Ok = true };
    }

    public void RaiseAlarm(Alarm alarm)
    {
        lock (_gate) _alarms.Add(alarm);
        PublishStatus();
    }

    public void ClearAlarms()
    {
        lock (_gate) _alarms.Clear();
        PublishStatus();
    }

    async Task RunLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
                Step();
            }
        }
        catch (OperationCanceledException) { }
    }

    // --- made-up inspections ------------------------------------------------------------------

    /// <summary>Makes one inspection now and sends it to every watcher. The loop calls it while
    /// running; tests call it directly.</summary>
    public InspectionResult Step(string partId = "")
    {
        InspectionResult result;
        LiveFrame frame;
        lock (_gate)
        {
            var (w, h, jpeg) = NextFrame();
            var n = _job.CodesPerCase;
            var cols = (int)Math.Ceiling(Math.Sqrt(n * (double)w / h));
            var rows = (int)Math.Ceiling(n / (double)cols);
            float cw = w / (float)cols, ch = h / (float)rows, side = Math.Min(cw, ch) * 0.55f;
            var bad = _random.NextDouble() < NgRate;
            var missed = bad ? _random.Next(n) : -1;
            var duplicate = bad && _random.NextDouble() < 0.25 ? (missed + 1) % n : -1;

            result = new InspectionResult
            {
                InspectionId = _nextInspection++,
                TriggerId = _nextTrigger++,
                Time = Timestamp.FromDateTime(DateTime.UtcNow),
                Job = _job.Name,
                Variant = _variant,
                PartId = partId,
                CycleMs = (float)(40 + _random.NextDouble() * (n * 4)),
            };
            frame = new LiveFrame
            {
                InspectionId = result.InspectionId,
                Camera = "Top",
                Jpeg = ByteString.CopyFrom(jpeg),
                Width = w,
                Height = h,
                Scale = 1,
                ImageId = $"img-{result.InspectionId}",
            };
            var reason = "";
            var drawn = new List<(float, float, float, float, bool)>(n);
            for (var i = 0; i < n; i++)
            {
                float cx = (i % cols + 0.5f) * cw, cy = (i / cols + 0.5f) * ch;
                var corners = new[]
                {
                    new Point { X = cx - side / 2, Y = cy - side / 2 }, new Point { X = cx + side / 2, Y = cy - side / 2 },
                    new Point { X = cx + side / 2, Y = cy + side / 2 }, new Point { X = cx - side / 2, Y = cy + side / 2 },
                };
                var code = new CodeRead { Format = _job.Format };
                code.Corners.AddRange(corners);
                string colour;
                if (i == missed)
                {
                    code.State = CodeRead.Types.State.Missed;
                    code.Reason = reason = "Blur";
                    colour = "status.ng";
                }
                else
                {
                    var text = i == duplicate ? $"RJ{_nextSerial - 1:D10}" : $"RJ{_nextSerial++:D10}";
                    code.Text = text;
                    code.Gs1["21"] = text;
                    var dup = !_seenCodes.Add(text);
                    code.State = dup ? CodeRead.Types.State.Duplicate : CodeRead.Types.State.Read;
                    if (dup) reason = "Duplicate";
                    colour = dup ? "status.duplicate" : "status.ok";
                }
                result.Codes.Add(code);
                drawn.Add((cx - side / 2, cy - side / 2, cx + side / 2, cy + side / 2, i == missed));
                var tooltip = code.State switch
                {
                    CodeRead.Types.State.Missed => $"Code {i + 1} not read: {code.Reason.ToLowerInvariant()}",
                    CodeRead.Types.State.Duplicate => $"Code {i + 1} seen before: {code.Text}",
                    _ => $"Code {i + 1}: {code.Text}",
                };
                var g = new Graphic { Kind = Graphic.Types.Kind.Polygon, Colour = colour, Tooltip = tooltip };
                g.Points.AddRange(corners);
                frame.Graphics.Add(g);
            }
            result.Pass = reason == "";
            frame.Pass = result.Pass;
            result.ImageIds.Add(frame.ImageId);
            _images[frame.ImageId] = new StoredImage(jpeg, w, h, drawn);
            _imageOrder.Enqueue(frame.ImageId);
            while (_imageOrder.Count > KeptImages) _images.Remove(_imageOrder.Dequeue());
            var tool = new ToolResult { Name = "Read codes", Kind = "Code", Pass = result.Pass, Confidence = result.Pass ? 1 : 0.9f, ReasonText = reason, TimeMs = result.CycleMs };
            result.Tools.Add(tool);

            _total++;
            if (result.Pass) _ok++; else { _ng++; _ngReasons[reason] = _ngReasons.GetValueOrDefault(reason) + 1; }
            _cycles.Add(result.CycleMs);
            if (_cycles.Count > 1000) _cycles.RemoveAt(0);
            _last = result;
        }
        Publish(_resultWatchers, result);
        Publish(_frameWatchers, frame);
        PublishStatus();
        return result;
    }

    (int W, int H, byte[] Jpeg) NextFrame()
    {
        if (_frames.Count == 0) return (1280, 960, []);     // no picture: the client draws the graphics on its own background
        var bytes = File.ReadAllBytes(_frames[(int)((_nextInspection - 1) % (ulong)_frames.Count)]);
        var (w, h) = JpegSize(bytes) ?? (1280, 960);
        return (w, h, bytes);
    }

    /// <summary>Width and height from a JPEG's start-of-frame marker, or null.</summary>
    public static (int W, int H)? JpegSize(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            var len = (b[i + 2] << 8) | b[i + 3];
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC)
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + len;
        }
        return null;
    }

    // --- watchers -----------------------------------------------------------------------------

    public ChannelReader<Status> WatchStatus(CancellationToken ct)
    {
        var ch = Watch(_statusWatchers, 16, ct);
        ch.Writer.TryWrite(Status());   // the first message at once
        return ch.Reader;
    }

    public ChannelReader<InspectionResult> WatchResults(CancellationToken ct) => Watch(_resultWatchers, 64, ct).Reader;

    /// <summary>One frame waits at most: a slow client gets the newest frame, never a queue.</summary>
    public ChannelReader<LiveFrame> WatchFrames(CancellationToken ct) => Watch(_frameWatchers, 1, ct).Reader;

    Channel<T> Watch<T>(List<Channel<T>> list, int capacity, CancellationToken ct)
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

    void PublishStatus() => Publish(_statusWatchers, Status());

    static void Publish<T>(List<Channel<T>> list, T item)
    {
        lock (list) foreach (var ch in list) ch.Writer.TryWrite(item);
    }

    public void Dispose()
    {
        _loop?.Cancel();
        foreach (var l in new System.Collections.IList[] { _statusWatchers, _resultWatchers, _frameWatchers })
            lock (l) l.Clear();
    }
}
