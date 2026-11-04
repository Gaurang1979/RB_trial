using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BarcodeReading;
using VisionCore;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// vs-runtime self-test [--grab &lt;n&gt;] [--json &lt;file&gt;] [--Studio:...]: checks a line PC the way the line uses it. The
/// engine DLLs load (with their C runtime), the AI models load into the engines, the folders can be
/// written, the camera driver lists the cameras, every job camera is found by its serial, and port
/// 502 for the PLC. With --grab it fires each job camera n times and prints the frame times and
/// the frames missed (stop the vs-runtime service first: it holds the cameras and port 502).
/// With --json the checks are also written to that file (group, check, found, verdict), which
/// first-run setup adds to the install report. Exit code 0 when every check passed, 1 when one failed.
/// </summary>
static class SelfTest
{
    static int _failed;
    static readonly List<CheckLine> _checks = [];

    /// <summary>One line of the report, in the install report's form (preflight-rules.psm1).</summary>
    internal sealed record CheckLine(string Group, string Check, string Found, string Required, string Verdict, string Note);

    static void Add(string verdict, string what, string detail)
    {
        _checks.Add(new CheckLine("Self-test", what, detail, "", verdict, ""));
        Console.WriteLine($"{(verdict == "PASS" ? "OK" : verdict),-5} {what}{(detail.Length > 0 ? ": " + detail : "")}");
    }
    static void Ok(string what, string detail = "") => Add("PASS", what, detail);
    static void Info(string what, string detail) => Add("INFO", what, detail);
    static void Fail(string what, string detail)
    {
        _failed++;
        Add("FAIL", what, detail);
    }

    /// <summary>The first line of a loader message, which can run to pages.</summary>
    static string Short(Exception e)
    {
        var m = e.Message.Split('\n', 2)[0].Trim();
        return m.Length > 240 ? m[..240] + "..." : m;
    }

    public static int Run(string[] args)
    {
        _failed = 0;
        _checks.Clear();
        var config = new ConfigurationBuilder();
        RuntimeHost.Configure(config, args.Skip(1).Where(a => a.StartsWith("--Studio:", StringComparison.Ordinal)).ToArray(), AppContext.BaseDirectory);
        var studio = config.Build().GetSection("Studio").Get<StudioOptions>() ?? new StudioOptions();
        studio.ResolvePaths(AppContext.BaseDirectory);
        var grab = Array.IndexOf(args, "--grab") is var g and >= 0 && g + 1 < args.Length && int.TryParse(args[g + 1], out var n) ? Math.Clamp(n, 1, 10000) : 0;

        Console.WriteLine($"vs-runtime {LineRuntime.Version} self-test, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"program {AppContext.BaseDirectory}");
        Info("licence check", studio.LicenceRequired ? "on" : "off (Studio:LicenceRequired=false)");

        Engines();
        Models(studio.ModelsFolder);
        Folder("jobs folder", studio.JobsFolder);
        Folder("data folder", studio.DataFolder);
        var found = Cameras();
        var jobCameras = JobCameras(studio.JobsFolder, found);
        Port502();
        if (grab > 0) Grab(jobCameras, grab);

        Console.WriteLine(_failed == 0 ? "self-test passed" : $"self-test FAILED: {_failed} check(s)");
        if (Array.IndexOf(args, "--json") is var j and >= 0 && j + 1 < args.Length)
        {
            var file = Path.GetFullPath(args[j + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var doc = new { overall = _failed == 0 ? "PASS" : "FAIL", version = LineRuntime.Version, results = _checks };
            File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;
    }

    static void Engines()
    {
        // Loading by full path also loads what each DLL needs (the C runtime): a missing one fails here.
        var engines = new List<(string Lib, string? Export)> { ("codereader", "cr_version"), ("vs_ocr", "vs_ocr_create"), ("vs_locate", "vs_loc_create") };
        // tool DLLs added later (vs_measure and the like) are loaded too, when the package has them
        var pattern = OperatingSystem.IsWindows() ? "vs_*.dll" : OperatingSystem.IsMacOS() ? "libvs_*.dylib" : "libvs_*.so";
        foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, pattern).Order())
        {
            var lib = Path.GetFileNameWithoutExtension(f);
            if (!OperatingSystem.IsWindows()) lib = lib[3..];
            if (!engines.Any(e => e.Lib == lib)) engines.Add((lib, null));
        }
        foreach (var (lib, export) in engines)
        {
            var file = OperatingSystem.IsWindows() ? lib + ".dll" : OperatingSystem.IsMacOS() ? "lib" + lib + ".dylib" : "lib" + lib + ".so";
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path)) { Fail(file, "missing next to vs-runtime"); continue; }
            try
            {
                var h = NativeLibrary.Load(path);
                if (export is not null && !NativeLibrary.TryGetExport(h, export, out _)) Fail(file, $"loads but has no {export} (old build?)");
                else Ok(file);
            }
            catch (Exception e) { Fail(file, $"does not load ({Short(e)}); on Windows install the VC++ 2015-2022 x64 runtime or copy vcruntime140*.dll / msvcp140*.dll next to it"); }
        }
        try { Ok("engine version", CodeReader.EngineVersion); }
        catch (Exception e) { Fail("engine version", Short(e)); }
    }

    static void Models(string folder)
    {
        string M(string f) => Path.Combine(folder, f);
        try
        {
            using var reader = new CodeReader(new ReaderSettings());
            Try("codenet.onnx (code locator)", M("codenet.onnx"), () => reader.LoadModel(M("codenet.onnx")));
            Try("enhancer.onnx (code enhancer)", M("enhancer.onnx"), () => reader.LoadEnhancer(M("enhancer.onnx")));
        }
        catch (Exception e) { Fail("code reader", Short(e)); }
        try
        {
            using var ocr = new OcrEngine(new OcrSettings());
            Try("ocrnet.onnx (OCR reader)", M("ocrnet.onnx"), () => ocr.LoadModel(M("ocrnet.onnx"), M("ocr_charset.txt")));
        }
        catch (Exception e) { Fail("OCR engine", Short(e)); }

        static void Try(string what, string path, Action load)
        {
            if (!File.Exists(path)) { Fail(what, $"missing: {path}"); return; }
            try { load(); Ok(what); }
            catch (Exception e) { Fail(what, Short(e)); }
        }
    }

    static void Folder(string what, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".selftest-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Ok(what, folder);
        }
        catch (Exception e) { Fail(what, $"{folder} cannot be written ({Short(e)})"); }
    }

    static IReadOnlyList<CameraInfo> Cameras()
    {
        try
        {
            var list = new IntakeDriverC1(new FrameStats()).List();
            if (list.Count == 0) Fail("cameras", "none found (power, cable, camera network card IP, or MVS runtime)");
            else foreach (var c in list) Ok("camera", $"{c.Label} (serial {c.Serial})");
            return list;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or FileNotFoundException or TypeInitializationException)
        {
            Fail("camera driver", $"MVS camera runtime not installed or not found ({e.GetType().Name}: {Short(e)})");
        }
        catch (Exception e) { Fail("camera driver", Short(e)); }
        return [];
    }

    static List<CameraConfig> JobCameras(string jobsFolder, IReadOnlyList<CameraInfo> found)
    {
        var cams = new List<CameraConfig>();
        JobStore store;
        try { store = new JobStore(jobsFolder); }
        catch (Exception e) { Fail("jobs", Short(e)); return cams; }
        var names = store.Names();
        if (names.Count == 0) { Info("jobs", "none yet (make the job in Vision Studio, then run the self-test again)"); return cams; }
        foreach (var name in names)
        {
            Job? job;
            try { job = store.Load(name); }
            catch (Exception e) { Fail($"job {name}", $"does not load ({Short(e)})"); continue; }
            if (job is null) continue;
            foreach (var c in job.Cameras)
            {
                if (string.IsNullOrWhiteSpace(c.Serial))
                    Info($"job {name}, {c.Name}", job.Cameras.Count > 1 ? "no serial: with two cameras set the serial of each" : "no serial: the first camera found is used");
                else if (found.Any(f => f.Serial == c.Serial)) Ok($"job {name}, {c.Name}", $"serial {c.Serial} found");
                else Fail($"job {name}, {c.Name}", $"serial {c.Serial} not found");
                if (!cams.Any(x => x.Serial == c.Serial)) cams.Add(c);
            }
        }
        return cams;
    }

    static void Port502()
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, 502);
            l.Start();
            l.Stop();
            Ok("PLC port 502", "free (vs-runtime opens it when the PLC link is Modbus TCP)");
        }
        catch (SocketException)
        {
            Info("PLC port 502", "in use: normal while the vs-runtime service runs; otherwise another program holds it");
        }
    }

    static void Grab(List<CameraConfig> cams, int count)
    {
        if (cams.Count == 0) { Fail("grab", "no job camera to fire"); return; }
        var driver = new IntakeDriverC1(new FrameStats());
        foreach (var cfg in cams)
        {
            var label = $"grab {cfg.Name}{(string.IsNullOrWhiteSpace(cfg.Serial) ? "" : " " + cfg.Serial)}";
            try
            {
                using var cam = driver.Open(cfg);
                var ms = new List<double>(count);
                var missed = 0;
                int w = 0, h = 0;
                for (var i = 0; i < count; i++)
                {
                    var t = Stopwatch.GetTimestamp();
                    var img = cam.Grab(1000);
                    if (img is null) { missed++; continue; }
                    ms.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    (w, h) = (img.Width, img.Height);
                }
                if (ms.Count == 0) { Fail(label, $"no frame in {count} triggers"); continue; }
                ms.Sort();
                var detail = $"{count} triggers, {missed} missed, {w}x{h}, trigger to frame mean {ms.Average():F1} ms, 95% {ms[(int)(ms.Count * 0.95)]:F1} ms, max {ms[^1]:F1} ms";
                if (missed > 0) Fail(label, detail);
                else Ok(label, detail);
            }
            catch (Exception e) { Fail(label, Short(e)); }
        }
    }
}
