using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionCore;
using Xunit;

/// <summary>
/// A job file never loads into a half-working job (JOB-FILE-FORMAT.md 2.2 and 4): a damaged, cut-short, over-size,
/// newer or wrongly typed file, or one whose pattern, font or model is missing, is refused with the reason, or
/// read with a note when it is an older version. Data/jobs holds the sample jobs, old versions included.
/// </summary>
public sealed class JobLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-jobload-" + Guid.NewGuid().ToString("N"));
    private readonly JobStore _store;

    public JobLoadTests() => _store = new JobStore(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string SamplesFolder => Path.Combine(AppContext.BaseDirectory, "Data", "jobs");
    public static IEnumerable<object[]> Samples() =>
        Directory.EnumerateFiles(SamplesFolder, "*.json").Order().Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    // the sample in the store, with the pattern files it names
    private string Install(string name)
    {
        var text = File.ReadAllText(Path.Combine(SamplesFolder, name + ".json"));
        File.WriteAllText(Path.Combine(_dir, name + ".json"), text);
        foreach (var l in JsonSerializer.Deserialize<Job>(text, JobStore.Json)!.Tools.OfType<LocateToolConfig>())
            if (l.PatternFile is { } f) WritePattern(f);
        return text;
    }

    [Fact]
    public void LoadedJobCarriesTheDigestOfItsFileBytes()
    {
        var name = Samples().Select(o => (string)o[0]).First();
        var text = Install(name);
        var job = _store.LoadForEdit(name)!;
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_dir, name + ".json")))), job.Digest);
        Assert.Equal(job.Digest, _store.LoadForEdit(name)!.Digest);
        // the same job with other bytes (a space added) is the same name and version with another digest
        File.WriteAllText(Path.Combine(_dir, name + ".json"), text + " ");
        Assert.NotEqual(job.Digest, _store.LoadForEdit(name)!.Digest);
    }

    // Requirements 12 CV-12: Start looks at the file on disk again
    [Fact]
    public void ChangedOnDisk_SameFileIsFine_SwappedGoneOrUnreadableIsRefused()
    {
        var name = Samples().Select(o => (string)o[0]).First();
        var text = Install(name);
        var job = _store.LoadForEdit(name)!;
        var path = Path.Combine(_dir, name + ".json");
        Assert.Null(_store.ChangedOnDisk(job));
        // saved again with the same bytes is still the same file
        File.WriteAllText(path, text);
        Assert.Null(_store.ChangedOnDisk(job));
        // other bytes under the same name and version
        File.WriteAllText(path, text + " ");
        var why = _store.ChangedOnDisk(job);
        Assert.Contains("changed since it was opened", why);
        Assert.Contains("Open the job again", why);
        // changed back: the loaded job matches the file again
        File.WriteAllText(path, text);
        Assert.Null(_store.ChangedOnDisk(job));
        File.Delete(path);
        Assert.Contains("gone", _store.ChangedOnDisk(job));
    }

    [Fact]
    public void ChangedOnDisk_JobWithoutDigestIsNotChecked() =>
        Assert.Null(_store.ChangedOnDisk(new Job { Name = "memory" }));

    private void WritePattern(string rel, int w = 40, int h = 36)
    {
        var full = Path.Combine(_dir, "assets", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 7);
        BarcodeReading.BmpImage.SaveGray(full, px, w, h);
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name + ".json"), text);
    private void Write(string name, JsonNode node) => Write(name, node.ToJsonString());

    private JobLoadException Refused(string name)
    {
        var e = Assert.Throws<JobLoadException>(() => _store.Load(name));
        Assert.Equal(name, e.Job);
        Assert.False(string.IsNullOrWhiteSpace(e.Reason));
        return e;
    }

    private static JsonObject Minimal(string name = "J") => new()
    {
        ["SchemaVersion"] = JobFile.CurrentSchema,
        ["Name"] = name,
        ["Cameras"] = new JsonArray(new JsonObject { ["Name"] = "Top" }),
        ["Tools"] = new JsonArray(new JsonObject { ["kind"] = "code", ["Name"] = "Code", ["Format"] = "QrCode" }),
    };

    // --- every sample job in the repository ---

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_job_loads_and_survives_a_save(string name)
    {
        Install(name);
        var job = _store.Load(name)!;
        Assert.Equal(name, job.Name);
        Assert.Empty(job.LoadProblems);
        foreach (var l in job.Tools.OfType<LocateToolConfig>().Where(l => l.PatternFile is not null))
            Assert.True(l.Pattern is { Length: > 0 } && l.PatternWidth > 0, $"{l.Name}: pattern not loaded");

        var before = JsonSerializer.Serialize(job, JobStore.Json);
        var saved = _store.Save(job, "test");
        Assert.Equal(JobFile.CurrentSchema, saved.SchemaVersion);
        var again = _store.Load(name)!;
        Assert.Empty(again.LoadNotes);
        Assert.Equal(Comparable(before), Comparable(JsonSerializer.Serialize(again, JobStore.Json)));
    }

    // what a save changes on purpose: the version, its time and user, the schema, and the pattern's file name
    private static string Comparable(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        foreach (var k in new[] { "SchemaVersion", "Version", "UpdatedAt", "UpdatedBy" }) o.Remove(k);
        foreach (var t in o["Tools"]!.AsArray()) t!.AsObject().Remove("PatternFile");
        return o.ToJsonString();
    }

    // --- every tool's settings ---

    public static IEnumerable<object[]> ToolKinds() => typeof(ToolConfig).GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
        .Select(a => new object[] { a.DerivedType.Name });

    [Theory]
    [MemberData(nameof(ToolKinds))]
    public void Every_setting_of_every_tool_round_trips_through_the_strict_reader(string kind)
    {
        var type = typeof(ToolConfig).Assembly.GetTypes().Single(t => t.Name == kind);
        var tool = (ToolConfig)Activator.CreateInstance(type)!;
        var fresh = JsonSerializer.Serialize(tool, JobStore.Json);
        Fill(tool, 0);
        tool.Camera = 1;
        var job = new Job { SchemaVersion = JobFile.CurrentSchema, Name = "All settings", Cameras = { new CameraConfig { Name = "B" } }, Tools = { tool } };
        Fill(job.Cameras[0], 0);
        var text = JsonSerializer.Serialize(job, JobStore.Json);
        var back = JobFile.Parse("All settings", Encoding.UTF8.GetBytes(text));
        Assert.Equal(text, JsonSerializer.Serialize(back, JobStore.Json));
        // every setting was written with a value of its own, not the default
        var filled = JsonNode.Parse(JsonSerializer.Serialize(tool, JobStore.Json))!.AsObject();
        var defaults = JsonNode.Parse(fresh)!.AsObject();
        foreach (var (key, value) in filled)
            if (key is not ("kind" or "Camera"))
                Assert.False(JsonNode.DeepEquals(value, defaults[key]), $"{kind}.{key} was not set by the test");
    }

    // every settable property gets a value that is not its default
    private static void Fill(object o, int depth)
    {
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0 || p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null) continue;
            var v = Value(p.PropertyType, p.GetValue(o), p.Name, depth);
            if (v is not null) p.SetValue(o, v);
        }
    }

    private static object? Value(Type t, object? current, string name, int depth)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return "x-" + name;
        if (t == typeof(bool)) return !(current as bool? ?? false);
        if (t == typeof(byte)) return (byte)((current as byte? ?? 0) + 3);
        if (t == typeof(int)) return (current as int? ?? 0) + 3;
        if (t == typeof(uint)) return (current as uint? ?? 0) + 3u;
        if (t == typeof(float)) return (current as float? ?? 0) + 0.25f;
        if (t == typeof(double)) return (current as double? ?? 0) + 0.25;
        if (t == typeof(DateTime)) return new DateTime(2026, 10, 31, 8, 0, 0);
        if (t.IsEnum)
        {
            // another value than the default; a non-zero one when there is, as zero is what a missing field reads as
            var values = Enum.GetValues(t).Cast<object>().Where(e => !e.Equals(current)).OrderBy(e => Convert.ToInt64(e) == 0).ToList();
            return values.Count > 0 ? values[0] : current;
        }
        if (t == typeof(RoiBox)) return new RoiBox(1, 2, 30, 40);
        if (t == typeof(RotBox)) return new RotBox(10, 20, 30, 4, 5);
        // a polymorphic list element (a Measure tool's features): its first kind
        if (t.IsAbstract && t.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>().FirstOrDefault() is { } derived)
            t = derived.DerivedType;
        if (depth > 4) return current;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(t)!;
            list.Add(Value(t.GetGenericArguments()[0], null, name, depth + 1));
            return list;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var dict = (IDictionary)Activator.CreateInstance(t)!;
            dict.Add("k-" + name, Value(t.GetGenericArguments()[1], null, name, depth + 1));
            return dict;
        }
        if (t.IsClass && t.GetConstructor(Type.EmptyTypes) is not null)
        {
            var inner = Activator.CreateInstance(t)!;
            Fill(inner, depth + 1);
            return inner;
        }
        return current;
    }

    // --- corrupt and cut-short files ---

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [InlineData("{")]
    [InlineData("{\"Name\": \"J\", \"Tools\": [")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"J\"")]
    [InlineData("{} {}")]
    [InlineData("\u0000\u0001garbage")]
    public void Damaged_or_cut_short_files_are_refused(string text)
    {
        Write("J", text);
        Refused("J");
    }

    [Fact]
    public void A_file_cut_short_anywhere_is_refused_never_half_loaded()
    {
        var full = Encoding.UTF8.GetBytes(Install("Harpic 200ml two cameras"));
        // the file ends in "}" plus a new line at most; every shorter cut drops part of the job
        int whole = Encoding.UTF8.GetString(full).TrimEnd().Length;
        for (int n = 0; n < whole; n += 7)
        {
            File.WriteAllBytes(Path.Combine(_dir, "Harpic 200ml two cameras.json"), full[..n]);
            Assert.Throws<JobLoadException>(() => _store.Load("Harpic 200ml two cameras"));
        }
    }

    [Fact]
    public void A_utf8_byte_order_mark_is_read()
    {
        File.WriteAllBytes(Path.Combine(_dir, "J.json"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Minimal().ToJsonString())]);
        Assert.Equal("J", _store.Load("J")!.Name);
    }

    [Fact]
    public void A_damaged_byte_that_is_not_utf8_is_refused()
    {
        var bytes = Encoding.UTF8.GetBytes(Minimal().ToJsonString());
        int at = Array.IndexOf(bytes, (byte)'J', Encoding.UTF8.GetString(bytes).IndexOf("\"Name\"", StringComparison.Ordinal));
        bytes[at] = 0xFF;                                   // inside the job's name
        File.WriteAllBytes(Path.Combine(_dir, "J.json"), bytes);
        Assert.Contains("not UTF-8", Assert.Throws<JobLoadException>(() => _store.Load("J")).Message);
    }

    // --- wrong types and missing fields ---

    [Theory]
    [InlineData("Number", "\"seven\"")]
    [InlineData("Number", "7.5")]
    [InlineData("Number", "99999999999")]
    [InlineData("Name", "42")]
    [InlineData("Name", "null")]
    [InlineData("Cameras", "{}")]
    [InlineData("Cameras", "null")]
    [InlineData("Cameras", "[null]")]
    [InlineData("Tools", "null")]
    [InlineData("Tools", "[null]")]
    [InlineData("Tools", "[{\"Name\": \"no kind\"}]")]
    [InlineData("Tools", "[{\"kind\": \"blob\", \"Name\": \"B\", \"Connectivity\": \"eight\"}]")]
    [InlineData("Tools", "[{\"kind\": \"nosuchtool\", \"Name\": \"B\"}]")]
    [InlineData("Tools", "[{\"kind\": \"code\", \"Name\": \"C\", \"Format\": 64}]")]
    [InlineData("Tools", "[{\"kind\": \"code\", \"Name\": \"C\", \"Format\": \"NoSuchCode\"}]")]
    [InlineData("Tools", "[{\"kind\": \"code\", \"Name\": \"C\", \"Format\": \"QrCode\", \"ExpectedCont\": 2}]")]
    [InlineData("Tools", "[{\"kind\": \"code\", \"Name\": \"C\", \"Format\": \"QrCode\", \"Expected\": null}]")]
    [InlineData("Tools", "[{\"kind\": \"code\", \"Name\": \"C\", \"Format\": \"QrCode\", \"Gs1Values\": {\"01\": null}}]")]
    [InlineData("Tools", "[{\"kind\": \"code\", \"Name\": \"C\", \"Format\": \"QrCode\", \"Roi\": {\"X\": 1}}]")]
    [InlineData("Tools", "[{\"kind\": \"text\", \"Name\": \"T\", \"Lines\": [null]}]")]
    [InlineData("Tools", "[{\"kind\": \"text\", \"Name\": \"T\", \"Lines\": [{\"Template\": \"A\"}], \"Mode\": 1}]")]
    [InlineData("Tools", "[{\"kind\": \"text\", \"Name\": \"T\", \"Lines\": [{\"Template\": \"A\"}], \"MinQuality\": 1e39}]")]
    [InlineData("Tools", "[{\"kind\": \"locate\", \"Name\": \"L\", \"MinScore\": \"high\"}]")]
    [InlineData("Unknown", "1")]
    public void Wrong_types_unknown_fields_and_empty_entries_are_refused(string field, string json)
    {
        var job = Minimal();
        job[field] = JsonNode.Parse(json);
        Write("J", job);
        Refused("J");
    }

    [Fact]
    public void A_field_written_twice_is_refused()
    {
        Write("J", Minimal().ToJsonString().Replace("\"Name\":\"J\"", "\"Name\":\"J\",\"Number\":1,\"Number\":2"));
        Refused("J");
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Cameras")]
    public void Missing_fields_take_their_default_only_where_the_job_still_holds(string field)
    {
        var job = Minimal();
        job.Remove(field);
        Write("J", job);
        if (field == "Name") Refused("J");   // the file is J, the job has no name
        else Assert.Single(_store.Load("J")!.Cameras);
    }

    [Theory]
    [InlineData("Camera", "5")]
    [InlineData("Camera", "-1")]
    [InlineData("ExpectedCount", "-2")]
    [InlineData("TimeBudgetMs", "-100")]
    [InlineData("MinGrade", "7")]
    [InlineData("Roi", "{\"X\": 0, \"Y\": 0, \"Width\": 0, \"Height\": 10}")]
    [InlineData("Roi", "{\"X\": -5, \"Y\": 0, \"Width\": 10, \"Height\": 10}")]
    [InlineData("Format", "\"Itf\"")]
    public void Settings_no_setup_screen_makes_are_refused_with_the_tool_named(string field, string json)
    {
        var job = Minimal();
        job["Tools"]![0]![field] = JsonNode.Parse(json);
        Write("J", job);
        Assert.Contains("Code", Refused("J").Reason);
    }

    [Fact]
    public void A_camera_with_no_exposure_is_refused()
    {
        var job = Minimal();
        job["Cameras"]![0]!["ExposureUs"] = 0;
        Write("J", job);
        Assert.Contains("exposure", Refused("J").Reason);
    }

    [Fact]
    public void A_file_holding_another_job_is_refused()
    {
        Write("J", Minimal("K"));
        Assert.Contains("\"K\"", Refused("J").Reason);
    }

    [Theory]
    [InlineData("../J")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void A_name_that_is_not_a_job_file_is_refused(string name) =>
        Assert.Throws<JobLoadException>(() => _store.Load(name));

    [Fact]
    public void No_file_is_no_job() => Assert.Null(_store.Load("Nothing here"));

    // --- versions ---

    [Fact]
    public void A_job_saved_before_schema_versions_reads_with_a_note()
    {
        Install("Toilet cleaner 500ml");
        var job = _store.Load("Toilet cleaner 500ml")!;
        Assert.Null(job.SchemaVersion);
        Assert.Contains(job.LoadNotes, n => n.Contains("schema 0.0"));
        Assert.Equal(0, job.Number);
        Assert.True(job.Tools.OfType<CodeToolConfig>().Single().QuietZoneCheck);   // added later: its default
        var saved = _store.Save(job, "test");
        Assert.Equal((JobFile.CurrentSchema, 4), (saved.SchemaVersion, saved.Version));
        Assert.Empty(_store.Load("Toilet cleaner 500ml")!.LoadNotes);
    }

    [Fact]
    public void An_older_minor_reads_with_a_note()
    {
        Install("Floor cleaner 1L");
        Assert.Contains(_store.Load("Floor cleaner 1L")!.LoadNotes, n => n.Contains("schema 0.0"));
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("1.0")]
    [InlineData("3.7")]
    public void A_job_from_a_newer_version_is_refused(string version)
    {
        var job = Minimal();
        job["SchemaVersion"] = version;
        job["FieldOfTheFuture"] = true;   // the version is the reason given, not the field
        Write("J", job);
        var e = Refused("J");
        Assert.Contains("newer version", e.Reason);
        Assert.Contains(version, e.Reason);
    }

    [Theory]
    [InlineData("\"one\"")]
    [InlineData("\"1\"")]
    [InlineData("\"0.1.2\"")]
    [InlineData("\"-1.0\"")]
    [InlineData("0.1")]
    public void A_schema_version_that_is_not_a_version_is_refused(string json)
    {
        var job = Minimal();
        job["SchemaVersion"] = JsonNode.Parse(json);
        Write("J", job);
        Assert.Contains("schema version", Refused("J").Reason);
    }

    // --- references to patterns, fonts, models, images ---

    [Fact]
    public void A_missing_pattern_refuses_the_run_but_opens_for_teaching()
    {
        Install("Harpic 200ml two cameras");
        File.Delete(Path.Combine(_dir, "assets", "Harpic 200ml two cameras", "Find label.v4.bmp"));
        Assert.Contains("Find label", Refused("Harpic 200ml two cameras").Reason);
        Assert.Null(_store.TryLoad("Harpic 200ml two cameras"));
        var edit = _store.LoadForEdit("Harpic 200ml two cameras")!;
        Assert.Contains(edit.LoadProblems, p => p.Contains("pattern"));
    }

    [Fact]
    public void A_damaged_pattern_is_refused()
    {
        Install("Harpic 200ml two cameras");
        var f = Path.Combine(_dir, "assets", "Harpic 200ml two cameras", "Find label.v4.bmp");
        File.WriteAllBytes(f, File.ReadAllBytes(f)[..200]);
        Assert.Contains("damaged", Refused("Harpic 200ml two cameras").Reason);
    }

    [Theory]
    [InlineData("../../outside.bmp")]
    [InlineData("/etc/hosts")]
    public void A_pattern_outside_the_assets_folder_is_refused(string file)
    {
        var job = Minimal();
        job["Tools"] = new JsonArray(new JsonObject { ["kind"] = "locate", ["Name"] = "L", ["PatternFile"] = file });
        Write("J", job);
        Assert.Contains("outside", Refused("J").Reason);
    }

    [Fact]
    public void A_locate_tool_never_taught_is_refused_unless_switched_off()
    {
        var job = Minimal();
        job["Tools"] = new JsonArray(new JsonObject { ["kind"] = "locate", ["Name"] = "L" });
        Write("J", job);
        Refused("J");
        job["Tools"]![0]!["Enabled"] = false;
        Write("J", job);
        Assert.NotNull(_store.Load("J"));
    }

    [Fact]
    public void A_font_reader_without_a_taught_font_is_refused()
    {
        var job = Minimal();
        job["Tools"] = new JsonArray(new JsonObject
        {
            ["kind"] = "text", ["Name"] = "Date", ["Mode"] = "Read", ["Reader"] = "Font",
            ["Lines"] = new JsonArray(new JsonObject { ["Entry"] = "Format", ["Template"] = "EXP {EXP:MM/YY}" }),
        });
        Write("J", job);
        Assert.Contains("font", Refused("J").Reason);
    }

    [Fact]
    public void A_rendered_font_not_drawn_as_saved_is_refused_not_redrawn_at_load()
    {
        var job = Minimal();
        job["Tools"] = new JsonArray(new JsonObject
        {
            ["kind"] = "text", ["Name"] = "Price", ["Reader"] = "Font", ["Font"] = "VSFONT1 damaged", ["CharHeightPx"] = 30,
            ["Rendered"] = new JsonObject { ["Family"] = "Arial", ["RenderedAs"] = "something else" },
            ["Lines"] = new JsonArray(new JsonObject { ["Template"] = "MRP" }),
        });
        Write("J", job);
        Assert.Contains("Price: The system font changed", Refused("J").Reason);
        Assert.Contains("VSFONT1 damaged", File.ReadAllText(Path.Combine(_dir, "J.json")));
    }

    [Fact]
    public void A_missing_ai_text_model_is_refused_on_a_pc_with_a_models_folder()
    {
        var models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(models);
        var store = new JobStore(_dir, new ModelPaths(models));
        var job = Minimal();
        job["Tools"] = new JsonArray(new JsonObject
        {
            ["kind"] = "text", ["Name"] = "Date", ["Mode"] = "Read",
            ["Lines"] = new JsonArray(new JsonObject { ["Entry"] = "Format", ["Template"] = "EXP {EXP:MM/YY}" }),
        });
        Write("J", job);
        Assert.Contains("AI text model", Assert.Throws<JobLoadException>(() => store.Load("J")).Reason);

        File.WriteAllText(Path.Combine(models, "ocrnet.onnx"), "model");
        File.WriteAllLines(Path.Combine(models, "ocr_charset.txt"), "EXP 0123456789/".Select(c => c.ToString()));
        Assert.NotNull(store.Load("J"));
    }

    [Fact]
    public void A_missing_teach_image_is_a_note_not_a_refusal()
    {
        var job = Minimal();
        job["Cameras"]![0]!["ReferenceImage"] = Path.Combine(_dir, "gone.bmp");
        Write("J", job);
        Assert.Contains(_store.Load("J")!.LoadNotes, n => n.Contains("teach image"));
    }

    // --- huge files ---

    [Fact]
    public void A_file_over_4_mb_is_refused_before_it_is_read()
    {
        var job = Minimal();
        job["Description"] = new string('x', JobFile.MaxBytes);
        Write("J", job);
        Assert.Contains("4 MB", Refused("J").Reason);
    }

    [Fact]
    public void Too_many_tools_or_lines_are_refused()
    {
        var job = Minimal();
        var tools = new JsonArray();
        for (int i = 0; i <= JobFile.MaxTools; i++) tools.Add(new JsonObject { ["kind"] = "code", ["Name"] = $"C{i}", ["Format"] = "QrCode" });
        job["Tools"] = tools;
        Write("J", job);
        Assert.Contains("at most", Refused("J").Reason);

        var lines = new JsonArray();
        for (int i = 0; i <= JobFile.MaxLinesPerTool; i++) lines.Add(new JsonObject { ["Template"] = $"L{i}" });
        job["Tools"] = new JsonArray(new JsonObject { ["kind"] = "text", ["Name"] = "T", ["Lines"] = lines });
        Write("J", job);
        Assert.Contains("at most", Refused("J").Reason);
    }

    [Fact]
    public void Deep_nesting_is_refused()
    {
        var job = Minimal();
        job["Description"] = null;
        Write("J", job.ToJsonString().Replace("\"Description\":null", "\"Description\":" + new string('[', 200) + new string(']', 200)));
        Refused("J");
    }

    // --- the store around a refused job ---

    [Fact]
    public void A_damaged_job_can_be_saved_over_and_keeps_counting_versions()
    {
        Install("Floor cleaner 1L");
        var job = _store.Load("Floor cleaner 1L")!;
        _store.Save(job, "a");
        _store.Save(job, "b");                                     // v2, v3
        Write("Floor cleaner 1L", "{ \"Name\": \"Floor clea");      // cut short
        Refused("Floor cleaner 1L");
        Assert.Equal(4, _store.Save(job, "c").Version);
        Assert.Equal("c", _store.Load("Floor cleaner 1L")!.UpdatedBy);
    }

    [Fact]
    public void The_plc_never_selects_a_job_that_cannot_load_and_a_damaged_sibling_does_not_stop_it()
    {
        Install("Floor cleaner 1L");                               // number 12
        var bad = Minimal("Bad");
        bad["Number"] = 13;
        bad["Tools"]![0]!["Camera"] = 4;
        Write("Bad", bad);
        Write("Aaa damaged", "{");                                 // listed first
        Assert.Equal("Floor cleaner 1L", _store.FindByNumber(12));
        Assert.Null(_store.FindByNumber(13));
        Assert.Null(_store.FindByNumber(0));
    }

    [Fact]
    public void A_refusal_is_an_invalid_operation_so_job_changes_refuse_it()
    {
        Write("J", "{");
        Assert.IsAssignableFrom<InvalidOperationException>(Refused("J"));
    }

    // --- fuzz: mutated copies of every sample ---

    [Theory]
    [MemberData(nameof(Samples))]
    public void Mutated_copies_are_refused_or_load_whole(string name)
    {
        var original = JsonNode.Parse(Install(name))!;
        var rnd = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 20261031);
        var bytes = Encoding.UTF8.GetBytes(original.ToJsonString());
        int refused = 0, loaded = 0;
        for (int i = 0; i < 400; i++)
        {
            byte[] mutated;
            if (i % 4 == 3)
            {
                // byte-level: flip, drop or insert a byte
                mutated = bytes.ToArray();
                int at = rnd.Next(mutated.Length);
                switch (rnd.Next(3))
                {
                    case 0: mutated[at] ^= (byte)(1 << rnd.Next(8)); break;
                    case 1: mutated = [.. mutated[..at], .. mutated[(at + 1)..]]; break;
                    default: mutated = [.. mutated[..at], (byte)rnd.Next(256), .. mutated[at..]]; break;
                }
            }
            else
            {
                var node = original.DeepClone();
                Mutate(node, rnd);
                mutated = Encoding.UTF8.GetBytes(node.ToJsonString());
            }
            File.WriteAllBytes(Path.Combine(_dir, name + ".json"), mutated);
            Job? job;
            try { job = _store.Load(name); }
            catch (JobLoadException) { refused++; continue; }
            // anything else thrown fails the test: a mutation is refused with a reason or loads whole
            Assert.NotNull(job);
            loaded++;
            Assert.Empty(job!.LoadProblems);
            Assert.Null(JobFile.Structure(job));
            job.Validate(drawFonts: false);
            var again = JobFile.Parse(name, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job, JobStore.Json)));
            Assert.Equal(JsonSerializer.Serialize(job, JobStore.Json), JsonSerializer.Serialize(again, JobStore.Json));
        }
        Assert.True(refused > 100, $"{refused} refused");
        Assert.True(loaded > 10, $"{loaded} loaded");
    }

    private static readonly JsonNode?[] Odd =
    [
        null, JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create(int.MaxValue), JsonValue.Create(-1e9), JsonValue.Create(1e39),
        JsonValue.Create(0.5), JsonValue.Create(""), JsonValue.Create("x"), JsonValue.Create(true), new JsonArray(), new JsonObject(),
    ];

    // one change at a random place: a field removed, its value replaced, a field added, a list entry removed or doubled
    private static void Mutate(JsonNode root, Random rnd)
    {
        var all = new List<JsonNode>();
        void Collect(JsonNode? n)
        {
            if (n is null) return;
            all.Add(n);
            if (n is JsonObject o) foreach (var (_, v) in o) Collect(v);
            if (n is JsonArray a) foreach (var v in a) Collect(v);
        }
        Collect(root);
        var target = all[rnd.Next(all.Count)];
        switch (target)
        {
            case JsonObject o when o.Count > 0:
                var key = o.ElementAt(rnd.Next(o.Count)).Key;
                switch (rnd.Next(3))
                {
                    case 0: o.Remove(key); break;
                    case 1: o[key] = Odd[rnd.Next(Odd.Length)]?.DeepClone(); break;
                    default: o["Extra" + rnd.Next(100)] = 1; break;
                }
                break;
            case JsonArray a when a.Count > 0:
                int i = rnd.Next(a.Count);
                if (rnd.Next(2) == 0) a.RemoveAt(i);
                else a.Add(a[i]?.DeepClone());
                break;
            default:
                if (target.Parent is JsonObject parent)
                    parent[target.GetPropertyName()] = Odd[rnd.Next(Odd.Length)]?.DeepClone();
                break;
        }
    }
}
