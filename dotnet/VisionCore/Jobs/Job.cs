using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionCore;

/// <summary>Where the AI models live (copied next to the program at build time).</summary>
public sealed record ModelPaths(string Folder)
{
    public string OcrNet => Path.Combine(Folder, "ocrnet.onnx");
    public string OcrCharset => Path.Combine(Folder, "ocr_charset.txt");
    /// <summary>Optional: a second OCR model of another kind, trained apart from ocrnet, read as a second reader in Read text.</summary>
    public string OcrNetB => Path.Combine(Folder, "ocrnet_b.onnx");
    public string CodeNet => Path.Combine(Folder, "codenet.onnx");
}

/// <summary>
/// An inspection job: the tools to run on every image and in which order. Saved as JSON
/// (a first subset of docs/vision-system/JOB-FILE-FORMAT.md); every saved version is kept.
/// </summary>
public sealed class Job
{
    public const int MaxCameras = 4;

    /// <summary>Layout of the file (<see cref="JobFile.CurrentSchema"/>), written on save; null = saved before versions were written.</summary>
    public string? SchemaVersion { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Number the PLC selects the job by (JobSelect, 1-999); 0 = not selectable by the PLC.</summary>
    public int Number { get; set; }
    public string Description { get; set; } = "";
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    /// <summary>The job's cameras (1 to 3 today). Each tool belongs to one of them.</summary>
    public List<CameraConfig> Cameras { get; set; } = new() { new CameraConfig { Name = "Camera 1" } };
    public List<ToolConfig> Tools { get; set; } = new();

    /// <summary>Lower-case hex SHA-256 of the job file's bytes as they were read (Req 12 CV-11); empty for a job not read from a
    /// file. A copy of the file made by hand with the same name and version has another digest only when its bytes differ.</summary>
    [JsonIgnore] public string Digest { get; set; } = "";

    /// <summary>What loading changed (an older schema read with defaults, a missing teach image); shown, not refused.</summary>
    [JsonIgnore] public IReadOnlyList<string> LoadNotes { get; set; } = Array.Empty<string>();
    /// <summary><see cref="JobStore.LoadForEdit"/> only: why this job cannot run until it is fixed. Empty = it can run.</summary>
    [JsonIgnore] public IReadOnlyList<string> LoadProblems { get; set; } = Array.Empty<string>();

    /// <summary>Everything the operator must enter at batch start, in tool order.</summary>
    public IReadOnlyList<FieldRequest> BatchFields()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Tools.Where(t => t.Enabled).SelectMany(t => t.Fields()).Where(f => seen.Add(f.Key)).ToList();
    }

    /// <summary>
    /// Refuses (ArgumentException with a sentence for the engineer) a job that cannot run, or whose tools are
    /// set up so that every part would fail. With <paramref name="models"/> a Read text tool on the AI reader
    /// is also checked against the characters the model reads. <paramref name="drawFonts"/>: a rendered font is drawn
    /// (on save); false when loading, where a font not drawn as saved is refused instead.
    /// </summary>
    public void Validate(ModelPaths? models = null, bool drawFonts = true)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("A job needs a name");
        if (Number is < 0 or > 999) throw new ArgumentException("The job number is 1 to 999 (0 = none)");
        if (Cameras.Count is < 1 or > MaxCameras) throw new ArgumentException($"A job has 1 to {MaxCameras} cameras");
        foreach (var t in Tools)
            if (t.Camera < 0 || t.Camera >= Cameras.Count) throw new ArgumentException($"{t.Name}: pick one of the job's cameras");
        if (Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The job name has characters a file name cannot hold");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tools)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) throw new ArgumentException("Every tool needs a name");
            if (!names.Add(t.Name)) throw new ArgumentException($"Two tools are called {t.Name}");
            if (t is LocateToolConfig && Tools.Count(o => o is LocateToolConfig && o.Enabled && o.Camera == t.Camera) > 1)
                throw new ArgumentException($"{t.Name}: one Find the part tool per camera");
            if (t is TextToolConfig x && x.Mode == TextMode.Check && x.Lines.Count == 0)
                throw new ArgumentException($"{t.Name}: add the lines to check");
            if (t is TextToolConfig tx && TextTool.SetupProblem(tx, models) is { } textProblem)
                throw new ArgumentException($"{t.Name}: {textProblem}");
            // a rendered font is drawn and taught here, so the job keeps the font that was checked
            if (t is TextToolConfig rx && (drawFonts ? FontRender.Prepare(rx) : FontRender.Kept(rx)) is { } renderProblem)
                throw new ArgumentException($"{t.Name}: {renderProblem}");
            if (RangeProblem(t) is { } range)
                throw new ArgumentException($"{t.Name}: {range}");
            if (t is CodeToolConfig cx && CodeTool.SetupProblem(cx) is { } codeProblem)
                throw new ArgumentException($"{t.Name}: {codeProblem}");
            if (t is BlobToolConfig bx && BlobTool.SetupProblem(bx, Cameras.ElementAtOrDefault(bx.Camera)?.Calibration) is { } blobProblem)
                throw new ArgumentException($"{t.Name}: {blobProblem}");
            if (t is LocateToolConfig lx && !(lx.MinScore is > 0 and <= 1))
                throw new ArgumentException($"{t.Name}: the lowest match must be above 0 and at most 1");
            if (t is MeasureToolConfig mx && MeasureTool.SetupProblem(mx, Cameras[mx.Camera].Calibration) is { } measureProblem)
                throw new ArgumentException($"{t.Name}: {measureProblem}");
            // compared only with what was taught, never with a part on the line (requirements 17.10 AUD-01)
            if (t is ITaughtReference { Enabled: true } tr && TaughtReference.Problem(tr) is { } refProblem)
                throw new ArgumentException($"{t.Name}: {refProblem}");
            if (t is DriftToolConfig { Enabled: true } dx && dx.Baseline.Count < DriftToolConfig.MinSamples)
                throw new ArgumentException($"{t.Name}: no optical baseline taught. Teach it from at least {DriftToolConfig.MinSamples} pictures of good parts");
            // a mode this software does not know would check nothing (AUD-03)
            if (UnknownMode(t) is { } mode)
                throw new ArgumentException($"{t.Name}: the setting {mode} is not one this software knows");
            // a report-only tool's text is not checked: a line checked against it would pass a misread twice over
            if (t is TextToolConfig { Mode: TextMode.Check } cl)
                for (int i = 0; i < cl.Lines.Count; i++)
                    if (cl.Lines[i].Source == ExpectedSource.Tool
                        && Tools.FirstOrDefault(o => o.Name.Equals(cl.Lines[i].SourceName, StringComparison.OrdinalIgnoreCase)) is { } src
                        && !TextTool.Decides(src))
                        throw new ArgumentException($"{t.Name}: line {i + 1} takes its text from {src.Name}, which is report only. Take it from a tool that decides OK/NG");
        }
        // a camera whose only tools are report only would pass every part with nothing checked
        for (int c = 0; c < Cameras.Count; c++)
        {
            var on = Tools.Where(t => t.Enabled && t.Camera == c).ToList();
            if (on.Count > 0 && !on.Any(TextTool.Decides))
                throw new ArgumentException($"{Cameras[c].Name}: every tool on it is report only, so nothing decides OK/NG. Add a tool that checks the part");
        }
        foreach (var c in Cameras)
            if (!(c.ExposureUs > 0)) throw new ArgumentException($"{c.Name}: the exposure must be above 0");
    }

    private static string? UnknownMode(ToolConfig t)
    {
        static bool Bad<T>(T v) where T : struct, Enum => !Enum.IsDefined(v);
        return t switch
        {
            PolarToolConfig p when Bad(p.Mode) => $"mode {p.Mode}",
            PhotometricToolConfig p when Bad(p.Mode) => $"mode {p.Mode}",
            PhotometricToolConfig p when Bad(p.Layout) => $"layout {p.Layout}",
            WaveletToolConfig w when Bad(w.Mode) => $"mode {w.Mode}",
            WaveletToolConfig w when Bad(w.TextureMode) => $"texture {w.TextureMode}",
            ThermalToolConfig th when Bad(th.Mode) => $"mode {th.Mode}",
            InfraredToolConfig ir when Bad(ir.Mode) => $"mode {ir.Mode}",
            InpaintToolConfig ip when Bad(ip.Method) => $"method {ip.Method}",
            _ => null,
        };
    }

    // values no setup screen makes, found in a damaged or hand-edited file
    private static string? RangeProblem(ToolConfig t)
    {
        static bool Box(RoiBox? r) => r is null || (r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0);
        if (!Box(t.Roi)) return "the area must lie in the image and have a size";
        return t switch
        {
            CodeToolConfig c when c.ExpectedCount < 0 || c.TimeBudgetMs < 0 => "the number of codes and the time budget cannot be negative",
            TextToolConfig x when x.CharHeightPx < 0 || x.TimeBudgetMs < 0 => "the letter height and the time budget cannot be negative",
            TextToolConfig x when !(x.MaxSkewDeg is >= 0 and <= 90) => "the slant allowed is 0 to 90 degrees",
            LocateToolConfig l when !Box(l.SearchArea) => "the search area must lie in the image and have a size",
            LocateToolConfig l when !(l.MaxAngleDeg is >= 0 and <= 180) => "the turn allowed is 0 to 180 degrees",
            _ => null,
        };
    }
}

/// <summary>One camera of a job: GigE or USB3, triggered by software (the PLC trigger comes later).</summary>
public sealed class CameraConfig
{
    public string Name { get; set; } = "";
    /// <summary>Camera driver (neutral code, see <see cref="ICameraDriver"/>); empty = find by serial.</summary>
    public string Driver { get; set; } = "";
    /// <summary>Camera serial number; empty = the first camera found (single-camera jobs).</summary>
    public string Serial { get; set; } = "";
    public float ExposureUs { get; set; } = 2000;
    public float GainDb { get; set; }
    /// <summary>Teach image for this camera (file path), kept with the job.</summary>
    public string? ReferenceImage { get; set; }
    /// <summary>Folder of stored images for this camera, for testing without the camera.</summary>
    public string? TestFolder { get; set; }
    /// <summary>The camera's lens and scale at the part's plane (chessboard, cr_calibrate_chessboard); null = sizes in px only.
    /// The Measure tool reads it for sizes in mm.</summary>
    public BarcodeReading.LensCalibration? Calibration { get; set; }
    /// <summary>Focal length of the camera's lens in mm; null = not known (the pixel check, requirements AS-14, then
    /// gives the lens as a factor of its focal length).</summary>
    public float? LensFocalMm { get; set; }
}

/// <summary>Jobs as JSON files: jobs/&lt;name&gt;.json, every version in jobs/history/&lt;name&gt;/v&lt;n&gt;.json.</summary>
public sealed class JobStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep ₹ ¥ readable
    };

    private readonly string _folder;
    private readonly ModelPaths? _models;
    private readonly object _lock = new();

    /// <param name="models">The AI models, so a save refuses a Read text line the AI reader can never read; null = not checked.</param>
    public JobStore(string folder, ModelPaths? models = null)
    {
        _folder = folder;
        _models = models;
        Directory.CreateDirectory(Path.Combine(folder, "history"));
    }

    public IReadOnlyList<string> Names() =>
        Directory.EnumerateFiles(_folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToList();

    /// <summary>
    /// The job, ready to run, or null when there is no such file. Anything that would leave it half working is
    /// refused with <see cref="JobLoadException"/>: a damaged, cut-short, over-size or newer file, a wrong type or
    /// unknown field, a setup the save would refuse, a missing pattern, font or AI model.
    /// </summary>
    public Job? Load(string name) => Read(name, run: true);

    /// <summary>
    /// For the teach and setup screens: as <see cref="Load"/>, except that a job which reads but cannot run yet
    /// (a pattern to teach again, a missing model) opens with the reasons in <see cref="Job.LoadProblems"/>, so
    /// it can be fixed. A file that does not read is still refused.
    /// </summary>
    public Job? LoadForEdit(string name) => Read(name, run: false);

    /// <summary>As <see cref="Load"/> (or <see cref="LoadForEdit"/>), with null for a job that is refused (lists and lookups skip it).</summary>
    public Job? TryLoad(string name, bool forEdit = false)
    {
        try { return Read(name, run: !forEdit); }
        catch (JobLoadException) { return null; }
    }

    /// <summary>The job the PLC selects by <paramref name="number"/>; a job that cannot load is never selected.</summary>
    public string? FindByNumber(int number) =>
        number <= 0 ? null : Names().FirstOrDefault(n => TryLoad(n)?.Number == number);

    /// <summary>
    /// Requirements 12 CV-12: why the job file on disk is no longer the file <paramref name="loaded"/> was read from, or
    /// null when it is the same bytes. A job without a digest (built in memory) is not checked. A file that is gone or
    /// cannot be read counts as changed: in doubt the line does not start.
    /// </summary>
    public string? ChangedOnDisk(Job loaded)
    {
        if (loaded.Digest.Length == 0) return null;
        const string again = "Open the job again so the supervisor checks it.";
        try
        {
            var p = Path.Combine(_folder, loaded.Name + ".json");
            if (!IsJobName(loaded.Name) || !File.Exists(p)) return $"The job file {loaded.Name}.json is gone from the jobs folder. {again}";
            if (new FileInfo(p).Length > JobFile.MaxBytes) return $"The job file {loaded.Name}.json changed since it was opened. {again}";
            var now = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
            return now == loaded.Digest ? null : $"The job file {loaded.Name}.json changed since it was opened (replaced or edited while loaded). {again}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The job file {loaded.Name}.json cannot be read to check it ({e.Message}). {again}";
        }
    }

    /// <summary>A name that is one file in the jobs folder: no folder parts, "..", or characters a file name cannot hold.</summary>
    public static bool IsJobName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= JobFile.MaxNameLength
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('\\') && !name.Contains('/') && name is not ("." or "..");

    private Job? Read(string name, bool run)
    {
        if (!IsJobName(name))
            throw new JobLoadException(name, "not a job name");
        var p = Path.Combine(_folder, name + ".json");
        if (!File.Exists(p)) return null;
        byte[] bytes;
        try
        {
            long size = new FileInfo(p).Length;
            if (size > JobFile.MaxBytes)
                throw new JobLoadException(name, $"the file is {size / 1048576.0:0.0} MB; a job file is at most {JobFile.MaxBytes / 1048576} MB");
            bytes = File.ReadAllBytes(p);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new JobLoadException(name, $"the file cannot be read: {e.Message}");
        }
        var job = JobFile.Parse(name, bytes);
        if (job.Name != name)
            throw new JobLoadException(name, $"the file holds the job \"{job.Name}\"; a job file is named after its job");

        var problems = new List<string>();
        var notes = job.LoadNotes.ToList();
        References(job, problems, notes);
        try { job.Validate(_models, drawFonts: false); }
        catch (ArgumentException e) { problems.Add(e.Message); }
        if (run && problems.Count > 0) throw new JobLoadException(name, string.Join("; ", problems));
        job.LoadProblems = problems;
        job.LoadNotes = notes;
        return job;
    }

    // what the job names outside its file: taught patterns, the AI model, taught fonts, teach images
    private void References(Job job, List<string> problems, List<string> notes)
    {
        var assets = Path.GetFullPath(Path.Combine(_folder, "assets"));
        foreach (var l in job.Tools.OfType<LocateToolConfig>())
        {
            if (l.PatternFile is null)
            {
                if (l.Enabled) problems.Add($"{l.Name}: no pattern taught. Teach the pattern");
                continue;
            }
            var f = Path.GetFullPath(Path.Combine(assets, l.PatternFile));
            if (Path.IsPathRooted(l.PatternFile) || !f.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                problems.Add($"{l.Name}: the pattern file {l.PatternFile} is outside the job's assets folder");
                continue;
            }
            if (!File.Exists(f))
            {
                if (l.Enabled) problems.Add($"{l.Name}: the pattern file {l.PatternFile} is missing. Teach the pattern again");
                continue;
            }
            try
            {
                var (px, w, h) = BarcodeReading.BmpImage.LoadGray(f);
                if (w <= 0 || h <= 0 || px.Length < w * h) throw new InvalidDataException("no pixels");
                l.Pattern = px;
                l.PatternWidth = w;
                l.PatternHeight = h;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or NotSupportedException)
            {
                problems.Add($"{l.Name}: the pattern file {l.PatternFile} is damaged. Teach the pattern again");
            }
        }
        foreach (var r in job.Tools.OfType<ITaughtReference>())
        {
            if (r.ReferenceFile is null) continue;   // Validate refuses an enabled tool with nothing taught
            var f = Path.GetFullPath(Path.Combine(assets, r.ReferenceFile));
            if (Path.IsPathRooted(r.ReferenceFile) || !f.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                problems.Add($"{r.Name}: the reference file {r.ReferenceFile} is outside the job's assets folder");
                continue;
            }
            if (!File.Exists(f))
            {
                if (r.Enabled) problems.Add($"{r.Name}: the reference file {r.ReferenceFile} is missing. Teach the reference again");
                continue;
            }
            try
            {
                var (px, w, h) = BarcodeReading.BmpImage.LoadGray(f);
                if (w <= 0 || h <= 0 || px.Length < w * h) throw new InvalidDataException("no pixels");
                r.Reference = px;
                r.ReferenceWidth = w;
                r.ReferenceHeight = h;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or NotSupportedException)
            {
                problems.Add($"{r.Name}: the reference file {r.ReferenceFile} is damaged. Teach the reference again");
            }
        }
        foreach (var t in job.Tools.OfType<TextToolConfig>().Where(t => t.Enabled))
        {
            if (t.Reader == OcrReader.Ai && _models is { } m && !(File.Exists(m.OcrNet) && File.Exists(m.OcrCharset)))
                problems.Add($"{t.Name}: the AI text model ({Path.GetFileName(m.OcrNet)}, {Path.GetFileName(m.OcrCharset)}) is not installed on this PC");
            if (t.Reader == OcrReader.Font && t.Rendered is null && string.IsNullOrWhiteSpace(t.Font))
                problems.Add($"{t.Name}: no font taught. Teach the characters from sample print");
        }
        foreach (var c in job.Cameras)
            if (c.ReferenceImage is { } r && !File.Exists(r))
                notes.Add($"{c.Name}: the teach image {r} is missing; teach from a new image when the job is changed");
    }

    // taught patterns as image files: assets/<job>/<tool>.v<version>.bmp, and taught references
    // assets/<job>/<tool>.reference.v<version>.bmp (every version kept)
    private void SavePatterns(Job job)
    {
        foreach (var l in job.Tools.OfType<LocateToolConfig>().Where(l => l.Pattern is not null))
        {
            var rel = Path.Combine(job.Name, $"{string.Concat(l.Name.Split(Path.GetInvalidFileNameChars()))}.v{job.Version}.bmp");
            var full = Path.Combine(_folder, "assets", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            BarcodeReading.BmpImage.SaveGray(full, l.Pattern, l.PatternWidth, l.PatternHeight);
            l.PatternFile = rel;
        }
        foreach (var r in job.Tools.OfType<ITaughtReference>().Where(r => r.Reference is not null))
        {
            var rel = Path.Combine(job.Name, $"{string.Concat(r.Name.Split(Path.GetInvalidFileNameChars()))}.reference.v{job.Version}.bmp");
            var full = Path.Combine(_folder, "assets", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            BarcodeReading.BmpImage.SaveGray(full, r.Reference!, r.ReferenceWidth, r.ReferenceHeight);
            r.ReferenceFile = rel;
        }
    }

    public Job Save(Job job, string user)
    {
        job.Validate(_models);
        lock (_lock)
        {
            job.Version = LastVersion(job.Name) + 1;
            job.SchemaVersion = JobFile.CurrentSchema;
            job.UpdatedAt = DateTime.Now;
            job.UpdatedBy = user;
            SavePatterns(job);
            var text = JsonSerializer.Serialize(job, Json);
            var hist = Path.Combine(_folder, "history", job.Name);
            Directory.CreateDirectory(hist);
            File.WriteAllText(Path.Combine(hist, $"v{job.Version}.json"), text);
            var path = Path.Combine(_folder, job.Name + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        return job;
    }

    // the newest version saved, also when the current file is damaged (a new save then replaces it)
    private int LastVersion(string name)
    {
        int last = 0;
        var hist = Path.Combine(_folder, "history", name);
        if (Directory.Exists(hist))
            foreach (var f in Directory.EnumerateFiles(hist, "v*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(1), out int v)) last = Math.Max(last, v);
        try
        {
            var p = Path.Combine(_folder, name + ".json");
            if (File.Exists(p) && new FileInfo(p).Length <= JobFile.MaxBytes)
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
                if (doc.RootElement.TryGetProperty(nameof(Job.Version), out var v) && v.TryGetInt32(out int n)) last = Math.Max(last, n);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return last;
    }

    public void Delete(string name)
    {
        var p = Path.Combine(_folder, name + ".json");
        if (File.Exists(p)) File.Move(p, Path.Combine(_folder, "history", $"{name}.deleted-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
    }

    /// <summary>
    /// Exports a job to a portable .vsrecipe bundle signed with the plant key (Req 17.11 AUD-06), with its taught
    /// Find the part patterns and reference pictures as they loaded (AUD-12). A picture that did not load is left
    /// out, so the import refuses the job until it is taught again.
    /// </summary>
    public string ExportBundle(string jobName, ECDsa signingKey, string? destinationPath = null)
    {
        var job = LoadForEdit(jobName) ?? throw new InvalidOperationException($"Job '{jobName}' not found.");
        string toolsJson = JsonSerializer.Serialize(job, Json);

        byte[] goldenBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // Standard PNG header
        var refImg = job.Cameras.FirstOrDefault()?.ReferenceImage;
        if (!string.IsNullOrEmpty(refImg) && File.Exists(refImg))
        {
            goldenBytes = File.ReadAllBytes(refImg);
        }

        var manifest = new RecipeManifest
        {
            RecipeId = Guid.NewGuid().ToString("N"),
            Name = job.Name,
            Sku = job.Number > 0 ? $"SKU-{job.Number:D3}" : job.Name,
            Version = $"1.{job.Version}.0",
            Author = string.IsNullOrWhiteSpace(job.UpdatedBy) ? "VisionStudio" : job.UpdatedBy,
            CreatedAtUtc = job.UpdatedAt.ToUniversalTime(),
            CameraCount = job.Cameras.Count,
            TargetCycleTimeMs = 50
        };

        var patterns = new Dictionary<int, byte[]>();
        var references = new Dictionary<int, byte[]>();
        for (int i = 0; i < job.Tools.Count; i++)
            if (job.Tools[i] is LocateToolConfig { Pattern: { } px } l && l.PatternWidth > 0 && l.PatternHeight > 0)
                patterns[i] = BarcodeReading.BmpImage.EncodeGray(px, l.PatternWidth, l.PatternHeight);
            else if (job.Tools[i] is ITaughtReference { Reference: { } rx } r && r.ReferenceWidth > 0 && r.ReferenceHeight > 0)
                references[i] = BarcodeReading.BmpImage.EncodeGray(rx, r.ReferenceWidth, r.ReferenceHeight);

        string outPath = destinationPath ?? Path.Combine(_folder, $"{job.Name}.vsrecipe");
        RecipeBundle.ExportToFile(outPath, signingKey, manifest, toolsJson, goldenBytes,
            patterns: patterns, references: references);
        return outPath;
    }

    /// <summary>
    /// Imports a .vsrecipe bundle signed with the plant key and saves it as the next version of its job, by
    /// <paramref name="user"/>. The signature, sizes, job name, job file and setup are all checked before anything
    /// is written; a refused bundle leaves the store as it was (Req 17.11 AUD-06).
    /// </summary>
    public Job ImportBundle(string bundleFilePath, string publicKeyPem, string user)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("The user who imports the bundle is needed.", nameof(user));
        var bundle = RecipeBundle.ImportFromFile(bundleFilePath, publicKeyPem);

        // the same strict reading as a job file on disk: size, unknown fields, types, schema
        var job = JobFile.Parse("bundle", Encoding.UTF8.GetBytes(bundle.ToolsJson));
        if (!IsJobName(job.Name))
            throw new InvalidDataException($"The bundle holds the job name \"{job.Name}\", which is not a job name.");

        // nothing from the other PC's disk is trusted: its teach image path and its pattern files are not ours.
        // The taught pictures come from the signed bundle, checked here before anything is written (AUD-12)
        foreach (var c in job.Cameras) c.ReferenceImage = null;
        TaughtFromBundle(job, bundle);
        job.Validate(_models);

        lock (_lock)
        {
            string? goldenPath = null;
            if (bundle.GoldenImage.Length > 8 && job.Cameras.Count > 0)
            {
                // one golden image per job version, so an older version keeps the image it was taught with
                var assets = Path.GetFullPath(Path.Combine(_folder, "assets"));
                goldenPath = Path.GetFullPath(Path.Combine(assets, job.Name, $"golden_master.v{LastVersion(job.Name) + 1}.png"));
                if (!goldenPath.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException($"The bundle's job \"{job.Name}\" would write outside the job's assets folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
                File.WriteAllBytes(goldenPath, bundle.GoldenImage);
                job.Cameras[0].ReferenceImage = goldenPath;
            }
            try { return Save(job, user); }
            catch
            {
                if (goldenPath is not null) File.Delete(goldenPath);
                throw;
            }
        }
    }

    // each tool the other PC taught takes its picture from the bundle; Save writes them as this PC's files
    private static void TaughtFromBundle(Job job, RecipeContents bundle)
    {
        var unused = bundle.Patterns.Keys.Select(RecipeBundle.PatternEntry)
            .Concat(bundle.References.Keys.Select(RecipeBundle.ReferenceEntry)).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < job.Tools.Count; i++)
        {
            if (job.Tools[i] is LocateToolConfig l)
            {
                bool taught = l.PatternFile is not null;
                l.PatternFile = null;
                if (!taught) continue;
                var entry = RecipeBundle.PatternEntry(i);
                if (bundle.Patterns.TryGetValue(i, out var bmp))
                {
                    (l.Pattern, l.PatternWidth, l.PatternHeight) = TaughtPicture(l.Name, entry, bmp);
                    unused.Remove(entry);
                }
                else if (l.Enabled)
                    throw new InvalidDataException($"{l.Name}: the bundle does not carry its taught pattern ({entry}). Export the job again, or teach the pattern on this PC");
            }
            else if (job.Tools[i] is ITaughtReference r)
            {
                bool taught = r.ReferenceFile is not null;
                r.ReferenceFile = null;
                if (!taught) continue;
                var entry = RecipeBundle.ReferenceEntry(i);
                if (bundle.References.TryGetValue(i, out var bmp))
                {
                    (r.Reference, r.ReferenceWidth, r.ReferenceHeight) = TaughtPicture(r.Name, entry, bmp);
                    unused.Remove(entry);
                }
                else if (r.Enabled)
                    throw new InvalidDataException($"{r.Name}: the bundle does not carry its taught reference ({entry}). Export the job again, or teach the reference on this PC");
            }
        }
        // a picture for no taught tool of its kind means tools.json and the pictures do not belong together
        if (unused.Count > 0)
            throw new InvalidDataException($"The bundle holds {unused.Order(StringComparer.Ordinal).First()}, which matches no taught tool of the job.");
    }

    private static (byte[] Pixels, int Width, int Height) TaughtPicture(string tool, string entry, byte[] bmp)
    {
        try
        {
            // every pixel takes at least a byte: a header claiming more pixels than bytes is refused before any memory is taken
            if (bmp.Length < 54 || (long)Math.Abs((long)BitConverter.ToInt32(bmp, 18)) * Math.Abs((long)BitConverter.ToInt32(bmp, 22)) > bmp.Length)
                throw new InvalidDataException("the picture size does not fit its bytes");
            var (px, w, h) = BarcodeReading.BmpImage.DecodeGray(bmp);
            if (w <= 0 || h <= 0 || px.Length < (long)w * h) throw new InvalidDataException("no pixels");
            return (px, w, h);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"{tool}: the bundle's {entry} is not a readable picture ({e.Message}).", e);
        }
    }

    public static Job Clone(Job j)
    {
        var c = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(j, Json), Json)!;
        // patterns and taught references are not in the JSON: carry them over
        foreach (var (a, b) in j.Tools.Zip(c.Tools))
            if (a is LocateToolConfig la && b is LocateToolConfig lb)
            {
                lb.Pattern = la.Pattern;
                lb.PatternWidth = la.PatternWidth;
                lb.PatternHeight = la.PatternHeight;
            }
            else if (a is ITaughtReference ra && b is ITaughtReference rb)
            {
                rb.Reference = ra.Reference;
                rb.ReferenceWidth = ra.ReferenceWidth;
                rb.ReferenceHeight = ra.ReferenceHeight;
            }
        return c;
    }
}

public sealed record JobResult(bool Pass, IReadOnlyList<ToolResult> Tools, double TotalMs, string? ImageName, int Camera = 0)
{
    public string? FirstProblem => Tools.FirstOrDefault(t => !t.Pass) is { } t ? $"{t.ToolName}: {t.Summary}" : null;

    /// <summary>Every tool's values as "tool.name" (the part's measurements for the plant server).</summary>
    public IReadOnlyDictionary<string, double> Values =>
        Tools.SelectMany(t => t.Values.Select(kv => (Key: $"{t.ToolName}.{kv.Key}", kv.Value)))
             .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
}

/// <summary>Runs a job's tools on images. Build once per job (tools hold native handles), dispose after.</summary>
public sealed class JobRunner : IDisposable
{
    private readonly List<ITool> _tools = new();

    public JobRunner(Job job, ModelPaths models)
    {
        Job = job;
        try
        {
            foreach (var t in job.Tools.Where(t => t.Enabled))
                _tools.Add(t switch
                {
                    CodeToolConfig c => new CodeTool(c, models),
                    TextToolConfig x => new TextTool(x, models),
                    LocateToolConfig l => new LocateTool(l),
                    MeasureToolConfig m => new MeasureTool(m, job.Cameras.ElementAtOrDefault(m.Camera)?.Calibration),
                    BlobToolConfig b => new BlobTool(b, job.Cameras.ElementAtOrDefault(b.Camera)?.Calibration),
                    InspectToolConfig ins => new InspectTool(ins),
                    AnomalyToolConfig anom => new AnomalyTool(anom),
                    ThermalToolConfig therm => new ThermalTool(therm),
                    InfraredToolConfig ir => new InfraredTool(ir),
                    PolarToolConfig pol => new PolarTool(pol),
                    PhotometricToolConfig photo => new PhotometricTool(photo),
                    WaveletToolConfig wave => new WaveletTool(wave),
                    InpaintToolConfig inp => new InpaintTool(inp),
                    RectifyToolConfig rect => new RectifyTool(rect),
                    DriftToolConfig drift => new DriftTool(drift),
                    DeformToolConfig def => new DeformTool(def),
                    PhaseToolConfig ph => new PhaseTool(ph),
                    MetamorphicToolConfig meta => new MetamorphicTool(meta),
                    AutotuneToolConfig at => new AutotuneTool(at),
                    _ => throw new NotSupportedException(t.GetType().Name),
                });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Job Job { get; }
    public IReadOnlyList<ITool> Tools => _tools;
    public bool HasTools(int camera) => _tools.Any(t => t.Config.Camera == camera);

    /// <summary>Runs the tools of one camera on that camera's image. A Locate tool on the camera runs first;
    /// the others then follow the part, and are skipped when the part is not found. <paramref name="plc"/>: the
    /// values the PLC sent with this trigger (null when not triggered by the PLC: lines expecting them fail).
    /// Tools run in job order, so a Check text tool can take its expected text from a tool listed before it.</summary>
    /// <summary>Job setup only (Try): the Read code tools measure each code's size for the pixel check
    /// (<see cref="PixelCheck"/>) with the engine's quality measure on, about 1 ms a code; their results are judged
    /// as without it. Never on the line.</summary>
    public bool MeasurePixels
    {
        set { foreach (var t in _tools.OfType<CodeTool>()) t.MeasureSize = value; }
    }

    public JobResult Run(int camera, GrayImage image, BatchFields batch, PlcValues? plc = null)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<ToolResult>(_tools.Count);
        Fixture? fixture = null;
        var mine = _tools.Where(t => t.Config.Camera == camera).ToList();
        var locate = mine.OfType<LocateTool>().FirstOrDefault();
        if (locate is not null)
        {
            try
            {
                var (r, fx) = locate.Find(image);
                results.Add(r);
                if (fx is null)
                    return new JobResult(false, results, sw.Elapsed.TotalMilliseconds, image.Name, camera);
                fixture = fx;
            }
            catch (Exception e)
            {
                results.Add(new ToolResult(locate.Config.Name, false, "Tool error: " + e.Message, Array.Empty<string>(), 0, Array.Empty<Graphic>())
                    { Explain = "explain.tool.error" });
                return new JobResult(false, results, sw.Elapsed.TotalMilliseconds, image.Name, camera);
            }
        }
        foreach (var t in mine.Where(t => t != locate))
        {
            try
            {
                var r = t is TextTool text ? text.Run(image, batch, fixture, new RunContext(plc, results.ToList())) : t.Run(image, batch, fixture);
                // a correction or advisory tool never makes a part OK on its own (AUD-03); its failure still fails it
                results.Add(ToolArea.Decides(t.Config) ? r : r with { ReportOnly = true });
            }
            catch (Exception e)
            {
                results.Add(new ToolResult(t.Config.Name, false, "Tool error: " + e.Message, Array.Empty<string>(), 0, Array.Empty<Graphic>())
                    { Explain = "explain.tool.error" });
            }
        }
        // report-only results never make a part OK on their own (Job.Validate refuses such a camera too):
        // a part passes only with a result from a tool that decides
        return new JobResult(results.Any(r => !r.ReportOnly) && results.All(r => r.Pass), results, sw.Elapsed.TotalMilliseconds, image.Name, camera);
    }

    public void Dispose()
    {
        foreach (var t in _tools) t.Dispose();
        _tools.Clear();
    }
}

/// <summary>Images from a folder in name order (BMP and PNG files), for testing a job on stored images.</summary>
public sealed class FolderSource
{
    public static IReadOnlyList<string> List(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(f => f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Order().ToList()
            : Array.Empty<string>();
}
