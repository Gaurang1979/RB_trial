using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BarcodeReading;
using BarcodeReading.Protection;
using PlantEvents;
using StationAgent;
using BarcodeReading.Hik;

// LineRunner: runs one aggregation station with a Hikrobot top camera (child codes) and an
// optional side camera (parent code), and prints one line per case. Code type, codes per case
// and camera settings come from the product master the operator picks.
//
//   LineRunner --list
//   LineRunner --products <folder> --list-products
//   LineRunner --products <folder> --add-product <code> --name "<name>" --format qr|dm --count 12|24|45|48|96
//              [--parent-format code128|qr|dm|ean13] [--exposure us] [--gain db] --user <name>
//   LineRunner --products <folder> --product <code> --child <serial> [--parent <serial>]
//              [--model codenet.onnx] [--software] [--hard-cases <folder>] [--hard-case-limit 500]
//              [--dup-children 10000] [--dup-cases 1000] [--licence <folder>] [--grade-every N] [--min-grade A|B|C|D]
//
// The AI locator loads from --model, or models\codenet.onnx next to the program when present.
// The tuning agent learns reader settings per product (saved in <products>\<code>.tuning.json)
// and the advisor prints what to fix on the line every 50 cases.
//
// --dup-children / --dup-cases: how many child (hologram) and case codes of accepted cases are
// remembered; a code seen again is a DUPLICATE and rejects the case (0 = off). This runner keeps
// them in memory only; the dashboard keeps them across restarts.
// --plant-server https://host:port --plant-token-file <file> --plant <id> --line <id> --station <id>
//              [--plant-queue <file>]: send every case, the line state, counters and alarms to the plant
//              server through the station agent (kept on this PC while the server is away). The token is
//              read from the file (or VS_PLANT_TOKEN), never given on the command line. Off with --software:
//              bench triggers are not production.
// --licence <folder>: Vision Studio's data folder on this PC (licence.json, the installation key). The
//              licence content key that decrypts the models and products is read from it (requirements 18.3,
//              ENC-HOST-01). Given: a licence is needed, and without the key the runner does not start: NG, the
//              plant is told (alarm and line state Fault), exit code 3; nothing falls back to a plaintext model.
//              Not given: a development PC, models and products in the clear.
// --grade-every N: grade the child codes of one case in N (1 = every case) and send the grades with the case's
//              plant result ("child.grade", "child.grade.<parameter>", ...; requirements 17.3 PQ-07), as Vision Studio's
//              Read code tool does. Off by default. --min-grade A|B|C|D: a sampled case with a child code below it, or one
//              that could not be graded, is rejected; alone it grades every case. Cases between samples are not judged
//              on their grade. The parent (case) label of the sampled cases is graded too ("parent.grade", ...;
//              PQ-08): each camera counts its own frames, so its reader is switched per case in step with the other.
// --software: press Enter to trigger both cameras (bench testing without a sensor).
// --hard-case-limit: most hard-case images kept in --hard-cases (oldest go first; default 500).
// A resource guard watches memory and disk while running and prints when the PC is pressed.

var opt = Options.Parse(args);
if (opt.List)
{
    foreach (var (model, serial) in HikCodeCamera.ListCameras())
        Console.WriteLine($"{model}  serial {serial}");
    return 0;
}
if (opt.Products is null)
{
    Console.Error.WriteLine("usage: see the comment at the top of LineRunner/Program.cs (--products <folder> is required)");
    return 2;
}
// The licence content key, installed before any product or model is read.
var contentKeys = VisionCore.Licensing.StoredLicenceKeySource.Install(opt.Licence);
var store = new ProductStore(opt.Products);

if (opt.ListProducts)
{
    foreach (var p in store.List())
        Console.WriteLine($"{p.ProductCode,-16} {p.Name,-30} {p.ChildFormat} x {p.CodesPerCase}{(p.IsStandardCaseSize ? "" : " (non-standard size)")}" +
                          $"  parent {p.ParentFormat}  exposure {p.ChildCamera.ExposureUs} us  v{p.Version} {p.Mode}");
    return 0;
}

if (opt.AddProduct is not null)
{
    if (opt.User is null) { Console.Error.WriteLine("--user is required to save a product"); return 2; }
    var saved = store.Save(new ProductMaster
    {
        ProductCode = opt.AddProduct,
        Name = opt.Name ?? opt.AddProduct,
        ChildFormat = opt.Format,
        CodesPerCase = opt.Count,
        ParentFormat = opt.ParentFormat,
        ChildCamera = new CameraSettings { ExposureUs = opt.ExposureUs, GainDb = opt.GainDb },
    }, opt.User);
    Console.WriteLine($"saved {saved.ProductCode} v{saved.Version}");
    return 0;
}

if (opt.Product is null || opt.ChildSerial is null)
{
    Console.Error.WriteLine("--product and --child are required to run");
    return 2;
}
// Line safety (PROT-04/05): no key on a PC that needs a licence = no inspection, before anything is loaded.
if (contentKeys?.InspectionBlockedReason() is { } blocked) return await Refuse(blocked);
ProductMaster? product;
try { product = store.Get(opt.Product); }
catch (ContentProtectionException e) { return await Refuse(e.Message); }
if (product is null) { Console.Error.WriteLine($"product {opt.Product} not found in {opt.Products}"); return 2; }

string trigger = opt.Software ? "Software" : "Line0";

using var childReader = new CodeReader(product.ChildReaderSettings());
string modelPath = opt.Model ?? Path.Combine(AppContext.BaseDirectory, "models", "codenet.onnx");
string enhancerPath = Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "enhancer.onnx");
try
{
    if (File.Exists(modelPath))
    {
        childReader.LoadModel(modelPath);
        Console.WriteLine("AI locator: " + modelPath);
    }
    if (File.Exists(enhancerPath))
    {
        childReader.LoadEnhancer(enhancerPath);
        Console.WriteLine("AI enhancer: " + enhancerPath);
    }
}
catch (ContentProtectionException e) { return await Refuse(e.Message); } // encrypted, and no key or a bad file
var tuner = new TuningAgent();
string tuningPath = Path.Combine(opt.Products, product.ProductCode + ".tuning.json");
tuner.Load(tuningPath);
// grading one case in N: each reader grades only the sampled cases, switched on its camera's thread before each decode
var grading = new CameraSampling(new CaseGrading(opt.GradeEvery, opt.MinGrade, CaseGrading.Child),
    on => childReader.Settings = ChildSettings(on));
var advisor = new LineAdvisor();
using var child = new HikCodeCamera(CameraSetup.FromProduct(product.ChildCamera, opt.ChildSerial, trigger), childReader)
{
    HardCaseFolder = opt.HardCases,
};
// the camera's own saver has no limit: keep the folder trimmed in the background
using var hardCaseQuota = opt.HardCases is null ? null
    : StationGuard.FolderQuota.Keep(opt.HardCases, "????????_??????_???_*.bmp", opt.HardCaseLimit, TimeSpan.FromMinutes(1), ".json");
// Watch only: this runner does not tell the guard when a part is being decoded, so no clean-up
// step that could pause the decode is registered here.
using var guard = new StationGuard.ResourceGuard(new StationGuard.ProcessProbe(opt.HardCases ?? AppContext.BaseDirectory));
guard.LevelChanged += (from, to, why) => Console.Error.WriteLine($"-- PC load {from} -> {to}: {why}");
guard.Start();

using var parentReader = new CodeReader(product.ParentReaderSettings());
// the parent label is graded on the same cases: its own count of parent frames, paired with the child's by arrival
var parentGrading = new CameraSampling(new CaseGrading(opt.GradeEvery, opt.MinGrade, CaseGrading.Parent), on =>
{
    var ps = product.ParentReaderSettings();
    ps.GradeQuality = on;
    parentReader.Settings = ps;
});
using var parent = opt.ParentSerial is null ? null : new HikCodeCamera(
    CameraSetup.FromProduct(product.ParentCamera, opt.ParentSerial, trigger, strobe: false), parentReader);

// The plant link: off unless --plant-server is given. It only queues; a case never waits for it.
if (opt.Software && opt.PlantServer is not null) Console.Error.WriteLine("-- plant link off: --software is bench testing, not production");
await using var plant = StationPublisher.Start(PlantOptions(), Environment.CurrentDirectory);
if (plant.Error is not null) Console.Error.WriteLine("-- " + plant.Error);
else if (plant.Enabled) Console.WriteLine($"plant server {opt.PlantServer}: station {opt.Station} on line {opt.Line}");
long good = 0, bad = 0, countsSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
plant.Counters = () => (Interlocked.Read(ref good), Interlocked.Read(ref bad), countsSince);
plant.Health = () => guard.Status.Last is { } h ? new HealthSample
{
    CpuPct = h.SystemCpuPct,
    RamPct = h.SystemTotalBytes > 0 ? Math.Clamp(100.0 * h.SystemLoadBytes / h.SystemTotalBytes, 0, 100) : null,
    DiskPct = h.DiskTotalBytes > 0 ? Math.Clamp(100.0 - 100.0 * h.DiskFreeBytes / h.DiskTotalBytes, 0, 100) : null,
} : null;
plant.Job(product.ProductCode, product.Version);
guard.LevelChanged += (_, to, _) => plant.Alarm("pc_pressure", to >= StationGuard.Pressure.High,
    to == StationGuard.Pressure.Critical ? AlarmPriority.High : AlarmPriority.Medium);

var dupChildren = new DuplicateMemory(opt.DupChildren);
var dupCases = new DuplicateMemory(opt.DupCases);

// pair child and parent results by arrival order: both cameras fire on the same trigger
// a null entry is a parent frame the camera took but the PC never got: that case has no parent read
// each frame carries whether its reader graded it, so a case is judged on the parent grade only when it was
var parents = new BlockingCollection<(CameraResult Frame, bool Graded)?>();
// each camera raises its own alarm and clears it with its own next frame
child.Error += e => { Console.Error.WriteLine("child camera: " + e); plant.Alarm("child_camera_error", true, AlarmPriority.High); };
if (parent is not null)
{
    parent.Error += e => { Console.Error.WriteLine("parent camera: " + e); plant.Alarm("parent_camera_error", true, AlarmPriority.High); };
    parent.FrameDecoded += r => { plant.Alarm("parent_camera_error", false); parents.Add((r, parentGrading.Decoded())); };
    parent.FramesMissed += (n, _) =>
    {
        parentGrading.Missed(n);
        for (int i = 0; i < n; i++) parents.Add(null);
    };
}

// Child frames the camera took but the PC never got (inspection behind, driver buffers full):
// each is a case that was not inspected, so it is NG, and its parent frame is set aside so the
// next case is paired with its own parent.
child.FramesMissed += (n, next) =>
{
    grading.Missed(n); // each was a case: the count stays in step with the parent camera's
    for (int i = 0; i < n; i++)
    {
        if (parent is not null) parents.TryTake(out _, 1000);
        Interlocked.Increment(ref bad);
        plant.Result(false, [new NgReason("camera", "missed_trigger")], 0);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} frame {next - (ulong)(n - i)} REJECT missed: the frame never reached the PC");
    }
};

child.FrameDecoded += r =>
{
    plant.Alarm("child_camera_error", false);
    // With a parent camera, no parent frame within a second is a parent NOT read: the case is rejected.
    string parentCode = "-";
    (CameraResult Frame, bool Graded)? p = null;
    if (parent is not null)
    {
        if (!parents.TryTake(out p, 1000)) p = null;
        parentCode = p?.Frame.Decode.Codes.FirstOrDefault()?.Text ?? "NO READ";
    }
    var s = r.Decode.Stats;
    tuner.Observe(s, childReader.Settings.TimeBudgetMs);
    advisor.Add(r.Decode);
    bool graded = grading.Decoded(); // this case was decoded with grading on; the reader is set for the next
    bool parentGraded = p?.Graded == true;
    if (tuner.Frames % 50 == 0 && tuner.Frames > 0)
    {
        tuner.Save(tuningPath);
        if (plant.Enabled)
        {
            var ps = plant.Status;
            Console.WriteLine($"-- plant server {(ps.Connected ? "connected" : "not reached")}, {ps.Waiting} waiting, {ps.Lost} could not be kept");
        }
        Console.WriteLine($"-- last {advisor.Frames} cases: read rate {advisor.ReadRate:P1}; tuner best: " +
                          string.Join(", ", tuner.Best().Select(b => $"{b.Knob}={b.Value}")));
        foreach (var a in advisor.Advise(product.ChildCamera, product.TimeBudgetMs))
            Console.WriteLine($"   ADVICE {a.Code}: {a.Message}" + (a.SuggestedExposureUs is { } e ? $" (suggested exposure {e:F0} us)" : ""));
    }
    // a child or case code already on a recent accepted case: re-used or copied label
    var texts = r.Decode.Codes.Select(c => c.Text).ToList();
    var dupKids = dupChildren.Duplicates(texts);
    bool hasParent = parent is not null && parentCode != "NO READ";
    bool dupParent = hasParent && dupCases.Contains(parentCode);
    // a sampled case: its child and parent grades go with the result; with a lowest grade, a low or ungradable code rejects it
    var (gradeValues, gradeWhy) = CaseGrading.JudgeCase(
        grading.Grading, graded, r.Decode.Codes.Select(c => c.Quality).ToList(),
        parent is null ? null : parentGrading.Grading, parentGraded, p?.Frame.Decode.Codes.Take(1).Select(c => c.Quality).ToList());
    // more codes than the case holds: a foreign or extra pack in it
    bool ok = s.Complete && !s.Extra && parentCode != "NO READ" && dupKids.Count == 0 && !dupParent && gradeWhy.Count == 0;
    if (ok)
    {
        dupChildren.Add(texts);
        if (hasParent) dupCases.Add(parentCode);
    }
    // to the plant server: the same verdict, with why it was rejected
    var why = new List<NgReason>();
    if (!s.Complete) why.Add(new NgReason("child_codes", "unread"));
    if (s.Extra) why.Add(new NgReason("child_codes", "extra"));
    if (parentCode == "NO READ") why.Add(new NgReason("parent_code", "no_read"));
    if (dupKids.Count > 0) why.Add(new NgReason("child_codes", "duplicate"));
    if (dupParent) why.Add(new NgReason("parent_code", "duplicate"));
    why.AddRange(gradeWhy);
    if (!ok && why.Count == 0) why.Add(new NgReason("case", "failed"));
    Interlocked.Increment(ref ok ? ref good : ref bad);
    plant.Result(ok, ok ? Array.Empty<NgReason>() : why, s.TotalMs, partId: hasParent ? parentCode : null, values: gradeValues);
    string status = ok ? "OK" : "REJECT";
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} frame {r.FrameNumber} {status} children {s.Codes}/{s.Expected} " +
                      $"parent {Printable(parentCode)} time {s.TotalMs:F0} ms label {s.LabelBrightness:F0}/255" +
                      (s.Complete ? "" : $" unread regions {r.Decode.Misses.Count}") +
                      (s.Extra ? " EXTRA codes (foreign or extra pack)" : "") +
                      (dupKids.Count > 0 ? $" DUPLICATE children {dupKids.Count}" : "") +
                      (dupParent ? " DUPLICATE parent" : "") +
                      (graded ? GradeText(CaseGrading.Child, gradeValues, gradeWhy) : "") +
                      (parentGraded && hasParent ? GradeText(CaseGrading.Parent, gradeValues, gradeWhy) : ""));
    foreach (var d in dupKids) Console.WriteLine($"   duplicate child {Printable(d)}");
};

child.Open();
parent?.Open();
child.Start();
parent?.Start();
plant.State(LineStateValue.Running);
Console.WriteLine($"running {product.ProductCode} ({product.Name}): {product.ChildFormat} x {product.CodesPerCase}, trigger {trigger}" +
                  (grading.Grading.Enabled ? $", grading 1 case in {grading.Grading.Every}" + (parent is null ? "" : " (child and parent)") + (grading.Grading.MinGrade >= 0 ? $", lowest grade {CodeQuality.Letter(grading.Grading.MinGrade)}" : "") : "") + ". " + (opt.Software ? "Enter = trigger, q = quit" : "q + Enter = quit"));

while (true)
{
    string? line = Console.ReadLine();
    if (line is null || line.Trim() == "q")
    {
        tuner.Save(tuningPath);
        plant.State(LineStateValue.Stopped, "operator");
        break;
    }
    if (opt.Software)
    {
        child.SoftwareTrigger();
        parent?.SoftwareTrigger();
    }
}
return 0;

PlantLinkOptions PlantOptions() => new()
{
    ServerUrl = opt.Software ? null : opt.PlantServer, TokenFile = opt.PlantTokenFile, Plant = opt.Plant ?? "", Line = opt.Line ?? "", Station = opt.Station ?? "",
    QueueFile = opt.PlantQueue ?? Path.Combine(opt.Products!, "plant-queue.db"),
};

// The station does not start: NG with the reason on the console, and the plant told (alarm, line state Fault).
async Task<int> Refuse(string why)
{
    Console.Error.WriteLine($"NG: {opt.Product} not started: {why}");
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} REJECT not started: {why}");
    await using var link = StationPublisher.Start(PlantOptions(), Environment.CurrentDirectory);
    link.Alarm("licence_content_key", true, AlarmPriority.High);
    link.State(LineStateValue.Fault, why);
    return 3;
}

ReaderSettings ChildSettings(bool grade)
{
    var s = tuner.Next(product!);
    s.GradeQuality = grade;
    return s;
}

static string GradeText(string tool, IReadOnlyDictionary<string, double> v, IReadOnlyList<NgReason> why)
{
    string part = tool == CaseGrading.Parent ? "parent_code" : "child_codes";
    return (v.TryGetValue(tool + ".grade", out var g) ? $" {tool} grade {CodeQuality.Letter((int)g)}" : $" {tool} grade -") +
           (why.Any(w => w.Tool == part && w.Reason == "grade_low") ? $" {tool.ToUpperInvariant()} GRADE LOW" : "") +
           (why.Any(w => w.Tool == part && w.Reason == "not_graded") ? $" {tool.ToUpperInvariant()} NOT GRADED" : "");
}

static string Printable(string s) => s.Replace("\u001D", "<GS>");

sealed class Options
{
    public bool List, ListProducts;
    public string? ChildSerial, ParentSerial, HardCases, Products, Product, AddProduct, Name, User, Model, Licence;
    public string? PlantServer, PlantTokenFile, Plant, Line, Station, PlantQueue;
    public int HardCaseLimit = 500;
    public CodeFormat Format = CodeFormat.QrCode, ParentFormat = CodeFormat.Gs1_128;
    public int Count, DupChildren = 10_000, DupCases = 1_000, GradeEvery, MinGrade = -1;
    public float ExposureUs = 2000, GainDb;
    public bool Software;

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i])
            {
                case "--list": o.List = true; break;
                case "--list-products": o.ListProducts = true; break;
                case "--products": o.Products = Next(); break;
                case "--product": o.Product = Next(); break;
                case "--add-product": o.AddProduct = Next(); break;
                case "--name": o.Name = Next(); break;
                case "--user": o.User = Next(); break;
                case "--gain": o.GainDb = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--child": o.ChildSerial = Next(); break;
                case "--parent": o.ParentSerial = Next(); break;
                case "--format": o.Format = ParseFormat(Next()); break;
                case "--parent-format": o.ParentFormat = ParseFormat(Next()); break;
                case "--count": o.Count = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--exposure": o.ExposureUs = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--software": o.Software = true; break;
                case "--dup-children": o.DupChildren = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--dup-cases": o.DupCases = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--hard-cases": o.HardCases = Next(); break;
                case "--hard-case-limit": o.HardCaseLimit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--model": o.Model = Next(); break;
                case "--licence": o.Licence = Next(); break;
                case "--plant-server": o.PlantServer = Next(); break;
                case "--plant-token-file": o.PlantTokenFile = Next(); break;
                case "--plant": o.Plant = Next(); break;
                case "--line": o.Line = Next(); break;
                case "--station": o.Station = Next(); break;
                case "--plant-queue": o.PlantQueue = Next(); break;
                case "--grade-every": o.GradeEvery = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--min-grade": o.MinGrade = CaseGrading.ParseGrade(Next()); break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        }
        return o;
    }

    static CodeFormat ParseFormat(string s) => s.ToLowerInvariant() switch
    {
        "dm" or "datamatrix" => CodeFormat.DataMatrix,
        "code128" or "gs1-128" => CodeFormat.Gs1_128,
        "ean13" => CodeFormat.Ean13,
        _ => CodeFormat.QrCode,
    };
}
