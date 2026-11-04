using System.Text.Json;
using PlantEvents;
using StationAgent;
using VisionCore;

namespace VisionStudio.Services;

/// <summary>Start was refused because the changeover is not checked yet (or was not accepted).</summary>
public sealed class ChangeoverException(string message) : InvalidOperationException(message);

/// <summary>What the Run screen shows about the changeover check.</summary>
public enum ChangeoverState { NotNeeded, Waiting, Accepted, Refused, NoServer }

/// <summary>The changeover check as the station sees it: its state, the short code both screens show, who decided,
/// and why the line still waits when the reason is on this PC (values that cannot be sent, a state that cannot be kept).</summary>
public sealed record ChangeoverStatus(bool Required, ChangeoverState State, string ShortCode, string? By, long? At, string? Note, string? Problem = null,
    bool AtLine = false);

/// <summary>
/// A supervisor checks each changeover before the line may run (docs/vision-system/CHANGEOVER-VERIFY.md, owner
/// 2026-10-31). The station's record is the job, its version, the variant and every batch value; each new record
/// is a new changeover with its own ask number, sent in every heartbeat until the plant server answers. Start is
/// allowed only when the check is off for the line, or the server accepted exactly this record and ask.
/// In doubt the check is on (C4): before the server has ever answered, while it cannot be reached, and when the
/// plant server was taken out of the settings after it had asked for the check. A station that never had a plant
/// server has no check, since nobody could ever accept.
/// The last answer is kept in the data folder. After a restart it counts only while the server cannot be reached;
/// with the server reachable the line waits for a fresh answer (a few seconds), so an edited file opens nothing.
/// While the server cannot be reached, a second person may accept at the line PC (owner, 2026-11-01); the station
/// sends that acceptance with the record until the server has recorded it. A refusal the server gave meanwhile wins
/// for the next start.
/// </summary>
public sealed class ChangeoverGate
{
    readonly string _file;
    readonly bool _configured;
    readonly Func<bool> _connected;
    readonly TimeProvider _time;
    readonly object _lock = new();
    Saved _s;
    // The stored answer has not been confirmed by the server since this process started.
    bool _fromFile;
    // The state could not be written: a restart could bring back an older answer, so the line waits.
    bool _saveFailed;
    // The server answered since this process started, so its "check off" is current.
    bool _heard;
    string? _unsendable;

    sealed class Saved
    {
        public bool Required { get; set; } = true;
        /// <summary>A plant server once said the check is on; taking the server out of the settings does not end it.</summary>
        public bool EverRequired { get; set; }
        public int Ask { get; set; }
        public string Code { get; set; } = "";
        public long ChangedAt { get; set; }
        public string Operator { get; set; } = "";
        public ChangeoverAnswer? Answer { get; set; }
        /// <summary>The second person's acceptance at the line PC, for <see cref="Code"/> and <see cref="Ask"/>.</summary>
        public ChangeoverLineAccept? LineAccept { get; set; }
    }

    public ChangeoverGate(string dataFolder, bool configured, Func<bool> connected, TimeProvider? time = null)
    {
        _file = Path.Combine(dataFolder, "changeover.json");
        _configured = configured;
        _connected = connected;
        _time = time ?? TimeProvider.System;
        _s = Load(_file);
        _fromFile = _s.Answer is not null;
    }

    /// <summary>Raised when the state changes (an answer, a new record); on the caller's or the agent's thread.</summary>
    public event Action? Changed;

    /// <summary>The record and ask of the current changeover; null with no job open, or values the plant server would
    /// refuse (then <see cref="Status"/> says why). Sent in each heartbeat.</summary>
    public ChangeoverAsk? Current { get; private set; }

    /// <summary>
    /// The job and batch values now in force. A record different from the last one starts a new changeover (a new ask
    /// number, no answer yet); the same record keeps its answer. <paramref name="op"/> is who made the change, when
    /// known, so the server never lets them accept their own changeover.
    /// </summary>
    public void Update(Job? job, BatchFields batch, string variant = "", string op = "")
    {
        bool changed;
        lock (_lock)
        {
            if (job is null) { changed = Current is not null; Current = null; _unsendable = null; }
            else
            {
                var fields = job.BatchFields().Select(f => new ChangeoverField(f.Key, f.Label, batch.Get(f.Key) ?? "")).ToList();
                var code = ChangeoverAsk.CodeOf(job.Name, job.Version, variant, fields, job.Digest);
                changed = code != _s.Code || _s.Ask == 0;
                if (changed)
                {
                    _s.Code = code;
                    _s.Ask = NextAsk();
                    _s.ChangedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
                    _s.Operator = op;
                    _s.Answer = null;
                    _s.LineAccept = null;
                    _fromFile = false;
                    Save();
                }
                else if (op.Length > 0 && _s.Operator.Length == 0)
                {
                    // The same values entered again by a named person: they made this changeover.
                    _s.Operator = op;
                    Save();
                }
                var ask = ChangeoverAsk.Of(job.Name, job.Version, variant, fields, _s.Ask, _s.Operator, _s.ChangedAt, job.Digest) with { AcceptedAtLine = PendingLineAccept() };
                var problems = new List<string>();
                ask.CheckFields(problems);
                _unsendable = problems.Count > 0 ? string.Join("; ", problems) : null;
                changed |= Current is null != _unsendable is not null || Current?.Operator != ask.Operator;
                Current = _unsendable is null ? ask : null;
            }
        }
        if (changed) Raise();
    }

    /// <summary>The server's answer to a heartbeat. Whether the check is on is taken from any answer; a decision only
    /// counts for the exact record and ask it names, so an answer about an earlier changeover never opens this one.</summary>
    public void OnAnswer(ChangeoverAnswer a)
    {
        lock (_lock)
        {
            var same = a.Code == _s.Code && a.Ask == _s.Ask;
            if (same) _fromFile = false;
            _heard = true;
            var ever = _s.EverRequired || a.Required;
            if (a.Required == _s.Required && ever == _s.EverRequired && (!same || a == _s.Answer)) return;
            _s.Required = a.Required;
            _s.EverRequired = ever;
            if (same)
            {
                _s.Answer = a;
                // The server has decided (it recorded the acceptance at the line, or someone decided first): stop sending it.
                if (a.State is ChangeoverAnswer.Accepted or ChangeoverAnswer.Refused && Current is not null)
                    Current = Current with { AcceptedAtLine = null };
            }
            Save();
        }
        Raise();
    }

    /// <summary>After a refusal, the operator says the values are right: the same record is asked again as a new request,
    /// in <paramref name="op"/>'s name when known.</summary>
    public bool AskAgain(string op = "")
    {
        lock (_lock)
        {
            if (Current is null || _s.Answer?.State != ChangeoverAnswer.Refused) return false;
            _s.Ask = NextAsk();
            _s.ChangedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
            if (op.Length > 0) _s.Operator = op;
            _s.Answer = null;
            _s.LineAccept = null;
            Current = Current with { Ask = _s.Ask, ChangedAt = _s.ChangedAt, Operator = _s.Operator, AcceptedAtLine = null };
            Save();
        }
        Raise();
        return true;
    }

    /// <summary>
    /// A second person accepts the current changeover at the line PC: only while the check waits because the plant server
    /// cannot be reached, only for the code they were shown, and never the operator who made the change. The caller has
    /// checked who they are and their role. Returns why not, or null when accepted.
    /// </summary>
    public string? AcceptAtLine(string shortCode, string by, string name)
    {
        lock (_lock)
        {
            if (Current is null) return "There is no changeover to accept.";
            var s = Status();
            if (s.Problem is { } problem) return problem;
            if (s.State != ChangeoverState.NoServer) return "Accept at the line only while the plant server cannot be reached; use the web page or the app.";
            if (!string.Equals(shortCode, s.ShortCode, StringComparison.OrdinalIgnoreCase)) return "The values changed after the check was shown. Look again.";
            if (string.Equals(by.Trim(), _s.Operator.Trim(), StringComparison.OrdinalIgnoreCase) && _s.Operator.Trim().Length > 0)
                return "A second person must accept: not the one who made the change.";
            _s.LineAccept = new ChangeoverLineAccept(by, name, _time.GetUtcNow().ToUnixTimeMilliseconds());
            _fromFile = false;
            Current = Current with { AcceptedAtLine = _s.LineAccept };
            Save();
        }
        Raise();
        return null;
    }

    ChangeoverLineAccept? PendingLineAccept() =>
        _s.LineAccept is { } l && _s.Answer?.State is not (ChangeoverAnswer.Accepted or ChangeoverAnswer.Refused) ? l : null;

    /// <summary>Why the line may not start with the current record; null when it may.</summary>
    public string? Refusal()
    {
        var s = Status();
        if (s.Problem is { } problem) return problem;
        return s.State switch
        {
            ChangeoverState.NotNeeded or ChangeoverState.Accepted => null,
            ChangeoverState.NoServer => "Supervisor check needed: the plant server cannot be reached.",
            ChangeoverState.Refused => $"The supervisor did not accept this changeover{(string.IsNullOrWhiteSpace(s.Note) ? "" : ": " + s.Note)}. Correct the values, or ask again.",
            _ => $"Waiting for a supervisor to check this changeover (code {s.ShortCode}).",
        };
    }

    public ChangeoverStatus Status()
    {
        lock (_lock)
        {
            var code = _s.Code.Length >= 8 ? _s.Code[..8] : _s.Code;
            if (!_configured)
                return !_s.EverRequired ? new(false, ChangeoverState.NotNeeded, code, null, null, null)
                    : _s.LineAccept is { } la ? new(true, ChangeoverState.Accepted, code, la.Name.Length > 0 ? la.Name : la.By, la.At, null, null, AtLine: true)
                    : new(true, ChangeoverState.NoServer, code, null, null, null);
            var connected = _connected();
            var a = _s.Answer;
            // A stored answer counts only while the server cannot confirm it.
            if (a is not null && _fromFile && connected) a = null;
            if (a?.State == ChangeoverAnswer.NotNeeded || (!_s.Required && a?.State != ChangeoverAnswer.Refused && (_heard || !connected)))
                return new(false, ChangeoverState.NotNeeded, code, null, null, null);
            var problem = _unsendable is { } u ? "These values cannot be sent for the supervisor's check: " + u
                : _saveFailed ? "The changeover check cannot be stored on this PC (data folder). Call your engineer." : null;
            // A second person's acceptance at the line PC, until the server has decided; a refusal from the server wins.
            if (problem is null && a?.State is not (ChangeoverAnswer.Accepted or ChangeoverAnswer.Refused)
                && _s.LineAccept is { } l && (!_fromFile || !connected))
                return new(true, ChangeoverState.Accepted, code, l.Name.Length > 0 ? l.Name : l.By, l.At, null, null, AtLine: true);
            return a?.State switch
            {
                _ when problem is not null => new(true, ChangeoverState.Waiting, code, null, null, null, problem),
                ChangeoverAnswer.Accepted => new(true, ChangeoverState.Accepted, code, a.By, a.At, a.Note),
                ChangeoverAnswer.Refused => new(true, ChangeoverState.Refused, code, a.By, a.At, a.Note),
                _ => new(true, connected ? ChangeoverState.Waiting : ChangeoverState.NoServer, code, null, null, null),
            };
        }
    }

    // Never an ask number used before, even when the file was lost: an old acceptance of the same values must not
    // open a new changeover. At least the seconds since 2026, and above the last one.
    int NextAsk()
    {
        var floor = (int)Math.Clamp((_time.GetUtcNow() - Epoch).TotalSeconds, 1, int.MaxValue - 1);
        return Math.Max(_s.Ask + 1, floor);
    }

    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    void Raise()
    {
        if (Changed is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList())
            try { ((Action)h)(); } catch (Exception) { }
    }

    // Written whole to a temporary file and moved, so a power cut leaves the old file or the new one.
    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_s));
            File.Move(tmp, _file, true);
            _saveFailed = false;
        }
        catch (Exception) { _saveFailed = true; }
    }

    // A missing or broken file starts with the check on and nothing accepted.
    static Saved Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }
}
