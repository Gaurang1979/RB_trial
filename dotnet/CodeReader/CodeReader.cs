using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BarcodeReading;

/// <summary>
/// Code types, as flags (the engine's cr_format). A job usually reads one type; several may be
/// combined (slower). A code of a type that is not enabled is never reported, even when it shares a
/// decoder family with an enabled one (a Micro QR is not a QR, a UPC-A is not an EAN-13).
/// </summary>
[Flags]
public enum CodeFormat
{
    QrCode = 1,
    DataMatrix = 2,
    /// <summary>Code 128, including GS1-128 / SSCC.</summary>
    Gs1_128 = 4,
    Ean13 = 8,
    Ean8 = 0x10,
    UpcA = 0x20,
    UpcE = 0x40,
    /// <summary>Code 39, standard and full ASCII.</summary>
    Code39 = 0x80,
    Code93 = 0x100,
    /// <summary>Interleaved 2 of 5, ITF-14.</summary>
    Itf = 0x200,
    Codabar = 0x400,
    /// <summary>GS1 DataBar: omnidirectional, truncated, stacked, limited, expanded.</summary>
    DataBar = 0x800,
    MicroQr = 0x1000,
    /// <summary>PDF417, compact PDF417 and MicroPDF417.</summary>
    Pdf417 = 0x2000,
    Aztec = 0x4000,
    /// <summary>
    /// Laetus Pharmacode (one-track), 2-16 bars, values 3-131070, read as standard: the bar on the right of
    /// the image weighs least. It has no check digit: the engine reports it only with a bar-count lock
    /// (MinLength / MaxLength count bars for this type) and clear quiet zones, bars upright within 75 degrees.
    /// </summary>
    Pharmacode = 0x8000,
    /// <summary>Pharmacode read the other way (the bar on the left weighs least): a code passing the camera turned round.</summary>
    PharmacodeReverse = 0x10000,
    /// <summary>AIM DotCode, GS1 and plain data.</summary>
    DotCode = 0x20000,
    /// <summary>Rectangular Micro QR (rMQR, ISO/IEC 23941); graded by ISO/IEC 15415 with its own fixed patterns.</summary>
    RMqr = 0x40000,
    /// <summary>Every 1D type (Code 128 to DataBar); Pharmacode has rules of its own.</summary>
    AllLinear = 0x0FFC,
    /// <summary>Every type: teaching and testing only (slower).</summary>
    Any = 0x7FFFF,
}

/// <summary>Check digit of Code 39 (modulo 43), ITF (GS1 modulo 10) and Codabar (modulo 16, the character
/// before the stop character).</summary>
public enum CheckDigitRule
{
    /// <summary>No extra check: the text as printed.</summary>
    AsRead = 0,
    /// <summary>The last character must be a correct check digit, else the read is dropped.</summary>
    Verify = 1,
    /// <summary>As Verify, and the check digit is removed from the text.</summary>
    Strip = 2,
}

/// <summary>Which way a Pharmacode's bars are read. It has no start or stop, so the other direction reads
/// another valid value: the job states the direction, the engine never guesses it, and a code lying the
/// other way is not read.</summary>
public enum PharmacodeDirection
{
    /// <summary>Bars upright: read across the image (standard: the bar on the right weighs least).</summary>
    Across = 0,
    /// <summary>Bars lying flat, the code turned a quarter turn: read down the image (standard: the bottom bar
    /// weighs least, a code turned clockwise; reverse: the top bar).</summary>
    Down = 1,
}

public static class CodeFormats
{
    /// <summary>The single types a job can pick, most common first.</summary>
    public static readonly CodeFormat[] Single =
    {
        CodeFormat.QrCode, CodeFormat.DataMatrix, CodeFormat.Ean13, CodeFormat.Gs1_128, CodeFormat.Ean8,
        CodeFormat.UpcA, CodeFormat.UpcE, CodeFormat.Code39, CodeFormat.Itf, CodeFormat.Code93,
        CodeFormat.Codabar, CodeFormat.DataBar, CodeFormat.Pdf417, CodeFormat.MicroQr, CodeFormat.Aztec,
        CodeFormat.DotCode, CodeFormat.Pharmacode, CodeFormat.PharmacodeReverse, CodeFormat.RMqr,
    };

    /// <summary>The name printed in the standards and on the operator's screen.</summary>
    public static string Name(CodeFormat f) => f switch
    {
        CodeFormat.QrCode => "QR",
        CodeFormat.DataMatrix => "Data Matrix",
        CodeFormat.Gs1_128 => "Code 128 / GS1-128",
        CodeFormat.Ean13 => "EAN-13",
        CodeFormat.Ean8 => "EAN-8",
        CodeFormat.UpcA => "UPC-A",
        CodeFormat.UpcE => "UPC-E",
        CodeFormat.Code39 => "Code 39",
        CodeFormat.Code93 => "Code 93",
        CodeFormat.Itf => "ITF / ITF-14",
        CodeFormat.Codabar => "Codabar",
        CodeFormat.DataBar => "GS1 DataBar",
        CodeFormat.MicroQr => "Micro QR",
        CodeFormat.Pdf417 => "PDF417",
        CodeFormat.Aztec => "Aztec",
        CodeFormat.Pharmacode => "Pharmacode",
        CodeFormat.PharmacodeReverse => "Pharmacode (reverse)",
        CodeFormat.DotCode => "DotCode",
        CodeFormat.RMqr => "rMQR",
        CodeFormat.Any => "any",
        _ => f.ToString(),
    };

    /// <summary>True for 1D (linear and stacked linear) types, where the length lock and check digit apply.</summary>
    public static bool IsLinear(CodeFormat f) => f != 0 && (f & ~CodeFormat.AllLinear) == 0;

    /// <summary>Pharmacode, either reading direction: its length lock counts bars.</summary>
    public static bool IsPharmacode(CodeFormat f) => f is CodeFormat.Pharmacode or CodeFormat.PharmacodeReverse;

    /// <summary>The bars of a Pharmacode value (3-131070), as the engine counts them for the lock; 0 for any other text.</summary>
    public static int PharmacodeBars(string text)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) ||
            v < 3 || v > 131070)
            return 0;
        int bars = 0;
        while (v > 0)
        {
            v = v % 2 == 0 ? (v - 2) / 2 : (v - 1) / 2;
            bars++;
        }
        return bars;
    }

    /// <summary>EAN and UPC have a fixed length and a check digit of their own: no length lock is needed.</summary>
    public static bool HasFixedLength(CodeFormat f) =>
        f is CodeFormat.Ean13 or CodeFormat.Ean8 or CodeFormat.UpcA or CodeFormat.UpcE;
}

/// <summary>How code regions are found.</summary>
public enum LocatorMode
{
    /// <summary>AI then classical when a model is loaded, else classical.</summary>
    Auto = 0,
    Classical = 1,
    /// <summary>AI only: fastest, needs a model.</summary>
    Ai = 2,
    AiThenClassical = 3,
}

/// <summary>Where the AI enhancer runs. The tuning agent learns the best one per product.</summary>
public enum EnhancerMode
{
    /// <summary>Per region, after the cheap image fixes failed (most codes overall).</summary>
    Last = 0,
    /// <summary>Per region, right after the plain attempt (dark or blurred lines, e.g. caps).</summary>
    Early = 1,
    /// <summary>Only after every region had the cheap fixes (lines where most codes are easy).</summary>
    SecondPass = 2,
    Off = 3,
}

/// <summary>Which processing step read a code. Used by the learning service and the dashboard.</summary>
public enum ReadStep
{
    Plain = 0,
    Upscale = 1,
    Clahe = 2,
    Sharpen = 3,
    Binary = 4,
    AiEnhance = 5,
    Flat = 6,
    FlatAdaptive = 7,
    DataMatrixFallback = 8,
    Deblur = 12,
    DeblurStrong = 13,
    Upscale4 = 14,
    GridFit = 15,
    /// <summary>Too faint to decode: matched to a gap in the case's serial run (see ReaderSettings.MatchSerials).</summary>
    Match = 16,
    /// <summary>Data Matrix read inside-out: the module grid from the texture, no finder or quiet zone needed.</summary>
    Lattice = 17,
    /// <summary>QR read with the content it shares with the codes already read in the case (all but the serial).</summary>
    Known = 18,
    /// <summary>Glare corrected: each small area stretched between its own darkest and brightest level.</summary>
    Glare = 19,
    /// <summary>QR read from several frames of the case fused (DecodeFrames).</summary>
    Frames = 20,
    /// <summary>1D code at any angle: scan lines along its bars fused, read by zxing.</summary>
    Linear = 21,
    /// <summary>SSCC whose bar edges are gone: digits fitted through the blur, confirmed by both check digits.</summary>
    LinearSscc = 22,
    /// <summary>Dot-printed modules, voids and specks: dots melted into solid modules (cell filters).</summary>
    Dots = 23,
    /// <summary>Pharmacode: bar widths on scan lines across its bars.</summary>
    Pharmacode = 24,
    /// <summary>DotCode: dot lattice fitted, dots sampled, Reed-Solomon corrected.</summary>
    DotCode = 25,
    FullImage = 10,
    FullImageBright = 11,
}

/// <summary>
/// Job settings, set by the operator at the start of a batch (code type and codes per case)
/// and by the dashboard (everything else). Defaults match the engine's defaults.
/// </summary>
public sealed class ReaderSettings
{
    public CodeFormat Format { get; set; } = CodeFormat.QrCode;
    /// <summary>Codes per case. The reader stops as soon as it has this many. 0 = unknown.</summary>
    public int ExpectedCount { get; set; }
    public int TimeBudgetMs { get; set; } = 200;
    /// <summary>0 = all CPU cores.</summary>
    public int Threads { get; set; }
    public int LocatorScale { get; set; } = 4;
    public int MaxUpscale { get; set; } = 2;
    public float Gamma { get; set; } = 0.5f;
    public float LocatorDensity { get; set; } = 0.12f;
    public bool FullImageFallback { get; set; } = true;
    public LocatorMode Locator { get; set; } = LocatorMode.Auto;
    /// <summary>AI locator probability threshold, 0.05..0.95.</summary>
    public float AiThreshold { get; set; } = 0.4f;
    public EnhancerMode Enhancer { get; set; } = EnhancerMode.Last;
    /// <summary>
    /// A QR code too faint to decode may be read by matching it to a gap in the serial run of the
    /// codes read in the same case (holograms and labels printed in order from a roll). Only a
    /// strong match, clearly ahead of every other candidate, counts; it is reported as ReadStep.Match.
    /// </summary>
    public bool MatchSerials { get; set; } = true;
    /// <summary>What calibration measured about the product's codes (0 = unknown): modules across one
    /// code (QR 21 + 4 x version, Data Matrix 10-26), its side in pixels, and symbols per code. A new
    /// reader starts from them, so its first case reads like the later ones.</summary>
    public int HintModules { get; set; }
    public float HintCodeSidePx { get; set; }
    public float HintSymbolsPerCode { get; set; }
    /// <summary>1D only, not EAN/UPC: fewest characters a read may have, as printed (check digit included);
    /// 0 = any. A shorter read (a partial scan of a damaged barcode) is dropped.</summary>
    public int MinLength { get; set; }
    /// <summary>1D only: most characters; 0 = any.</summary>
    public int MaxLength { get; set; }
    /// <summary>Check digit of Code 39, ITF and Codabar.</summary>
    public CheckDigitRule CheckDigit { get; set; } = CheckDigitRule.AsRead;
    /// <summary>Pharmacode only: which way its bars are read.</summary>
    public PharmacodeDirection PharmacodeDirection { get; set; } = PharmacodeDirection.Across;
    /// <summary>Measure each code's print and image quality (DecodedCode.Quality); about 1 ms per code.</summary>
    public bool GradeQuality { get; set; }
    /// <summary>Region of interest in pixels; null = whole image.</summary>
    public (int X, int Y, int Width, int Height)? Roi { get; set; }

    internal Native.Params ToNative() => new()
    {
        StructSize = Native.ParamsSize,
        Format = (int)Format,
        ExpectedCount = ExpectedCount,
        TimeBudgetMs = TimeBudgetMs,
        Threads = Threads,
        LocatorScale = LocatorScale,
        MaxUpscale = MaxUpscale,
        Gamma = Gamma,
        LocatorDensity = LocatorDensity,
        FullPass = FullImageFallback ? 1 : 0,
        RoiX = Roi?.X ?? 0,
        RoiY = Roi?.Y ?? 0,
        RoiW = Roi?.Width ?? 0,
        RoiH = Roi?.Height ?? 0,
        LocatorMode = (int)Locator,
        AiThreshold = AiThreshold,
        EnhancerMode = (int)Enhancer,
        MatchSerials = MatchSerials ? 1 : 0,
        GradeQuality = GradeQuality ? 1 : 0,
        HintModules = HintModules,
        HintCodeSidePx = HintCodeSidePx,
        HintSymbolsPerCode = HintSymbolsPerCode,
        MinLength = MinLength,
        MaxLength = MaxLength,
        CheckDigit = (int)CheckDigit,
        PharmaDirection = (int)PharmacodeDirection,
    };
}

public readonly record struct PointF(float X, float Y);

public readonly record struct Region(int X, int Y, int Width, int Height);

/// <summary>
/// Image quality of one decoded code (ReaderSettings.GradeQuality): what calibration and the
/// line advisor need. Contrast in % ((light - dark) / light), 0..1 shares otherwise.
/// </summary>
public sealed record CodeQuality(float SymbolContrast, float Modulation, float ModuleSizePx, float LightingUniformity,
    float Saturated, int OverallGrade)
{
    /// <summary>True when the reader had a reflectance calibration: contrasts are reflectance %, as a verifier's.</summary>
    public bool Calibrated { get; init; }
    /// <summary>The module (X-dimension) in mm; 0 when no scale was set (SetLens).</summary>
    public float XDimMm { get; init; }
    /// <summary>1D codes, after ISO/IEC 15416 (means over 10 scan lines); null for 2D codes.</summary>
    public LinearGrade? Linear { get; init; }
    /// <summary>Each graded parameter and its grade (4 = A .. 0 = F), in the standard's order; parameters not
    /// measured are left out.</summary>
    public IReadOnlyList<(string Parameter, int Grade)> Grades { get; init; } = Array.Empty<(string, int)>();
    /// <summary>2D codes: the measured value behind each ISO 15415 parameter grade (shares 0..1; grid
    /// non-uniformity in modules), by the parameter names in Grades; empty for 1D codes.</summary>
    public IReadOnlyDictionary<string, float> Values { get; init; } = new Dictionary<string, float>();
    /// <summary>1D codes: each scan line as graded (ISO/IEC 15416); empty for 2D codes.</summary>
    public IReadOnlyList<LinearScan> ScanLines { get; init; } = Array.Empty<LinearScan>();
    /// <summary>QR, Micro QR, Data Matrix and Aztec: the module grid the engine graded (rows down, columns across,
    /// between the code's corners); 0 when not known (1D, PDF417).</summary>
    public int GridRows { get; init; }
    /// <summary>See GridRows.</summary>
    public int GridCols { get; init; }

    /// <summary>A grade as the letter on a verifier report: 4 = A ... 0 = F; "-" not measured.</summary>
    public static string Letter(int grade) => grade switch { 4 => "A", 3 => "B", 2 => "C", 1 => "D", 0 => "F", _ => "-" };
}

/// <summary>1D print quality after ISO/IEC 15416. Grades 4 = A .. 0 = F, -1 not measured.</summary>
public sealed record LinearGrade(float EdgeContrastMin, float Defects, float Decodability, float RMin, float RMax,
    int Scans, int DecodedScans, int GradeEdgeContrast, int GradeDefects, int GradeDecodability);

/// <summary>One 1D scan line (ISO/IEC 15416): reflectances in %, grades 4 = A .. 0 = F; Grade is its worst parameter.</summary>
public sealed record LinearScan(float RMin, float RMax, float SymbolContrast, float EdgeContrastMin, float Modulation, float Defects,
    float Decodability, bool Decoded, bool QuietZoneOk, int GradeRmin, int GradeQuietZone, int Grade);

/// <summary>Lens distortion and scale at the case's top (CodeReader.CalibrateChessboard).</summary>
public sealed record LensCalibration(float Fx, float Fy, float Cx, float Cy, float K1, float K2, float P1, float P2, float K3,
    float MmPerPx, float RmsPx);

public sealed record DecodedCode(
    string Text,
    CodeFormat Format,
    ReadStep Step,
    IReadOnlyList<PointF> Corners,
    int Copies)
{
    /// <summary>Measured quality, or null when grading was off or the code could not be measured.</summary>
    public CodeQuality? Quality { get; init; }

    /// <summary>AIM symbology identifier from the decoder ("]Q3" GS1 QR, "]d2" GS1 Data Matrix, "]C1" GS1-128,
    /// "]Q1" QR without FNC1 ...); "" when not known (a code read by one of the engine's own decoders).</summary>
    public string SymbologyId { get; init; } = "";

    /// <summary>The text with its symbology identifier in front when known, as a GS1 check reads it.</summary>
    public string TextWithId => SymbologyId + Text;

    /// <summary>Reading direction in degrees clockwise from left-to-right, 0..360 (180 = upside down).</summary>
    public float Angle { get; init; }

    /// <summary>True when the check digit was verified (EAN/UPC, Code 128, Code 93, DataBar always; Code 39,
    /// ITF and Codabar under CheckDigitRule Verify or Strip); null when not checked (no check digit, 2D).</summary>
    public bool? CheckDigitOk { get; init; }

    /// <summary>1D except DataBar: true when the type's quiet zone (about 10 bar widths) is clear on both
    /// ends; false when a mark is in it or it runs off the image; null when not checked.</summary>
    public bool? QuietZoneOk { get; init; }

    /// <summary>EAN/UPC 2- or 5-digit add-on, "" when none; Text holds the main symbol only.</summary>
    public string AddOn { get; init; } = "";

    /// <summary>Direct part mark grade after ISO/IEC TR 29158 (CodeReader.SetDpm), Data Matrix and QR only;
    /// null when DPM grading is off or the code could not be graded.</summary>
    public DpmGrade? Dpm { get; init; }

    /// <summary>Mean side length of the code in pixels.</summary>
    public float SidePx
    {
        get
        {
            float s = 0;
            for (int k = 0; k < 4; k++)
            {
                var a = Corners[k];
                var b = Corners[(k + 1) % 4];
                s += MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }
            return s / 4;
        }
    }

    public PointF Center => new((Corners[0].X + Corners[2].X) / 2, (Corners[0].Y + Corners[2].Y) / 2);

    /// <summary>GS1 fields parsed from the text, or null when the code is not GS1.</summary>
    public Gs1Data? Gs1 { get; } = Gs1Data.TryParse(Text);
}

public sealed record DecodeStats(
    int Codes,
    int Expected,
    int Candidates,
    int Misses,
    float LocateMs,
    float DecodeMs,
    float TotalMs,
    bool TimedOut,
    float LabelBrightness)
{
    /// <summary>1D reads dropped by the length lock (often a partial scan); &gt; 0 on a short image says why.</summary>
    public int RejectedLength { get; init; }

    /// <summary>1D reads dropped for a wrong check digit.</summary>
    public int RejectedCheckDigit { get; init; }

    /// <summary>Codes seen more often than the product's label carries: an extra pack with a copy of a code
    /// already in the case (the engine merges reads of one text, so the count does not show it).</summary>
    public int CopiesOver { get; init; }

    /// <summary>Code-sized regions the engine had no time to decode once the expected count was read: an extra
    /// pack there would not be seen. Reported, not judged.</summary>
    public int ExtraUnchecked { get; init; }

    /// <summary>EAN / UPC reads the ISO/IEC 15420 reference decode did not read again: never reported, so a
    /// &gt; 0 on a short image says why a code is missing.</summary>
    public int RefusedEan { get; init; }

    /// <summary>EAN-2 / EAN-5 add-ons the reference decode did not read again after the main symbol: reported as
    /// no add-on (a job that requires the add-on gives NG).</summary>
    public int RefusedAddon { get; init; }

    public bool Complete => Expected <= 0 || Codes >= Expected;

    /// <summary>More codes than the case holds, or a code seen more often than its label carries: a foreign or
    /// extra pack in it. The case is NG.</summary>
    public bool Extra => Expected > 0 && (Codes > Expected || CopiesOver > 0);
}

/// <summary>Code regions read and not read per zone of the image (4 x 4, row-major).</summary>
public sealed record ZoneStats(IReadOnlyList<int> Read, IReadOnlyList<int> Unread)
{
    public const int Size = 4;
    /// <summary>Share of the zone's code regions that did not read, 0..1; null when the zone saw none.</summary>
    public double? MissRate(int zone) => Read[zone] + Unread[zone] == 0 ? null : (double)Unread[zone] / (Read[zone] + Unread[zone]);
}

/// <summary>A region the finder proposed: yellow while searching, then green (read) or red (not read).</summary>
public readonly record struct FoundRegion(Region Box, bool FromAi, bool Read);

public sealed record DecodeResult(
    IReadOnlyList<DecodedCode> Codes,
    IReadOnlyList<Region> Misses,
    DecodeStats Stats)
{
    /// <summary>Everything the finder proposed for this image, with what became of it.</summary>
    public IReadOnlyList<FoundRegion> Regions { get; init; } = Array.Empty<FoundRegion>();

    /// <summary>Why each unread region did not read, in the order of <see cref="Misses"/> (short cases only).</summary>
    public IReadOnlyList<MissDiagnosis> MissInfo { get; init; } = Array.Empty<MissDiagnosis>();
}

/// <summary>
/// Runtime anti-tamper state (the engine's cr_tamper). The engine watches for tampering after it is
/// loaded; on a positive detection it latches a process-global state that never clears, and every
/// decode then fails closed to NG (see <see cref="TamperDetectedException"/>). The line software
/// polls <see cref="CodeReader.IntegrityState"/> to raise a clear tamper alarm.
/// </summary>
public enum TamperState
{
    /// <summary>No tampering detected.</summary>
    None = 0,
    /// <summary>A debugger or instrumentation is attached to the process.</summary>
    Debugger = 1,
    /// <summary>A critical engine code region was patched in memory after load.</summary>
    CodePatched = 2,
}

/// <summary>
/// Thrown by a decode when the engine's runtime anti-tamper check has tripped (cr_decode returned
/// CR_DECODE_TAMPER). The case is NG - never a read - and the host must raise a tamper alarm to the
/// line (requirements section 12, owner 2026-11-01). The state is latched, so every later decode
/// throws this until the process is restarted on a clean, untampered binary.
/// </summary>
public sealed class TamperDetectedException : InvalidOperationException
{
    /// <summary>Which tamper the engine detected.</summary>
    public TamperState State { get; }

    public TamperDetectedException(TamperState state, string message) : base(message) => State = state;
}

/// <summary>
/// One reader per camera job (for example child codes on the top camera, parent code on the
/// side camera). Not thread safe: use one instance per thread.
/// </summary>
public sealed class CodeReader : IDisposable
{
    private IntPtr _handle;
    private ReaderSettings _settings;
    private readonly Native.FoundFn _foundFn; // kept here so the GC never frees the native callback
    private Action<IReadOnlyList<FoundRegion>>? _onFound;

    private DpmSettings _dpm = new();

    public CodeReader(ReaderSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _foundFn = OnFoundNative;
        var p = settings.ToNative();
        _handle = Native.Create(ref p);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("codereader: create failed");
    }

    /// <summary>
    /// The engine's latched runtime anti-tamper state, read without re-running the checks. The line
    /// software polls this (cheaply, on its alarm thread) to raise a tamper alarm even when it is not
    /// decoding. Once it is not <see cref="TamperState.None"/> it stays so until the process restarts.
    /// </summary>
    public static TamperState IntegrityState => (TamperState)Native.IntegrityState();

    /// <summary>
    /// Runs the runtime anti-tamper checks now and returns the latched state (may newly latch). Use
    /// this for an explicit check; ordinary decoding runs the checks on its own and fails closed.
    /// </summary>
    public static TamperState RunIntegrityCheck() => (TamperState)Native.IntegrityCheck();

    /// <summary>
    /// Loads the AI code locator (ONNX file from ml/train.py). The model is decrypted in memory when it is
    /// protected at rest (AES-256-GCM, content key from the licence; LICENSING.md 7.4) and loaded from the
    /// buffer, so no plaintext model is on the shipped disk. A missing key or a tampered file throws, so the
    /// reader never runs without the model it was told to use (requirements section 12).
    /// </summary>
    public void LoadModel(string onnxPath)
    {
        if (!TryReadModel(onnxPath, out byte[] bytes))
        {
            if (Native.LoadModel(Handle, onnxPath) != 0)
                throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
            return;
        }
        if (Native.LoadModelBuffer(Handle, bytes, (nuint)bytes.Length) != 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>Loads the AI enhancer (ONNX file from ml/enhance.py), tried last on regions that do not read.
    /// Decrypted in memory when protected at rest, as with <see cref="LoadModel"/>.</summary>
    public void LoadEnhancer(string onnxPath)
    {
        if (!TryReadModel(onnxPath, out byte[] bytes))
        {
            if (Native.LoadEnhancer(Handle, onnxPath) != 0)
                throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
            return;
        }
        if (Native.LoadEnhancerBuffer(Handle, bytes, (nuint)bytes.Length) != 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>
    /// Reads a model file into memory, decrypting it if it is protected at rest. Returns false when the file
    /// is missing, so the caller falls back to the native path load and its usual "file not found" error; a
    /// protected file with no key or a bad tag throws (<see cref="Protection.ContentProtectionException"/>).
    /// </summary>
    private static bool TryReadModel(string onnxPath, out byte[] bytes)
    {
        try { bytes = Protection.ContentProtection.ReadArtifact(onnxPath); return true; }
        catch (FileNotFoundException) { bytes = Array.Empty<byte>(); return false; }
        catch (DirectoryNotFoundException) { bytes = Array.Empty<byte>(); return false; }
    }

    public static string EngineVersion => Marshal.PtrToStringUTF8(Native.Version()) ?? "";

    public ReaderSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var p = value.ToNative();
            Native.SetParams(Handle, ref p);
            _settings = value;
        }
    }

    /// <summary>Decode an 8-bit grey image (Mono8 camera buffer). stride = bytes per row.</summary>
    public unsafe DecodeResult Decode(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (pixels.Length < (long)stride * (height - 1) + width)
            throw new ArgumentException("pixel buffer is smaller than width/height/stride", nameof(pixels));

        fixed (byte* ptr = pixels)
            return Decode((IntPtr)ptr, width, height, stride);
    }

    /// <summary>
    /// Decode an 8-bit grey image held in unmanaged memory, such as a camera SDK frame buffer.
    /// The buffer must stay valid until this call returns; it is not copied.
    /// </summary>
    public DecodeResult Decode(IntPtr pixels, int width, int height, int stride) => Decode(pixels, width, height, stride, null);

    /// <summary>
    /// As <see cref="Decode(IntPtr,int,int,int)"/>; <paramref name="onFound"/> is called once,
    /// on this thread, when finding is done and decoding starts, with every region found (the
    /// live view draws them in yellow). It must be quick: decoding waits for it.
    /// </summary>
    public unsafe DecodeResult Decode(IntPtr pixels, int width, int height, int stride, Action<IReadOnlyList<FoundRegion>>? onFound)
    {
        if (pixels == IntPtr.Zero) throw new ArgumentNullException(nameof(pixels));
        int rc;
        if (onFound is null)
        {
            rc = Native.Decode(Handle, (byte*)pixels, width, height, stride);
        }
        else
        {
            _onFound = onFound;
            Native.SetFoundCallback(Handle, _foundFn, IntPtr.Zero);
            try { rc = Native.Decode(Handle, (byte*)pixels, width, height, stride); }
            finally
            {
                Native.SetFoundCallback(Handle, null, IntPtr.Zero);
                _onFound = null;
            }
        }
        return ReadResult(rc);
    }

    /// <summary>
    /// Reads a case from several frames (2-8, same size; still or slowly moving): every frame is
    /// read and the reads united, and a code unread in every frame is decoded from the frames
    /// fused (module evidence summed over the frames, aligned by their shift). Glare moves between
    /// strobed frames, and very small modules gain from the different sub-pixel phases.
    /// </summary>
    public unsafe DecodeResult DecodeFrames(IReadOnlyList<byte[]> frames, int width, int height, int stride)
    {
        if (frames is null || frames.Count == 0) throw new ArgumentException("no frames", nameof(frames));
        if (frames.Count == 1) return Decode(frames[0], width, height, stride);
        var handles = new GCHandle[frames.Count];
        var ptrs = stackalloc byte*[frames.Count];
        try
        {
            for (int k = 0; k < frames.Count; k++)
            {
                if (frames[k].Length < stride * height) throw new ArgumentException($"frame {k} is too small", nameof(frames));
                handles[k] = GCHandle.Alloc(frames[k], GCHandleType.Pinned);
                ptrs[k] = (byte*)handles[k].AddrOfPinnedObject();
            }
            return ReadResult(Native.DecodeFrames(Handle, ptrs, frames.Count, width, height, stride));
        }
        finally
        {
            foreach (var h in handles) if (h.IsAllocated) h.Free();
        }
    }

    // what cr_get_code_ext adds to code i
    private readonly record struct Ext(string SymbologyId, int GradeRmin, int ScanCount, float Angle, int CheckDigitOk, int QuietZoneOk,
        string AddOn, float CodewordYield, int GradeCodewordYield, int GridRows, int GridCols);

    private unsafe Ext ReadExt(int i)
    {
        var e = new Native.CodeExt { StructSize = Marshal.SizeOf<Native.CodeExt>() };
        // a failed call leaves the 1D checks "not measured" (-1): the Read code tool then fails a 1D code
        if (Native.GetCodeExt(Handle, i, ref e) != 0) return new Ext("", -1, 0, 0f, -1, -1, "", -1f, -1, 0, 0);
        int n = 0;
        while (n < 3 && e.SymbologyId[n] != 0) n++;
        return new Ext(System.Text.Encoding.ASCII.GetString(e.SymbologyId, n), e.GradeRmin, e.ScanCount,
                e.Angle, e.CheckDigitOk, e.QuietZoneOk, Marshal.PtrToStringUTF8(e.AddOn) ?? "",
                e.CodewordYield, e.GradeCodewordYield, e.GridRows, e.GridCols);
    }

    // PDF417's measured values beside its grades (1D codes have none here: their values are in LinearGrade)
    private static Dictionary<string, float> LinearValues(Native.Quality q, Ext ext)
    {
        var v = new Dictionary<string, float>();
        if (ext.GradeCodewordYield >= 0) v["Codeword yield"] = ext.CodewordYield;
        if (q.GradeUec >= 0) v["Unused error correction"] = q.UnusedErrorCorrection;
        return v;
    }

    private List<LinearScan> ReadScans(int i, int count)
    {
        var scans = new List<LinearScan>(count);
        for (int k = 0; k < count; k++)
        {
            var s = new Native.Scan { StructSize = Marshal.SizeOf<Native.Scan>() };
            if (Native.GetScan(Handle, i, k, ref s) != 0) break;
            scans.Add(new LinearScan(s.RMin, s.RMax, s.SymbolContrast, s.EdgeContrastMin, s.Modulation, s.Defects, s.Decodability,
                s.Decoded != 0, s.QuietZoneOk != 0, s.GradeRmin, s.GradeQuietZone, s.Grade));
        }
        return scans;
    }

    // the engine's result after a decode call: codes, misses, stats, regions
    // cr_decode's return when the runtime anti-tamper check has latched (codereader.h CR_DECODE_TAMPER).
    private const int DecodeTamper = -3;

    private DecodeResult ReadResult(int rc)
    {
        // A tamper fault is a fault like any other negative return - the case is NG - but the host
        // needs to tell it apart to raise a tamper alarm, so it gets its own exception type.
        if (rc == DecodeTamper)
            throw new TamperDetectedException((TamperState)Native.IntegrityState(),
                "codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
        if (rc < 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));

        var codes = new List<DecodedCode>(rc);
        for (int i = 0; i < Native.CodeCount(Handle); i++)
        {
            Native.GetCode(Handle, i, out var c);
            var corners = new PointF[4];
            for (int k = 0; k < 4; k++) corners[k] = new PointF(c.X[k], c.Y[k]);
            var ext = ReadExt(i);
            CodeQuality? quality = null;
            if (_settings.GradeQuality && Native.GetQuality(Handle, i, out var q) == 0 && q.Measured != 0)
                quality = new CodeQuality(q.SymbolContrast, q.Modulation, q.ModuleSizePx, q.LightingUniformity, q.Saturated, q.Overall)
                {
                    Calibrated = q.Calibrated != 0,
                    XDimMm = q.XDimMm,
                    Linear = q.Linear != 0
                        ? new LinearGrade(q.EdgeContrastMin, q.Defects, q.Decodability, q.RMin, q.RMax, q.Scans, q.DecodedScans,
                            q.GradeEc, q.GradeDefects, q.GradeDecodability)
                        : null,
                    Grades = (q.Linear != 0 && (CodeFormat)c.Format == CodeFormat.Pdf417
                        // PDF417 in the 1D fields, after ISO/IEC 15415's multi-row method: contrast and edge contrast
                        // per row scan, modulation, defects and decodability per codeword against the error
                        // correction (a row without start and stop lowers the row scans' mean grade)
                        ? new[] { ("Symbol contrast", q.GradeSc), ("Modulation", q.GradeMod), ("Edge contrast", q.GradeEc),
                                  ("Defects", q.GradeDefects), ("Decodability", q.GradeDecodability),
                                  ("Codeword yield", ext.GradeCodewordYield), ("Unused error correction", q.GradeUec),
                                  ("Quiet zone", q.GradeQz) }
                        : q.Linear != 0
                        ? new[] { ("Symbol contrast", q.GradeSc), ("Modulation", q.GradeMod), ("Edge contrast", q.GradeEc),
                                  ("Defects", q.GradeDefects), ("Decodability", q.GradeDecodability),
                                  // a scan that did not read back to the text grades F
                                  ("Decode", q.Scans > 0 ? (q.DecodedScans == q.Scans ? 4 : 0) : -1),
                                  // graded per scan: the mean of the scans' grades
                                  ("Rmin", ext.GradeRmin), ("Quiet zone", q.GradeQz) }
                        : new[] { ("Symbol contrast", q.GradeSc), ("Modulation", q.GradeMod), ("Reflectance margin", q.GradeRm),
                                  ("Fixed pattern damage", q.GradeFpd), ("Axial non-uniformity", q.GradeAn),
                                  ("Grid non-uniformity", q.GradeGn), ("Unused error correction", q.GradeUec), ("Quiet zone", q.GradeQz) })
                        .Where(g => g.Item2 >= 0).ToList(),
                    Values = q.Linear != 0 ? LinearValues(q, ext) : new Dictionary<string, float>
                    {
                        ["Reflectance margin"] = q.ReflectanceMargin, ["Fixed pattern damage"] = q.FixedPatternDamage,
                        ["Axial non-uniformity"] = q.AxialNonuniformity, ["Grid non-uniformity"] = q.GridNonuniformity,
                        ["Unused error correction"] = q.UnusedErrorCorrection, ["Print growth"] = q.PrintGrowth,
                    },
                    ScanLines = ReadScans(i, ext.ScanCount),
                    GridRows = ext.GridRows,
                    GridCols = ext.GridCols,
                };
            codes.Add(new DecodedCode(
                Marshal.PtrToStringUTF8(c.Text) ?? "",
                (CodeFormat)c.Format,
                (ReadStep)c.Step,
                corners,
                c.Copies)
            {
                Quality = quality,
                SymbologyId = ext.SymbologyId,
                Angle = ext.Angle,
                CheckDigitOk = ext.CheckDigitOk < 0 ? null : ext.CheckDigitOk != 0,
                QuietZoneOk = ext.QuietZoneOk < 0 ? null : ext.QuietZoneOk != 0,
                AddOn = ext.AddOn,
                Dpm = _dpm.Enabled ? ReadDpm(i) : null,
            });
        }

        var misses = new List<Region>();
        var missInfo = new List<MissDiagnosis>();
        for (int i = 0; i < Native.MissCount(Handle); i++)
        {
            Native.GetMiss(Handle, i, out var m);
            misses.Add(new Region(m.X, m.Y, m.W, m.H));
            if (Native.GetMissInfo(Handle, i, out var d) == 0) missInfo.Add(MissDiagnosis.FromNative(d));
        }

        Native.GetStats(Handle, out var s);
        var sx = new Native.StatsExt { StructSize = Marshal.SizeOf<Native.StatsExt>() };
        Native.GetStatsExt(Handle, ref sx);
        var stats = new DecodeStats(s.Codes, s.Expected, s.Candidates, s.Misses,
            s.LocateMs, s.DecodeMs, s.TotalMs, s.TimedOut != 0, s.LabelBrightness)
        {
            RejectedLength = sx.RejectedLength, RejectedCheckDigit = sx.RejectedCheckDigit,
            CopiesOver = sx.CopiesOver, ExtraUnchecked = sx.ExtraUnchecked,
            RefusedEan = sx.RefusedEan, RefusedAddon = sx.RefusedAddon,
        };
        var regions = new List<FoundRegion>();
        for (int i = 0; i < Native.RegionCount(Handle); i++)
        {
            Native.GetRegion(Handle, i, out var g);
            regions.Add(new FoundRegion(new Region(g.X, g.Y, g.W, g.H), g.FromAi != 0, g.Read != 0));
        }
        return new DecodeResult(codes, misses, stats) { Regions = regions, MissInfo = missInfo };
    }

    private unsafe void OnFoundNative(IntPtr user, IntPtr regions, int count)
    {
        var cb = _onFound;
        if (cb is null) return;
        var list = new List<FoundRegion>(count);
        var p = (Native.FoundRegion*)regions;
        for (int i = 0; i < count; i++)
            list.Add(new FoundRegion(new Region(p[i].X, p[i].Y, p[i].W, p[i].H), p[i].FromAi != 0, false));
        try { cb(list); }
        catch
        {
            // an exception must not cross into the engine; the live view can miss one frame
        }
    }

    /// <summary>
    /// Where this reader loses codes: the image cut into 4 x 4 zones (row-major, top left first);
    /// code regions read and not read per zone over every image decoded so far. Each zone also
    /// learns its own order of image fixes.
    /// </summary>
    public ZoneStats GetZoneStats()
    {
        var read = new int[16];
        var unread = new int[16];
        Native.GetZoneStats(Handle, read, unread);
        return new ZoneStats(read, unread);
    }

    /// <summary>
    /// What this reader has learned (step order per zone, zone read map, code sizes, pass costs,
    /// the printer's encoding) as a text blob, to save beside the product and hand to the next
    /// reader of the same product with <see cref="SetLearned"/>, so a shift starts where the last
    /// one stopped. Learned state never crosses products: keep it under one product's key and drop
    /// it when the product, its camera settings or the engine version change.
    /// </summary>
    public string GetLearned()
    {
        int need = Native.GetLearned(Handle, null, 0);
        if (need <= 0) throw new InvalidOperationException("codereader: learned state unavailable");
        // a decode on another thread may grow a count between the sizing call and the copy
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buf = new byte[need + 256];
            int got = Native.GetLearned(Handle, buf, buf.Length);
            if (got > 0 && got <= buf.Length) return Encoding.UTF8.GetString(buf, 0, got - 1);
            need = got;
        }
        throw new InvalidOperationException("codereader: learned state keeps changing size");
    }

    /// <summary>
    /// Replaces the learned state from a blob of <see cref="GetLearned"/>. Throws when the blob is
    /// not one of this engine's (another version or layout); the reader is then unchanged. The
    /// loaded encoding is a hint until the first decoded code confirms it.
    /// </summary>
    public void SetLearned(string blob)
    {
        if (Native.SetLearned(Handle, blob) != 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>
    /// Flat field for the grades: a picture of a plain white card under the line's lighting, at the
    /// size of the images decoded later. Throws when it is not a white card under working light.
    /// </summary>
    public unsafe void SetFlatField(byte[] pixels, int width, int height, int stride)
    {
        fixed (byte* p = pixels)
            if (Native.SetFlatField(Handle, (IntPtr)p, width, height, stride) != 0)
                throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>Removes the flat field.</summary>
    public void ClearFlatField() => Native.SetFlatField(Handle, IntPtr.Zero, 0, 0, 0);

    /// <summary>
    /// Direct part mark grading after ISO/IEC TR 29158 (DecodedCode.Dpm): every Data Matrix and QR read is
    /// graded with these settings from the next decode on. Throws on a setting the engine refuses.
    /// </summary>
    public void SetDpm(DpmSettings dpm)
    {
        ArgumentNullException.ThrowIfNull(dpm);
        var p = dpm.ToNative();
        if (Native.SetDpm(Handle, ref p) != 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
        _dpm = dpm;
    }

    private DpmGrade? ReadDpm(int i)
    {
        var d = new Native.Dpm { StructSize = Marshal.SizeOf<Native.Dpm>() };
        return Native.GetDpm(Handle, i, ref d) == 0 && d.Measured != 0 ? DpmGrade.FromNative(d, _dpm.WavelengthNm) : null;
    }

    /// <summary>
    /// Reflectance calibration: the grey levels read on a card's dark and light patches (after the
    /// flat field) and their certified reflectance in %. Grades are then in reflectance %, as a verifier's.
    /// </summary>
    public void SetReflectance(float greyDark, float greyLight, float reflDarkPct, float reflLightPct)
    {
        if (Native.SetReflectance(Handle, greyDark, greyLight, reflDarkPct, reflLightPct) != 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>
    /// The grey levels of a reflectance card's dark and light patches (filling the middle of the
    /// picture), after the flat field: what SetReflectance needs. Throws when no two patches show.
    /// </summary>
    public unsafe (float GreyDark, float GreyLight) MeasureCard(byte[] pixels, int width, int height, int stride)
    {
        fixed (byte* p = pixels)
            if (Native.MeasureCard(Handle, (IntPtr)p, width, height, stride, out var d, out var l) == 0) return (d, l);
        throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>Lens distortion and mm per pixel (from CalibrateChessboard, or entered). Throws
    /// InvalidOperationException for a distortion model whose RmsPx is not 0..0.5 px: the engine clears the lens
    /// (no model, no scale), so nothing is measured in mm from a poor calibration.</summary>
    public void SetLens(LensCalibration l)
    {
        var n = new Native.Lens { Fx = l.Fx, Fy = l.Fy, Cx = l.Cx, Cy = l.Cy, K1 = l.K1, K2 = l.K2, P1 = l.P1, P2 = l.P2, K3 = l.K3, MmPerPx = l.MmPerPx, RmsPx = l.RmsPx };
        if (Native.SetLens(Handle, ref n) != 0)
            throw new InvalidOperationException("codereader: " + Marshal.PtrToStringUTF8(Native.LastError(Handle)));
    }

    /// <summary>
    /// Lens and scale from pictures of a chessboard lying on the case's top (cols x rows inner
    /// corners, square side in mm). Null when no view showed the board; three or more views whose lens
    /// model reprojects worse than 0.5 px are refused (InvalidOperationException), never kept as the scale alone.
    /// </summary>
    public static unsafe LensCalibration? CalibrateChessboard(IReadOnlyList<byte[]> views, int width, int height, int stride, int cols, int rows, float squareMm)
    {
        var handles = new System.Runtime.InteropServices.GCHandle[views.Count];
        var ptrs = new IntPtr[views.Count];
        try
        {
            for (int i = 0; i < views.Count; i++)
            {
                handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(views[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                ptrs[i] = handles[i].AddrOfPinnedObject();
            }
            int used = Native.CalibrateChessboard(ptrs, views.Count, width, height, stride, cols, rows, squareMm, out var l);
            if (used == -2) throw new NotSupportedException("codereader: built without OpenCV's calibration modules");
            if (used == -3)
                throw new InvalidOperationException("the lens model from these views is poor (reprojection worse than 0.5 px): " +
                                                    "take the chessboard views again, flat, sharp and from different angles");
            return used > 0 ? new LensCalibration(l.Fx, l.Fy, l.Cx, l.Cy, l.K1, l.K2, l.P1, l.P2, l.K3, l.MmPerPx, l.RmsPx) : null;
        }
        finally
        {
            foreach (var h in handles) if (h.IsAllocated) h.Free();
        }
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(CodeReader));

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Native.Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
