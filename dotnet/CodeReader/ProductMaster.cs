using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BarcodeReading;

/// <summary>Learning mode for a product: the tuner may change settings, or they are frozen.</summary>
public enum LearningMode
{
    Learning,
    Locked,
}

/// <summary>Camera settings stored per product. Applied to the camera when the batch starts.</summary>
public sealed class CameraSettings
{
    public float ExposureUs { get; set; } = 2000;
    public float GainDb { get; set; }
    /// <summary>From the trigger (case sensor or PLC) to the start of the exposure.</summary>
    public float TriggerDelayUs { get; set; }
    /// <summary>Region of interest in pixels (the case; the rest of the image is not processed); null = full image.</summary>
    public RoiSettings? Roi { get; set; }
    /// <summary>How cases are presented: standing still under the camera, or moving slowly.</summary>
    public CaseMotion Motion { get; set; } = CaseMotion.Still;
    /// <summary>Conveyor speed in image pixels per millisecond, measured by calibration; 0 = not measured.</summary>
    public float SpeedPxPerMs { get; set; }
    /// <summary>The direction cases move in the image (unit vector), measured by calibration.</summary>
    public float MotionDirX { get; set; } = 1;
    public float MotionDirY { get; set; }
    public LightSettings Light { get; set; } = new();
    /// <summary>
    /// Frames per case (1-3). With 2 or 3, the PLC triggers the camera and the strobe that many
    /// times per case within a short window; the station reads the frames together (glare moves
    /// between frames, and very small modules gain from the different sub-pixel phases). 1 = one
    /// frame per case.
    /// </summary>
    public int FramesPerCase { get; set; } = 1;
    /// <summary>Measured by calibration (0 = not yet): modules across one code and its side in pixels.
    /// A new reader starts from them (ReaderSettings.HintModules / HintCodeSidePx).</summary>
    public int CodeModules { get; set; }
    public float CodeSidePx { get; set; }
    /// <summary>When this product's camera was last calibrated, and by whom.</summary>
    public DateTime? CalibratedUtc { get; set; }
    public string? CalibratedBy { get; set; }

    public CameraSettings Clone() =>
        JsonSerializer.Deserialize<CameraSettings>(JsonSerializer.Serialize(this, ProductStore.Json), ProductStore.Json)!;
}

public enum CaseMotion
{
    Still,
    Moving,
}

/// <summary>Who switches the LED light.</summary>
public enum LightControl
{
    /// <summary>The camera's Line1 output drives the light during the exposure (strobe).</summary>
    CameraStrobe,
    /// <summary>The PLC switches the light and triggers the camera; it uses the timing calibration gives.</summary>
    Plc,
}

/// <summary>LED light timing around the exposure.</summary>
public sealed class LightSettings
{
    /// <summary>The PLC switches the light on the lines being built (owner, 2026-10-23); the camera strobe remains an option.</summary>
    public LightControl Control { get; set; } = LightControl.Plc;
    /// <summary>Light on this long before the exposure starts (the LED needs time to reach full brightness).</summary>
    public float PreOnUs { get; set; } = 100;
    /// <summary>Light stays on this long after the exposure ends.</summary>
    public float PostOnUs { get; set; } = 50;
    /// <summary>Longest pulse the light allows (overdriven strobes); 0 = no limit.</summary>
    public float MaxOnUs { get; set; }

    public LightSettings Clone() => new() { Control = Control, PreOnUs = PreOnUs, PostOnUs = PostOnUs, MaxOnUs = MaxOnUs };
}

public sealed class RoiSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Reader settings stored per product (the dashboard's Auto / Manual values).</summary>
public sealed class ReaderTuning
{
    /// <summary>Decode time per case in ms; 0 = the line's target for the case size (see ProductMaster.TargetTimeMs).</summary>
    public int TimeBudgetMs { get; set; }
    public int LocatorScale { get; set; } = 4;
    public int MaxUpscale { get; set; } = 2;
    public float Gamma { get; set; } = 0.5f;
    public float LocatorDensity { get; set; } = 0.12f;
    public bool FullImageFallback { get; set; } = true;
    public LocatorMode Locator { get; set; } = LocatorMode.Auto;
    public float AiThreshold { get; set; } = 0.4f;
    public EnhancerMode Enhancer { get; set; } = EnhancerMode.Last;
    /// <summary>Read faint QR codes by matching them to the gaps in the case's serial run.</summary>
    public bool MatchSerials { get; set; } = true;

    /// <summary>
    /// Settings the tuning agent may change (Auto); the others keep the values above (Manual).
    /// Names as in <see cref="TuningAgent.DefaultKnobs"/>. Ignored when the product is Locked.
    /// </summary>
    public List<string> AutoKnobs { get; set; } = TuningAgent.DefaultKnobs.Select(k => k.Name).ToList();
}

/// <summary>
/// One product in the product master: what the operator picks at batch start. Holds the code
/// types, codes per case and the camera and reader settings tuned for this product.
/// </summary>
public sealed class ProductMaster
{
    /// <summary>Case sizes the line runs today. Other counts are allowed but flagged.</summary>
    public static readonly IReadOnlyList<int> StandardCaseSizes = new[] { 12, 24, 45, 48, 96 };

    public string ProductCode { get; set; } = "";
    public string Name { get; set; } = "";
    public CodeFormat ChildFormat { get; set; } = CodeFormat.QrCode;
    public int CodesPerCase { get; set; }
    public CodeFormat ParentFormat { get; set; } = CodeFormat.Gs1_128;
    /// <summary>Expected GTIN on the child codes; empty = not checked.</summary>
    public string? Gtin { get; set; }

    public CameraSettings ChildCamera { get; set; } = new();
    public CameraSettings ParentCamera { get; set; } = new() { ExposureUs = 1000 };
    public ReaderTuning Reader { get; set; } = new();
    public LearningMode Mode { get; set; } = LearningMode.Learning;

    // audit fields, set by the store
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }

    /// <summary>Problems that block saving; empty when the product is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ProductCode)) errors.Add("product code is empty");
        else if (ProductCode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ProductCode.Contains(' '))
            errors.Add("product code may hold only letters, digits, '-' and '_'");
        if (CodesPerCase < 1 || CodesPerCase > 500) errors.Add("codes per case must be 1-500");
        if (ChildFormat is not (CodeFormat.QrCode or CodeFormat.DataMatrix)) errors.Add("child codes must be QR or Data Matrix");
        if (ChildCamera.ExposureUs <= 0 || ParentCamera.ExposureUs <= 0) errors.Add("exposure must be positive");
        if (Gtin is { Length: > 0 } g && (g.Length != 14 || !Gs1Data.CheckDigitOk(g))) errors.Add("GTIN must be 14 digits with a valid check digit");
        return errors;
    }

    /// <summary>True when the case size is one of <see cref="StandardCaseSizes"/>.</summary>
    [JsonIgnore]
    public bool IsStandardCaseSize => StandardCaseSizes.Contains(CodesPerCase);

    /// <summary>
    /// The line's time target per case (owner, 2026-10-23): 12 codes 100 ms, 24 codes 250-300 ms,
    /// 45 and 48 codes 500 ms, 96 codes 600 ms.
    /// </summary>
    public static int TargetTimeMs(int codesPerCase) => codesPerCase switch
    {
        <= 0 => 500,
        <= 12 => 100,
        <= 24 => 275,
        <= 48 => 500,
        _ => 600,
    };

    /// <summary>The product's time budget, or the line's target for its case size when not set.</summary>
    [JsonIgnore]
    public int TimeBudgetMs => Reader.TimeBudgetMs > 0 ? Reader.TimeBudgetMs : TargetTimeMs(CodesPerCase);

    /// <summary>Reader settings for the top camera (child codes).</summary>
    public ReaderSettings ChildReaderSettings() => new()
    {
        Format = ChildFormat,
        ExpectedCount = CodesPerCase,
        TimeBudgetMs = TimeBudgetMs,
        LocatorScale = Reader.LocatorScale,
        MaxUpscale = Reader.MaxUpscale,
        Gamma = Reader.Gamma,
        LocatorDensity = Reader.LocatorDensity,
        FullImageFallback = Reader.FullImageFallback,
        Locator = Reader.Locator,
        AiThreshold = Reader.AiThreshold,
        Enhancer = Reader.Enhancer,
        MatchSerials = Reader.MatchSerials,
        HintModules = ChildCamera.CodeModules,
        HintCodeSidePx = ChildCamera.CodeSidePx,
        Roi = ChildCamera.Roi is { } r ? (r.X, r.Y, r.Width, r.Height) : null,
    };

    /// <summary>Reader settings for the side camera (one parent code).</summary>
    public ReaderSettings ParentReaderSettings() => new()
    {
        Format = ParentFormat,
        ExpectedCount = 1,
        TimeBudgetMs = TimeBudgetMs,
        Roi = ParentCamera.Roi is { } r ? (r.X, r.Y, r.Width, r.Height) : null,
    };

    internal ProductMaster Clone() =>
        JsonSerializer.Deserialize<ProductMaster>(JsonSerializer.Serialize(this, ProductStore.Json), ProductStore.Json)!;
}

/// <summary>Where product masters live. Your database can implement this instead of the JSON files.</summary>
public interface IProductStore
{
    IReadOnlyList<ProductMaster> List();
    ProductMaster? Get(string productCode);
    /// <summary>Saves a new version; returns the saved copy with Version and audit fields set.</summary>
    ProductMaster Save(ProductMaster product, string user);
    /// <summary>Every saved version, oldest first (audit trail).</summary>
    IReadOnlyList<ProductMaster> History(string productCode);
}

/// <summary>
/// Product master as JSON files: products/&lt;code&gt;.json is the current version and
/// products/history/&lt;code&gt;/v&lt;n&gt;.json keeps every version. Writes are atomic.
/// </summary>
public sealed class ProductStore : IProductStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;
    private readonly object _lock = new();

    public ProductStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(Path.Combine(_folder, "history"));
    }

    public IReadOnlyList<ProductMaster> List()
    {
        lock (_lock)
        {
            // only <code>.json files that hold that product (other JSON may share the folder)
            return Directory.GetFiles(_folder, "*.json")
                .Select(f => (File: f, Product: Read(f)))
                .Where(x => x.Product is not null && x.Product.ProductCode.Length > 0 &&
                            string.Equals(Path.GetFileNameWithoutExtension(x.File), x.Product.ProductCode, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Product!)
                .OrderBy(p => p.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public ProductMaster? Get(string productCode)
    {
        lock (_lock)
        {
            string path = CurrentPath(productCode);
            return File.Exists(path) ? Read(path) : null;
        }
    }

    public ProductMaster Save(ProductMaster product, string user)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("user is required for the audit trail", nameof(user));
        var errors = product.Validate();
        if (errors.Count > 0) throw new ArgumentException("invalid product: " + string.Join("; ", errors));

        lock (_lock)
        {
            var saved = product.Clone();
            var previous = Get(product.ProductCode);
            saved.Version = (previous?.Version ?? 0) + 1;
            saved.UpdatedUtc = DateTime.UtcNow;
            saved.UpdatedBy = user;
            // a change to a locked product needs a fresh approval
            if (saved.Mode == LearningMode.Locked && saved.ApprovedBy is null)
                throw new InvalidOperationException("a locked product needs ApprovedBy (e-signature) on every change");

            string historyDir = Path.Combine(_folder, "history", saved.ProductCode);
            Directory.CreateDirectory(historyDir);
            WriteAtomic(Path.Combine(historyDir, $"v{saved.Version}.json"), saved);
            WriteAtomic(CurrentPath(saved.ProductCode), saved);
            return saved;
        }
    }

    public IReadOnlyList<ProductMaster> History(string productCode)
    {
        lock (_lock)
        {
            string dir = Path.Combine(_folder, "history", productCode);
            if (!Directory.Exists(dir)) return Array.Empty<ProductMaster>();
            return Directory.GetFiles(dir, "v*.json")
                .Select(Read)
                .Where(p => p is not null)
                .Select(p => p!)
                .OrderBy(p => p.Version)
                .ToList();
        }
    }

    private string CurrentPath(string productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode) || productCode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("bad product code", nameof(productCode));
        return Path.Combine(_folder, productCode + ".json");
    }

    private static ProductMaster? Read(string path)
    {
        // The product master and the learned sizes/encoding it holds are encrypted at rest when the PC is
        // licensed (AES-256-GCM, content key from the licence; LICENSING.md 7.4). ReadText decrypts in
        // memory, or returns the plaintext of a developer / pre-encryption file. A protected file with no
        // key or a bad tag throws ContentProtectionException: the product does not load and the line is NG,
        // never a wrong product read in the clear.
        try { return JsonSerializer.Deserialize<ProductMaster>(Protection.ContentProtection.ReadText(path), Json); }
        catch (JsonException) { return null; }
    }

    private static void WriteAtomic(string path, ProductMaster p)
    {
        // Encrypted in place when a licence content key is available (production), plaintext otherwise
        // (developer checkout). The file name is unchanged; the content is a .vsenc container that Read detects.
        string tmp = path + ".tmp";
        Protection.ContentProtection.WriteText(tmp, JsonSerializer.Serialize(p, Json), Path.GetFileName(path));
        File.Move(tmp, path, overwrite: true);
    }
}
