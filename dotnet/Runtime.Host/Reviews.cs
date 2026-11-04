using System.Globalization;
using System.Text.Json;
using StationGuard;
using VisionCore;
using VisionCore.Plc;
using VisionStudio.Remote.V1;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// The supervisor's NG review (owner, 2026-10-31): each reviewed bad part's full picture, kept with the
/// decision under data/reviews/&lt;job&gt;/cam&lt;n&gt;/good (accepted: it was a good part), bad (confirmed) or
/// accepted-record-only (accepted after a double-check, never learned from), with a .json beside it (who,
/// when, the camera's name, what the software found). A picture is named by a key that is unique for good
/// (its image name, which holds the line, camera, job, time and serial), so a runtime restart never lets a
/// new part overwrite an old decision; the same picture reviewed again moves to its new decision.
/// </summary>
public sealed class ReviewStore(string dataFolder)
{
    /// <summary>Pictures kept per camera and decision; the oldest go first.</summary>
    public const int MaxPerFolder = 2000;
    /// <summary>Bytes kept per camera and decision (full pictures are large); the oldest go first.</summary>
    public const long MaxBytesPerFolder = 4L << 30;

    public enum Kind { Good, Bad, Record }

    /// <summary>One reviewed picture as the learning sees it: its file and whether it must pass.</summary>
    public sealed record Entry(string File, string Name, bool Good);

    readonly object _lock = new();
    readonly string _root = Path.Combine(dataFolder, "reviews");
    /// <summary>The "Was OK" flags, apart from the decisions (the reviews tree holds only cam&lt;n&gt;/&lt;decision&gt; folders).</summary>
    readonly string _flagRoot = Path.Combine(dataFolder, "review-flags");

    /// <summary>The folder name of a job (its name without characters a file name may not hold).</summary>
    public static string FolderName(string job) => Safe(job);

    static string Safe(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } s ? s : "job";

    string Folder(string job, int camera, Kind kind) => Path.Combine(_root, Safe(job), $"cam{camera}", kind switch
    {
        Kind.Good => "good",
        Kind.Bad => "bad",
        _ => "accepted-record-only",
    });

    /// <summary>Keeps a copy of the picture under the decision. The copy is made first and only then is an
    /// earlier decision on the same part removed, so a failed copy never loses a decision. The fault keys
    /// ("tool|reason") and the software's verdict are kept too, so a later change of the decision is held to the
    /// same rules after the saved bad picture is gone (AS-04j). A "Was OK" flag on the part is cleared. Returns the
    /// decision it replaced, or null.</summary>
    public Kind? Review(string job, int camera, string cameraName, string key, string sourceFile, Kind kind, string user, string note,
        DateTimeOffset at, IReadOnlyList<string> faults, IReadOnlyList<string>? faultKeys = null, bool softwareOk = false)
    {
        lock (_lock)
        {
            var name = $"{Safe(key)}{Path.GetExtension(sourceFile).ToLowerInvariant()}";
            var folder = Folder(job, camera, kind);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name);
            var temp = target + ".part";
            File.Copy(sourceFile, temp, overwrite: true);
            File.WriteAllText(temp + ".json", JsonSerializer.Serialize(new
            {
                key, camera = cameraName,
                decision = kind switch { Kind.Good => "accepted", Kind.Bad => "confirmed_ng", _ => "accepted_record_only" },
                user, note, at = at.ToString("O", CultureInfo.InvariantCulture), faults,
                fault_keys = faultKeys ?? [], software_ok = softwareOk,
            }));
            Kind? before = null;
            foreach (var k in Enum.GetValues<Kind>())
            {
                var old = Path.Combine(Folder(job, camera, k), name);
                if (File.Exists(old)) { File.Delete(old); before = k; }
                if (File.Exists(old + ".json")) File.Delete(old + ".json");
            }
            File.Move(temp, target);
            File.Move(temp + ".json", target + ".json");
            Trim(folder);
            var flag = FlagFile(job, key);
            if (File.Exists(flag)) File.Delete(flag);
            return before;
        }
    }

    static void Trim(string folder)
    {
        var files = Pictures(folder).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        long bytes = 0;
        var kept = 0;
        foreach (var f in files)
        {
            bytes += new FileInfo(f).Length;
            if (++kept <= MaxPerFolder && bytes <= MaxBytesPerFolder) continue;
            File.Delete(f);
            if (File.Exists(f + ".json")) File.Delete(f + ".json");
        }
    }

    static IEnumerable<string> Pictures(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".bmp" or ".png" or ".tif" or ".tiff")
            : [];

    /// <summary>The camera name a picture was reviewed under (its .json), or null.</summary>
    static string? CameraOf(string picture)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(picture + ".json"));
            return doc.RootElement.TryGetProperty("camera", out var c) ? c.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The learning set of one camera: accepted pictures (must pass) and confirmed ones (must fail),
    /// only those reviewed under this camera's name, so a job whose cameras were reordered never learns from
    /// another view. Pictures accepted as a record only are never in it.</summary>
    public IReadOnlyList<Entry> Learning(string job, int camera, string cameraName)
    {
        lock (_lock)
            return Pictures(Folder(job, camera, Kind.Good)).Select(f => (f, good: true))
                .Concat(Pictures(Folder(job, camera, Kind.Bad)).Select(f => (f, good: false)))
                .Where(p => CameraOf(p.f) == cameraName)
                .OrderBy(p => p.good ? 0 : 1).ThenBy(p => p.f, StringComparer.Ordinal)
                .Select(p => new Entry(p.f, Path.GetFileName(p.f), p.good)).ToList();
    }

    /// <summary>The names (without extension) of the pictures of a camera already decided, whatever the decision:
    /// the sorting lists only the others.</summary>
    public HashSet<string> Decided(string job, int camera)
    {
        lock (_lock)
            return Enum.GetValues<Kind>().SelectMany(k => Pictures(Folder(job, camera, k)))
                .Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A reviewed picture as the supervisor's list shows it (AS-04j). FaultKeys is null for a review saved
    /// before 2026-11-04, whose fault keys were not kept: a change then treats its cause as not known.</summary>
    public sealed record Reviewed(string Key, string File, string Job, int Camera, string CameraName, Kind Kind, string User,
        DateTimeOffset At, string[] Faults, string[]? FaultKeys, bool SoftwareOk, string Note);

    static Reviewed? ReadReviewed(string picture, string job, int camera, Kind kind)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(picture + ".json"));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string[]? A(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToArray() : null;
            var at = DateTimeOffset.TryParse(S("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t : new DateTimeOffset(File.GetLastWriteTimeUtc(picture), TimeSpan.Zero);
            var key = S("key") is { Length: > 0 } k ? k : Path.GetFileNameWithoutExtension(picture);
            return new Reviewed(key, picture, job, camera, S("camera"), kind, S("user"), at, A("faults") ?? [], A("fault_keys"),
                r.TryGetProperty("software_ok", out var ok) && ok.ValueKind == JsonValueKind.True, S("note"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    IEnumerable<Reviewed> All(string job, int camera)
    {
        var jobFolder = Path.Combine(_root, Safe(job));
        if (!Directory.Exists(jobFolder)) yield break;
        foreach (var cam in Directory.EnumerateDirectories(jobFolder, "cam*"))
        {
            if (!int.TryParse(Path.GetFileName(cam)[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var c)) continue;
            if (camera >= 0 && c != camera) continue;
            foreach (var k in Enum.GetValues<Kind>())
                foreach (var f in Pictures(Folder(job, c, k)))
                    if (ReadReviewed(f, job, c, k) is { } r) yield return r;
        }
    }

    /// <summary>The reviewed pictures of a job (camera -1: every camera), newest decision first.</summary>
    public IReadOnlyList<Reviewed> List(string job, int camera = -1)
    {
        lock (_lock) return All(job, camera).OrderByDescending(r => r.At).ToList();
    }

    /// <summary>A reviewed picture by its key (the image id the list gives); null when none is kept. With
    /// <paramref name="jobFolderOk"/>, only in the job folders it accepts (the jobs that still exist), so a copy left
    /// under a deleted or renamed job is never taken for the live decision.</summary>
    public Reviewed? Find(string key, Func<string, bool>? jobFolderOk = null)
    {
        var name = Safe(key);
        lock (_lock)
        {
            if (!Directory.Exists(_root)) return null;
            foreach (var jobFolder in Directory.EnumerateDirectories(_root).Where(j => jobFolderOk?.Invoke(Path.GetFileName(j)) != false))
                foreach (var cam in Directory.EnumerateDirectories(jobFolder, "cam*"))
                    foreach (var kindFolder in Directory.EnumerateDirectories(cam))
                        foreach (var f in Pictures(kindFolder))
                        {
                            if (Path.GetFileNameWithoutExtension(f) != name) continue;
                            if (!int.TryParse(Path.GetFileName(cam)[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var c)) continue;
                            var kind = Path.GetFileName(kindFolder) switch { "good" => Kind.Good, "bad" => Kind.Bad, _ => Kind.Record };
                            if (ReadReviewed(f, Path.GetFileName(jobFolder), c, kind) is { } r) return r;
                        }
        }
        return null;
    }

    /// <summary>The decision kept for a picture of a camera, or null when it has none.</summary>
    public Kind? KindOf(string job, int camera, string key)
    {
        var name = Safe(key);
        lock (_lock)
            foreach (var k in Enum.GetValues<Kind>())
                if (Pictures(Folder(job, camera, k)).Any(f => Path.GetFileNameWithoutExtension(f) == name)) return k;
        return null;
    }

    /// <summary>Removes a decision (AS-04j): the picture is no longer reviewed, so it is not learned from and, while its
    /// saved bad picture is kept, waits for a review again. Returns what was removed, or null.</summary>
    public Reviewed? Remove(string key, Func<string, bool>? jobFolderOk = null)
    {
        lock (_lock)
        {
            if (Find(key, jobFolderOk) is not { } r || !File.Exists(r.File)) return null;
            File.Delete(r.File);
            if (File.Exists(r.File + ".json")) File.Delete(r.File + ".json");
            return r;
        }
    }

    // --- the operator's "Was OK" flag (AS-04k) ---------------------------------------------------------------------

    /// <summary>A bad part an operator marked "Was OK": shown to the supervisor, never learned from.</summary>
    public sealed record Flag(string ImageId, int Camera, string User, DateTimeOffset At, string Note);

    string FlagFile(string job, string key) => Path.Combine(_flagRoot, Safe(job), Safe(key) + ".json");

    /// <summary>Keeps the flag under the part's review key, so its review clears it.</summary>
    public void FlagWasOk(string job, int camera, string imageId, string key, string user, string note, DateTimeOffset at)
    {
        lock (_lock)
        {
            var file = FlagFile(job, key);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".part", JsonSerializer.Serialize(new
            {
                image_id = imageId, camera, user, note, at = at.ToString("O", CultureInfo.InvariantCulture),
            }));
            File.Move(file + ".part", file, overwrite: true);
            TrimFlags(Path.GetDirectoryName(file)!);
        }
    }

    static void TrimFlags(string folder)
    {
        foreach (var f in Directory.EnumerateFiles(folder, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(MaxPerFolder))
            File.Delete(f);
    }

    /// <summary>The "Was OK" flags of a job by image id; a flag on a part reviewed since is not listed.</summary>
    public IReadOnlyDictionary<string, Flag> Flags(string job)
    {
        var flags = new Dictionary<string, Flag>(StringComparer.Ordinal);
        lock (_lock)
        {
            var folder = Path.Combine(_flagRoot, Safe(job));
            if (!Directory.Exists(folder)) return flags;
            foreach (var f in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    var id = r.GetProperty("image_id").GetString() ?? "";
                    var at = DateTimeOffset.TryParse(r.GetProperty("at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                        ? t : DateTimeOffset.MinValue;
                    if (id.Length > 0)
                        flags[id] = new Flag(id, r.GetProperty("camera").GetInt32(), r.GetProperty("user").GetString() ?? "", at,
                            r.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "");
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or FormatException) { }
            }
        }
        return flags;
    }

    public (int Accepted, int Confirmed, int RecordOnly) Count(string job, int camera)
    {
        lock (_lock)
            return (Pictures(Folder(job, camera, Kind.Good)).Count(), Pictures(Folder(job, camera, Kind.Bad)).Count(),
                Pictures(Folder(job, camera, Kind.Record)).Count());
    }

    /// <summary>One review decision as its .json says it, for the browser's Operator flags tab (WEB-07).</summary>
    public sealed record Decision(DateTimeOffset At, string Job, int Camera, string CameraName, string Key, string Kind,
        string User, string Note, IReadOnlyList<string> Faults);

    /// <summary>The newest review decisions, newest first, read only. The files' write times pick the newest before
    /// any is read, so a large store costs a directory walk and <paramref name="max"/> small reads.</summary>
    public IReadOnlyList<Decision> Recent(int max)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_root)) return [];
            var list = new List<Decision>();
            foreach (var f in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".part.json", StringComparison.Ordinal))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(max))
            {
                try
                {
                    // reviews/<job>/cam<n>/<decision>/<picture>.json
                    var camDir = Path.GetDirectoryName(Path.GetDirectoryName(f))!;
                    if (!int.TryParse(Path.GetFileName(camDir).AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var cam)) continue;
                    var job = Path.GetFileName(Path.GetDirectoryName(camDir))!;
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    string Str(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    var at = DateTimeOffset.TryParse(Str("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : File.GetLastWriteTimeUtc(f);
                    var faults = r.TryGetProperty("faults", out var fa) && fa.ValueKind == JsonValueKind.Array
                        ? fa.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
                    list.Add(new Decision(at, job, cam, Str("camera"), Str("key"), Str("decision"), Str("user"), Str("note"), faults));
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return list.OrderByDescending(d => d.At).ToList();
        }
    }

    public ReviewsSummary Summary(Job job)
    {
        var s = new ReviewsSummary { Job = job.Name };
        for (var i = 0; i < job.Cameras.Count; i++)
        {
            var (a, c, r) = Count(job.Name, i);
            s.Cameras.Add(new CameraReviews { Camera = i, Name = job.Cameras[i].Name, Accepted = (uint)a, ConfirmedNg = (uint)c, AcceptedRecordOnly = (uint)r });
        }
        return s;
    }
}

/// <summary>
/// The supervisor's review of one bad part (owner, 2026-10-31): finds the part's full picture (the NG image
/// store's saved file, or the stored picture of a simulated camera) and files it under the decision. A part
/// the software found clearly bad (<see cref="ClearlyBad"/>) is not accepted on one tap: the answer is
/// looks_bad with the faults, and when the supervisor accepts it all the same (confirm_looks_bad) it is kept
/// as a record only, never in the learning set (owner: "mention not to add in good records to avoid bad
/// images teaching"). That rule is the runtime's, whatever the app sends. Allowed while the line runs: it
/// only copies a file.
/// <para>The learning session (owner, 2026-11-04) sorts saved pictures too, good ones included (<see cref="SortQueue"/>):
/// a picture no longer among the recent bad parts is found in the image stores. A bad picture the software could
/// not read (<see cref="Unreadable"/>) is validated a second time before it is accepted, and may still be kept as a
/// record only ("still can skip the learning for good to avoid wrong product accept"). A picture the software
/// passed and the supervisor rejects is a possible escape: the plant is told (alarm possible_escape, cleared at the
/// next start) and the audit trail records it.</para>
/// </summary>
public sealed class ReviewDesk(LineRuntime line, Station station, JobStore jobs, ReviewStore reviews, TimeProvider? time = null,
    SortQueue? sorts = null, StationAgent.StationPublisher? plant = null, VisionReports.ResultLog? audit = null,
    LearningSettingsStore? settings = null)
{
    /// <summary>The plant alarm raised when a part the software passed is rejected in the sorting.</summary>
    public const string PossibleEscapeAlarm = "possible_escape";

    /// <summary>Whether a bad picture is one the software could not read or decide (owner, 2026-11-04: validated twice
    /// before it may teach "good"): none of its faults ("tool|reason") is a defect the tool saw (<see cref="ClearlyBad"/>).
    /// Decided on the safe side: nothing read or found, an unsure read, a tool error, a reason not known here and no
    /// fault at all all count, so a new reason never teaches "good" on one tap.</summary>
    public static bool Unreadable(IEnumerable<string> faults) => !ClearlyBad(faults).Clearly;

    /// <summary>Reasons that mean the tool saw a defect, not that it was unsure: wrong or missing data, wrong
    /// or missing print, a size out of tolerance, a foreign mark. With the words the supervisor reads. Decided
    /// on the safe side: fewer codes than the product has may be a missing pack, so it asks too. A low print
    /// grade and a code not found at all (often faint print) are left to the supervisor's one tap.</summary>
    static readonly Dictionary<string, string> Defects = new(StringComparer.Ordinal)
    {
        ["explain.code.wrong"] = "the code holds the wrong data",
        ["explain.code.extra"] = "more codes than the product has",
        ["explain.code.short"] = "fewer codes than the product has (a pack may be missing)",
        ["explain.code1d.check_digit"] = "the code's check digit is wrong",
        ["explain.code1d.addon_wrong"] = "the add-on code is wrong",
        ["explain.code1d.addon_missing"] = "the add-on code is missing",
        ["explain.gs1.wrong_value"] = "a GS1 value is wrong",
        ["explain.gs1.missing_ai"] = "a GS1 field (batch, expiry, ...) is missing",
        ["explain.gs1.bad_date"] = "a GS1 date is not valid",
        ["explain.gs1.check_digit"] = "the GTIN check digit is wrong",
        ["explain.gs1.association"] = "GS1 fields that must go together do not",
        ["explain.gs1.syntax"] = "the GS1 data is malformed",
        ["explain.ocv.wrong"] = "a character is wrong",
        ["explain.ocv.missing"] = "a character or line is missing",
        ["explain.ocv.extra"] = "extra print",
        ["explain.ocv.mismatch"] = "the text is not what it should be",
        ["explain.ocv.broken"] = "a character is broken",
        ["explain.ocv.shift"] = "the print is shifted",
        ["explain.ocr.no_text"] = "no print found",
        ["explain.format.mismatch"] = "the text is not in the expected form",
        ["explain.format.date_invalid"] = "the date is not a real date",
        ["explain.format.date_wrong"] = "the date is wrong",
        ["explain.format.price_invalid"] = "the price is not valid",
        ["explain.geometry.out_of_tolerance"] = "a size is out of tolerance",
        ["explain.caliper.out_of_tolerance"] = "a size is out of tolerance",
        ["explain.circle.diameter"] = "a diameter is out of tolerance",
        ["explain.circle.out_of_round"] = "a circle is out of round",
        ["explain.line.angle"] = "an edge is at the wrong angle",
        ["explain.line.straightness"] = "an edge is not straight",
        ["explain.blob.present"] = "a mark or spot that must not be there",
        ["explain.blob.area"] = "a mark of the wrong size",
        ["explain.blob.too_many"] = "too many marks or spots",
        ["explain.blob.too_few"] = "too few marks",
        ["explain.blob.total_area"] = "the marked area is wrong",
        // a saved bad picture whose cause was not kept (saved before 2026-11-04, or rejected by the PLC): never taught as good
        [SortQueue.UnknownCause] = "what the software found was not kept with the picture",
    };

    /// <summary>A key for this process's parts that have no NG store id (simulated cameras): the runtime's own
    /// ids start again at 1 after a restart, so they are never used alone as a file name.</summary>
    static readonly string Run = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The faults of a picture ("tool|reason") in words; Clearly is true when any is a real defect.</summary>
    public static (bool Clearly, List<string> Words) ClearlyBad(IEnumerable<string> faults)
    {
        var clearly = false;
        var words = new List<string>();
        foreach (var f in faults)
        {
            var bar = f.IndexOf('|');
            var tool = bar < 0 ? "" : f[..bar];
            var reason = bar < 0 ? f : f[(bar + 1)..];
            if (Defects.TryGetValue(reason.Trim(), out var w)) { clearly = true; words.Add($"{tool}: {w}"); }
            else if (reason.Length > 0) words.Add(tool.Length > 0 ? $"{tool}: {reason}" : reason);
        }
        return (clearly, words);
    }

    /// <summary>Where a decision files the picture: a part the software found clearly bad is a record only, always,
    /// and so is one the supervisor chose to keep as a record.</summary>
    public static ReviewStore.Kind KindFor(ReviewDecision decision, bool clearlyBad, bool keepAsRecord = false) =>
        decision == ReviewDecision.ConfirmedNg ? ReviewStore.Kind.Bad : clearlyBad || keepAsRecord ? ReviewStore.Kind.Record : ReviewStore.Kind.Good;

    static ReviewPartResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };

    public ReviewPartResponse Review(ReviewPartRequest req, string user)
    {
        if (req.Decision is not (ReviewDecision.Accepted or ReviewDecision.ConfirmedNg))
            return No("Say whether the part was good or bad.", "runtime.refused.reviewDecision");
        var wasOk = false;
        string? file;
        string? key = null;
        (string Job, int Camera, string CameraName, string? File, string[] Faults) src;
        // a decision changed from the reviewed list (AS-04j): the copy kept with it, held to the same rules with what the
        // software found then; a review whose fault keys were not kept (before 2026-11-04) is taken from the line's own
        // picture while it is kept, else as a part whose cause is not known, which is never taught as good
        var done = reviews.Find(req.ImageId, f => JobName(f) is not null);
        var doneJob = done is null ? null : JobName(done.Job);
        // a review kept before 2026-11-04 does not say whether the software passed the part: one accepted as good, or with
        // no fault, may have been a passed picture, so a rejection of it is told as a possible escape (the safe side)
        var maybePassed = false;
        if (done is { FaultKeys: { } doneFaults } && doneJob is not null)
        {
            src = (doneJob, done.Camera, done.CameraName, done.File, doneFaults);
            file = done.File;
            wasOk = done.SoftwareOk;
            key = done.Key;
        }
        else if (line.ImageSource(req.ImageId) is { } recent)
        {
            src = recent;
            file = src.File;
            if (file is null)
            {
                var (queued, saved) = line.SavedPicture(req.ImageId);
                if (queued) return No("The picture is still being saved. Try again in a moment.", "runtime.refused.imageSaving", 409);
                file = saved;
            }
        }
        else if (sorts?.Find(req.ImageId) is { } kept && CameraName(kept) is { } camName)
        {
            // a saved picture of the learning session (good or bad), sorted after the part left the recent list
            src = (kept.Job, kept.Camera, camName, kept.File, kept.Faults);
            file = kept.File;
            wasOk = kept.Ok;
        }
        else if (done is not null && doneJob is not null)
        {
            src = (doneJob, done.Camera, done.CameraName, done.File, [$"|{SortQueue.UnknownCause}"]);
            file = done.File;
            key = done.Key;
            maybePassed = done.Kind == ReviewStore.Kind.Good || done.Faults.Length == 0;
        }
        else
            return No("This part's picture is not kept: it is older than the last 500 bad parts, or the camera gave no picture.",
                "runtime.refused.imageGone", 404);
        if (file is null || !File.Exists(file))
            return No("The full picture of this part was not saved (picture saving is off, or the disk was full), so it cannot be reviewed.",
                "runtime.refused.imageNotSaved", 404);

        var (clearly, words) = ClearlyBad(src.Faults);
        if (!wasOk && req.Decision == ReviewDecision.Accepted && clearly && !req.ConfirmLooksBad)
        {
            var ask = new ReviewPartResponse
            {
                ErrorCode = 409, ErrorKey = "runtime.review.looksBad", LooksBad = true,
                ErrorText = "The software found this part clearly bad. Look again before you accept it.",
            };
            ask.Faults.AddRange(words);
            return ask;
        }
        // could not be read: validated a second time before it may teach "good" (owner, 2026-11-04)
        if (!wasOk && req.Decision == ReviewDecision.Accepted && !clearly && Unreadable(src.Faults) && !req.ConfirmUnreadable && !req.KeepAsRecord)
        {
            var ask = new ReviewPartResponse
            {
                ErrorCode = 409, ErrorKey = "runtime.review.unreadable", Unreadable = true,
                ErrorText = "The software could not read this part. Check it again: accept it as good only if you are sure, or keep it as a record only.",
            };
            ask.Faults.AddRange(words);
            return ask;
        }
        var kind = KindFor(req.Decision, !wasOk && clearly, req.KeepAsRecord);
        // an image name (L01_C2_J045_261031-104512_000123) holds the line, camera, job, time and serial: unique for
        // good; an old-style id ("<inspection>-<camera>") starts again after a restart, so it gets this run's key
        key ??= ImageName.TryParse(req.ImageId, out _) ? req.ImageId : $"{Run}-{req.ImageId}";
        ReviewStore.Kind? before;
        try
        {
            before = reviews.Review(src.Job, src.Camera, src.CameraName, key, file, kind, user, req.Note, (time ?? TimeProvider.System).GetUtcNow(),
                words, src.Faults, wasOk);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return No("The picture could not be kept: " + e.Message, "runtime.refused.diskError", 500);
        }
        var r = new ReviewPartResponse { Ok = true, Learned = kind != ReviewStore.Kind.Record };
        if (before is { } was && was != kind)
            Audit("review_changed", $"{key} ({src.Job}, camera {src.Camera + 1}): {Words(was)} -> {Words(kind)} by {user}");
        // the software passed a part a person now finds bad: a bad part may have left the line (once per part)
        if ((wasOk || maybePassed) && req.Decision == ReviewDecision.ConfirmedNg && before != ReviewStore.Kind.Bad)
        {
            r.PossibleEscape = true;
            try { plant?.Alarm(PossibleEscapeAlarm, true, PlantEvents.AlarmPriority.High); } catch (Exception) { }
            Audit(PossibleEscapeAlarm, $"{req.ImageId} passed by the software, rejected by {user} in the sorting", true);
        }
        r.Faults.AddRange(words);
        if (jobs.Names().Contains(src.Job) && jobs.TryLoad(src.Job, forEdit: true) is { } job) r.Summary = reviews.Summary(job);
        return r;
    }

    static string Words(ReviewStore.Kind k) => k switch
    {
        ReviewStore.Kind.Good => "accepted",
        ReviewStore.Kind.Bad => "confirmed bad",
        _ => "accepted, record only",
    };

    void Audit(string kind, string detail, bool active = false)
    {
        try { audit?.AddEvent(DateTime.UtcNow, kind, active, detail); }
        catch (Exception) { /* the decision is kept; the audit trail reports its own faults */ }
    }

    /// <summary>The job of a reviewed picture's folder, or null when the job is gone.</summary>
    string? JobName(string folder) => jobs.Names().FirstOrDefault(n => ReviewStore.FolderName(n) == folder);

    /// <summary>Removes a decision (AS-04j): an audit-trail event; the picture is no longer learned from, and a
    /// proposal learned with it cannot be applied (the reviews changed).</summary>
    public RemoveReviewResponse Remove(RemoveReviewRequest req, string user)
    {
        ReviewStore.Reviewed? gone;
        try { gone = reviews.Remove(req.ImageId, f => JobName(f) is not null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RemoveReviewResponse { ErrorCode = 500, ErrorText = "The decision could not be removed: " + e.Message, ErrorKey = "runtime.refused.diskError" };
        }
        if (gone is null)
            return new RemoveReviewResponse { ErrorCode = 404, ErrorText = "This picture has no review.", ErrorKey = "runtime.refused.notReviewed" };
        Audit("review_removed", $"{gone.Key} ({gone.Job}, camera {gone.Camera + 1}): {Words(gone.Kind)} removed by {user}");
        var r = new RemoveReviewResponse { Ok = true };
        if (JobName(gone.Job) is { } name && jobs.TryLoad(name, forEdit: true) is { } job) r.Summary = reviews.Summary(job);
        return r;
    }

    /// <summary>The reviewed pictures of a job, newest decision first, and its "Was OK" flags (AS-04j, AS-04k).</summary>
    public ListReviewedResponse Reviewed(ListReviewedRequest req)
    {
        var r = new ListReviewedResponse();
        var name = string.IsNullOrEmpty(req.Job) ? station.Job?.Name : req.Job;
        if (name is null || !jobs.Names().Contains(name)) return r;
        var all = reviews.List(name, req.Camera);
        foreach (var x in all.Take(SortQueue.MaxListed))
        {
            var item = new ReviewedPicture
            {
                ImageId = x.Key, Camera = x.Camera, CameraName = x.CameraName,
                Decision = x.Kind == ReviewStore.Kind.Bad ? ReviewDecision.ConfirmedNg : ReviewDecision.Accepted,
                Learned = x.Kind != ReviewStore.Kind.Record, User = x.User, SoftwareOk = x.SoftwareOk, Note = x.Note,
                Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(x.At),
            };
            item.Faults.AddRange(x.Faults);
            r.Items.Add(item);
        }
        r.Total = (uint)all.Count;
        foreach (var f in reviews.Flags(name).Values.Where(f => req.Camera < 0 || f.Camera == req.Camera).OrderByDescending(f => f.At))
            r.Flags.Add(new WasOkFlag { ImageId = f.ImageId, Camera = f.Camera, User = f.User, Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(f.At) });
        return r;
    }

    /// <summary>An operator's "Was OK" on a bad part (AS-04k): kept with who and when for the supervisor. It changes
    /// nothing else: the part stays bad until a supervisor decides, and the flag never teaches the job.</summary>
    public FlagWasOkResponse FlagWasOk(FlagWasOkRequest req, string user)
    {
        static FlagWasOkResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        // only a part with its picture's name (line, camera, job, time, serial): an old-style id starts again after a
        // restart, and a flag on it would be shown on another part
        if (!ImageName.TryParse(req.ImageId, out _))
            return No("This part has no saved picture, so it cannot be marked. Tell your supervisor.", "runtime.refused.imageNotSaved", 404);
        (string Job, int Camera)? src = line.ImageSource(req.ImageId) is { } recent ? (recent.Job, recent.Camera)
            : sorts?.Find(req.ImageId) is { Ok: false } kept ? (kept.Job, kept.Camera) : null;
        if (src is not { } s)
            return No("This part's picture is not kept: it is older than the last 500 bad parts, or it is not a bad part.", "runtime.refused.imageGone", 404);
        var key = req.ImageId;
        if (reviews.KindOf(s.Job, s.Camera, key) is not null)
            return No("A supervisor has already decided this part.", "runtime.refused.alreadyReviewed", 409);
        try { reviews.FlagWasOk(s.Job, s.Camera, req.ImageId, key, user, req.Note, (time ?? TimeProvider.System).GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return No("The mark could not be kept: " + e.Message, "runtime.refused.diskError", 500);
        }
        Audit("was_ok_flag", $"{req.ImageId} ({s.Job}, camera {s.Camera + 1}) marked Was OK by {user}");
        return new FlagWasOkResponse { Ok = true };
    }

    /// <summary>The camera's name in the job a saved picture belongs to; null when the job or camera is gone.</summary>
    string? CameraName(SavedPicture p) =>
        jobs.Names().Contains(p.Job) && jobs.TryLoad(p.Job, forEdit: true) is { } j && p.Camera >= 0 && p.Camera < j.Cameras.Count
            ? j.Cameras[p.Camera].Name : null;

    /// <summary>The pictures of a job waiting to be sorted (the learning session).</summary>
    public ListToSortResponse ToSort(ListToSortRequest req)
    {
        var r = new ListToSortResponse();
        var name = string.IsNullOrEmpty(req.Job) ? station.Job?.Name : req.Job;
        // the setting off: the sorting is not offered (AS-04b); the NG review stays
        if (sorts is null || settings?.Enabled == false || name is null || !jobs.Names().Contains(name) || jobs.TryLoad(name, forEdit: true) is not { } job) return r;
        var (items, total) = sorts.Unsorted(name, req.Camera);
        var flags = reviews.Flags(name);
        foreach (var p in items.Where(p => p.Camera < job.Cameras.Count))
            r.Items.Add(SortQueue.Item(p, job.Cameras[p.Camera].Name, flags.GetValueOrDefault(p.Id)));
        r.Total = (uint)total;
        return r;
    }

    public ReviewsSummary Summary(GetReviewsRequest req)
    {
        var name = string.IsNullOrEmpty(req.Job) ? station.Job?.Name : req.Job;
        return name is not null && jobs.Names().Contains(name) && jobs.TryLoad(name, forEdit: true) is { } job
            ? reviews.Summary(job) : new ReviewsSummary { Job = name ?? "" };
    }
}

/// <summary>
/// Learning from the reviews (owner, 2026-10-31: "based on that Ai should start auto tuning and learning to
/// match the operator requirement"): AutoTune (AS-03) on one camera's reviewed pictures. Line safety
/// (requirements section 12):
/// <list type="bullet">
/// <item>Refused while the line runs, and stopped when the line starts during it: three runs of the job over
/// the pictures take the CPU the parts need. The learning session (owner, 2026-11-04) may also learn on a running
/// line that is idle (no part for the set idle time, the PLC's RunEnable off: <see cref="LineRuntime.Idleness"/>);
/// it stops at the next picture when a part comes, RunEnable comes on or the line stops or starts.</item>
/// <item>Tuned on the job as saved. Applying is refused while the line runs, when the job was saved again
/// since, and when the reviewed pictures changed since (a part confirmed bad after the learning was never
/// proved against).</item>
/// <item>A proposal may be applied only when every confirmed bad picture fails and no accepted picture that
/// passed is lost (<see cref="AutoTune.Verify"/>). AutoTune never proposes a setting a known bad picture passes.</item>
/// <item>A check is made less strict only when every loosened setting has a confirmed bad picture it alone
/// rejects, and at least <see cref="MinConfirmedForLooser"/> confirmed bad pictures are rejected by the tuned
/// settings alone (the other checks pass them): only those prove a looser setting.</item>
/// <item>Nothing changes the job until a person applies it; the result is a new job version.</item>
/// </list>
/// </summary>
public sealed class Learning(Station station, JobStore jobs, ModelPaths models, ReviewStore reviews, LineRuntime? line = null)
{
    /// <summary>Confirmed bad pictures only the tuned settings reject, needed before a check may be made less strict.</summary>
    public const int MinConfirmedForLooser = 5;
    /// <summary>A proposal not applied within this time is dropped: the line may have learned more since.</summary>
    public static readonly TimeSpan ProposalLife = TimeSpan.FromHours(8);

    sealed record Pending(string Id, Job From, int Camera, string CameraName, TuneProposal Proposal, DateTimeOffset At, string Reviews);

    readonly object _lock = new();
    Pending? _pending;

    static LearnResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };

    /// <summary>The reviewed pictures as one string: names and decisions. Apply compares it with the one learned on.</summary>
    static string Fingerprint(IEnumerable<ReviewStore.Entry> entries) =>
        string.Join("\n", entries.Select(e => (e.Good ? "+" : "-") + e.Name));

    /// <summary>Whether a looser proposal is proved: each loosened setting rejects a confirmed bad picture by itself,
    /// and enough confirmed bad pictures are rejected by the tuned settings alone. Null when it is, else why not.</summary>
    public static string? LooserNotProved(TuneProposal p, IReadOnlyList<TuneSample> relaxed)
    {
        var looser = p.Knobs.Where(k => k.Changed && k.Looser).ToList();
        if (looser.Count == 0) return null;
        if (looser.FirstOrDefault(k => k.HighestBad is null) is { } blind)
            return $"This would make {blind.Tool} accept more parts, but no confirmed bad part shows where its limit must stay. Confirm bad parts that this check catches first.";
        // in the relaxed run the tuned checks pass everything: a bad picture that still fails there is caught by another check
        var proving = relaxed.Count(s => !s.Image.Good && s.Result.Pass);
        return proving >= MinConfirmedForLooser ? null
            : $"This would make a check accept more parts. Confirm at least {MinConfirmedForLooser} bad parts that this check catches (now {proving}), so it is proved that bad parts still fail.";
    }

    public LearnResponse Learn(LearnRequest req, CancellationToken token = default)
    {
        var wasRunning = station.Running;
        if (wasRunning && line?.Idleness().MayLearn != true)
            return No("Stop the line to learn from the reviews: learning runs the job on every reviewed picture and the line needs the PC.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        var partsAtStart = line?.Parts ?? 0;
        var name = string.IsNullOrEmpty(req.Job) ? station.Job?.Name : req.Job;
        if (name is null) return No("No job is open.", "runtime.refused.noJob", PlcCodes.JobNotFound);
        // the job as saved: the station keeps the version it opened
        var job = jobs.Names().Contains(name) ? jobs.Load(name) : null;
        if (job is null) return No("No such job.", "runtime.refused.noJob", PlcCodes.JobNotFound);
        if (req.Camera < 0 || req.Camera >= job.Cameras.Count) return No("No such camera in this job.", "runtime.refused.jobInvalid");
        var missing = job.BatchFields().Where(f => station.Batch.Get(f.Key) is null).Select(f => f.Label).ToList();
        if (missing.Count > 0) return No("Enter the batch first: " + string.Join(", ", missing), "runtime.refused.batchMissing");

        var cameraName = job.Cameras[req.Camera].Name;
        var entries = reviews.Learning(name, req.Camera, cameraName);
        var images = entries.Select(e => new TuneImage(e.File, e.Good)).ToList();
        var accepted = images.Count(i => i.Good);
        var confirmed = images.Count - accepted;
        var r = new LearnResponse
        {
            Ok = true, Accepted = (uint)accepted, ConfirmedNg = (uint)confirmed,
            GoodPassPctBefore = -1, BadFailPctBefore = -1, GoodPassPctAfter = -1, BadFailPctAfter = -1,
        };
        if (accepted == 0 || confirmed == 0)
        {
            r.Problem = "Learning needs at least one accepted part and one confirmed bad part of this camera. Review more bad parts first.";
            return r;
        }

        // the line started (or, idle, got a part or RunEnable) meanwhile: learning gives the PC back at once
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool Busy() => station.Running != wasRunning || (wasRunning && (line is null || line.Parts != partsAtStart || !line.Idleness().MayLearn));
        void Watch(int _) { if (Busy()) stop.Cancel(); }
        var batch = station.Batch;
        try
        {
            // 1. the job as it is, 2. with the tuned checks relaxed (the scores), 3. with the proposal (the proof)
            var before = AutoTune.Run(job, req.Camera, images, models, batch, stop.Token, Watch);
            var relaxed = AutoTune.Run(AutoTune.Relaxed(job, req.Camera), req.Camera, images, models, batch, stop.Token, Watch);
            var p = AutoTune.Propose(job, req.Camera, relaxed);
            var countBefore = TuneCount.Of(before);
            r.Before = countBefore.Words();
            (r.GoodPassPctBefore, r.BadFailPctBefore) = (Pct(countBefore.GoodPassPct), Pct(countBefore.BadFailPct));
            (r.GoodPassPctAfter, r.BadFailPctAfter) = (-1, -1);
            r.StillRejected.AddRange(p.GoodAlwaysFail);
            if (p.Problem is not null) { r.Problem = p.Problem; return r; }
            foreach (var k in p.Knobs.Where(k => k.Changed))
                r.Settings.Add(new TunedSetting { Tool = k.Tool, Setting = k.Setting, Current = k.Show(k.Current), Proposed = k.Show(k.Proposed), Looser = k.Looser });
            if (r.Settings.Count == 0) { r.Problem = "The job already matches the reviews: nothing to change."; return r; }
            var after = AutoTune.Run(AutoTune.Apply(job, p), req.Camera, images, models, batch, stop.Token, Watch);
            var verdict = AutoTune.Verify(p, before, after);
            r.After = verdict.Words;
            (r.GoodPassPctAfter, r.BadFailPctAfter) = (Pct(verdict.After.GoodPassPct), Pct(verdict.After.BadFailPct));
            r.CanApply = verdict.CanApply;
            if (r.CanApply && LooserNotProved(p, relaxed) is { } why)
            {
                r.CanApply = false;
                r.Problem = why;
            }
            if (r.CanApply)
            {
                var id = Guid.NewGuid().ToString("N");
                lock (_lock) _pending = new Pending(id, job, req.Camera, cameraName, p, DateTimeOffset.UtcNow, Fingerprint(entries));
                r.ProposalId = id;
            }
            return r;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return No("The line needed the PC (it started or stopped, a part came, or RunEnable came on): learning stopped. Learn again when the line is stopped or idle.",
                "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return No("The reviewed pictures changed while learning. Learn again.", "runtime.refused.reviewsChanged");
        }
    }

    static float Pct(double v) => v < 0 ? -1 : (float)Math.Round(v, 1);

    /// <summary>For tests: a proposal as Learn keeps it, without running the job.</summary>
    internal string Remember(Job from, int camera, TuneProposal p, IEnumerable<ReviewStore.Entry> reviewed)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock) _pending = new Pending(id, from, camera, from.Cameras[camera].Name, p, DateTimeOffset.UtcNow, Fingerprint(reviewed));
        return id;
    }

    public ApplyLearningResponse Apply(ApplyLearningRequest req, string user)
    {
        static ApplyLearningResponse No(string text, string key, uint code = 400) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        // nothing that decides OK or NG changes while the line runs (AS-04)
        if (station.Running) return No("Stop the line before the job changes.", "runtime.refused.stopFirst", PlcCodes.JobChangeWhileBusy);
        Pending p;
        lock (_lock)
        {
            if (_pending is not { } q || q.Id != req.ProposalId || DateTimeOffset.UtcNow - q.At > ProposalLife)
                return No("This proposal is no longer there. Learn again.", "runtime.refused.proposalGone");
            p = q;
        }
        // a job saved since the learning is not overwritten with settings worked out on the old one
        if (jobs.Load(p.From.Name)?.Version != p.From.Version)
            return No("The job was saved since the learning. Learn again.", "runtime.refused.jobChanged");
        // a part reviewed since was never proved against: a newly confirmed bad part might pass
        if (Fingerprint(reviews.Learning(p.From.Name, p.Camera, p.CameraName)) != p.Reviews)
            return No("Parts were reviewed since the learning. Learn again, so the change is proved on them too.", "runtime.refused.reviewsChanged");
        Job saved;
        try { saved = jobs.Save(AutoTune.Apply(p.From, p.Proposal), string.IsNullOrEmpty(user) ? "learning" : user); }
        catch (ArgumentException e) { return No(e.Message, "runtime.refused.jobInvalid"); }
        lock (_lock) _pending = null;
        if (station.Job?.Name == saved.Name)
        {
            try { station.Reload(); }
            catch (InvalidOperationException e) { return No(e.Message, "runtime.refused.loadFailed", PlcCodes.JobNotFound); }
        }
        return new ApplyLearningResponse { Ok = true, Job = saved.Name, Version = saved.Version };
    }
}
