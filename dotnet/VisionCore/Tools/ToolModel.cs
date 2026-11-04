using System.Text.Json.Serialization;

namespace VisionCore;

/// <summary>A rectangle in image pixels.</summary>
public sealed record RoiBox(int X, int Y, int Width, int Height);

/// <summary>An 8-bit grey image. Pixels are row by row with <see cref="Stride"/> bytes per row.</summary>
public sealed class GrayImage
{
    public GrayImage(byte[] pixels, int width, int height, int? stride = null)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride ?? width;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    /// <summary>When the image was taken (file name time for stored images); drives {TIME} / {DATE} checks.</summary>
    public DateTime? TakenAt { get; init; }
    public string? Name { get; init; }

    /// <summary>Reads an image file by its content, not its name: BMP, or PNG / JPEG (camera tools often
    /// save PNG under a .bmp name). Colour becomes grey as for BMP, the mean of R, G and B.</summary>
    public static GrayImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var (px, w, h) = data is [(byte)'B', (byte)'M', ..] ? BarcodeReading.BmpImage.DecodeGray(data) : DecodeOther(data);
        return new GrayImage(px, w, h) { Name = Path.GetFileName(path), TakenAt = TimeFromName(Path.GetFileName(path)) };
    }

    static (byte[] Pixels, int Width, int Height) DecodeOther(byte[] data)
    {
        using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(data))
            ?? throw new InvalidDataException("not an image (BMP, PNG or JPEG)");
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        var rgba = new byte[info.BytesSize];
        unsafe
        {
            fixed (byte* p = rgba)
                if (codec.GetPixels(info, (IntPtr)p) != SkiaSharp.SKCodecResult.Success)
                    throw new InvalidDataException("the image could not be decoded");
        }
        var gray = new byte[info.Width * info.Height];
        for (int i = 0; i < gray.Length; i++)
            gray[i] = (byte)((rgba[4 * i] + rgba[4 * i + 1] + rgba[4 * i + 2]) / 3);
        return (gray, info.Width, info.Height);
    }

    /// <summary>Camera file names such as 20261023_154234728.bmp -> 2026-10-23 15:42:34.</summary>
    public static DateTime? TimeFromName(string name)
    {
        if (name.Length >= 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t))
            return t;
        return null;
    }
}

public enum GraphicKind { Ok, Fail, Info }

/// <summary>An outline drawn over the image: a found code, a text line, one character.</summary>
public sealed record Graphic(PointF[] Points, GraphicKind Kind, string? Label = null);

/// <summary>What one tool found on one image.</summary>
public sealed record ToolResult(
    string ToolName,
    bool Pass,
    string Summary,
    IReadOnlyList<string> Details,
    double TimeMs,
    IReadOnlyList<Graphic> Graphics)
{
    /// <summary>Plain-language reason when the tool failed (key for translation + English text).</summary>
    public string? Explain { get; init; }
    /// <summary>Per-line text results (OCR / OCV tools only).</summary>
    public IReadOnlyList<OcrLine>? Lines { get; init; }
    /// <summary>Measured values kept with the part's result and sent to the plant server (for trends), by name,
    /// e.g. "grade" and "grade.modulation" of a Read code tool; empty when the tool measured nothing this time.</summary>
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();
    /// <summary>Each code read, with its grade when grading is on (Read code tool only).</summary>
    public IReadOnlyList<CodeRead>? Codes { get; init; }
    /// <summary>Check text: PrintQuality (tool spec 7.3), the lowest character score 0..100 over every expected line
    /// (0 when a line is not found or a run could not check); null for other tools and Read text.</summary>
    public float? PrintQuality { get; init; }
    /// <summary>Find the part: how well the taught pattern matched, 0..1, found or not; null for other tools.</summary>
    public float? MatchScore { get; init; }
    /// <summary>Count / spot: the blobs counted, in the tool's sort order; null for other tools.</summary>
    public IReadOnlyList<BlobInfo>? Blobs { get; init; }
    /// <summary>A Read text tool set to report only: Pass is always true and the result never decides the part
    /// (a setup error or a tool error still fails it). Show it as information, not as OK.</summary>
    public bool ReportOnly { get; init; }
}

/// <summary>Values the PLC sent with the trigger, for tools whose expected text comes from the PLC.</summary>
public sealed record PlcValues(string PartId, uint ProductId)
{
    /// <summary>The field that names a string in the text block (PLC-INTERFACE.md section 3.7); where it lies is
    /// the line's <see cref="TextLineSpec.PlcText"/>.</summary>
    public const string TextField = "Text";

    /// <summary>The PLC fields a tool can take its expected text from.</summary>
    public static readonly IReadOnlyList<string> Fields = new[] { "PartId", "ProductId", "UserIn1", "UserIn2", "UserIn3", "UserIn4", TextField };

    /// <summary>UserIn1..4 at the trigger (empty when the PLC sent none).</summary>
    public IReadOnlyList<int> UserIn { get; init; } = Array.Empty<int>();
    /// <summary>The text block taken with the trigger; null when the PLC has none.</summary>
    public Plc.PlcText? Text { get; init; }

    /// <summary>The values of a PLC trigger.</summary>
    public static PlcValues Of(Plc.PlcTrigger t) => new(t.PartId, t.ProductId) { UserIn = t.UserIn, Text = t.Text };

    /// <summary>The value of a field; null when the PLC sent none (an empty part ID, product 0, user value 0).
    /// The text block is read with <see cref="GetText"/>.</summary>
    public string? Get(string field) => field.ToLowerInvariant() switch
    {
        "partid" => string.IsNullOrWhiteSpace(PartId) ? null : PartId.Trim(),
        "productid" => ProductId == 0 ? null : ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "userin1" or "userin2" or "userin3" or "userin4" when UserIn.ElementAtOrDefault(field[^1] - '1') is var v and not 0
            => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A string of the text block; null, with the reason, when the PLC has no text block or the string is
    /// stale (not from this trigger), empty or unreadable.</summary>
    public string? GetText(Plc.PlcTextSpec? spec, out string why)
    {
        why = "";
        if (spec is null) { why = "the line does not say where in the text block its string lies"; return null; }
        if (Text is null) { why = "the PLC sent no text block with this trigger"; return null; }
        return Text.Get(spec, out why);
    }
}

/// <summary>
/// What a tool can take from outside its own image in one run: the PLC values of the trigger (null when not
/// triggered by the PLC) and the results of the tools that ran before it on the same camera.
/// </summary>
public sealed class RunContext
{
    public RunContext(PlcValues? plc = null, IReadOnlyList<ToolResult>? earlier = null)
    {
        Plc = plc;
        Earlier = earlier ?? Array.Empty<ToolResult>();
    }

    public PlcValues? Plc { get; }
    public IReadOnlyList<ToolResult> Earlier { get; }

    /// <summary>
    /// The text another tool gave in this run: the one code a Read code tool read, or the one line a Read text
    /// tool read. Null, with the reason, when that tool did not run before this one, did not pass, or gave no
    /// text or more than one (line safety: an expected text in doubt is never guessed).
    /// </summary>
    public string? TextOf(string tool, out string why)
    {
        var r = Earlier.LastOrDefault(t => string.Equals(t.ToolName, tool, StringComparison.OrdinalIgnoreCase));
        why = "";
        if (r is null) { why = $"No result from {tool}: it must run before this tool, on the same camera"; return null; }
        if (!r.Pass) { why = $"{tool} did not pass, so its text cannot be the expected text"; return null; }
        var texts = r.Codes is not null ? r.Codes.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList()
                  : r.Lines is not null ? r.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                  : new List<string>();
        if (texts.Count == 1) return texts[0];
        why = texts.Count == 0 ? $"{tool} gave no text" : $"{tool} gave {texts.Count} texts; the expected text needs exactly one";
        return null;
    }
}

/// <summary>One code a Read code tool read: its type, text, corners in the image and print quality (null = not graded).</summary>
public sealed record CodeRead(BarcodeReading.CodeFormat Format, string Text, PointF[] Corners, BarcodeReading.CodeQuality? Quality)
{
    /// <summary>ScanAngle (spec 5.2): reading direction in degrees clockwise from left-to-right, 0..360.</summary>
    public float ScanAngle { get; init; }
    /// <summary>CheckDigitOk (spec 5.2): true = verified; null = the type has none or the tool does not check it.</summary>
    public bool? CheckDigitOk { get; init; }
    /// <summary>QuietZoneOk (spec 5.2): 1D quiet zones clear; null = not checked (2D, DataBar, too small).</summary>
    public bool? QuietZoneOk { get; init; }
    /// <summary>EAN/UPC 2- or 5-digit add-on ("" = none), apart from Text.</summary>
    public string AddOn { get; init; } = "";
    /// <summary>AIM symbology identifier from the engine ("]d2" GS1 Data Matrix, "]Q3" GS1 QR, "]C1" GS1-128 ...;
    /// "" = not known): whether the symbol starts with FNC1.</summary>
    public string SymbologyId { get; init; } = "";
}

/// <summary>Values the operator enters at the start of a batch (batch number, MRP, dates, full lines).</summary>
public sealed class BatchFields
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

/// <summary>A configured tool in a job. Subclasses are stored in the job file by "kind".</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CodeToolConfig), "code")]
[JsonDerivedType(typeof(TextToolConfig), "text")]
[JsonDerivedType(typeof(LocateToolConfig), "locate")]
[JsonDerivedType(typeof(MeasureToolConfig), "measure")]
[JsonDerivedType(typeof(BlobToolConfig), "blob")]
[JsonDerivedType(typeof(InspectToolConfig), "inspect")]
[JsonDerivedType(typeof(AnomalyToolConfig), "anomaly")]
[JsonDerivedType(typeof(ThermalToolConfig), "thermal")]
[JsonDerivedType(typeof(InfraredToolConfig), "infrared")]
[JsonDerivedType(typeof(PolarToolConfig), "polar")]
[JsonDerivedType(typeof(PhotometricToolConfig), "photometric")]
[JsonDerivedType(typeof(WaveletToolConfig), "wavelet")]
[JsonDerivedType(typeof(InpaintToolConfig), "inpaint")]
[JsonDerivedType(typeof(RectifyToolConfig), "rectify")]
[JsonDerivedType(typeof(DriftToolConfig), "drift")]
[JsonDerivedType(typeof(DeformToolConfig), "deform")]
[JsonDerivedType(typeof(PhaseToolConfig), "phase")]
[JsonDerivedType(typeof(MetamorphicToolConfig), "metamorphic")]
[JsonDerivedType(typeof(AutotuneToolConfig), "autotune")]
public abstract class ToolConfig
{
    public string Name { get; set; } = "";
    /// <summary>Which camera of the job this tool looks at (index into <see cref="Job.Cameras"/>).</summary>
    public int Camera { get; set; }
    public RoiBox? Roi { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Read code and text tools: the engineer accepted fewer pixels than the tool needs (requirements AS-14);
    /// null = not accepted. Advice only: it never changes the tool's result.</summary>
    public PixelAcceptance? PixelsAccepted { get; set; }
    /// <summary>Batch fields this tool needs from the operator (template names, full-text lines).</summary>
    public abstract IEnumerable<FieldRequest> Fields();
}

public sealed record FieldRequest(string Key, string Label, string? Example = null);

/// <summary>A running tool built from its config. Owns native handles: dispose it.</summary>
public interface ITool : IDisposable
{
    ToolConfig Config { get; }
    /// <summary>Runs on one image. With a fixture (the camera's Locate tool found the part) the tool's
    /// area moves and turns with the part.</summary>
    ToolResult Run(GrayImage image, BatchFields batch, Fixture? fixture = null);
}
