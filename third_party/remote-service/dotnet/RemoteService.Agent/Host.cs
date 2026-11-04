namespace Sundaram.RemoteService;

/// <summary>The engineer's role, from their relay account. A role never widens what the host allows.</summary>
public enum RemoteRole { Service, Oem }

/// <summary>What the host's Service-code check answered. <see cref="Held"/>: too many wrong codes, the code was not checked.</summary>
public enum CodeResult { Accepted, Wrong, Held }

/// <summary>Checks today's Service code (the host knows its licence key and secret, and counts wrong codes in its own file).</summary>
public interface IServiceCodeCheck
{
    CodeResult Check(string code, string engineer, RemoteRole role);
}

/// <summary>One entry of the remote user log. <see cref="Data"/> holds the facts in fields; <see cref="Detail"/> one line for people.</summary>
public sealed record AuditEntry(DateTimeOffset Time, string Session, string User, RemoteRole Role, string Kind, string Detail,
    IReadOnlyDictionary<string, string> Data);

/// <summary>The host's tamper-evident log (Vision System: the HMAC-chained results ledger). It must not throw for a full disk
/// silently: an entry that cannot be written makes the agent refuse the action it records.</summary>
public interface IAuditSink
{
    void Write(AuditEntry entry);
}

/// <summary>
/// A host log that is a hash chain (Vision System's results ledger) tells its current head. The agent puts the head as it
/// stands just before each session's end entry into that entry (it covers every action of the session); the relay keeps it and ERPNext stores it with the session (docs/COMPARABLE-PROJECTS.md item 2), so a
/// later rewrite of the chain on the machine no longer matches the copy kept outside.
/// </summary>
public interface IAuditChain
{
    /// <summary>The chain's newest hash (hex), or null when the chain cannot be read.</summary>
    string? Head { get; }
}

/// <summary>Whether the machine may be changed now (uploads, installs, actions that need it). When the host cannot tell, the
/// answer must be "no" (line safety).</summary>
public interface IMachineState
{
    bool SafeToChange(out string reason);
}

/// <summary>What the operator is asked on the machine's own screen. Never names a feature or a process step (screen
/// shielding): only what will change and who asked.</summary>
public sealed record ConfirmRequest(string Title, string What, string RequestedBy);

/// <summary>Asks the operator on the machine's own screen and waits for the answer (false: rejected or no answer in time).</summary>
public interface IOperatorConfirmation
{
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);
}

/// <summary>
/// A folder the engineer may read, and maybe write. Only these folders are reachable; paths are checked by the agent.
/// </summary>
/// <param name="Name">The name the engineer sees (e.g. "logs").</param>
/// <param name="Folder">The folder on this PC.</param>
/// <param name="Writable">Uploads allowed (role OEM only).</param>
/// <param name="Extensions">File types an upload may have (".job", ".onnx"); empty = any. Downloads are not limited.</param>
/// <param name="ConfirmWrites">The operator confirms each upload on the machine's screen.</param>
/// <param name="NeedsStopped">Uploads only while <see cref="IMachineState"/> says the machine is safe to change.</param>
/// <param name="MaxBytes">Largest file to download or upload.</param>
/// <param name="CheckUpload">The host's own check of an uploaded file before it is put in place (a signature, a format);
/// returns why it is refused, or null.</param>
public sealed record FileRoot(string Name, string Folder, bool Writable = false, IReadOnlyList<string>? Extensions = null,
    bool ConfirmWrites = true, bool NeedsStopped = true, long MaxBytes = 256L << 20, Func<string, string?>? CheckUpload = null);

/// <summary>
/// A named action the engineer may run (restart a service, clear temp files, check drivers). Nothing else can be run:
/// there is no command prompt.
/// </summary>
/// <param name="Run">Does the work; returns one line for the engineer. Arguments are checked by the action itself.</param>
public sealed record RemoteAction(string Name, string Title, string Description, Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> Run,
    bool NeedsStopped = false, bool NeedsConfirmation = false, IReadOnlyList<RemoteRole>? Roles = null)
{
    public bool AllowedFor(RemoteRole role) => Roles is null || Roles.Contains(role);
}

/// <summary>Rows of a read-only data view.</summary>
public sealed record DataTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);

/// <summary>
/// A read-only view of the host's data (inspections, batches, alarms). The host writes the query; the engineer only fills the
/// declared parameters. There is no way to change data through a view.
/// </summary>
public sealed record DataView(string Name, string Title, IReadOnlyList<string> Parameters,
    Func<IReadOnlyDictionary<string, string>, int, CancellationToken, Task<DataTable>> Query, IReadOnlyList<RemoteRole>? Roles = null)
{
    public bool AllowedFor(RemoteRole role) => Roles is null || Roles.Contains(role);
}

/// <summary>A field of a record that may be corrected. <see cref="Choices"/>: the only values allowed (empty: free text up to
/// <see cref="MaxLength"/>).</summary>
public sealed record CorrectionField(string Name, string Title, IReadOnlyList<string>? Choices = null, int MaxLength = 200);

/// <summary>One field's value before and after a correction.</summary>
public sealed record FieldChange(string Old, string New);

/// <summary>A proposed correction of one record (DESIGN.md section 3, capability 8): what changes, why, and who asks.</summary>
public sealed record Correction(string Id, string Kind, string Record, IReadOnlyDictionary<string, FieldChange> Changes, string Reason,
    string RequestedBy, RemoteRole Role);

/// <summary>
/// A kind of record the engineer may propose to correct (batch details, a part's reading noted wrongly). <b>Never an edit:</b>
/// the host adds a new correcting entry next to the original, chained in its ledger, after a supervisor on site approves; the
/// original stays as it was. Fields that decide OK or NG should not be declared here at all.
/// </summary>
/// <param name="Find">The record's current values of the declared fields, or null when there is no such record.</param>
/// <param name="Append">Adds the correcting entry (with the approver's name) and returns its reference in the host's ledger.</param>
/// <param name="Validate">The host's own check of the new values; returns why they are refused, or null.</param>
public sealed record CorrectionKind(string Name, string Title, IReadOnlyList<CorrectionField> Fields,
    Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>?>> Find,
    Func<Correction, string, CancellationToken, Task<string>> Append,
    Func<Correction, string?>? Validate = null, bool NeedsStopped = false, IReadOnlyList<RemoteRole>? Roles = null)
{
    /// <summary>OEM only unless the host names the roles (DESIGN.md section 3).</summary>
    public bool AllowedFor(RemoteRole role) => (Roles ?? [RemoteRole.Oem]).Contains(role);
}

/// <summary>
/// Asks a supervisor at the machine to approve a correction (4-eyes): the screen shows the record, each field's old and new
/// value, the reason and who asked; the supervisor approves with their own sign-in (PIN, card). Returns the approver's name,
/// or null when rejected or not answered in time.
/// </summary>
public interface ISupervisorApproval
{
    Task<string?> ApproveAsync(Correction correction, CancellationToken ct);
}

/// <summary>Writes the diagnostics bundle (logs, versions, settings with secrets removed) into the zip.</summary>
public delegate Task DiagnosticsWriter(System.IO.Compression.ZipArchive zip, CancellationToken ct);

/// <summary>Everything the host supplies. Only <see cref="Code"/> and <see cref="Audit"/> are required; a capability whose part is
/// missing is off.</summary>
public sealed class RemoteServiceHost
{
    public required IServiceCodeCheck Code { get; init; }
    public required IAuditSink Audit { get; init; }
    /// <summary>Missing: nothing that needs the machine stopped is ever allowed.</summary>
    public IMachineState? Machine { get; init; }
    /// <summary>Missing: nothing that needs the operator's confirmation is ever allowed.</summary>
    public IOperatorConfirmation? Operator { get; init; }
    public IReadOnlyList<FileRoot> Roots { get; init; } = [];
    public IReadOnlyList<RemoteAction> Actions { get; init; } = [];
    public IReadOnlyList<DataView> Views { get; init; } = [];
    public DiagnosticsWriter? Diagnostics { get; init; }
    /// <summary>Signed software installs (OEM only); missing: off.</summary>
    public IPackageInstaller? Packages { get; init; }
    /// <summary>Records that may be corrected (append-only, supervisor approval); empty: off.</summary>
    public IReadOnlyList<CorrectionKind> Corrections { get; init; } = [];
    /// <summary>Missing: no correction is ever applied.</summary>
    public ISupervisorApproval? Supervisor { get; init; }
    /// <summary>The product name shown in the console ("Vision System").</summary>
    public string Product { get; init; } = "";
}
