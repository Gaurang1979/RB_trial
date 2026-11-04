using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using StationGuard;
using VisionCore;
using VisionStudio.Remote;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// Builds the vs-runtime host: the station and everything around it (cameras through the frame
/// intake, the PLC link, the licence, the resource guard, NG images, results and shift reports, the
/// plant link) and the runtime contract on the local endpoint, plus the network port when one is set.
/// </summary>
public static class RuntimeHost
{
    public const string ConfigFile = "vs-runtime.json";

    /// <summary>The configuration: vs-runtime.json next to the program, VS_RUNTIME_* environment
    /// variables, then the command line (--Studio:DataFolder=... or --Studio:DataFolder ...).
    /// No appsettings.json: the one VisionStudio ships opens a web port the runtime must not.</summary>
    public static void Configure(IConfigurationBuilder c, string[] args, string baseDir)
    {
        c.Sources.Clear();
        c.AddJsonFile(Path.Combine(baseDir, ConfigFile), optional: true, reloadOnChange: false);
        c.AddEnvironmentVariables("VS_RUNTIME_");
        c.AddCommandLine(args.Where(a => a != "--console").ToArray());
    }

    public static WebApplication Build(string[] args, string? baseDir = null) => Build(args, baseDir, null, null);

    /// <summary>The tests' seam (requirements 17.14 TST-HYG-02): they change the resource guard's and the image
    /// stores' limits, so a full development disk never deletes a test's images. Production passes none.</summary>
    internal static WebApplication Build(string[] args, string? baseDir,
        Func<GuardOptions, GuardOptions>? guardOptions, Func<NgImageOptions, NgImageOptions>? imageOptions)
    {
        guardOptions ??= g => g;
        imageOptions ??= i => i;
        baseDir ??= AppContext.BaseDirectory;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = baseDir,   // as a Windows service the working folder is System32
        });
        Configure(builder.Configuration, args, baseDir);
        builder.WebHost.UseUrls();   // only the endpoints below, never a URL from a copied appsettings.json
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        // a no-op unless started by the Windows service manager; "--console" runs it as a program
        builder.Services.AddWindowsService(o => o.ServiceName = "vs-runtime");

        builder.Services.Configure<RuntimeOptions>(builder.Configuration.GetSection("Runtime"));
        builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection("Studio"));
        builder.Services.PostConfigure<StudioOptions>(o => o.ResolvePaths(baseDir));
        var rt = builder.Configuration.GetSection("Runtime").Get<RuntimeOptions>() ?? new RuntimeOptions();
        builder.Services.AddSingleton(new WebCertificate(Listen(builder, rt)));

        builder.Services.AddSingleton(sp => new ModelPaths(sp.GetRequiredService<IOptions<StudioOptions>>().Value.ModelsFolder));
        builder.Services.AddSingleton(sp => new JobStore(sp.GetRequiredService<IOptions<StudioOptions>>().Value.JobsFolder, sp.GetRequiredService<ModelPaths>()));
        builder.Services.AddSingleton(sp => new AccessStore(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder));
        // Support and OEM open their roles with today's code (AS-04h): counted in the runtime's OtpAttempts (per user, apart
        // from the remote support count), checked against this PC's licence and the commissioning secrets, audited
        builder.Services.AddSingleton(sp => new Sessions(sp.GetRequiredService<AccessStore>(), sp.GetRequiredService<IOptions<RuntimeOptions>>().Value,
            codes: new SupportCodes(sp.GetRequiredService<OtpAttempts>(),
                () => sp.GetRequiredService<LicenceService>().Settings.LicenceKey,
                () => sp.GetRequiredService<VisionCore.Licensing.CommissioningSecrets>().ServiceOtp,
                () => sp.GetRequiredService<VisionCore.Licensing.CommissioningSecrets>().FactoryOtp,
                detail => sp.GetRequiredService<VisionReports.ResultLog>().AddEvent(DateTime.UtcNow, "support_access", false, detail))));
        builder.Services.AddSingleton<FrameStats>();
        // Micro850 trial: the PLC fires the cameras on their trigger line and takes PASS / FAIL over Modbus TCP
        builder.Services.Configure<Micro850Options>(builder.Configuration.GetSection("Micro850"));
        builder.Services.AddSingleton<HardwareFrames>();
        builder.Services.AddSingleton<Micro850Bridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Micro850Bridge>());
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
            var m850 = sp.GetRequiredService<IOptions<Micro850Options>>().Value;
            var stats = sp.GetRequiredService<FrameStats>();
            var d = new CameraDrivers();
            // the simulator first: a job camera with no driver key opens on it (development PCs)
            if (!string.IsNullOrWhiteSpace(o.SimulatorFolder)) d.Add(new SimulatorDriver(o.SimulatorFolder, stats, o.SimulatorFrameMs, o.SimulatorLink));
            d.Add(m850.Enabled
                ? new IntakeDriverC1(stats, sp.GetRequiredService<HardwareFrames>(), m850.CameraTrigger, m850.TriggerDebounceUs)
                : new IntakeDriverC1(stats));
            return d;
        });
        builder.Services.AddSingleton<ImageCache>();
        // Anti-tamper gate (docs/vision-system/PACKAGE-PROTECTION.md): checked once at start, read by the
        // Station before it runs production. A failed check keeps the line NG (line safety, section 12).
        builder.Services.AddSingleton<PackageIntegrityService>();
        builder.Services.AddSingleton<LicenceService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LicenceService>());
        // Keeps the PC fast and stable while running (memory, leaks, disk), and keeps NG images with counts.
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            var guard = new ResourceGuard(new ProcessProbe(o.DataFolder), guardOptions(new GuardOptions { BudgetBytes = (long)o.MemoryBudgetMb << 20 }));
            guard.Add(new Reliever("trim-image-cache", Pressure.Elevated, TimeSpan.FromMilliseconds(5), sp.GetRequiredService<ImageCache>().TrimNonLive));
            return guard;
        });
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            var guard = sp.GetRequiredService<ResourceGuard>();
            var store = new NgImageStore(imageOptions(new NgImageOptions
            {
                Folder = Path.Combine(o.DataFolder, "ng-images"),
                Mode = Enum.TryParse<SaveMode>(o.SaveImages, true, out var m) ? m : SaveMode.NgOnly,
                EveryNth = o.SaveEveryNth,
                MaxFiles = o.ImagesMaxFiles,
                MaxBytes = (long)(o.ImagesMaxGb * (1L << 30)),
            }), () => guard.Level);
            guard.Add(store.DropQueuedReliever()).Add(store.TrimDiskReliever()).Add(new CompactHeapReliever());
            return store;
        });
        // The plant link (station agent): off unless the "Plant" section names a server. The token
        // lives in a file (TokenFile) or VS_PLANT_TOKEN, never in vs-runtime.json.
        builder.Services.AddSingleton(sp =>
        {
            var o = builder.Configuration.GetSection("Plant").Get<StationAgent.PlantLinkOptions>() ?? new();
            var studio = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            if (string.IsNullOrWhiteSpace(builder.Configuration["Plant:QueueFile"])) o.QueueFile = Path.Combine(studio.DataFolder, "plant-queue.db");
            var p = StationAgent.StationPublisher.Start(o, baseDir);
            if (p.Error is not null) sp.GetRequiredService<ILoggerFactory>().CreateLogger("Plant").LogWarning("{Error}", p.Error);
            return p;
        });
        builder.Services.AddSingleton<Station>();
        builder.Services.AddSingleton<PlcService>();
        // the ledger key, the Service code's secret and the support link token from the protected commissioning store,
        // else the service account's environment (Req 17.10 MRG-09)
        builder.Services.AddSingleton(sp =>
        {
            var secrets = VisionCore.Licensing.CommissioningSecrets.ForThisPc(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder);
            if (secrets.Error is { } e) sp.GetRequiredService<ILoggerFactory>().CreateLogger("Secrets").LogError("{Error}", e);
            return secrets;
        });
        builder.Services.AddSingleton(sp => new VisionReports.ResultLog(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder,
            VisionReports.DatabaseSecurity.ParseKey(sp.GetRequiredService<VisionCore.Licensing.CommissioningSecrets>().LedgerKeyBase64)));
        // wrong Service codes, counted on disk like Vision Studio's (Req 17.10 MRG-09 item 5)
        builder.Services.AddSingleton(sp => new OtpAttempts(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder,
            OperatingSystem.IsWindows() ? new VisionCore.Licensing.DpapiSecretProtector() : null, null,
            sp.GetRequiredService<ILogger<OtpAttempts>>(), OtpAttempts.RemoteFileName));
        builder.Services.AddSingleton(sp => new LedgerGate(sp.GetRequiredService<VisionReports.ResultLog>()));
        builder.Services.AddSingleton<Recorder>();
        builder.Services.AddSingleton<ReportService>();
        builder.Services.AddHostedService<ShiftReporter>();
        // the setup checks at job load (AS-13, AS-14): advice before Run, never in a running batch
        builder.Services.AddSingleton(sp => new SetupCheck(
            new StationSetupLine(sp.GetRequiredService<Station>(), sp.GetRequiredService<IOptions<RuntimeOptions>>().Value),
            SetupCheck.ToolsOf(sp.GetRequiredService<ModelPaths>())));
        builder.Services.AddSingleton<LineRuntime>();
        builder.Services.AddSingleton<JobDesigner>();
        builder.Services.AddSingleton(sp => new ReviewStore(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder));
        // the learning session (owner, 2026-11-04): good pictures kept for learning in their own store, the setting,
        // and the pictures waiting to be sorted
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
            var guard = sp.GetRequiredService<ResourceGuard>();
            var store = new NgImageStore(imageOptions(new NgImageOptions
            {
                Folder = Path.Combine(o.DataFolder, "learning-ok"),
                Mode = SaveMode.All,
                MaxFiles = 2000,
                MaxBytes = 2L << 30,
            }), () => guard.Level);
            guard.Add(store.DropQueuedReliever()).Add(store.TrimDiskReliever());
            return new OkSampler(store);
        });
        builder.Services.AddSingleton(sp => new LearningSettingsStore(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder));
        builder.Services.AddSingleton(sp => new SortQueue(sp.GetRequiredService<NgImageStore>(), sp.GetRequiredService<OkSampler>(),
            sp.GetRequiredService<ReviewStore>()));
        builder.Services.AddSingleton(sp => new ReviewDesk(sp.GetRequiredService<LineRuntime>(), sp.GetRequiredService<Station>(),
            sp.GetRequiredService<JobStore>(), sp.GetRequiredService<ReviewStore>(), null, sp.GetRequiredService<SortQueue>(),
            sp.GetRequiredService<StationAgent.StationPublisher>(), sp.GetRequiredService<VisionReports.ResultLog>(),
            sp.GetRequiredService<LearningSettingsStore>()));
        builder.Services.AddSingleton(sp => new Learning(sp.GetRequiredService<Station>(), sp.GetRequiredService<JobStore>(),
            sp.GetRequiredService<ModelPaths>(), sp.GetRequiredService<ReviewStore>(), sp.GetRequiredService<LineRuntime>()));
        builder.Services.AddSingleton<WebFrames>();
        // remote service (REQ-11): the remote-service agent, one outbound link to support.sundaramtech.com; off unless set up
        builder.Services.AddSingleton<RemoteServiceBadge>();
        builder.Services.AddSingleton<RemoteRequests>();
        builder.Services.AddSingleton<LineStopped>();
        builder.Services.AddSingleton<RemoteServiceRunner>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteServiceRunner>());
        // a picture tried in the job designer comes from the engineer's PC (a 20 MP BMP is 20 MB)
        builder.Services.AddGrpc(o => { o.MaxSendMessageSize = 64 << 20; o.MaxReceiveMessageSize = 64 << 20; });

        var app = builder.Build();
        app.MapGrpcService<SessionService>();
        app.MapGrpcService<RuntimeService>();
        app.MapGrpcService<NgReviewsService>();
        if (rt.WebPort > 0 && rt.WebPort != rt.NetworkPort) WebDashboard.Map(app, rt.WebPort);
        // Build the image store before the guard starts, so every clean-up step is registered;
        // then the line: the PLC link and result recording start with the program, not with a client.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // Run the integrity check before anything inspects, so a tampered install is caught at once
            // (reading .Ok runs and logs the check when it is required; the Station enforces it on start).
            _ = app.Services.GetRequiredService<PackageIntegrityService>().Ok;
            // the results ledger is checked as the runtime starts (alarm 801 shows before anyone presses Start) and
            // again at every production start (Req 17.10 REQ-16 item 5); off the start-up thread, it reads all of results.db
            var station = app.Services.GetRequiredService<Station>();
            _ = Task.Run(station.CheckLedger);
            app.Services.GetRequiredService<NgImageStore>();
            app.Services.GetRequiredService<ResourceGuard>().Start();
            app.Services.GetRequiredService<PlcService>();
            app.Services.GetRequiredService<Recorder>();
            var line = app.Services.GetRequiredService<LineRuntime>();
            var settings = app.Services.GetRequiredService<LearningSettingsStore>();
            var ok = app.Services.GetRequiredService<OkSampler>();
            ok.Enabled = settings.Enabled;
            station.OkSamples = ok;
            line.LearningSettings = settings;
            line.OkSamples = ok;
            line.Sorts = app.Services.GetRequiredService<SortQueue>();
            line.Reviews = app.Services.GetRequiredService<ReviewStore>();
            if (rt.WebPort > 0) app.Services.GetRequiredService<WebFrames>().Start();
            // only this user may open the socket (the pipe is limited by its own ACL)
            if (rt.LocalEndpoint().SocketPath is { } sock && !OperatingSystem.IsWindows() && File.Exists(sock))
                File.SetUnixFileMode(sock, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        });
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            // the line stops with the service: the PLC heartbeat stops and the PLC holds the line (PRD section 12)
            try { app.Services.GetRequiredService<Station>().Stop(); } catch (Exception) { }
        });
        return app;
    }

    /// <summary>Opens the endpoints; returns the dashboard's certificate (null without one).</summary>
    static X509Certificate2? Listen(WebApplicationBuilder builder, RuntimeOptions rt)
    {
        var local = rt.LocalEndpoint();
        if (local.PipeName is not null && OperatingSystem.IsWindows() && WindowsAccess.IsService())
        {
            // the service runs as LocalService, Vision Studio as the signed-in user: allow interactive users, never the network
            var security = WindowsAccess.PipeSecurity();
            builder.WebHost.UseNamedPipes(o => { o.CurrentUserOnly = false; o.PipeSecurity = security; });
        }
        X509Certificate2? cert = null;
        if ((rt.NetworkPort > 0 || rt.WebPort > 0) && !string.IsNullOrWhiteSpace(rt.CertificateFile))
            cert = X509CertificateLoader.LoadPkcs12FromFile(rt.CertificateFile, Environment.GetEnvironmentVariable(rt.CertificatePasswordVariable));
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (local.PipeName is not null)
                k.ListenNamedPipe(local.PipeName, l => l.Protocols = HttpProtocols.Http2);
            else
            {
                if (File.Exists(local.SocketPath)) File.Delete(local.SocketPath);   // left over from a crash
                Directory.CreateDirectory(Path.GetDirectoryName(local.SocketPath!)!);
                k.ListenUnixSocket(local.SocketPath!, l => l.Protocols = HttpProtocols.Http2);
            }
            if (rt.WebPort > 0 && rt.WebPort == rt.NetworkPort)
                Console.Error.WriteLine($"-- dashboard port {rt.WebPort} not opened: Runtime:WebPort must differ from Runtime:NetworkPort");
            else if (rt.WebPort > 0)
            {
                // browsers speak HTTP/1.1 or HTTP/2 over TLS; plain HTTP only on this PC, for tests
                if (cert is not null)
                    k.ListenAnyIP(rt.WebPort, l => { l.Protocols = HttpProtocols.Http1AndHttp2; l.UseHttps(cert); });
                else if (rt.AllowInsecureNetwork)
                    k.ListenLocalhost(rt.WebPort, l => l.Protocols = HttpProtocols.Http1);
                else
                    Console.Error.WriteLine($"-- dashboard port {rt.WebPort} not opened: set Runtime:CertificateFile (the dashboard needs TLS)");
            }
            if (rt.NetworkPort <= 0) return;
            if (cert is not null)
                k.ListenAnyIP(rt.NetworkPort, l => { l.Protocols = HttpProtocols.Http2; l.UseHttps(cert); });
            else if (rt.AllowInsecureNetwork)
                k.ListenLocalhost(rt.NetworkPort, l => l.Protocols = HttpProtocols.Http2);   // this PC only, for tests
            else
                Console.Error.WriteLine($"-- network port {rt.NetworkPort} not opened: set Runtime:CertificateFile (remote access needs TLS)");
        });
        return rt.WebPort > 0 ? cert : null;
    }
}

static class WindowsAccess
{
    public static bool IsService() =>
        OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static System.IO.Pipes.PipeSecurity PipeSecurity()
    {
        var s = new System.IO.Pipes.PipeSecurity();
        var rw = System.IO.Pipes.PipeAccessRights.ReadWrite | System.IO.Pipes.PipeAccessRights.Synchronize;
        var allow = System.Security.AccessControl.AccessControlType.Allow;
        s.AddAccessRule(new System.IO.Pipes.PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.InteractiveSid, null), rw, allow));
        s.AddAccessRule(new System.IO.Pipes.PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.IO.Pipes.PipeAccessRights.FullControl, allow));
        s.AddAccessRule(new System.IO.Pipes.PipeAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null),
            System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        return s;
    }
}
