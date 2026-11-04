namespace VisionSystem.Studio.Runtime;

/// <summary>
/// The supervisor's NG review and learning from it (owner, 2026-10-31), and the learning session (owner, 2026-11-04):
/// sorting the saved good and bad pictures, and the setting that turns it on. Supervisor, QA, Engineer and Admin
/// only; the runtime refuses everyone else and says why. A review is allowed while the line runs; learning is
/// refused while it runs (unless it is idle with RunEnable off) and applying always is. Nothing changes the job
/// until a person applies a proposal.
/// </summary>
public interface IReviewClient
{
    /// <summary>Keeps the part's full picture with the decision. When the software found the part clearly bad, an
    /// Accepted is answered LooksBad with the faults and nothing is kept, unless <see cref="ReviewConfirm.LooksBad"/>;
    /// such a part is then kept as a record only, never learned from (the runtime's rule). When it could not read the
    /// part, an Accepted is answered Unreadable unless <see cref="ReviewConfirm.Unreadable"/> or
    /// <see cref="ReviewConfirm.KeepAsRecord"/>. A good picture rejected is a PossibleEscape.</summary>
    Task<ReviewOutcome> ReviewPartAsync(string imageId, ReviewDecision decision, ReviewConfirm confirm, string note, CancellationToken ct);
    Task<IReadOnlyList<CameraReviewCounts>> GetReviewsAsync(CancellationToken ct);
    /// <summary>A proposal from one camera's reviewed pictures (the open job). Takes a while: it runs the job three times.</summary>
    Task<LearnOutcome> LearnAsync(int camera, CancellationToken ct);
    Task<ApplyOutcome> ApplyLearningAsync(string proposalId, CancellationToken ct);
    /// <summary>The saved good and bad pictures of the open job not sorted yet (camera -1: every camera), newest first.</summary>
    Task<SortList> ListToSortAsync(int camera, CancellationToken ct);
    Task<LearningSettingsInfo?> GetLearningSettingsAsync(CancellationToken ct);
    Task<CommandResult> SetLearningSettingsAsync(bool enabled, int idleMinutes, CancellationToken ct);
    /// <summary>The reviewed pictures of the open job (camera -1: every camera), newest decision first, and the bad
    /// parts operators marked "Was OK" (AS-04j, AS-04k). A decision is changed with <see cref="ReviewPartAsync"/> and the
    /// reviewed picture's id, by the same rules as a first review.</summary>
    Task<ReviewedList> ListReviewedAsync(int camera, CancellationToken ct);
    /// <summary>Removes a decision: the picture no longer teaches the job, and waits for a review again while its saved
    /// bad picture is kept. An audit-trail event.</summary>
    Task<ReviewOutcome> RemoveReviewAsync(string imageId, CancellationToken ct);
    /// <summary>The operator's "Was OK" on a bad part (anyone who may run the line): shown to the supervisor, never
    /// learned from.</summary>
    Task<CommandResult> FlagWasOkAsync(string imageId, string note, CancellationToken ct);
}

public enum ReviewDecision { Accepted, ConfirmedNg }

/// <summary>What the person confirmed after the runtime asked to look again.</summary>
[Flags]
public enum ReviewConfirm
{
    None = 0,
    /// <summary>Clearly bad, accepted all the same: a record only.</summary>
    LooksBad = 1,
    /// <summary>Could not be read, checked again: learned as good.</summary>
    Unreadable = 2,
    /// <summary>Keep the decision as a record only, never in the learning set.</summary>
    KeepAsRecord = 4,
}

/// <summary>A saved picture waiting to be sorted: Ok is what the software decided; NearLimit: a good part that passed close to a limit.</summary>
public sealed record SortPicture(string ImageId, int Camera, string CameraName, bool Ok, IReadOnlyList<string> Faults, DateTimeOffset Time, bool NearLimit)
{
    /// <summary>An operator marked this bad part "Was OK" (empty: not marked).</summary>
    public string FlaggedBy { get; init; } = "";
    public DateTimeOffset? FlaggedAt { get; init; }
    /// <summary>The decision kept for it: set for a picture of the reviewed list.</summary>
    public ReviewedInfo? Reviewed { get; init; }
}

/// <summary>A decision kept for a picture: Learned is false for one accepted as a record only.</summary>
public sealed record ReviewedInfo(ReviewDecision Decision, bool Learned, string User, DateTimeOffset At);

/// <summary>A bad part an operator marked "Was OK", not reviewed yet.</summary>
public sealed record WasOkMark(string ImageId, int Camera, string User, DateTimeOffset At);

/// <summary>The reviewed pictures (as pictures to show, each with its <see cref="SortPicture.Reviewed"/>), how many in all,
/// and the "Was OK" marks.</summary>
public sealed record ReviewedList(CommandResult Result, IReadOnlyList<SortPicture> Items, int Total, IReadOnlyList<WasOkMark> Flags);

public sealed record SortList(CommandResult Result, IReadOnlyList<SortPicture> Items, int Total);

/// <summary>The learning session's setting: on or off, the idle minutes before the notice, who changed it last.</summary>
public sealed record LearningSettingsInfo(bool Enabled, int IdleMinutes, string ChangedBy, DateTimeOffset? ChangedAt);

/// <summary>A camera's reviewed pictures: accepted and learned from, confirmed bad, and accepted as a record only.</summary>
public sealed record CameraReviewCounts(int Camera, string Name, int Accepted, int Confirmed, int RecordOnly);

/// <summary>LooksBad: nothing kept, the supervisor should double-check (Faults say what the software found).
/// Learned: kept in the learning set (false: as a record only).</summary>
public sealed record ReviewOutcome(CommandResult Result, bool LooksBad, IReadOnlyList<string> Faults, bool Learned,
    IReadOnlyList<CameraReviewCounts> Cameras)
{
    /// <summary>Nothing kept: the software could not read the part; validate it a second time.</summary>
    public bool Unreadable { get; init; }
    /// <summary>A part the software passed was rejected: a bad part may have left the line (the plant is told).</summary>
    public bool PossibleEscape { get; init; }
}

/// <summary>One tool setting the learning would change. Looser: the check would accept more parts.</summary>
public sealed record TunedSettingInfo(string Tool, string Setting, string Current, string Proposed, bool Looser);

/// <summary>What learning proposes. ProposalId is empty when nothing may be applied; Problem says why.</summary>
public sealed record LearnOutcome(CommandResult Result, string ProposalId, IReadOnlyList<TunedSettingInfo> Settings,
    string Before, string After, bool CanApply, string Problem, IReadOnlyList<string> StillRejected, int Accepted, int Confirmed)
{
    public static LearnOutcome Refused(CommandResult r) => new(r, "", [], "", "", false, "", [], 0, 0);

    /// <summary>Acceptable % (good parts that pass) and not acceptable % (bad parts that fail), now and with the
    /// proposal; -1 when not known.</summary>
    public double GoodPassBefore { get; init; } = -1;
    public double BadFailBefore { get; init; } = -1;
    public double GoodPassAfter { get; init; } = -1;
    public double BadFailAfter { get; init; } = -1;
}

public sealed record ApplyOutcome(CommandResult Result, string Job, int Version);
