using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionCore;

/// <summary>
/// A job file that cannot run as it is: refused at load with the reason, so the line stays NG and not ready
/// rather than running a half-loaded job (JOB-FILE-FORMAT.md 2.2 and 4). An InvalidOperationException, so the
/// callers that refuse a job change on one refuse this too.
/// </summary>
public sealed class JobLoadException(string job, string reason) : InvalidOperationException($"Job {job} cannot be loaded: {reason}")
{
    public string Job { get; } = job;
    public string Reason { get; } = reason;
}

/// <summary>
/// Reads the JSON of a job file strictly: size, complete JSON, schema version, field names and types, no nulls
/// where a value is needed, finite numbers. Anything else is refused with a sentence for the engineer.
/// </summary>
public static class JobFile
{
    /// <summary>
    /// Layout of the JSON job written today, "major.minor". Major 0 is the first subset of JOB-FILE-FORMAT.md
    /// (one JSON file, tools in a list); the full .vsjob format is 1.0 and will migrate these. A file without
    /// a schema version was saved before versions were written and reads as 0.0.
    /// </summary>
    public const string CurrentSchema = "0.1";
    /// <summary>JOB-FILE-FORMAT.md section 1: job.json is at most 4 MB.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxTools = 100;
    public const int MaxLinesPerTool = 32;
    public const int MaxNameLength = 100;
    private const int MaxDepth = 32;

    private static readonly (int Major, int Minor) Current = (0, 1);

    // Unknown fields (a typo, or a field of a newer version) and integers for named values are refused: either
    // would leave a setting at its default without anyone knowing. Duplicate fields are ambiguous.
    private static readonly JsonSerializerOptions Strict = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        AllowOutOfOrderMetadataProperties = true,   // "kind" anywhere in a tool, as a hand edit may leave it
        MaxDepth = MaxDepth,
    };

    /// <summary>The job in <paramref name="bytes"/> (the file <paramref name="name"/>.json), or JobLoadException.
    /// Notes on what was migrated are in <see cref="Job.LoadNotes"/>.</summary>
    public static Job Parse(string name, byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
            throw new JobLoadException(name, $"the file is {bytes.Length / 1048576.0:0.0} MB; a job file is at most {MaxBytes / 1048576} MB");
        int start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var body = bytes.AsMemory(start);
        if (body.Span.Trim((ReadOnlySpan<byte>)" \t\r\n"u8).IsEmpty)
            throw new JobLoadException(name, "the file is empty (cut short while it was written?)");
        // a damaged byte in a name or text would otherwise surface later as a transcoding error, not a refusal
        if (!System.Text.Unicode.Utf8.IsValid(body.Span))
            throw new JobLoadException(name, "the file is not UTF-8 text (damaged?)");

        var notes = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxDepth, AllowDuplicateProperties = false });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JobLoadException(name, "the file does not hold a job");
            CheckSchema(name, doc.RootElement, notes);
        }
        catch (JsonException e)
        {
            throw new JobLoadException(name, $"the file is not complete JSON (cut short or damaged) near line {(e.LineNumber ?? 0) + 1}");
        }

        Job job;
        try { job = JsonSerializer.Deserialize<Job>(body.Span, Strict) ?? throw new JobLoadException(name, "the file does not hold a job"); }
        catch (JsonException e) { throw new JobLoadException(name, Plain(e)); }
        catch (NotSupportedException e) { throw new JobLoadException(name, $"a tool without a known kind (code, text, locate, measure, blob): {FirstSentence(e.Message)}"); }
        catch (InvalidOperationException e) when (e is not JobLoadException) { throw new JobLoadException(name, FirstSentence(e.Message)); }
        if (Structure(job) is { } bad) throw new JobLoadException(name, bad);
        job.LoadNotes = notes;
        job.Digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        return job;
    }

    // JOB-FILE-FORMAT.md 2.2. The line runs a job only as its version wrote it: a newer version, even a newer
    // minor whose added fields this software would ignore, is refused rather than run with settings left out.
    private static void CheckSchema(string name, JsonElement root, List<string> notes)
    {
        if (!root.TryGetProperty(nameof(Job.SchemaVersion), out var v) || v.ValueKind == JsonValueKind.Null)
        {
            notes.Add($"Saved before job files carried a schema version: read as schema 0.0, fields added since take their defaults. Save the job to write schema {CurrentSchema}.");
            return;
        }
        if (v.ValueKind != JsonValueKind.String || !TryVersion(v.GetString()!, out var ver))
            throw new JobLoadException(name, $"the schema version {v.GetRawText()} is not a version (major.minor)");
        if (ver.CompareTo(Current) > 0)
            throw new JobLoadException(name, $"the job was made with a newer version (schema {ver.Major}.{ver.Minor}). Update the software to load it");
        if (ver.Major < Current.Major)
            throw new JobLoadException(name, $"schema {ver.Major}.{ver.Minor} is older than this software can migrate");
        if (ver.CompareTo(Current) < 0)
            notes.Add($"Saved in schema {ver.Major}.{ver.Minor}: fields added since take their defaults. Save the job to write schema {CurrentSchema}.");
    }

    private static bool TryVersion(string s, out (int Major, int Minor) v)
    {
        v = default;
        var p = s.Split('.');
        if (p.Length != 2 || !p.All(x => x.Length is > 0 and <= 4 && x.All(char.IsAsciiDigit))) return false;
        v = (int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static string Plain(JsonException e)
    {
        var where = string.IsNullOrEmpty(e.Path) || e.Path == "$" ? "" : $"at {e.Path.TrimStart('$', '.')}: ";
        return $"{where}{FirstSentence(e.Message)}";
    }

    private static string FirstSentence(string m)
    {
        int i = m.IndexOf(" Path:", StringComparison.Ordinal);
        return (i > 0 ? m[..i] : m).Trim().TrimEnd('.');
    }

    /// <summary>What the JSON reader lets through but no job may hold: an empty entry in a list, a number that is
    /// not finite, more tools or lines than a job has. Null when the job is whole.</summary>
    public static string? Structure(Job job)
    {
        if (job.Tools.Count > MaxTools) return $"{job.Tools.Count} tools; a job has at most {MaxTools}";
        if (job.Name.Length > MaxNameLength) return $"the job name is longer than {MaxNameLength} characters";
        foreach (var t in job.Tools.OfType<TextToolConfig>())
            if (t.Lines.Count > MaxLinesPerTool) return $"{t.Name}: {t.Lines.Count} lines; a tool checks at most {MaxLinesPerTool}";
        return Walk(job, "", 0);
    }

    // every settings object of the job, through its public properties: list entries and dictionary values are
    // never null, float and double values are finite
    private static string? Walk(object o, string path, int depth)
    {
        if (depth > MaxDepth) return $"{path}: nested too deep";
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetCustomAttribute<JsonIgnoreAttribute>() is not null || !p.CanWrite) continue;
            var v = p.GetValue(o);
            var here = path.Length == 0 ? p.Name : $"{path}.{p.Name}";
            switch (v)
            {
                case null or string or Enum or bool or int or long or DateTime: break;
                case float f when !float.IsFinite(f): return $"{here} is not a number";
                case double d when !double.IsFinite(d): return $"{here} is not a number";
                case float or double: break;
                case IDictionary dict:
                    foreach (DictionaryEntry e in dict)
                        if (e.Value is null) return $"{here}[{e.Key}] is empty";
                    break;
                case IEnumerable list:
                    int i = 0;
                    foreach (var e in list)
                    {
                        if (e is null) return $"{here}[{i}] is empty";
                        if (Settings(e) && Walk(e, $"{here}[{i}]", depth + 1) is { } bad) return bad;
                        i++;
                    }
                    break;
                default:
                    if (Settings(v) && Walk(v, here, depth + 1) is { } inner) return inner;
                    break;
            }
        }
        return null;
    }

    private static bool Settings(object o) => o.GetType().Namespace is { } ns && (ns.StartsWith("VisionCore", StringComparison.Ordinal) || ns.StartsWith("BarcodeReading", StringComparison.Ordinal));
}
