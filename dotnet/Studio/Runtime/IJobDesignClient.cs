namespace VisionSystem.Studio.Runtime;

/// <summary>
/// The job designer's calls (UI-UX-SPEC.md 4.7, 4.8) and Start batch. Only an Engineer or Admin
/// may design jobs; the runtime refuses everyone else and says why. A job travels as its file's
/// JSON (JOB-FILE-FORMAT.md), so settings this app does not show are kept as they are.
/// </summary>
public interface IJobDesignClient
{
    Task<JobFile> GetJobAsync(string name, CancellationToken ct);
    Task<SaveOutcome> SaveJobAsync(string json, CancellationToken ct);
    Task<PictureList> ListPicturesAsync(string json, int camera, CancellationToken ct);
    /// <summary>The draft's tools of one camera on one picture; with <paramref name="teachTool"/> that tool is
    /// taught from the picture first and the taught draft comes back.</summary>
    Task<TryOutcome> TryJobAsync(TryRequest request, CancellationToken ct);
    /// <summary>One batch value (Start batch); refused while the line runs.</summary>
    Task<CommandResult> SetBatchAsync(string key, string value, CancellationToken ct);
    /// <summary>After a supervisor refused the changeover: the same values as a new request.</summary>
    Task<CommandResult> AskChangeoverAgainAsync(CancellationToken ct);
    /// <summary>While the plant server cannot be reached: a second person accepts the changeover at the line PC.
    /// <paramref name="secretKind"/> is "pin", "password" or "card" (user empty with a card); <paramref name="code"/> is
    /// the 8 characters the dialog showed. The secret is passed on and never kept.</summary>
    Task<CommandResult> AcceptChangeoverAtLineAsync(string user, string secretKind, string secret, string code, CancellationToken ct);
    /// <summary>The answer at the line PC to a remote service request (REMOTE-SUPPORT.md 4b). A confirmation or a No needs no
    /// secret (<paramref name="secretKind"/> ""); a supervisor approves with "pin", "password" or "card". The secret is passed
    /// on and never kept.</summary>
    Task<CommandResult> AnswerRemoteAsync(string id, bool approve, string user, string secretKind, string secret, CancellationToken ct);
    /// <summary>One picture from a job camera while the line is stopped, kept with the camera's stored
    /// pictures; the draft comes back with the camera's pictures folder filled in when it had none.</summary>
    Task<GrabOutcome> GrabPictureAsync(string json, int camera, CancellationToken ct);
    /// <summary>The tools auto find proposes on a stored picture: on the whole picture, or in the window
    /// dragged (full-image pixels). Suggestions only; nothing is added to the job here.</summary>
    Task<FindOutcome> FindToolsAsync(string json, int camera, string stored, (int X, int Y, int Width, int Height)? area, CancellationToken ct);
    /// <summary>The outline tolerances a Blob tool's taught outline would need on stored pictures of good parts
    /// (requirements 17.12 BLB-OUT-06). A suggestion only: nothing changes until the engineer accepts it.</summary>
    Task<BlobToleranceOutcome> SuggestBlobToleranceAsync(string json, int camera, string tool, IReadOnlyList<string> pictures, CancellationToken ct);
}

/// <summary>A camera's lens and scale at the part's plane, as the runtime uses it; MmPerPx 0 = no real size.</summary>
public sealed record LensCalibrationInfo(float Fx, float Fy, float Cx, float Cy, float K1, float K2, float P1, float P2, float K3, float MmPerPx, float RmsPx);

/// <summary>Auto tolerance for a Blob tool: Found false (with Reason) when a picture shows no single whole outline,
/// never a made-up limit; the limits and worst good values are in Unit ("px" or "mm").</summary>
public sealed record BlobToleranceOutcome(bool Ok, bool Found = false, float MaxDeviation = 0, float MaxDentDepth = 0, string Unit = "px",
    int Pictures = 0, float WorstDeviation = 0, float WorstDent = 0, string Reason = "", LensCalibrationInfo? Calibration = null,
    CommandResult? Refused = null);

public sealed record GrabOutcome(bool Ok, string Name = "", string Json = "", CommandResult? Refused = null);

/// <summary>A tool auto find proposes: what was read (one entry per code or text line) with each entry's field
/// type, its outline in full-image pixels, and the tool as in the job file.</summary>
public sealed record ToolSuggestion(string Kind, string Summary, IReadOnlyList<string> Read, IReadOnlyList<string> Fields,
    IReadOnlyList<Point2> Outline, string ToolJson);

/// <summary>The suggestions, and the picture (as in <see cref="TryOutcome"/>) so it shows before the camera has a tool.</summary>
public sealed record FindOutcome(bool Ok, IReadOnlyList<ToolSuggestion>? Suggestions = null, float TimeMs = 0, byte[]? Jpeg = null,
    int Width = 0, int Height = 0, double Scale = 1, int ImageWidth = 0, int ImageHeight = 0, CommandResult? Refused = null);

/// <summary>Refused: the runtime's reason (key and text in the user's language); else the job file.</summary>
public sealed record JobFile(bool Ok, string Json = "", int Version = 0, bool Open = false, CommandResult? Refused = null);

public sealed record SaveOutcome(bool Ok, string Name = "", int Version = 0, CommandResult? Refused = null);

public sealed record PictureList(bool Ok, IReadOnlyList<string> Names, CommandResult? Refused = null);

/// <summary>A picture of the camera's stored pictures by name, or a recent part's image by id.
/// PlcText: the PLC's text block to try with, first register to string. SkipPictureCheck: no picture check
/// (AS-13) on this try, for "Try all pictures", which keeps only pass and fail. TeachSpot: with TeachTool on a Blob
/// tool, the spot whose outline is taught by its number on the picture (BLB-OUT-07); 0 = the first.</summary>
public sealed record TryRequest(string Json, int Camera, string? Stored = null, string? ImageId = null, string? TeachTool = null,
    IReadOnlyDictionary<string, string>? Batch = null, IReadOnlyDictionary<int, string>? PlcText = null, bool SkipPictureCheck = false, bool NotShown = false,
    int TeachSpot = 0);

/// <summary>A line a text tool read; Expected is what a Check text line should say. Format and Field: the format
/// the runtime suggests from the text ("EXP {EXP:MM/YYYY}") and its field type ("expiry date"); empty when none.</summary>
public sealed record TextLineRead(string Tool, string Text, string Expected, bool Pass, float Quality, string Format = "", string Field = "");

/// <summary>One tool's result on the tried picture. ReportOnly: a Read text tool set to report only, shown as
/// information and never as OK. CodeGrade: a graded Read code's lowest print grade, 4 = A .. 0 = F; null when
/// not graded (or from an older runtime). Pixels: a Read code or text tool's pixel check (AS-14); null when nothing
/// was measured.</summary>
public sealed record ToolTried(string Name, string Kind, bool Pass, string Reason, float TimeMs, float? PrintQuality = null,
    bool ReportOnly = false, float? CodeGrade = null, PixelCheckTried? Pixels = null);

/// <summary>A code or text line found and its size: Measure "module", "bar" or "letter"; corners in full-image pixels.</summary>
public sealed record PixelItemTried(string Name, string Measure, float SizePx, bool Below, IReadOnlyList<Point2> Corners);

/// <summary>
/// A tool's pixel check on the tried picture (requirements AS-14): its worst item against what the tool needs, the
/// factor missing (1 = enough) and the remedies: the camera Factor times nearer, a lens of LensAdvisedMm (0 = the lens
/// is not known: Factor times its focal length), or a camera with PixelsTimes the pixels. Advice only.
/// </summary>
public sealed record PixelCheckTried(string Measure, float NeedPx, float UnsurePx, float FoundPx, float Factor, float PixelsTimes,
    float LensMm, float LensAdvisedMm, bool Accepted, string AcceptedBy, DateTimeOffset? AcceptedAt, IReadOnlyList<PixelItemTried> Items)
{
    public bool Enough => FoundPx >= NeedPx;
    public bool Unsure => FoundPx < UnsurePx;
    /// <summary>"Too few pixels" in the job tree until an engineer accepts it.</summary>
    public bool TooFew => !Enough && !Accepted;
}

public sealed record TryOutcome(bool Ok, bool Pass = false, float TimeMs = 0, byte[]? Jpeg = null, int Width = 0, int Height = 0,
    double Scale = 1, int ImageWidth = 0, int ImageHeight = 0,
    IReadOnlyList<ToolTried>? Tools = null, IReadOnlyList<CodeOutcome>? Codes = null, IReadOnlyList<ResultGraphic>? Graphics = null,
    IReadOnlyList<TextLineRead>? Lines = null, string TaughtJson = "", string TaughtText = "", CommandResult? Refused = null,
    string ImageId = "",
    PictureCheckOutcome? PictureCheck = null);

/// <summary>One zone of the picture check (requirements AS-13): the corner or centre cell of a 3 x 3 grid in
/// full-image pixels, its sharpness 0..100 (null: nothing there to judge), mean grey and mark
/// ("good", "close", "bad", "no_detail").</summary>
public sealed record PictureZoneCheck(string Zone, int X, int Y, int Width, int Height, float? Sharpness, float Brightness, string State);

/// <summary>One line of advice ("tilt", "focus", "clean_lens", "vignetting", "dark", "bright") and its zones.</summary>
public sealed record PictureAdviceLine(string Kind, IReadOnlyList<string> Zones);

/// <summary>The picture check of a tried picture: advice for the lens and the light only, never part of a result.</summary>
public sealed record PictureCheckOutcome(IReadOnlyList<PictureZoneCheck> Zones, IReadOnlyList<PictureAdviceLine> Advice);
