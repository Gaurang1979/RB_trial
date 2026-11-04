namespace VisionSystem.Studio.Runtime;

// What Vision Studio needs from the runtime, in the app's own terms. GrpcRuntimeClient maps the
// contract (proto/visionstudio/remote/v1/runtime.proto) onto these, so view-models and their
// tests never see protobuf types.

public enum LineState { Stopped, Running }

/// <summary>Status colour and shape of a result or graphic (UI-UX-SPEC.md principle 5).</summary>
public enum Verdict { None, Ok, Ng, Warn, Duplicate, Info }

/// <summary>The product running: a job and one of its variants ("Tablets 10x10 · PH-1010-24").</summary>
public sealed record ProductRef(string Job, string Variant)
{
    public string Name => Variant.Length > 0 ? $"{Job} · {Variant}" : Job;
}

public sealed record Counters(long Good, long Bad)
{
    public long Total => Good + Bad;
}

public sealed record RuntimeStatus
{
    public LineState State { get; init; } = LineState.Stopped;
    public ProductRef? Product { get; init; }
    public Counters Counters { get; init; } = new(0, 0);
    public bool CameraOk { get; init; } = true;
    public bool PlcOk { get; init; } = true;
    public bool LicenceOk { get; init; } = true;
    /// <summary>The highest active alarm, already in the user's language ("What is wrong. What to do.").</summary>
    public string? Alarm { get; init; }
    /// <summary>The "What do I do?" steps of that alarm, one per line, or null when it has none.</summary>
    public string? AlarmHelp { get; init; }
    public string? Warning { get; init; }
    public string? WarningHelp { get; init; }
    /// <summary>The job's cameras, in job order; one camera chip each on the Run screen.</summary>
    public IReadOnlyList<string> Cameras { get; init; } = [];
    /// <summary>What the operator enters before the line starts (Start batch), with what was entered.</summary>
    public IReadOnlyList<BatchEntry> Batch { get; init; } = [];
    /// <summary>The speed the line is set to run at (parts per minute) and the time one part may take
    /// (ms), drawn as limit lines on the Overview charts; 0 when the runtime does not say.</summary>
    public double LineSpeedPpm { get; init; }
    public double CycleBudgetMs { get; init; }
    /// <summary>The PLC's signals for the Batch and PLC card; null when no PLC is set up.</summary>
    public PlcSignals? Plc { get; init; }
    /// <summary>The setup checks of the loaded job (requirements AS-13, AS-14), null when nothing was checked.</summary>
    public SetupCheckOutcome? SetupCheck { get; init; }
    /// <summary>The supervisor's check of the changeover (CHANGEOVER-VERIFY.md); null from a runtime without it.</summary>
    public ChangeoverInfo? Changeover { get; init; }
    /// <summary>The learning session (owner, 2026-11-04); null when no job is open or the runtime has none.</summary>
    public LearningInfo? Learning { get; init; }
    /// <summary>Remote service (REMOTE-SUPPORT.md): whether a session is open (the neutral badge) and what it asks at the line
    /// PC; null when there is neither.</summary>
    public RemoteServiceInfo? Remote { get; init; }
}

/// <summary>Remote service on this line: a session open (never who: REQ-08), and the requests waiting at the line PC.</summary>
public sealed record RemoteServiceInfo(bool Active, IReadOnlyList<RemoteAsk> Requests);

/// <summary>A remote service request at the line PC: a confirmation (the operator's Yes or No) or an approval of a correction
/// (a supervisor's own PIN, password or card).</summary>
public sealed record RemoteAsk(string Id, bool Approval, string Title, string What, IReadOnlyList<RemoteFieldChange> Changes, string Reason,
    DateTimeOffset? Expires);

public sealed record RemoteFieldChange(string Field, string Old, string New);

/// <summary>The learning session on the line: on or off, the idle minutes, whether the line is idle (and for how long),
/// the pictures of the open job waiting to be sorted, and whether learning may start now.</summary>
public sealed record LearningInfo(bool Enabled, int IdleMinutes, bool Idle, int IdleSeconds, int ToSort, bool MayLearn)
{
    /// <summary>Saved bad pictures of the open job with no review yet, whatever the setting (AS-04i).</summary>
    public int BadToReview { get; init; }
    /// <summary>How many of them an operator marked "Was OK".</summary>
    public int FlaggedOk { get; init; }
}

public enum ChangeoverCheck { NotNeeded, Waiting, Accepted, Refused, NoServer }

/// <summary>The changeover check: Start waits unless it is NotNeeded or Accepted. Code is the 8 characters the supervisor's
/// screen shows too; By, At and Note are the decision. AtLine: accepted at the line PC while the plant server could not be reached.</summary>
public sealed record ChangeoverInfo(ChangeoverCheck State, string Code, string By, DateTimeOffset? At, string Note, bool AtLine = false)
{
    public bool AllowsStart => State is ChangeoverCheck.NotNeeded or ChangeoverCheck.Accepted;
}

public enum SetupCheckState { Checking, Done, Stopped }

/// <summary>
/// The setup checks at job load (requirements AS-13, AS-14, section 16 "When the picture and pixel checks run"): one
/// or two pictures per camera taken after the job was loaded and before Run, with the lens, light and pixel advice.
/// Advice only: never a reason to refuse Run. Stopped: the line started before the check ended.
/// </summary>
public sealed record SetupCheckOutcome(SetupCheckState State, string Job, IReadOnlyList<CameraSetupOutcome> Cameras);

/// <summary>One camera's check: Pictures checked (0 to 2), from "camera" (grabbed by software) or "stored" pictures;
/// Error says why none came ("" when one did). Tools: each Read code and text tool that measured something, with its
/// pixel check; Unmeasured: those that found nothing to measure.</summary>
public sealed record CameraSetupOutcome(string Camera, int Pictures, string Source, string Error, PictureCheckOutcome? PictureCheck,
    IReadOnlyList<ToolPixels> Tools, IReadOnlyList<string> Unmeasured);

public sealed record ToolPixels(string Tool, PixelCheckTried Pixels);

/// <summary>The PLC link as the runtime sees it. LastTriggerId 0 and LastResultTime null: none yet.
/// LastResultPass is the last part's result as the PLC was given it.</summary>
public sealed record PlcSignals(bool LinkUp, bool HeartbeatOk, bool RunEnable, uint LastTriggerId = 0,
    DateTimeOffset? LastResultTime = null, bool LastResultPass = false, string Link = "");

/// <summary>One value of the batch: Value is empty while not entered (the runtime then refuses Start).</summary>
public sealed record BatchEntry(string Key, string Label, string Example, string Value);

/// <summary>Counters of the batch from GetCounters: yield and cycle time for the Run screen.</summary>
public sealed record RunCounters(long Total, long Ok, long Ng, double YieldPercent, double MedianCycleMs, double MaxCycleMs);

/// <summary>A full-resolution image (an NG part or a product picture) as the runtime encoded it.</summary>
public sealed record FullImage(byte[] Data, int Width, int Height);

public sealed record Point2(double X, double Y);

public enum GraphicKind { Polygon, Polyline, Cross, Text }

/// <summary>One result graphic in image pixels, drawn by the app over the live image (never burned in).
/// Tooltip is the runtime's text shown when the graphic is tapped.</summary>
public sealed record ResultGraphic(GraphicKind Kind, IReadOnlyList<Point2> Points, Verdict Verdict,
    string? Label = null, string? Tooltip = null)
{
    /// <summary>False: the shape only, no label tag (another graphic carries the label).</summary>
    public bool Tagged { get; init; } = true;
    /// <summary>A try's graphic: the tool that drew it (requirements 17.12 BLB-OUT-07); null elsewhere and from an older runtime.</summary>
    public string? Tool { get; init; }
}

/// <summary>One tool's result for the part shown. PrintQuality: Check text's lowest character score
/// 0..100 (tool spec 7.3); null for other tools. ReportOnly: shown as information, never as OK.</summary>
public sealed record ToolOutcome(string Name, bool Pass, float? PrintQuality = null, bool ReportOnly = false);

/// <summary>A code read for the part shown. AddOn: the EAN/UPC 2- or 5-digit add-on, "" when none. From a try
/// also its corners (full-image pixels), its GS1 elements by AI and their sentence ("GS1: GTIN (01), ..."), when it has
/// them, and the tool that read it.</summary>
public sealed record CodeOutcome(string Text, string Format, string AddOn = "", IReadOnlyList<Point2>? Corners = null,
    IReadOnlyDictionary<string, string>? Gs1 = null, string Field = "", string Tool = "");

/// <summary>A downscaled live frame (JPEG, may be empty) with the result of the part it shows.
/// ImageId names the full-resolution image for GetImage, when the runtime keeps one. Tools and Codes
/// are that camera's tool results and codes (empty from an older runtime or for a preview).</summary>
public sealed record LiveFrame(ulong Sequence, DateTimeOffset Time, byte[] Jpeg, int Width, int Height,
    Verdict Result, IReadOnlyList<ResultGraphic> Graphics, string? ImageId = null,
    IReadOnlyList<ToolOutcome>? Tools = null, IReadOnlyList<CodeOutcome>? Codes = null, string Camera = "");

/// <summary>
/// One inspected part from StreamResults: every part, at any line speed, unlike the live frames.
/// Reason is the first failed check in the runtime's words ("" when it passed); the code counts
/// are the Code Reader's codes of that part (0 for other tools). ImageId names the first camera's
/// full image for GetImage, when the runtime keeps one.
/// </summary>
public sealed record PartResult(ulong Id, DateTimeOffset Time, string Product, string PartId, Verdict Result,
    double CycleMs, string Reason, int CodesRead = 0, int CodesMissed = 0, int CodesDuplicate = 0, string? ImageId = null);

/// <summary>Ordered from least to most rights.</summary>
public enum Role { Operator, Qa, Supervisor, Engineer, Admin }

/// <summary>A user who may sign in; tiles show those with a PIN, the password form takes any name.</summary>
public sealed record UserTile(string Id, string DisplayName, Role Role, bool HasPin = true, bool HasPassword = false);

/// <summary>IdleSignOut is the plant's idle time for this app (WhoAmI), or null when the runtime
/// does not say: the app then uses <see cref="DefaultIdleSignOut"/>.
/// MayDesignJobs: the job designer is open to this user (Engineer, Admin).
/// MayTestPlc: the PLC test signal buttons are shown to this user (Engineer, Admin).
/// MayLearn: the Learning button and the sorting (Supervisor, QA, Engineer, Admin; Support and OEM with today's code,
/// AS-04h); null when the runtime does not say (an older one): the role decides.</summary>
public sealed record SignedInUser(string Id, string DisplayName, Role Role, bool MayOperate, TimeSpan? IdleSignOut = null,
    bool MayDesignJobs = false, bool MayTestPlc = false, bool? MayLearn = null)
{
    public static readonly TimeSpan DefaultIdleSignOut = TimeSpan.FromMinutes(10);
}

/// <summary>Wrong: the PIN, password or card was not right. Refused: any other "no" (locked, not here).</summary>
public enum SignInOutcome { Ok, Wrong, Refused }

/// <summary>Text is the runtime's message for a refusal, in the user's language.</summary>
public sealed record SignInResult(SignInOutcome Outcome, SignedInUser? User = null, string? Text = null);

/// <summary>A product: a variant of a job (the contract's ListJobs and ChangeJob). PictureId names its
/// picture for GetImage, or is empty.</summary>
public sealed record ProductInfo(string Job, string Variant, string PictureId = "")
{
    public string Name => Variant.Length > 0 ? $"{Job} · {Variant}" : Job;
}

/// <summary>
/// Result of a changing call. ReasonKey is a string-table key; ReasonText the runtime's own text
/// in the user's language, shown when the app has no text for the key.
/// </summary>
public sealed record CommandResult(bool Ok, string? ReasonKey = null, string? ReasonText = null)
{
    public static readonly CommandResult Done = new(true);
}
