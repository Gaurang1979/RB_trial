using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using SkiaSharp;
using VisionCore;
using VisionCore.Plc;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using V1 = VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>
/// The job designer's calls (UI-UX-SPEC.md 4.7, 4.8): the job file as saved, its cameras' stored
/// pictures, and a draft tried on one picture. Callers have checked access (Engineer, Admin).
/// Line safety (requirements section 12): a job is saved only through <see cref="Job.Validate"/>
/// (a job that can never pass is refused); the open job is never saved while the line runs; nothing
/// is tried while the line runs, so a try never takes the CPU a part needs.
/// </summary>
public sealed class JobDesigner(Station station, JobStore jobs, ModelPaths models, LineRuntime line, IOptions<StudioOptions> options) : IDisposable
{
    const int MaxPictures = 2000;
    readonly object _lock = new();
    // The runner of the draft tried last: "Run all" tries one draft on many pictures, and building
    // the tools (the AI models) takes far longer than running them.
    string? _runnerJson;
    JobRunner? _runner;
    // The pictures tried last, newest first, so Vision Studio can fetch the one on screen at full
    // resolution without loss when the engineer zooms in (requirements AS-17). Kept up to TriedBytes,
    // and always the newest one.
    const long TriedBytes = 96L << 20;
    readonly List<(string Id, GrayImage Image)> _tried = [];

    public GetJobResponse GetJob(GetJobRequest req)
    {
        var name = string.IsNullOrEmpty(req.Name) ? station.Job?.Name : req.Name;
        if (name is null) return new GetJobResponse { ErrorCode = PlcCodes.JobNotFound, ErrorText = "No job is open.", ErrorKey = "runtime.refused.noJob" };
        Job? job;
        // opened for editing: a job that cannot run yet (a pattern to teach again) opens so it can be fixed;
        // a file that cannot be read whole is refused with the reason
        try { job = jobs.Names().Contains(name) ? jobs.LoadForEdit(name) : null; }
        catch (JobLoadException e)
        {
            return new GetJobResponse { ErrorCode = PlcCodes.JobNotFound, ErrorText = $"The job file cannot be read: {e.Reason}", ErrorKey = "runtime.refused.loadFailed" };
        }
        if (job is null) return new GetJobResponse { ErrorCode = PlcCodes.JobNotFound, ErrorText = "No such job.", ErrorKey = "runtime.refused.noJob" };
        return new GetJobResponse { Ok = true, Json = JsonSerializer.Serialize(job, JobStore.Json), Version = job.Version, Open = station.Job?.Name == job.Name };
    }

    public SaveJobResponse SaveJob(SaveJobRequest req, string user)
    {
        static SaveJobResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (Parse(req.Json, out var job) is { } bad) return No(bad, "runtime.refused.jobInvalid");
        var open = station.Job?.Name == job!.Name;
        if (open && station.Running) return No("Stop the line before saving the job it runs.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        // the PLC selects a job by its number: two jobs with one number would leave the choice to chance
        if (job.Number > 0)
            foreach (var other in jobs.Names().Where(n => !string.Equals(n, job.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // a job that cannot run yet still holds its number; only an unreadable file is passed over
                var o = jobs.TryLoad(other, forEdit: true);
                if (o?.Number == job.Number) return No($"Job {other} already has number {job.Number}. Give this job another number.", "runtime.refused.jobNumberTaken");
            }
        // "Accept as is" on too few pixels (AS-14) is recorded as the signed-in engineer's, at the runtime's time
        VisionCore.PixelCheck.Stamp(job, jobs.TryLoad(job.Name, forEdit: true), user, DateTimeOffset.Now);
        try { job = jobs.Save(job, user); }
        catch (ArgumentException e) { return No(e.Message, "runtime.refused.jobInvalid"); }
        if (open)
        {
            try { station.Reload(); }
            catch (InvalidOperationException e) { return No(e.Message, "runtime.refused.loadFailed", PlcCodes.JobNotFound); }
        }
        return new SaveJobResponse { Ok = true, Name = job.Name, Version = job.Version };
    }

    public ListPicturesResponse ListPictures(ListPicturesRequest req)
    {
        if (Parse(req.JobJson, out var job) is { } bad) return new ListPicturesResponse { ErrorCode = 400, ErrorText = bad, ErrorKey = "runtime.refused.jobInvalid" };
        if (req.Camera < 0 || req.Camera >= job!.Cameras.Count) return new ListPicturesResponse { ErrorCode = 400, ErrorText = "No such camera in the job.", ErrorKey = "runtime.refused.noCamera" };
        var r = new ListPicturesResponse { Ok = true };
        r.Names.AddRange(Pictures(job.Cameras[req.Camera]).Take(MaxPictures).Select(p => Path.GetFileName(p)));
        return r;
    }

    /// <summary>A camera's stored pictures: its test folder's BMP files (PNG content too), in name order.</summary>
    static IReadOnlyList<string> Pictures(CameraConfig c) => FolderSource.List(c.TestFolder ?? "");

    public TryJobResponse TryJob(TryJobRequest req)
    {
        static TryJobResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (station.Running) return No("Stop the line to try a job on pictures: the line needs the PC while it runs.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        if (Parse(req.JobJson, out var job) is { } bad) return No(bad, "runtime.refused.jobInvalid");
        if (req.Camera < 0 || req.Camera >= job!.Cameras.Count) return No("No such camera in the job.", "runtime.refused.noCamera");
        GrayImage? img;
        try { img = Picture(req, job.Cameras[req.Camera]); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            return No($"The picture cannot be read: {e.Message}", "runtime.refused.picture");
        }
        if (img is null) return No("This picture is no longer kept.", "runtime.refused.picture", 404);

        string? taughtJson = null, taughtText = null;
        if (req.TeachTool.Length > 0)
        {
            var tool = job.Tools.FirstOrDefault(t => t.Name == req.TeachTool);
            if (tool is null || tool.Camera != req.Camera) return No($"{req.TeachTool} is not a tool of this camera.", "runtime.refused.noTool");
            try { taughtText = ToolTeach.Teach(tool, img, models, job, req.TeachSpot == 0 ? 1 : req.TeachSpot); }
            catch (InvalidOperationException e) { return No(e.Message, "runtime.refused.teach"); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return No($"The tools cannot be built: {e.Message}", "runtime.refused.jobInvalid"); }
            req.JobJson = taughtJson = JsonSerializer.Serialize(job, JobStore.Json);
        }
        var batch = new BatchFields();
        foreach (var (k, v) in req.Batch.Count > 0 ? req.Batch : (IEnumerable<KeyValuePair<string, string>>)station.Batch.Values) batch.Values[k] = v;
        var plc = req.PlcPartId.Length > 0 || req.PlcUserIn.Count > 0 || req.PlcText.Count > 0
            ? new PlcValues(req.PlcPartId, 0) { UserIn = req.PlcUserIn.ToArray(), Text = req.PlcText.Count > 0 ? TextBlock(job, req.PlcText) : null } : null;
        JobResult result;
        lock (_lock)
        {
            if (_runnerJson != req.JobJson)
            {
                _runner?.Dispose();
                _runner = null;
                _runnerJson = null;
                // a try grades every picture (the line grades one part in GradeEvery): its grade is the
                // engineer's margin, and a picture is judged on its grade as the line's sampled parts are
                foreach (var code in job.Tools.OfType<CodeToolConfig>()) code.GradeEvery = 1;
                try { _runner = new JobRunner(job, models) { MeasurePixels = true }; }
                catch (Exception e) { return No($"The tools cannot be built: {e.Message}", "runtime.refused.jobInvalid"); }
                _runnerJson = req.JobJson;
            }
            if (!_runner!.HasTools(req.Camera)) return No("This camera has no tools to try.", "runtime.refused.noTools");
            result = _runner.Run(req.Camera, img, batch, plc);
        }

        var frame = Render.Draw(img, null, null, req.MaxWidth > 0 ? Math.Min(req.MaxWidth, 4096) : 1280);
        var r = new TryJobResponse
        {
            Ok = true,
            Pass = result.Pass,
            TimeMs = (float)result.TotalMs,
            Jpeg = ByteString.CopyFrom(frame.Jpeg),
            Width = frame.Width,
            Height = frame.Height,
            Scale = (float)frame.Scale,
            ImageWidth = img.Width,
            ImageHeight = img.Height,
            TaughtJson = taughtJson ?? "",
            TaughtText = taughtText ?? "",
            ImageId = req.NotShown ? "" : KeepTried(img),
            PictureCheck = req.SkipPictureCheck ? null : Check(VisionCore.PictureCheck.Measure(img)),
        };
        foreach (var t in result.Tools)
        {
            var cfg = job.Tools.FirstOrDefault(x => x.Name == t.ToolName);
            var tool = new V1.ToolResult
            {
                Name = t.ToolName,
                Kind = cfg is null ? "" : Station.LicenceToolOf(cfg),
                Pass = t.Pass,
                Confidence = t.Pass ? 1 : 0,
                ReasonText = t.Summary,
                TimeMs = (float)t.TimeMs,
            };
            LineRuntime.AddMeasures(tool, t);
            if (cfg is not null && VisionCore.PixelCheck.Check(cfg, t, job.Cameras[req.Camera]) is { } pixels) tool.Pixels = Pixels(pixels);
            r.Tools.Add(tool);
            foreach (var code in t.Codes ?? [])
            {
                var read = new V1.CodeRead { Text = code.Text, Format = code.Format.ToString(), State = V1.CodeRead.Types.State.Read, AddOn = code.AddOn, Tool = t.ToolName };
                read.Corners.AddRange(code.Corners.Select(p => new V1.Point { X = p.X, Y = p.Y }));
                if (CodeTeach.Gs1Data(code.Format, code.Text) is { } gs1)
                {
                    foreach (var (ai, value) in gs1.Elements) read.Gs1[ai] = value;
                    read.Field = Gs1Field(gs1);
                }
                r.Codes.Add(read);
            }
            // one set of date names per tool, as Teach proposes them: two lines never both get MFD
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in t.Lines ?? [])
            {
                var line = new TextRead { Tool = t.ToolName, Text = l.Text, Expected = l.Expected ?? "", Pass = l.Pass, Quality = l.Quality };
                line.Corners.AddRange(l.Corners.Select(p => new V1.Point { X = p.X, Y = p.Y }));
                if (cfg is TextToolConfig text) (line.Format, line.Field) = LineFormat(text, l.Text, used, img.TakenAt ?? DateTime.Now);
                r.Lines.Add(line);
            }
            // a Blob tool's dent and deviation marks (BLB-OUT-08) take the place of the rule's own diamond
            var blob = cfg as BlobToolConfig;
            foreach (var g in t.Graphics.Where(g => blob is null || !BlobMarks.IsRuleMark(g))) r.Graphics.Add(Graphic(g, t.ToolName, frame.Scale));
            if (blob is not null) r.Graphics.AddRange(BlobMarks.For(blob, t, job.Cameras[req.Camera].Calibration, frame.Scale));
        }
        return r;
    }

    /// <summary>The format a line read suggests (requirement AS-15), as Teach would write it: a Read text tool's
    /// format (<see cref="TextFormat.Propose"/>) or a Check text tool's template, with its field type as auto find
    /// names it; empty when nothing was read. Advice for the engineer only: the job changes when they accept it.</summary>
    public static (string Format, string Field) LineFormat(TextToolConfig tool, string read, ISet<string> used, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(read)) return ("", "");
        var format = tool.Mode == TextMode.Read
            ? TextFormat.Propose(read, used, TextTeach.KnownMonthLanguages(tool))
            : TextTeach.Suggest(read, when);
        return (format, AutoFind.LineField(read, format));
    }

    /// <summary>A GS1 code's elements as its suggested format: "GS1: GTIN (01), batch (10)".</summary>
    public static string Gs1Field(BarcodeReading.Gs1Result gs1) =>
        "GS1: " + string.Join(", ", gs1.Elements.Select(e => $"{BarcodeReading.Gs1Check.NameOf(e.Ai)} ({e.Ai})"));

    /// <summary>
    /// One picture from a job camera for teaching (UI-UX-SPEC.md 4.6 step 2), kept with the camera's stored
    /// pictures so Try, Try all and Find tools use it like any other. A camera with no stored-pictures folder
    /// gets one under the data folder (pictures/&lt;job&gt;/camera &lt;n&gt;), and the draft comes back with it.
    /// </summary>
    public GrabPictureResponse GrabPicture(GrabPictureRequest req)
    {
        static GrabPictureResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (station.Running) return No("Stop the line to take a picture for teaching: the line has the cameras while it runs.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        if (Parse(req.JobJson, out var job) is { } bad) return No(bad, "runtime.refused.jobInvalid");
        if (req.Camera < 0 || req.Camera >= job!.Cameras.Count) return No("No such camera in the job.", "runtime.refused.noCamera");
        var camera = job.Cameras[req.Camera];
        if (string.IsNullOrWhiteSpace(camera.TestFolder))
            camera.TestFolder = Path.Combine(options.Value.DataFolder, "pictures", Safe(job.Name.Length > 0 ? job.Name : "New job"), $"camera {req.Camera + 1}");
        if (Pictures(camera).Count >= MaxPictures) return No($"This camera already keeps {MaxPictures} pictures. Remove some first.", "runtime.refused.tooManyPictures");
        GrayImage? img;
        try { img = station.GrabOne(camera); }
        catch (InvalidOperationException e) { return No(e.Message, "runtime.refused.camera", PlcCodes.NoFrame); }
        if (img is null) return No($"{camera.Name}: no picture came from the camera. Check its cable and power.", "runtime.refused.noFrame", PlcCodes.NoFrame);
        var name = $"grab-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bmp";
        try
        {
            Directory.CreateDirectory(camera.TestFolder);
            BarcodeReading.BmpImage.SaveGray(Path.Combine(camera.TestFolder, name), Packed(img), img.Width, img.Height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return No($"The picture cannot be stored: {e.Message}", "runtime.refused.picture");
        }
        return new GrabPictureResponse { Ok = true, Name = name, JobJson = JsonSerializer.Serialize(job, JobStore.Json) };
    }

    /// <summary>Most pictures one suggestion reads: each is held in memory while it is measured.</summary>
    public const int MaxTolerancePictures = 30;

    /// <summary>
    /// The outline tolerances a Blob tool would need on stored pictures of good parts (requirements 17.12 BLB-OUT-06):
    /// <see cref="BlobTool.AutoTolerance"/> on each picture, where the camera's Locate tool puts the part. A suggestion
    /// only: the runtime never changes the job, and no suggestion (found false) is made from a picture that shows no
    /// single whole outline, so a missing, cut or doubled part never sets a limit.
    /// </summary>
    public SuggestBlobToleranceResponse SuggestBlobTolerance(SuggestBlobToleranceRequest req)
    {
        static SuggestBlobToleranceResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (station.Running) return No("Stop the line to measure pictures: the line needs the PC while it runs.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        if (Parse(req.JobJson, out var job) is { } bad) return No(bad, "runtime.refused.jobInvalid");
        if (req.Camera < 0 || req.Camera >= job!.Cameras.Count) return No("No such camera in the job.", "runtime.refused.noCamera");
        if (job.Tools.FirstOrDefault(t => t.Name == req.Tool) is not BlobToolConfig blob || blob.Camera != req.Camera)
            return No($"{req.Tool} is not a Blob tool of this camera.", "runtime.refused.noTool");
        if (blob.TaughtOutline is null) return No($"{blob.Name}: teach the outline on a good part first.", "runtime.refused.notTaught");
        var camera = job.Cameras[req.Camera];
        var calibration = camera.Calibration;
        var mm = blob.OutlineUnit == BlobOutlineUnit.Mm;
        if (mm && !(calibration is { MmPerPx: > 0 } && float.IsFinite(calibration.MmPerPx)))
            return No("Outline limits in mm need the camera's calibration. Calibrate the camera, or use px.", "runtime.refused.needsCalibration");
        if (BlobTool.SetupProblem(ToolTeach.MeasureCopy(blob), calibration) is { } problem) return No($"{blob.Name}: {problem}.", "runtime.refused.jobInvalid");
        var names = req.Pictures.Distinct().ToList();
        if (names.Count is < 3 or > MaxTolerancePictures)
            return No($"Choose 3 to {MaxTolerancePictures} pictures of good parts ({names.Count} chosen).", "runtime.refused.pictures");
        var r = new SuggestBlobToleranceResponse { Ok = true, Unit = mm ? "mm" : "px", Pictures = names.Count, Calibration = Map(calibration) };
        var images = new List<GrayImage>(names.Count);
        var fixtures = new List<Fixture?>(names.Count);
        try
        {
            foreach (var name in names)
            {
                GrayImage img;
                try { img = Picture(camera, name, null, null)!; }
                catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
                {
                    return No($"The picture {name} cannot be read: {e.Message}", "runtime.refused.picture");
                }
                try { fixtures.Add(ToolTeach.Locate(job, req.Camera, img)); }
                catch (InvalidOperationException e)
                {
                    r.ReasonText = $"No suggestion: {name}: {e.Message}";
                    return r;
                }
                images.Add(img);
            }
            var t = BlobTool.AutoTolerance(blob, images, calibration, fixtures: fixtures);
            if (t is null)
            {
                r.ReasonText = "No suggestion: every picture must show one whole outline of the part, found on the grey. Leave out pictures of missing, cut or doubled parts, or set the threshold and area so the part is one spot.";
                return r;
            }
            r.Found = true;
            r.MaxDeviation = t.MaxDeviation;
            r.MaxDentDepth = t.MaxDentDepth;
            r.WorstDeviation = t.WorstDeviation;
            r.WorstDent = t.WorstDent;
            return r;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return No($"The tools cannot be built: {e.Message}", "runtime.refused.jobInvalid");
        }
    }

    /// <summary>A camera's calibration for the contract; null when it has none.</summary>
    public static V1.LensCalibration? Map(BarcodeReading.LensCalibration? c) => c is null ? null : new V1.LensCalibration
    {
        Fx = c.Fx, Fy = c.Fy, Cx = c.Cx, Cy = c.Cy, K1 = c.K1, K2 = c.K2, P1 = c.P1, P2 = c.P2, K3 = c.K3,
        MmPerPx = float.IsFinite(c.MmPerPx) && c.MmPerPx > 0 ? c.MmPerPx : 0, RmsPx = c.RmsPx,
    };

    // the pixels row after row with no padding, as a BMP file is written from
    static byte[] Packed(GrayImage img)
    {
        if (img.Stride == img.Width && img.Pixels.Length == img.Width * img.Height) return img.Pixels;
        var px = new byte[img.Width * img.Height];
        for (var y = 0; y < img.Height; y++) Array.Copy(img.Pixels, y * img.Stride, px, y * img.Width, img.Width);
        return px;
    }

    // a job name as a folder name: characters a file system refuses become '_'
    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c)).Trim().TrimEnd('.');

    /// <summary>
    /// The tools auto find proposes on one picture (requirements AS-01, UI-UX-SPEC.md 4.6 step 3): every code
    /// with its type and every block of text with each line's field type, on the whole picture or in the window
    /// the user dragged. Suggestions only: the runtime adds nothing to a job (line safety, AS-12).
    /// </summary>
    public FindToolsResponse FindTools(FindToolsRequest req)
    {
        static FindToolsResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (station.Running) return No("Stop the line to find tools on a picture: the line needs the PC while it runs.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        if (Parse(req.JobJson, out var job) is { } bad) return No(bad, "runtime.refused.jobInvalid");
        if (req.Camera < 0 || req.Camera >= job!.Cameras.Count) return No("No such camera in the job.", "runtime.refused.noCamera");
        GrayImage? img;
        try { img = Picture(req, job.Cameras[req.Camera]); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            return No($"The picture cannot be read: {e.Message}", "runtime.refused.picture");
        }
        if (img is null) return No("This picture is no longer kept.", "runtime.refused.picture", 404);
        var area = req.Area is { Width: > 0, Height: > 0 } a ? new RoiBox(a.X, a.Y, a.Width, a.Height) : null;
        var sw = Stopwatch.StartNew();
        List<Suggestion> found;
        try { found = AutoFind.Suggest(img, models, area: area); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return No($"The tools cannot be built: {e.Message}", "runtime.refused.jobInvalid");
        }
        var frame = Render.Draw(img, null, null, 1280);
        var r = new FindToolsResponse
        {
            Ok = true,
            TimeMs = (float)sw.Elapsed.TotalMilliseconds,
            Jpeg = ByteString.CopyFrom(frame.Jpeg),
            Width = frame.Width,
            Height = frame.Height,
            Scale = (float)frame.Scale,
            ImageWidth = img.Width,
            ImageHeight = img.Height,
        };
        foreach (var s in found)
        {
            var t = new ToolSuggestion
            {
                Kind = s.Kind == SuggestionKind.Code ? "code" : "text",
                Summary = s.Summary,
                ToolJson = JsonSerializer.Serialize(s.Tool, JobStore.Json),
            };
            t.Read.AddRange(s.Read);
            t.Fields.AddRange(s.Fields);
            t.Outline.AddRange(s.Outline.Select(p => new V1.Point { X = p.X, Y = p.Y }));
            r.Suggestions.Add(t);
        }
        return r;
    }

    /// <summary>A text block as the PLC would send it with trigger 1: each string at its register, in the
    /// encoding and byte order of the job's line that reads it there (ASCII, high byte first otherwise).</summary>
    static PlcText TextBlock(Job job, IDictionary<int, string> strings)
    {
        const ushort trigger = 1;
        var b = new PlcBlock(PlcTextBlock.Size, bigEndian: false);
        b.SetU16(0, trigger);
        var specs = job.Tools.OfType<TextToolConfig>().SelectMany(t => t.Lines).Select(l => l.PlcText).OfType<PlcTextSpec>().ToList();
        foreach (var (register, text) in strings)
        {
            if (register < PlcTextBlock.FirstDataRegister || register > PlcTextBlock.LastRegister) continue;
            var spec = specs.FirstOrDefault(s => s.Register == register) ?? new PlcTextSpec { Register = register };
            var chars = System.Text.Encoding.ASCII.GetBytes(text);
            var raw = spec.Encoding == PlcTextEncoding.S7String
                ? new byte[] { (byte)spec.Length, (byte)Math.Min(chars.Length, spec.Length) }.Concat(chars).ToArray()
                : chars;
            var at = 2 * (register - PlcTextBlock.StartRegister);
            for (var i = 0; i < raw.Length && at + i + 1 < PlcTextBlock.Size; i += 2)
            {
                byte c0 = raw[i], c1 = i + 1 < raw.Length ? raw[i + 1] : (byte)0;
                b.SetU16(at + i, spec.ByteOrder == PlcByteOrder.HighFirst ? (ushort)(c0 << 8 | c1) : (ushort)(c1 << 8 | c0));
            }
        }
        return new PlcText(b.Bytes, bigEndian: false, trigger, repeated: false);
    }

    static string MeasureName(PixelMeasure m) => m switch { PixelMeasure.Module => "module", PixelMeasure.NarrowBar => "bar", _ => "letter" };

    internal static V1.PixelCheck Pixels(PixelReport p)
    {
        var v = new V1.PixelCheck
        {
            Measure = MeasureName(p.Measure),
            NeedPx = p.NeedPx,
            UnsurePx = p.UnsurePx,
            FoundPx = p.FoundPx,
            Factor = p.Factor,
            PixelsTimes = p.PixelsTimes,
            LensMm = p.LensMm ?? 0,
            LensAdvisedMm = p.LensAdvisedMm ?? 0,
            Accepted = p.Accepted is not null,
            AcceptedBy = p.Accepted?.By ?? "",
        };
        if (p.Accepted is { } a) v.AcceptedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(a.At);
        foreach (var i in p.Items)
        {
            var item = new V1.PixelItem { Name = i.Name, Measure = MeasureName(i.Measure), SizePx = i.SizePx, Below = i.Below };
            item.Corners.AddRange(i.Corners.Select(c => new V1.Point { X = c.X, Y = c.Y }));
            v.Items.Add(item);
        }
        return v;
    }

    /// <summary>The picture check (requirements AS-13) as the contract carries it: advice only.</summary>
    internal static V1.PictureCheck Check(PictureCheckResult c)
    {
        var v = new V1.PictureCheck();
        foreach (var z in c.Zones)
            v.Zones.Add(new V1.PictureZone
            {
                Zone = ZoneName(z.Zone),
                X = z.Box.X, Y = z.Box.Y, Width = z.Box.Width, Height = z.Box.Height,
                Sharpness = double.IsNaN(z.Sharpness) ? -1 : (float)z.Sharpness,
                Brightness = (float)z.Brightness,
                State = z.State switch { ZoneState.Good => "good", ZoneState.Close => "close", ZoneState.Bad => "bad", _ => "no_detail" },
            });
        foreach (var a in c.Advice)
        {
            var advice = new V1.PictureAdvice
            {
                Kind = a.Kind switch
                {
                    PictureAdviceKind.Tilt => "tilt", PictureAdviceKind.Focus => "focus", PictureAdviceKind.CleanLens => "clean_lens",
                    PictureAdviceKind.Vignetting => "vignetting", PictureAdviceKind.Dark => "dark", _ => "bright",
                },
            };
            advice.Zones.AddRange(a.Zones.Select(ZoneName));
            v.Advice.Add(advice);
        }
        return v;
    }

    static string ZoneName(PictureZoneName z) => z switch
    {
        PictureZoneName.TopLeft => "top_left", PictureZoneName.TopRight => "top_right",
        PictureZoneName.BottomLeft => "bottom_left", PictureZoneName.BottomRight => "bottom_right", _ => "centre",
    };

    internal static V1.Graphic Graphic(VisionCore.Graphic g, string tool, double scale)
    {
        var v = new V1.Graphic
        {
            Kind = V1.Graphic.Types.Kind.Polygon,
            Colour = g.Kind switch { GraphicKind.Ok => "status.ok", GraphicKind.Fail => "status.ng", _ => "status.info" },
            Label = g.Label ?? "",
            Tooltip = g.Label ?? tool,
            Tool = tool,
        };
        v.Points.AddRange(g.Points.Select(p => new V1.Point { X = (float)(p.X * scale), Y = (float)(p.Y * scale) }));
        return v;
    }

    /// <summary>An id TryJob gave (never a line image's: those name the part).</summary>
    public static bool IsTried(string imageId) => imageId.StartsWith("try-", StringComparison.Ordinal);

    string KeepTried(GrayImage img)
    {
        var id = "try-" + Guid.NewGuid().ToString("N")[..16];
        lock (_tried)
        {
            _tried.Insert(0, (id, img));
            long bytes = 0;
            for (var i = 0; i < _tried.Count; i++)
            {
                bytes += (long)_tried[i].Image.Stride * _tried[i].Image.Height;
                if (i > 0 && bytes > TriedBytes) { _tried.RemoveRange(i, _tried.Count - i); break; }
            }
        }
        return id;
    }

    /// <summary>A picture tried lately (TryJobResponse.image_id) at full resolution, without graphics; null when it is
    /// no longer kept. While the line runs none is kept or sent: encoding a full picture takes the CPU a part needs.</summary>
    public (byte[] Data, int Width, int Height, string Format)? TriedImage(string imageId, GetImageRequest.Types.Encoding encoding)
    {
        if (!IsTried(imageId)) return null;
        GrayImage? img;
        lock (_tried)
        {
            if (station.Running) _tried.Clear();
            img = _tried.FirstOrDefault(t => t.Id == imageId).Image;
        }
        return img is null ? null : Encode(img, encoding);
    }

    /// <summary>A grey picture as GetImage sends it: raw 8-bit rows, PNG (without loss) or JPEG.</summary>
    public static (byte[] Data, int Width, int Height, string Format) Encode(GrayImage img, GetImageRequest.Types.Encoding encoding)
    {
        if (encoding == GetImageRequest.Types.Encoding.Raw)
        {
            var raw = new byte[img.Width * img.Height];
            for (var y = 0; y < img.Height; y++) img.Pixels.AsSpan(y * img.Stride, img.Width).CopyTo(raw.AsSpan(y * img.Width));
            return (raw, img.Width, img.Height, "gray8");
        }
        using var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        unsafe
        {
            var dst = (byte*)bmp.GetPixels();
            for (var y = 0; y < img.Height; y++)
                img.Pixels.AsSpan(y * img.Stride, img.Width).CopyTo(new Span<byte>(dst + (long)y * bmp.RowBytes, img.Width));
        }
        var jpeg = encoding == GetImageRequest.Types.Encoding.Jpeg;
        using var data = bmp.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        return (data.ToArray(), img.Width, img.Height, jpeg ? "jpeg" : "png");
    }

    GrayImage? Picture(TryJobRequest req, CameraConfig camera) => req.PictureCase switch
    {
        TryJobRequest.PictureOneofCase.Stored => Picture(camera, req.Stored, null, null),
        TryJobRequest.PictureOneofCase.ImageId => Picture(camera, null, req.ImageId, null),
        TryJobRequest.PictureOneofCase.Upload => Picture(camera, null, null, req.Upload),
        _ => throw new ArgumentException("choose a picture"),
    };

    GrayImage? Picture(FindToolsRequest req, CameraConfig camera) => req.PictureCase switch
    {
        FindToolsRequest.PictureOneofCase.Stored => Picture(camera, req.Stored, null, null),
        FindToolsRequest.PictureOneofCase.ImageId => Picture(camera, null, req.ImageId, null),
        FindToolsRequest.PictureOneofCase.Upload => Picture(camera, null, null, req.Upload),
        _ => throw new ArgumentException("choose a picture"),
    };

    GrayImage? Picture(CameraConfig camera, string? stored, string? imageId, ByteString? upload)
    {
        if (stored is not null)
        {
            // a name from ListPictures, never a path: only the camera's own folder is read
            var file = Pictures(camera).FirstOrDefault(p => Path.GetFileName(p) == stored)
                ?? throw new ArgumentException($"{stored} is not one of this camera's stored pictures");
            return Decode(File.ReadAllBytes(file), Path.GetFileName(file));
        }
        if (imageId is not null)
            return line.Image(imageId, GetImageRequest.Types.Encoding.Raw, false) is { Format: "gray8" } raw
                ? new GrayImage(raw.Data, raw.Width, raw.Height) { Name = imageId } : null;
        return Decode(upload!.ToByteArray(), "upload");
    }

    /// <summary>An 8-bit grey picture from a BMP, PNG or JPEG file's bytes (many ".bmp" files are PNG).</summary>
    public static GrayImage Decode(byte[] data, string name)
    {
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            var (px, w, h) = BarcodeReading.BmpImage.DecodeGray(data);
            return new GrayImage(px, w, h) { Name = name };
        }
        using var codec = SKCodec.Create(new MemoryStream(data)) ?? throw new InvalidDataException("not a BMP, PNG or JPEG picture");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        var res = codec.GetPixels(info, bmp.GetPixels());
        if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidDataException($"the picture cannot be decoded ({res})");
        var pixels = new byte[info.Width * info.Height];
        unsafe
        {
            var src = (byte*)bmp.GetPixels();
            for (var y = 0; y < info.Height; y++)
                new ReadOnlySpan<byte>(src + (long)y * bmp.RowBytes, info.Width).CopyTo(pixels.AsSpan(y * info.Width));
        }
        return new GrayImage(pixels, info.Width, info.Height) { Name = name };
    }

    /// <summary>The draft from its JSON, read as strictly as a job file (<see cref="JobFile.Parse"/>: unknown
    /// fields, integers for named values, a newer schema are refused, as each would leave a setting at its
    /// default unseen), with the checks a try needs (cameras and tools that fit); the full check
    /// (<see cref="Job.Validate"/>) runs when it is saved. Null when it is usable.</summary>
    static string? Parse(string json, out Job? job)
    {
        job = null;
        if (string.IsNullOrWhiteSpace(json)) return "The job is empty.";
        try { job = JobFile.Parse("draft", System.Text.Encoding.UTF8.GetBytes(json)); }
        catch (JobLoadException e) { return $"The job is not valid: {e.Reason}."; }
        if (job is null) return "The job is empty.";
        if (job.Cameras.Count is < 1 or > Job.MaxCameras) return $"A job has 1 to {Job.MaxCameras} cameras.";
        foreach (var t in job.Tools)
            if (t.Camera < 0 || t.Camera >= job.Cameras.Count) return $"{t.Name}: pick one of the job's cameras.";
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _runner?.Dispose();
            _runner = null;
        }
        lock (_tried) _tried.Clear();
    }
}
