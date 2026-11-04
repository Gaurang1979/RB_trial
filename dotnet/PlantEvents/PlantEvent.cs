using System.Text.Json.Serialization;

namespace PlantEvents;

/// <summary>
/// One event from a station to the plant server. Schema v1 of
/// docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md section 3: every event
/// names where it comes from, when, and its place in the station's
/// gap-free sequence, so the server can tell when events are missing.
/// The schema is a shared core: the station agent, the plant server, the
/// web dashboard and the mobile app all read it. A field that changes
/// meaning needs a new <see cref="CurrentSchema"/>.
/// </summary>
public abstract record PlantEvent
{
    public const int CurrentSchema = 1;

    /// <summary>The last millisecond a .NET or PostgreSQL time can hold (9999-12-31).</summary>
    public const long MaxTs = 253_402_300_799_999;

    /// <summary>The event kind, such as "inspection.result"; fixed per type.</summary>
    [JsonPropertyOrder(-10)]
    public string Kind => KindCore;

    /// <summary>Each kind's fixed name; protected so it is written once, as "kind".</summary>
    protected abstract string KindCore { get; }

    [JsonPropertyOrder(-9)]
    public int Schema { get; init; } = CurrentSchema;

    [JsonPropertyOrder(-8)] public string Plant { get; init; } = "";
    [JsonPropertyOrder(-7)] public string Line { get; init; } = "";
    [JsonPropertyOrder(-6)] public string Station { get; init; } = "";
    [JsonPropertyOrder(-5)] public string? Camera { get; init; }

    /// <summary>When it happened: UTC, milliseconds since 1970.</summary>
    [JsonPropertyOrder(-4)] public long Ts { get; init; }

    /// <summary>
    /// The station's sequence number, 1, 2, 3 … with no gaps; set by the
    /// station agent when it stores the event. A heartbeat is never stored:
    /// it carries the last number given out, so the server knows how far the
    /// station has got even while events are still queued.
    /// </summary>
    [JsonPropertyOrder(-3)] public long Seq { get; init; }

    /// <summary>
    /// The id of the station's queue file that gave <see cref="Seq"/>. A new
    /// file (a new disk, a reinstall) starts again at 1 with a new id, so the
    /// server keys events by station, queue id and seq.
    /// </summary>
    [JsonPropertyOrder(-3)] public string? QueueId { get; init; }

    [JsonPropertyOrder(-2)] public string? Job { get; init; }
    [JsonPropertyOrder(-1)] public int? JobVersion { get; init; }

    /// <summary>Problems that make the event unusable; empty when it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (Schema != CurrentSchema) p.Add($"schema {Schema} is not {CurrentSchema}");
        if (string.IsNullOrWhiteSpace(Plant)) p.Add("plant is empty");
        if (string.IsNullOrWhiteSpace(Line)) p.Add("line is empty");
        if (string.IsNullOrWhiteSpace(Station)) p.Add("station is empty");
        if (Ts <= 0) p.Add("ts is not set");
        // Milliseconds; a value in micro- or nanoseconds would be past year 9999.
        else if (Ts > MaxTs) p.Add("ts is not in milliseconds (past year 9999)");
        if (Seq < 0) p.Add("seq is negative");
        if (Seq == 0 && this is not Heartbeat) p.Add("seq is not set");
        if (JobVersion is < 0) p.Add("job_version is negative");
        if (Seq > 0 && string.IsNullOrWhiteSpace(QueueId) && this is not Heartbeat) p.Add("a stored event has no queue_id");
        Check(p);
        return p;
    }

    /// <summary>Checks of the kind's own fields.</summary>
    protected virtual void Check(List<string> problems) { }

    protected static void Percent(List<string> p, string name, double? v)
    {
        if (v is { } x && (!double.IsFinite(x) || x < 0 || x > 100)) p.Add($"{name} {x} is not 0-100");
    }

    /// <summary>
    /// JSON has no NaN or infinity, so such a number would make the event
    /// impossible to store. A measurement that failed is left out of the
    /// event instead (and the tool's NG reason says why).
    /// </summary>
    protected static void Finite(List<string> p, string name, double? v)
    {
        if (v is { } x && !double.IsFinite(x)) p.Add($"{name} is not a finite number");
    }

    protected static void Defined<TEnum>(List<string> p, string name, TEnum v) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(v)) p.Add($"{name} {v} is not a known value");
    }
}

/// <summary>Why one tool said NG, as a string key of the localisation tables (result.&lt;tool&gt;.reason.*).</summary>
public sealed record NgReason(string Tool, string Reason);

/// <summary>One inspected part.</summary>
public sealed record InspectionResult : PlantEvent
{
    public const string KindName = "inspection.result";
    protected override string KindCore => KindName;

    /// <summary>The part's id or read code, when the job has one.</summary>
    public string? PartId { get; init; }
    [JsonRequired] public bool Ok { get; init; }
    public IReadOnlyList<NgReason> Reasons { get; init; } = [];
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();
    public double CycleMs { get; init; }
    /// <summary>Model version per AI tool, so every result can be traced to its model.</summary>
    public IReadOnlyDictionary<string, string> Models { get; init; } = new Dictionary<string, string>();
    /// <summary>The saved image, when one was saved (NG, or every Nth OK).</summary>
    public string? ImageId { get; init; }

    protected override void Check(List<string> p)
    {
        // JSON null reaches these despite their types; refuse it, never crash.
        if (Reasons is null || Values is null || Scores is null || Models is null)
        {
            p.Add("reasons, values, scores and models may not be null");
            return;
        }
        if (Reasons.Any(r => r is null)) { p.Add("an NG reason is null"); return; }
        // An NG must say why (the operator screens show the reason), and an
        // OK carries no NG reason: a result that says both is not trusted.
        if (!Ok && Reasons.Count == 0) p.Add("an NG result has no reason");
        if (Ok && Reasons.Count > 0) p.Add("an OK result has NG reasons");
        if (!double.IsFinite(CycleMs) || CycleMs < 0) p.Add("cycle_ms is not a number of 0 or more");
        foreach (var (k, v) in Values) Finite(p, $"values[{k}]", v);
        foreach (var (k, v) in Scores) Finite(p, $"scores[{k}]", v);
        foreach (var r in Reasons)
            if (string.IsNullOrWhiteSpace(r.Tool) || string.IsNullOrWhiteSpace(r.Reason)) p.Add("an NG reason has no tool or reason");
    }
}

public enum LineStateValue { Running, Stopped, Starved, Blocked, Changeover, Fault }

/// <summary>The line changed state; sent on change only.</summary>
public sealed record LineState : PlantEvent
{
    public const string KindName = "line.state";
    protected override string KindCore => KindName;
    [JsonRequired] public LineStateValue State { get; init; }
    /// <summary>Reason code, from the PLC or tapped by the operator.</summary>
    public string? Reason { get; init; }

    protected override void Check(List<string> p) => Defined(p, "state", State);
}

/// <summary>Counters since the shift started; every 10 seconds.</summary>
public sealed record CounterSnapshot : PlantEvent
{
    public const string KindName = "counter.snapshot";
    protected override string KindCore => KindName;
    [JsonRequired] public long Good { get; init; }
    [JsonRequired] public long Bad { get; init; }
    [JsonRequired] public long Total { get; init; }
    [JsonRequired] public long ShiftStart { get; init; }

    protected override void Check(List<string> p)
    {
        if (Good < 0 || Bad < 0) p.Add("a counter is negative");
        if (Total != Good + Bad) p.Add("total is not good + bad");
    }
}

public enum AlarmPriority { High, Medium, Low }

/// <summary>An alarm from the alarm catalogue (alarm.&lt;code&gt;.* in the string tables).</summary>
public abstract record AlarmEvent : PlantEvent
{
    public string Code { get; init; } = "";
    public AlarmPriority Priority { get; init; } = AlarmPriority.Medium;
    /// <summary>The user who acknowledged or cleared it; empty when the station did.</summary>
    public string? By { get; init; }

    protected override void Check(List<string> p)
    {
        if (string.IsNullOrWhiteSpace(Code)) p.Add("alarm code is empty");
        Defined(p, "priority", Priority);
    }
}

public sealed record AlarmRaised : AlarmEvent
{
    public const string KindName = "alarm.raised";
    protected override string KindCore => KindName;
}

public sealed record AlarmCleared : AlarmEvent
{
    public const string KindName = "alarm.cleared";
    protected override string KindCore => KindName;
}

public sealed record AlarmAcknowledged : AlarmEvent
{
    public const string KindName = "alarm.acknowledged";
    protected override string KindCore => KindName;

    protected override void Check(List<string> p)
    {
        base.Check(p);
        if (string.IsNullOrWhiteSpace(By)) p.Add("an acknowledgement has no user");
    }
}

/// <summary>The station changed to another job (product change).</summary>
public sealed record JobChanged : PlantEvent
{
    public const string KindName = "job.changed";
    protected override string KindCore => KindName;
    public string? From { get; init; }
    public string To { get; init; } = "";
    public string? By { get; init; }
    /// <summary>The e-signature, in 21 CFR Part 11 mode.</summary>
    public string? SignatureId { get; init; }

    protected override void Check(List<string> p)
    {
        if (string.IsNullOrWhiteSpace(To)) p.Add("job.changed has no new job");
    }
}

/// <summary>A new AI model went into use on a tool.</summary>
public sealed record ModelDeployed : PlantEvent
{
    public const string KindName = "model.deployed";
    protected override string KindCore => KindName;
    public string Tool { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string? By { get; init; }
    /// <summary>The validation result the model passed before deployment.</summary>
    public string? Validation { get; init; }

    protected override void Check(List<string> p)
    {
        if (string.IsNullOrWhiteSpace(Tool) || string.IsNullOrWhiteSpace(ModelVersion)) p.Add("model.deployed has no tool or model version");
    }
}

/// <summary>Camera, light and PC health; every 60 seconds.</summary>
public sealed record HealthSample : PlantEvent
{
    public const string KindName = "health.sample";
    protected override string KindCore => KindName;
    public double? FocusScore { get; init; }
    public double? Brightness { get; init; }
    public double? ExposureUs { get; init; }
    public long? FrameDrops { get; init; }
    public double? CpuPct { get; init; }
    public double? GpuPct { get; init; }
    public double? RamPct { get; init; }
    public double? DiskPct { get; init; }
    public double? TempC { get; init; }

    protected override void Check(List<string> p)
    {
        Percent(p, "cpu_pct", CpuPct);
        Percent(p, "gpu_pct", GpuPct);
        Percent(p, "ram_pct", RamPct);
        Percent(p, "disk_pct", DiskPct);
        Finite(p, "focus_score", FocusScore);
        Finite(p, "brightness", Brightness);
        Finite(p, "exposure_us", ExposureUs);
        Finite(p, "temp_c", TempC);
        if (FrameDrops is < 0) p.Add("frame_drops is negative");
    }
}

/// <summary>The station agent is alive; every 5 seconds, never stored.</summary>
public sealed record Heartbeat : PlantEvent
{
    public const string KindName = "heartbeat";
    protected override string KindCore => KindName;
    public string AgentVersion { get; init; } = "";
    /// <summary>Events stored and not yet accepted by the server.</summary>
    public long QueueDepth { get; init; }
    /// <summary>Events dropped unsent because they passed the retention time.</summary>
    public long Dropped { get; init; }
    /// <summary>The changeover the station would run, for the supervisor's check (CHANGEOVER-VERIFY.md); null with no job open.</summary>
    public ChangeoverAsk? Changeover { get; init; }

    protected override void Check(List<string> p) => Changeover?.CheckFields(p);
}

/// <summary>A second person accepted the changeover at the line PC while the plant server could not be reached; sent until
/// the server has it, which records it as that person's decision.</summary>
public sealed record ChangeoverLineAccept(string By, string Name, long At);

/// <summary>One batch value of a changeover, as the operator entered it.</summary>
public sealed record ChangeoverField(string Key, string Label, string Value);

/// <summary>
/// What a supervisor checks before the line may run (docs/vision-system/CHANGEOVER-VERIFY.md
/// section 2): the job, its version, the variant and every batch value, and its code. Each
/// changeover of the station has its own <see cref="Ask"/> number, so the same values checked
/// again later are a new request.
/// </summary>
public sealed record ChangeoverAsk
{
    public const int MaxFields = 32;

    public string Code { get; init; } = "";
    public int Ask { get; init; }
    public string Job { get; init; } = "";
    public int JobVersion { get; init; }
    /// <summary>SHA-256 (lower-case hex) of the job file, or empty from a station that sends none; part of the code only when set (CV-11).</summary>
    public string JobDigest { get; init; } = "";
    public string Variant { get; init; } = "";
    public IReadOnlyList<ChangeoverField> Batch { get; init; } = [];
    public string Operator { get; init; } = "";
    public long ChangedAt { get; init; }
    /// <summary>Set while a second person's acceptance at the line PC is not yet recorded by the server; not part of the code.</summary>
    public ChangeoverLineAccept? AcceptedAtLine { get; init; }

    /// <summary>A record with its code worked out.</summary>
    public static ChangeoverAsk Of(string job, int jobVersion, string variant, IReadOnlyList<ChangeoverField> batch, int ask, string op, long changedAt, string jobDigest = "") =>
        new() { Code = CodeOf(job, jobVersion, variant, batch, jobDigest), JobDigest = jobDigest, Ask = ask, Job = job, JobVersion = jobVersion, Variant = variant, Batch = batch, Operator = op, ChangedAt = changedAt };

    /// <summary>
    /// The lower-case hex SHA-256 of the canonical form: job, version, variant, then each field's key,
    /// label and value, each written as its UTF-8 byte count, ':' and its bytes; then the job file's digest the
    /// same way, only when there is one (so a record without a digest keeps the code it always had). The plant
    /// server works it out the same way, so no JSON escaping is involved.
    /// </summary>
    public static string CodeOf(string job, int jobVersion, string variant, IEnumerable<ChangeoverField> batch, string jobDigest = "")
    {
        var b = new List<byte>();
        void Add(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            b.AddRange(System.Text.Encoding.ASCII.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            b.AddRange(bytes);
        }
        Add(job);
        Add(jobVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(variant);
        foreach (var f in batch) { Add(f.Key); Add(f.Label); Add(f.Value); }
        if (jobDigest.Length > 0) Add(jobDigest);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b.ToArray()));
    }

    /// <summary>Problems of the record; empty when the plant server takes it. Lengths are in characters (code points), as
    /// the server counts them, and no text may hold a NUL or half of a surrogate pair (the server refuses both).</summary>
    public void CheckFields(List<string> p)
    {
        if (Code.Length != 64 || !Code.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) p.Add("changeover.code is not 64 lower-case hex");
        else if (Code != CodeOf(Job, JobVersion, Variant, Batch, JobDigest)) p.Add("changeover.code does not match its record");
        if (Ask < 1) p.Add("changeover.ask is below 1");
        if (!Text(Job, 1, 128)) p.Add("changeover.job is not 1-128 characters");
        if (JobVersion < 0) p.Add("changeover.job_version is negative");
        if (JobDigest.Length > 0 && (JobDigest.Length != 64 || !JobDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            p.Add("changeover.job_digest is not empty or 64 lower-case hex");
        if (!Text(Variant, 0, 256)) p.Add("changeover.variant is not text of at most 256 characters");
        if (Batch.Count > MaxFields) p.Add($"changeover.batch has over {MaxFields} fields");
        foreach (var f in Batch)
            if (!Text(f.Key, 1, 64) || !Text(f.Label, 0, 128) || !Text(f.Value, 0, 256))
            {
                p.Add($"the value of {f.Label} is too long or holds a character that cannot be sent");
                break;
            }
        if (!Text(Operator, 0, 64)) p.Add("changeover.operator is not text of at most 64 characters");
        if (ChangedAt < 0) p.Add("changeover.changed_at is negative");
        if (AcceptedAtLine is { } l && (!Text(l.By, 1, 64) || !Text(l.Name, 0, 128) || l.At <= 0))
            p.Add("changeover.accepted_at_line needs a user of 1-64 characters, a name of at most 128 and a time");
    }

    static bool Text(string s, int lo, int hi)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++, n++)
        {
            var c = s[i];
            if (c == '\0') return false;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(c)) return false;
        }
        return n >= lo && n <= hi;
    }
}
