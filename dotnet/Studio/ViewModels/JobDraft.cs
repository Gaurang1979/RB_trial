using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionSystem.Studio.ViewModels;

/// <summary>
/// A job as the designer edits it: the job file's JSON (JOB-FILE-FORMAT.md; VisionCore's Job with
/// PascalCase names, enums by name, tools told apart by "kind"), changed in place. Settings this
/// app does not show are kept as they are, so a save never drops what another tool set.
/// </summary>
public sealed class JobDraft
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public JobDraft(JsonObject root)
    {
        Root = root;
        if (Root["Cameras"] is not JsonArray) Root["Cameras"] = new JsonArray();
        if (Root["Tools"] is not JsonArray) Root["Tools"] = new JsonArray();
        Cameras = new ObservableCollection<CameraDraft>(CameraArray.OfType<JsonObject>().Select(c => new CameraDraft(c)));
        Tools = new ObservableCollection<ToolDraft>(ToolArray.OfType<JsonObject>().Select(t => new ToolDraft(t)));
    }

    public static JobDraft Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The job file is not a JSON object."));

    /// <summary>A new job with the cameras asked for and no tools.</summary>
    public static JobDraft New(string name, int cameras)
    {
        var root = new JsonObject { ["Name"] = name, ["Number"] = 0, ["Description"] = "", ["Cameras"] = new JsonArray(), ["Tools"] = new JsonArray() };
        var job = new JobDraft(root);
        for (var i = 0; i < cameras; i++) job.AddCamera();
        return job;
    }

    public JsonObject Root { get; }
    private JsonArray CameraArray => (JsonArray)Root["Cameras"]!;
    private JsonArray ToolArray => (JsonArray)Root["Tools"]!;
    public ObservableCollection<CameraDraft> Cameras { get; }
    public ObservableCollection<ToolDraft> Tools { get; }

    public string Name
    {
        get => Json.Str(Root, "Name");
        set => Root["Name"] = value.Trim();
    }

    /// <summary>The number the PLC selects the job by, 1 to 999; 0 = none.</summary>
    public int Number
    {
        get => Json.Int(Root, "Number");
        set => Root["Number"] = value;
    }

    public int Version => Json.Int(Root, "Version");

    public string ToJson() => Root.ToJsonString(Indented);

    /// <summary>Cameras a job may have: the runtime's limit (VisionCore Job.MaxCameras).</summary>
    public const int MaxCameras = 4;

    public CameraDraft? AddCamera()
    {
        if (Cameras.Count >= MaxCameras) return null;
        var c = new JsonObject { ["Name"] = $"Camera {Cameras.Count + 1}", ["Driver"] = "", ["Serial"] = "", ["ExposureUs"] = 2000, ["GainDb"] = 0 };
        CameraArray.Add(c);
        var draft = new CameraDraft(c);
        Cameras.Add(draft);
        return draft;
    }

    public IEnumerable<ToolDraft> ToolsOf(int camera) => Tools.Where(t => t.Camera == camera);

    /// <summary>A new tool on a camera, named "Read code 1", "Check text 2", ... with the settings a new tool has
    /// in VisionCore; the type discriminator comes first, as the job file needs.</summary>
    public ToolDraft AddTool(ToolKind kind, int camera)
    {
        var baseName = kind switch { ToolKind.ReadCode => "Read code", ToolKind.ReadText => "Read text", ToolKind.Blob => "Blob", _ => "Check text" };
        var n = 1;
        while (Tools.Any(t => string.Equals(t.Name, $"{baseName} {n}", StringComparison.OrdinalIgnoreCase))) n++;
        var t = kind switch
        {
            ToolKind.ReadCode => new JsonObject { ["kind"] = "code", ["Name"] = $"{baseName} {n}", ["Camera"] = camera, ["Enabled"] = true, ["Format"] = "QrCode", ["ExpectedCount"] = 1, ["Expected"] = "" },
            // a new Blob tool asks for a spot (BLB-OUT-03): one with no rule would pass every picture
            ToolKind.Blob => new JsonObject { ["kind"] = "blob", ["Name"] = $"{baseName} {n}", ["Camera"] = camera, ["Enabled"] = true,
                ["Threshold"] = "Auto", ["Polarity"] = "DarkOnLight", ["MinArea"] = 10, ["Presence"] = "Present" },
            _ => new JsonObject { ["kind"] = "text", ["Name"] = $"{baseName} {n}", ["Camera"] = camera, ["Enabled"] = true,
                ["Mode"] = kind == ToolKind.ReadText ? "Read" : "Check", ["Lines"] = new JsonArray(), ["Print"] = "Auto", ["Polarity"] = "Auto" },
        };
        ToolArray.Add(t);
        var draft = new ToolDraft(t);
        Tools.Add(draft);
        return draft;
    }

    /// <summary>A tool from its job-file JSON (an auto find suggestion the engineer accepted) on a camera, under
    /// its own name, or that name with a number when another tool has it.</summary>
    public ToolDraft AddToolJson(string json, int camera)
    {
        var t = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The tool is not a JSON object.");
        t["Camera"] = camera;
        var baseName = Json.Str(t, "Name") is { Length: > 0 } given ? given : "Tool";
        var name = baseName;
        for (var n = 2; Tools.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{baseName} {n}";
        t["Name"] = name;
        ToolArray.Add(t);
        var draft = new ToolDraft(t);
        Tools.Add(draft);
        return draft;
    }

    public void RemoveTool(ToolDraft tool)
    {
        ToolArray.Remove(tool.Node);
        Tools.Remove(tool);
    }
}

public enum ToolKind { ReadCode, ReadText, CheckText, Blob }

/// <summary>Reads and writes of JSON values that tolerate a missing or differently typed value.</summary>
internal static class Json
{
    public static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : "";

    public static int Int(JsonObject o, string key, int fallback = 0) => (int)Math.Round(Num(o, key, fallback));

    public static double Num(JsonObject o, string key, double fallback = 0)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<float>(out var f)) return f;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
    }

    public static bool Bool(JsonObject o, string key, bool fallback = false) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
}

/// <summary>One camera of the job: its name, its stored pictures (for trying and the simulator) and its serial.</summary>
public sealed class CameraDraft(JsonObject node) : ObservableObject
{
    public JsonObject Node { get; } = node;

    public string Name
    {
        get => Json.Str(Node, "Name");
        set { Node["Name"] = value; OnPropertyChanged(); }
    }

    public string TestFolder
    {
        get => Json.Str(Node, "TestFolder");
        set { if (string.IsNullOrWhiteSpace(value)) Node.Remove("TestFolder"); else Node["TestFolder"] = value.Trim(); OnPropertyChanged(); }
    }

    public string Serial
    {
        get => Json.Str(Node, "Serial");
        set { Node["Serial"] = value.Trim(); OnPropertyChanged(); }
    }

    /// <summary>Focal length of the camera's lens in mm; null = not known (the pixel check then gives the lens as a factor).</summary>
    public decimal? LensFocalMm
    {
        get => Node["LensFocalMm"] is JsonValue ? (decimal)Json.Num(Node, "LensFocalMm") : null;
        set { if (value is > 0) Node["LensFocalMm"] = (double)value.Value; else Node.Remove("LensFocalMm"); OnPropertyChanged(); }
    }

    /// <summary>The camera's real size, mm per pixel at the part's plane, from its calibration in the job file
    /// (Cameras[].Calibration); null without one: sizes are in px only (BLB-OUT-05).</summary>
    public double? MmPerPx => Node["Calibration"] is JsonObject c && Json.Num(c, "MmPerPx") is var mm && double.IsFinite(mm) && mm > 0 ? mm : null;
}

/// <summary>One line of a Read text or Check text tool.</summary>
public sealed class LineDraft(JsonObject node) : ObservableObject
{
    /// <summary>Where a Check text line's expected text comes from (VisionCore ExpectedSource).</summary>
    public static readonly IReadOnlyList<string> Sources = ["Job", "Plc", "Tool"];
    /// <summary>The PLC fields a line can take its text from (VisionCore PlcValues.Fields).</summary>
    public static readonly IReadOnlyList<string> PlcFields = ["PartId", "ProductId", "UserIn1", "UserIn2", "UserIn3", "UserIn4", "Text"];

    public JsonObject Node { get; } = node;

    /// <summary>Check text: the text with {FIELD} parts for the batch ("B.NO {BATCH}"); Read text: the format
    /// ("EXP {EXP:MM/YY}").</summary>
    public string Template
    {
        get => Json.Str(Node, "Template");
        set { Node["Template"] = value; OnPropertyChanged(); }
    }

    public string Source
    {
        get => Json.Str(Node, "Source") is { Length: > 0 } s ? s : "Job";
        set { Node["Source"] = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsSourceName)); OnPropertyChanged(nameof(IsPlcText)); }
    }

    public string SourceName
    {
        get => Json.Str(Node, "SourceName");
        set
        {
            Node["SourceName"] = value.Trim();
            // the PLC's text block (PLC-INTERFACE.md 3.7): the line needs the place of its string
            if (IsPlcText && Node["PlcText"] is not JsonObject)
                Node["PlcText"] = new JsonObject { ["Register"] = 501, ["Length"] = 40, ["Encoding"] = "Ascii", ["ByteOrder"] = "HighFirst" };
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlcText));
            OnPropertyChanged(nameof(PlcRegister));
            OnPropertyChanged(nameof(PlcLength));
        }
    }

    public bool NeedsSourceName => Source != "Job";

    /// <summary>The line's text comes from the PLC's text block (registers 501-627).</summary>
    public bool IsPlcText => Source == "Plc" && string.Equals(SourceName, "Text", StringComparison.OrdinalIgnoreCase);

    /// <summary>First register of the string in the PLC's text block.</summary>
    public int PlcRegister
    {
        get => Node["PlcText"] is JsonObject p ? Json.Int(p, "Register", 501) : 501;
        set { if (Node["PlcText"] is JsonObject p) { p["Register"] = value; OnPropertyChanged(); } }
    }

    /// <summary>Most characters of the string.</summary>
    public int PlcLength
    {
        get => Node["PlcText"] is JsonObject p ? Json.Int(p, "Length", 40) : 40;
        set { if (Node["PlcText"] is JsonObject p) { p["Length"] = value; OnPropertyChanged(); } }
    }
}

/// <summary>
/// One tool: Read code (kind "code") or Read text / Check text (kind "text"), with the settings the
/// designer shows (UI-UX-SPEC.md 4.8, tool spec 5-7). Every setter writes the JSON at once.
/// </summary>
public sealed partial class ToolDraft : ObservableObject
{
    /// <summary>The code types offered, as VisionCore names them; "Gs1_128" is Code 128 (GS1-128 too).</summary>
    public static readonly IReadOnlyList<string> CodeFormats = ["QrCode", "DataMatrix", "MicroQr", "Gs1_128", "Ean13", "Ean8", "UpcA", "Itf", "Code39", "Code93", "Codabar", "DataBar"];
    public static readonly IReadOnlyList<string> CheckDigits = ["AsRead", "Verify", "Strip"];
    /// <summary>Lowest print grade: none, then A (4) down to D (1).</summary>
    public static readonly IReadOnlyList<string> Grades = ["None", "A", "B", "C", "D"];
    public static readonly IReadOnlyList<string> PrintTypes = ["Auto", "Solid", "DotMatrix", "Inkjet"];
    public static readonly IReadOnlyList<string> Polarities = ["Auto", "DarkOnLight", "LightOnDark"];

    public ToolDraft(JsonObject node)
    {
        Node = node;
        if (IsText)
        {
            if (Node["Lines"] is not JsonArray) Node["Lines"] = new JsonArray();
            Lines = new ObservableCollection<LineDraft>(((JsonArray)Node["Lines"]!).OfType<JsonObject>().Select(l => new LineDraft(l)));
        }
    }

    public JsonObject Node { get; }
    public string Kind => Json.Str(Node, "kind");
    public bool IsCode => Kind == "code";
    public bool IsText => Kind == "text";
    public bool IsCheck => IsText && Mode == "Check";
    public bool IsRead => IsText && Mode == "Read";
    public ObservableCollection<LineDraft> Lines { get; } = [];

    public bool IsBlob => Kind == "blob";

    public string KindText => IsCode ? "Read code" : IsCheck ? "Check text" : IsText ? "Read text" : IsBlob ? "Blob" : Kind;

    public string Name
    {
        get => Json.Str(Node, "Name");
        set { Node["Name"] = value.Trim(); OnPropertyChanged(); }
    }

    public int Camera => Json.Int(Node, "Camera");

    public bool Enabled
    {
        get => Json.Bool(Node, "Enabled", true);
        set { Node["Enabled"] = value; OnPropertyChanged(); }
    }

    // --- area ---------------------------------------------------------------------------------

    /// <summary>The search area in full-image pixels; null = the whole picture.</summary>
    public (int X, int Y, int Width, int Height)? Roi
    {
        get => Node["Roi"] is JsonObject r ? (Json.Int(r, "X"), Json.Int(r, "Y"), Json.Int(r, "Width"), Json.Int(r, "Height")) : null;
        set
        {
            if (value is { } v && v.Width > 0 && v.Height > 0)
                Node["Roi"] = new JsonObject { ["X"] = v.X, ["Y"] = v.Y, ["Width"] = v.Width, ["Height"] = v.Height };
            else Node.Remove("Roi");
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoiText));
        }
    }

    public string RoiText => Roi is { } r ? $"{r.X}, {r.Y}  {r.Width} × {r.Height} px" : "Whole picture";

    // --- Read code ----------------------------------------------------------------------------

    public string Format
    {
        get => Json.Str(Node, "Format") is { Length: > 0 } f ? f : "Ean13";
        set { Node["Format"] = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLinear)); }
    }

    /// <summary>1D types: the length lock and check digit apply.</summary>
    public bool IsLinear => Format is not ("QrCode" or "DataMatrix" or "MicroQr" or "Pdf417" or "Aztec" or "DotCode");

    public int ExpectedCount
    {
        get => Json.Int(Node, "ExpectedCount", 1);
        set { Node["ExpectedCount"] = Math.Max(0, value); OnPropertyChanged(); }
    }

    /// <summary>Text the code must hold, with {FIELD} parts from the batch; empty = any content.</summary>
    public string Expected
    {
        get => Json.Str(Node, "Expected");
        set { Node["Expected"] = value; OnPropertyChanged(); }
    }

    public int MinLength
    {
        get => Json.Int(Node, "MinLength");
        set { Node["MinLength"] = Math.Max(0, value); OnPropertyChanged(); }
    }

    public int MaxLength
    {
        get => Json.Int(Node, "MaxLength");
        set { Node["MaxLength"] = Math.Max(0, value); OnPropertyChanged(); }
    }

    public string CheckDigit
    {
        get => Json.Str(Node, "CheckDigit") is { Length: > 0 } c ? c : "AsRead";
        set { Node["CheckDigit"] = value; OnPropertyChanged(); }
    }

    /// <summary>Check the GS1 data (tool spec 6.4): the AIs the code must carry, comma separated.</summary>
    public bool Gs1Check
    {
        get => Node["Gs1"] is JsonObject;
        set
        {
            if (value == Gs1Check) return;
            if (value) Node["Gs1"] = new JsonObject { ["Required"] = new JsonArray(), ["ExpiryNotPast"] = true };
            else Node.Remove("Gs1");
            OnPropertyChanged();
            OnPropertyChanged(nameof(Gs1Required));
        }
    }

    public string Gs1Required
    {
        get => Node["Gs1"] is JsonObject g && g["Required"] is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : "";
        set
        {
            if (Node["Gs1"] is not JsonObject g) return;
            var ais = value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('(', ')')).Where(s => s.Length > 0);
            g["Required"] = new JsonArray(ais.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            OnPropertyChanged();
        }
    }

    /// <summary>Fills the required AIs with the pharma set (Req 17.13 GS1-PH-01): GTIN, expiry, batch, serial.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void UseGs1Pharma() => Gs1Required = "01, 17, 10, 21";

    /// <summary>"None", or the lowest grade a code may have, "A" to "D".</summary>
    public string MinGrade
    {
        get => Json.Int(Node, "MinGrade", -1) switch { 4 => "A", 3 => "B", 2 => "C", 1 => "D", _ => "None" };
        set { Node["MinGrade"] = value switch { "A" => 4, "B" => 3, "C" => 2, "D" => 1, _ => -1 }; OnPropertyChanged(); }
    }

    /// <summary>The lowest grade as a number, 4 = A .. 1 = D; -1 when the code is not graded.</summary>
    public int GradeLimit => Json.Int(Node, "MinGrade", -1);

    public int GradeEvery
    {
        get => Json.Int(Node, "GradeEvery", 1);
        set { Node["GradeEvery"] = Math.Max(1, value); OnPropertyChanged(); }
    }

    // --- Read text / Check text ---------------------------------------------------------------

    public string Mode
    {
        get => Json.Str(Node, "Mode") is { Length: > 0 } m ? m : "Check";
        set { Node["Mode"] = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCheck)); OnPropertyChanged(nameof(IsRead)); OnPropertyChanged(nameof(KindText)); }
    }

    public string Print
    {
        get => Json.Str(Node, "Print") is { Length: > 0 } p ? p : "Auto";
        set { Node["Print"] = value; OnPropertyChanged(); }
    }

    /// <summary>Laser print on a dark pack is LightOnDark.</summary>
    public string Polarity
    {
        get => Json.Str(Node, "Polarity") is { Length: > 0 } p ? p : "Auto";
        set { Node["Polarity"] = value; OnPropertyChanged(); }
    }

    public int CharHeightPx
    {
        get => Json.Int(Node, "CharHeightPx");
        set { Node["CharHeightPx"] = Math.Max(0, value); OnPropertyChanged(); }
    }

    public double MinQuality
    {
        get => Json.Num(Node, "MinQuality", 60);
        set { Node["MinQuality"] = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    public LineDraft AddLine(string template = "")
    {
        var l = new JsonObject
        {
            ["Entry"] = IsRead ? "Format" : "Template",
            ["Source"] = "Job",
            ["SourceName"] = "",
            ["Template"] = template,
        };
        ((JsonArray)Node["Lines"]!).Add(l);
        var draft = new LineDraft(l);
        Lines.Add(draft);
        return draft;
    }

    public void RemoveLine(LineDraft line)
    {
        ((JsonArray)Node["Lines"]!).Remove(line.Node);
        Lines.Remove(line);
    }

    // --- Blob (Count / spot, tool spec 4; VisionCore BlobToolConfig) ---------------------------

    public static readonly IReadOnlyList<string> BlobThresholds = ["Auto", "Fixed", "Band", "Local"];
    public static readonly IReadOnlyList<string> BlobPolarities = ["DarkOnLight", "LightOnDark"];
    public static readonly IReadOnlyList<string> BlobMorphologies = ["None", "Open3", "Close3"];
    public static readonly IReadOnlyList<string> BlobSorts = ["None", "Area", "X", "Y", "Perimeter", "Circularity", "Elongation", "MeanGrey"];
    public static readonly IReadOnlyList<string> BlobPresences = ["Any", "Present", "Absent"];
    public static readonly IReadOnlyList<int> Connectivities = [8, 4];

    /// <summary>A setting with no value (no limit): null, and the key is left out of the job file.</summary>
    private decimal? Opt(string key) => Node[key] is JsonValue ? (decimal)Json.Num(Node, key) : null;

    private void SetOpt(string key, decimal? value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (value is { } v) Node[key] = (double)v; else Node.Remove(key);
        OnPropertyChanged(property);
    }

    private string Named(string key, string fallback) => Json.Str(Node, key) is { Length: > 0 } v ? v : fallback;

    private void SetNum(string key, double value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        Node[key] = value;
        OnPropertyChanged(property);
    }

    public string Threshold
    {
        get => Named("Threshold", "Auto");
        set { Node["Threshold"] = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLevel)); OnPropertyChanged(nameof(IsBand)); OnPropertyChanged(nameof(IsLocal)); OnPropertyChanged(nameof(IsAutoThreshold)); }
    }

    public bool HasLevel => Threshold is "Fixed" or "Band";
    public bool IsBand => Threshold == "Band";
    public bool IsLocal => Threshold == "Local";
    public bool IsAutoThreshold => Threshold == "Auto";

    /// <summary>The Blob tool's polarity (no Auto, unlike text): the same job-file key as a text tool's.</summary>
    public string BlobPolarity
    {
        get => Named("Polarity", "DarkOnLight");
        set { Node["Polarity"] = value; OnPropertyChanged(); }
    }

    public int Level { get => Json.Int(Node, "Level", 128); set => SetNum("Level", Math.Clamp(value, 0, 255)); }
    public int LevelHigh { get => Json.Int(Node, "LevelHigh", 255); set => SetNum("LevelHigh", Math.Clamp(value, 0, 255)); }
    public int LocalWindow { get => Json.Int(Node, "LocalWindow", 31); set => SetNum("LocalWindow", Math.Max(3, value)); }
    public int LocalOffset { get => Json.Int(Node, "LocalOffset", 10); set => SetNum("LocalOffset", value); }
    public int MinContrast { get => Json.Int(Node, "MinContrast", 15); set => SetNum("MinContrast", Math.Clamp(value, 0, 255)); }
    public int Connectivity { get => Json.Int(Node, "Connectivity", 8); set => SetNum("Connectivity", value is 4 ? 4 : 8); }
    public bool FillHoles { get => Json.Bool(Node, "FillHoles"); set { Node["FillHoles"] = value; OnPropertyChanged(); } }
    public bool ExcludeEdge { get => Json.Bool(Node, "ExcludeEdge", true); set { Node["ExcludeEdge"] = value; OnPropertyChanged(); } }
    public string Morphology { get => Named("Morphology", "None"); set { Node["Morphology"] = value; OnPropertyChanged(); } }

    public double MinArea { get => Json.Num(Node, "MinArea", 10); set => SetNum("MinArea", Math.Max(1, value)); }
    /// <summary>0 = no upper limit (as for circularity and elongation).</summary>
    public double MaxArea { get => Json.Num(Node, "MaxArea"); set => SetNum("MaxArea", Math.Max(0, value)); }
    public double MinCircularity { get => Json.Num(Node, "MinCircularity"); set => SetNum("MinCircularity", Math.Clamp(value, 0, 1)); }
    public double MaxCircularity { get => Json.Num(Node, "MaxCircularity"); set => SetNum("MaxCircularity", Math.Clamp(value, 0, 1)); }
    public double MinElongation { get => Json.Num(Node, "MinElongation"); set => SetNum("MinElongation", Math.Max(0, value)); }
    public double MaxElongation { get => Json.Num(Node, "MaxElongation"); set => SetNum("MaxElongation", Math.Max(0, value)); }
    public string SortBy { get => Named("SortBy", "None"); set { Node["SortBy"] = value; OnPropertyChanged(); } }
    public bool SortDescending { get => Json.Bool(Node, "SortDescending"); set { Node["SortDescending"] = value; OnPropertyChanged(); } }
    /// <summary>Keep only the first N after sorting; 0 = all.</summary>
    public int KeepFirst { get => Json.Int(Node, "KeepFirst"); set => SetNum("KeepFirst", Math.Max(0, value)); }

    public string Presence { get => Named("Presence", "Any"); set { Node["Presence"] = value; OnPropertyChanged(); } }
    public decimal? MinCount { get => Opt("MinCount"); set => SetOpt("MinCount", value is { } v ? Math.Max(0, Math.Round(v)) : null); }
    public decimal? MaxCount { get => Opt("MaxCount"); set => SetOpt("MaxCount", value is { } v ? Math.Max(0, Math.Round(v)) : null); }
    public decimal? BlobAreaMin { get => Opt("BlobAreaMin"); set => SetOpt("BlobAreaMin", value); }
    public decimal? BlobAreaMax { get => Opt("BlobAreaMax"); set => SetOpt("BlobAreaMax", value); }
    public decimal? TotalAreaMin { get => Opt("TotalAreaMin"); set => SetOpt("TotalAreaMin", value); }
    public decimal? TotalAreaMax { get => Opt("TotalAreaMax"); set => SetOpt("TotalAreaMax", value); }

    public bool CheckOutline { get => Json.Bool(Node, "CheckOutline"); set { Node["CheckOutline"] = value; OnPropertyChanged(); } }
    /// <summary>Largest dent and largest deviation are in <see cref="OutlineUnit"/>; null = no limit.</summary>
    public decimal? MaxDentDepth { get => Opt("MaxDentDepth"); set => SetOpt("MaxDentDepth", value); }
    public decimal? MaxDeviation { get => Opt("MaxDeviation"); set => SetOpt("MaxDeviation", value); }
    public decimal? MinConvexity { get => Opt("MinConvexity"); set => SetOpt("MinConvexity", value is { } v ? Math.Clamp(v, 0, 1) : null); }
    /// <summary>Indents shallower than this (px) are not listed; always px, as the engine measures.</summary>
    public double MinDefectDepth { get => Json.Num(Node, "MinDefectDepth", 1); set => SetNum("MinDefectDepth", Math.Max(0, value)); }
    public double OutlineSmoothing { get => Json.Num(Node, "OutlineSmoothing", 1); set => SetNum("OutlineSmoothing", Math.Clamp(value, 0, 5)); }
    /// <summary>Grey level of the edge; null = halfway between the spot and its surroundings.</summary>
    public decimal? EdgeLevel { get => Opt("EdgeLevel"); set => SetOpt("EdgeLevel", value is { } v ? Math.Clamp(Math.Round(v), 0, 255) : null); }

    /// <summary>"Px" or "Mm" (job file `OutlineUnit`); change it with <see cref="ChangeOutlineUnit"/>, which keeps the limits' meaning.</summary>
    public string OutlineUnit => Named("OutlineUnit", "Px");
    public bool OutlineInMm => OutlineUnit == "Mm";
    public string OutlineUnitText => OutlineInMm ? "mm" : "px";

    public bool HasTaughtOutline => Node["TaughtOutline"] is JsonArray { Count: >= 6 };
    public int TaughtPoints => Node["TaughtOutline"] is JsonArray a ? a.Count / 2 : 0;

    /// <summary>The outline unit changed (BLB-OUT-05): the largest dent and largest deviation already set are converted
    /// with the camera's mm per pixel, so a limit never changes its meaning. False (nothing changed) without a calibration,
    /// unless no limit is set and px is asked.</summary>
    public bool ChangeOutlineUnit(string unit, double? mmPerPx)
    {
        if (unit == OutlineUnit) return true;
        // without a calibration nothing converts: mm is refused, and so is px while a limit in mm is set (dropping it
        // would loosen the check)
        if (mmPerPx is not > 0 && (unit == "Mm" || MaxDentDepth is not null || MaxDeviation is not null)) return false;
        decimal? Convert(decimal? v) => v is not { } x ? null
            : unit == "Mm" ? Math.Round(x * (decimal)mmPerPx!.Value, 4) : Math.Round(x / (decimal)mmPerPx!.Value, 3);
        var dent = Convert(MaxDentDepth);
        var deviation = Convert(MaxDeviation);
        Node["OutlineUnit"] = unit;
        MaxDentDepth = dent;
        MaxDeviation = deviation;
        OnPropertyChanged(nameof(OutlineUnit));
        OnPropertyChanged(nameof(OutlineInMm));
        OnPropertyChanged(nameof(OutlineUnitText));
        return true;
    }
}
