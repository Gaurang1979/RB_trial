using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Sundaram.RemoteService;
using VisionReports;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>The dashboard's TLS certificate (null when it runs plain HTTP for tests), so the agent trusts exactly the dashboard it
/// forwards to on this PC.</summary>
public sealed record WebCertificate(X509Certificate2? Certificate);

/// <summary>
/// Whether a remote service session is open, for Status.remote_service (the line screen's neutral badge, REQ-08): set by
/// <see cref="RemoteServiceRunner"/>, read by <see cref="LineRuntime"/>. A separate object so neither needs the other.
/// </summary>
public sealed class RemoteServiceBadge
{
    public Func<(bool Active, DateTimeOffset? Since)> Read { get; set; } = () => (false, null);
    /// <summary>What remote service asks at the line PC now (4b), from memory.</summary>
    public Func<IReadOnlyList<VisionStudio.Remote.V1.RemoteRequest>> Requests { get; set; } = () => [];
    /// <summary>Raised when the badge or the requests change: the line's status is published again at once.</summary>
    public event Action? Changed;
    public void Raise() => Changed?.Invoke();
}

/// <summary>
/// Remote service (REQ-11, PROT-26; docs/vision-system/REMOTE-SUPPORT.md): the runtime hosts the remote-service agent
/// (third_party/remote-service) and dials the relay at support.sundaramtech.com. Step 4a (owner, 2026-11-04):
/// <list type="bullet">
/// <item>Sign-in with today's Service code (role service) or Factory code (role oem; PROT-32), counted in signin-remote.dat.</item>
/// <item>Every remote event goes first into the service log, its own HMAC chain with the ledger key (no key: no remote
/// service), and a copy into the audit trail's events; the chain head goes to the relay and ERPNext at each session end.</item>
/// <item>The engineer reads the reports, NG images and jobs folders, downloads a diagnostics bundle, opens read-only views of
/// inspections, batches, events and NG reasons, rechecks the ledgers, and opens the browser dashboard with the session.</item>
/// <item>Nothing that changes the line yet: no writable folder, packages or corrections until Vision Studio can ask the operator
/// and the supervisor (step 4b).</item>
/// </list>
/// </summary>
public sealed class RemoteServiceRunner : BackgroundService
{
    readonly IServiceProvider _sp;
    readonly ILogger<RemoteServiceRunner> _log;

    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile RemoteAgent? _agent;
    volatile string? _off;

    public RemoteServiceRunner(IServiceProvider sp, ILogger<RemoteServiceRunner> log)
    {
        _sp = sp;
        _log = log;
    }

    /// <summary>Null until <see cref="Ready"/>, and when remote service is off.</summary>
    public RemoteAgent? Agent => _agent;
    /// <summary>Why remote service is off, or null when it is on (or simply not set up).</summary>
    public string? Off => _off;
    /// <summary>Done once the agent is built (or known to be off).</summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// Builds the agent only after the runtime has started (ApplicationStarted): the line, the PLC link and the integrity check at
    /// start keep exactly the order they have without remote service, and the service log's full check runs off the start-up path.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (_sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started.TrySetResult()))
        using (stop.Register(() => started.TrySetCanceled()))
        {
            try { await started.Task; }
            catch (OperationCanceledException) { _ready.TrySetResult(); return; }
        }
        try { (_agent, _off) = Build(); }
        catch (Exception e)
        {
            // a broken remote service never stops the line: it is only off
            (_agent, _off) = (null, "remote service could not start: " + e.Message);
        }
        if (_agent is null && _off is not null) _log.LogWarning("Remote service off: {Why}", _off);
        // the badge is read in the line's status, on the thread that inspects parts: memory only, never a sweep (which writes)
        if (_agent is { } agent) _sp.GetRequiredService<RemoteServiceBadge>().Read = () => agent.Sessions.Badge;
        _ready.TrySetResult();
        if (_agent is not null) await _agent.RunAsync(stop);
    }

    (RemoteAgent?, string?) Build()
    {
        var rt = _sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
        if (string.IsNullOrWhiteSpace(rt.SupportRelay)) return (null, null);   // not set up: not an error
        var studio = _sp.GetRequiredService<IOptions<StudioOptions>>().Value;
        var secrets = _sp.GetRequiredService<VisionCore.Licensing.CommissioningSecrets>();
        var recorder = _sp.GetRequiredService<Recorder>();
        if (DatabaseSecurity.ParseKey(secrets.LedgerKeyBase64) is not { } key)
            return (null, "the ledger key (VS_LEDGER_KEY) is not set, so remote actions could not be recorded");
        var serviceLog = new ServiceLog(studio.DataFolder, key);
        // a new, empty log is worth a line in the audit trail: had an earlier one been deleted, this is where it shows
        if (serviceLog.Created) recorder.Note("remote_support", "Service log started (no earlier service log on this PC)");
        try { serviceLog.Verify(); }
        catch (DatabaseTamperingException e)
        {
            recorder.Note("remote_service_off", "Remote service off: " + e.Message);
            return (null, e.Message);
        }

        Uri? web = null;
        byte[]? certHash = null;
        if (rt.WebPort > 0 && rt.WebPort != rt.NetworkPort)
        {
            var cert = _sp.GetRequiredService<WebCertificate>().Certificate;
            web = new Uri($"{(cert is null ? "http" : "https")}://127.0.0.1:{rt.WebPort}");
            certHash = cert?.GetCertHash(HashAlgorithmName.SHA256);
        }
        var options = new RemoteServiceOptions
        {
            Relay = rt.SupportRelay, Machine = rt.SupportMachine.Trim(), Token = secrets.SupportLink, LocalWeb = web,
            LocalWebCertificateSha256 = certHash, AllowInsecure = rt.AllowInsecureNetwork,
            DataFolder = Path.Combine(studio.DataFolder, "remote-service"),
            ExtraReturnHeaders = ["X-Inspection-Id", "X-Pass"],
        };
        if (options.Problem() is { } problem) return (null, problem);

        var reports = _sp.GetRequiredService<ReportService>();
        var line = _sp.GetRequiredService<LineRuntime>();
        reports.Corrections = batch => serviceLog.BatchCorrections(batch);
        var requests = _sp.GetRequiredService<RemoteRequests>();
        var badge = _sp.GetRequiredService<RemoteServiceBadge>();
        requests.Changed += badge.Raise;
        var host = new RemoteServiceHost
        {
            Product = "Vision System",
            Code = new ServiceCode(_sp.GetRequiredService<OtpAttempts>(), _sp.GetRequiredService<LicenceService>(), secrets),
            Audit = new LedgerSink(serviceLog, recorder),
            // 4b: changes only while the line is stopped and after the operator confirms; corrections after a supervisor approves
            Machine = _sp.GetRequiredService<LineStopped>(),
            Operator = requests,
            Supervisor = requests,
            Corrections = [BatchCorrections.Kind(reports.Log, serviceLog)],
            Roots =
            [
                new FileRoot("reports", reports.Folder),
                new FileRoot("ng-images", Path.Combine(studio.DataFolder, "ng-images")),
                // job files (OEM): checked as a job file, put in place only while the line is stopped and after the operator
                // confirms; a job loaded when its file changes is not run until it is opened again and checked (CV-12)
                new FileRoot("jobs", studio.JobsFolder, Writable: true, Extensions: [".json"], ConfirmWrites: true, NeedsStopped: true,
                    MaxBytes: VisionCore.JobFile.MaxBytes, CheckUpload: path => CheckJobFile(path, _sp.GetRequiredService<VisionCore.JobStore>())),
            ],
            Views = Views(reports.Log),
            Actions = [new RemoteAction("verify-ledgers", "Check the results ledger and the service log",
                "Reads both hash chains from the start and reports whether every row is as written. Changes nothing and raises no alarm: " +
                "a tampered results ledger is also found, and alarm 801 raised, at the next production start.",
                (_, _) => Task.FromResult(VerifyLedgers(reports.Log, serviceLog)))],
            Diagnostics = (zip, ct) => Diagnostics(zip, line, reports.Log, rt, ct),
        };
        var agent = new RemoteAgent(options, host, _sp.GetRequiredService<ILogger<RemoteAgent>>());
        agent.Sessions.Changed += badge.Raise;
        badge.Requests = () => requests.Open;
        return (agent, null);
    }

    /// <summary>True when the request came through the agent from this PC (its key, fixed-time, over loopback), whether or
    /// not it carries a session: the dashboard then offers no password sign-in, only the service console.</summary>
    public bool ViaAgent(HttpContext ctx)
    {
        if (Agent is null || ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return false;
        var h = ctx.Request.Headers[RemoteAgent.KeyHeader].ToString();
        return h.Length == Agent.Key.Length
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(Agent.Key));
    }

    /// <summary>The engineer's remote session of a request the agent forwarded, or null.</summary>
    public RemoteSession? Session(HttpContext ctx) =>
        Agent?.Forwarded(ctx.Request.Headers[RemoteAgent.KeyHeader], ctx.Request.Headers[RemoteAgent.SessionHeader], ctx.Connection.RemoteIpAddress);

    /// <summary>Ends the session of a forwarded request (the dashboard's sign-out through the relay).</summary>
    public void SignOut(HttpContext ctx)
    {
        if (Session(ctx) is not null) Agent!.Sessions.SignOut(ctx.Request.Headers[RemoteAgent.SessionHeader]);
    }

    /// <summary>
    /// An uploaded job file must read as a job file, and its PLC job number must not be another job's: the PLC selects jobs by
    /// number, and two files with one number would let it load the wrong one. Name and references are checked again when the
    /// job is opened (and a loaded job whose file changed does not run until then, CV-12).
    /// </summary>
    static string? CheckJobFile(string path, VisionCore.JobStore jobs)
    {
        VisionCore.Job job;
        try { job = VisionCore.JobFile.Parse("upload", File.ReadAllBytes(path)); }
        catch (Exception e) when (e is VisionCore.JobLoadException or System.Text.Json.JsonException or ArgumentException or IOException)
        {
            return "not a job file";
        }
        if (job.Number > 0 && jobs.Names().FirstOrDefault(n => n != job.Name && jobs.TryLoad(n, forEdit: true)?.Number == job.Number) is { } other)
            return $"job number {job.Number} is already the job \"{other}\"; give the job its own number";
        return null;
    }

    static string VerifyLedgers(ResultLog results, ServiceLog service)
    {
        string Try(Func<string> f)
        {
            try { return f(); }
            catch (DatabaseTamperingException e) { return "TAMPERED: " + e.Message; }
        }
        var r = Try(() => results.VerifyLedger() is { } rep ? $"results ledger good ({rep.V2Rows} chained rows, {rep.Unchained} unchained)" : "results ledger has no key");
        var s = Try(() => $"service log good ({service.Verify()} rows)");
        return r + "; " + s;
    }

    static IReadOnlyList<DataView> Views(ResultLog log)
    {
        static (DateTime From, DateTime To) Range(IReadOnlyDictionary<string, string> p)
        {
            var hours = int.TryParse(p.GetValueOrDefault("hours"), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? Math.Clamp(h, 1, 24 * 31) : 24;
            var to = DateTime.UtcNow;
            return (to.AddHours(-hours), to);
        }
        static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        static string? Text(object? v) => v switch { null => null, double d => d.ToString("0.##", CultureInfo.InvariantCulture), _ => Convert.ToString(v, CultureInfo.InvariantCulture) };

        return
        [
            new DataView("inspections", "Inspections", ["hours", "batch"], (p, limit, _) =>
            {
                var (from, to) = Range(p);
                var batch = p.GetValueOrDefault("batch") is { Length: > 0 } b ? b : null;
                var rows = log.Inspections(from, to, batch, limit + 1);
                return Task.FromResult(new DataTable(["time (UTC)", "batch", "job", "camera", "part", "result", "tool", "reason", "ms", "image"],
                    rows.Take(limit).Select(r => (IReadOnlyList<string?>)[Iso(r.TimeUtc), r.Batch, r.Job, Text(r.Camera), Text(r.PartId), r.Ok ? "OK" : "NG",
                        r.FailTool, r.Reason, Text(r.Ms), r.ImageId]).ToList(), rows.Count > limit));
            }),
            new DataView("batches", "Batches", ["hours"], (p, limit, _) =>
            {
                var (from, to) = Range(p);
                var rows = log.Batches(from, to, limit + 1);
                return Task.FromResult(new DataTable(["name", "job", "started (UTC)", "ended (UTC)", "user"],
                    rows.Take(limit).Select(r => (IReadOnlyList<string?>)[r.Name, r.Job, Iso(r.StartedUtc), r.EndedUtc is { } e ? Iso(e) : "", r.User]).ToList(),
                    rows.Count > limit));
            }),
            new DataView("events", "Events and alarms", ["hours"], (p, limit, _) =>
            {
                var (from, to) = Range(p);
                var rows = log.Events(from, to);
                return Task.FromResult(new DataTable(["time (UTC)", "kind", "active", "detail"],
                    rows.TakeLast(limit).Select(r => (IReadOnlyList<string?>)[Iso(r.TimeUtc), r.Kind, r.Active ? "yes" : "", r.Detail]).ToList(), rows.Count > limit));
            }),
            new DataView("ng-reasons", "NG reasons", ["hours"], (p, limit, _) =>
            {
                var (from, to) = Range(p);
                var rows = log.NgReasons(from, to, Math.Min(limit, 50));
                return Task.FromResult(new DataTable(["reason", "count"], rows.Select(r => (IReadOnlyList<string?>)[r.Reason, Text(r.Count)]).ToList(), false));
            }),
        ];
    }

    /// <summary>The diagnostics bundle: versions, the line's status, the runtime settings (no secrets live there: they are in
    /// the protected store) and the last day's events.</summary>
    static async Task Diagnostics(System.IO.Compression.ZipArchive zip, LineRuntime line, ResultLog log, RuntimeOptions rt, CancellationToken ct)
    {
        async Task Entry(string name, string text)
        {
            await using var w = new StreamWriter(zip.CreateEntry(name).Open());
            await w.WriteAsync(text.AsMemory(), ct);
        }
        await Entry("versions.txt", $"vs-runtime {LineRuntime.Version}\n.NET {Environment.Version}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n");
        await Entry("status.json", Google.Protobuf.JsonFormatter.Default.Format(line.Status()));
        await Entry("runtime-settings.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            rt.WebPort, rt.NetworkPort, rt.Source, rt.LineSpeedPpm, rt.CycleBudgetMs, rt.SupportMachine,
            CertificateFile = string.IsNullOrEmpty(rt.CertificateFile) ? "" : Path.GetFileName(rt.CertificateFile),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var sb = new StringBuilder("time_utc,kind,active,detail\n");
        foreach (var e in log.Events(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).TakeLast(5000))
            sb.Append(CultureInfo.InvariantCulture, $"{e.TimeUtc:O},{e.Kind},{(e.Active ? 1 : 0)},\"{e.Detail.Replace("\"", "\"\"")}\"\n");
        await Entry("events-last-day.csv", sb.ToString());
    }

    /// <summary>Today's Service code for role service, today's Factory code for role oem (PROT-32: the service team never opens
    /// factory settings), for this PC's licence; wrong codes of every engineer counted together in signin-remote.dat.</summary>
    sealed class ServiceCode(OtpAttempts attempts, LicenceService licence, VisionCore.Licensing.CommissioningSecrets secrets) : IServiceCodeCheck
    {
        const string Key = "remote-support";

        public CodeResult Check(string code, string engineer, RemoteRole role)
        {
            var tier = role == RemoteRole.Oem ? Roles.OEM : Roles.Service;
            var outcome = attempts.Decide(Key, [tier], code, licence.Settings.LicenceKey, secrets.ServiceOtp, secrets.FactoryOtp);
            return outcome.Locked ? CodeResult.Held : outcome.Granted.Contains(tier) ? CodeResult.Accepted : CodeResult.Wrong;
        }
    }

    /// <summary>The user log: the service log's chain first (it must be written, else the action is refused), then a copy in
    /// the audit trail's events for the Audit trail page.</summary>
    sealed class LedgerSink(ServiceLog log, Recorder recorder) : IAuditSink, IAuditChain
    {
        public void Write(AuditEntry e)
        {
            log.Append(e.Time.UtcDateTime, e.Session, e.User, e.Role == RemoteRole.Oem ? "oem" : "service", e.Kind, e.Detail, e.Data);
            recorder.Note("remote_support", e.Detail);
        }

        public string? Head => log.Head;
    }
}
