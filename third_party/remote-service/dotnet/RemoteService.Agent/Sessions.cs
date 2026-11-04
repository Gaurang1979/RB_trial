using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sundaram.RemoteService;

/// <summary>One signed-in engineer on this machine.</summary>
public sealed record RemoteSession(string Id, string User, RemoteRole Role, string Reason, DateTimeOffset Started)
{
    /// <summary>What this role may use (DESIGN.md section 3): uploads only for OEM.</summary>
    public bool MayWrite => Role == RemoteRole.Oem;
}

/// <summary>
/// The user log (DESIGN.md section 4): every entry goes to the host's audit sink first, then to the relay. When the host cannot
/// write it, the call that wanted to record it is refused, so nothing remote happens unrecorded.
/// <para>Delivery to the relay is confirmed (docs/COMPARABLE-PROJECTS.md item 3): each entry carries this run's
/// <see cref="Epoch"/> and a number; the relay answers "ack" with the highest number it has saved, and only then is an entry
/// dropped here. What is not confirmed is sent again on the next link and kept in rs-log.json, so a restart loses nothing.
/// The relay ignores an entry it already has.</para>
/// </summary>
public sealed class RemoteLog
{
    public const string FileName = "rs-log.json";
    const int MaxKept = 1000;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly IAuditSink _sink;
    readonly TimeProvider _time;
    readonly string? _path;
    readonly object _lock = new();
    readonly List<Pending> _pending = [];
    long _seq;

    /// <summary>One entry the relay has not confirmed yet. <see cref="SentOn"/>: the link it was last sent on.</summary>
    public sealed record Pending(string Epoch, long Seq, AuditEntry Entry)
    {
        [JsonIgnore] public int SentOn { get; set; }
    }

    public RemoteLog(IAuditSink sink, TimeProvider time, string? dataFolder = null)
    {
        _sink = sink;
        _time = time;
        Epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        if (dataFolder is null) return;
        _path = Path.Combine(dataFolder, FileName);
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<List<Pending>>(File.ReadAllBytes(_path), Json) is { } kept)
                _pending.AddRange(kept.TakeLast(MaxKept));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* the host's own log has them */ }
    }

    /// <summary>This run's mark: numbers start again at 1 in each run, so the relay keeps them apart by it.</summary>
    public string Epoch { get; }

    /// <summary>Raised when an entry is queued for the relay (the link sends it).</summary>
    public event Action? Queued;

    public int Unconfirmed { get { lock (_lock) return _pending.Count; } }

    /// <summary>The host chain's head now, when the host's log is a chain.</summary>
    public string? ChainHead
    {
        get
        {
            try { return (_sink as IAuditChain)?.Head; }
            catch (Exception) { return null; }
        }
    }

    public AuditEntry Write(RemoteSession? s, string user, RemoteRole role, string kind, string detail, IReadOnlyDictionary<string, string>? data = null)
    {
        var e = new AuditEntry(_time.GetUtcNow(), s?.Id ?? "", s?.User ?? user, s?.Role ?? role, kind, detail, data ?? new Dictionary<string, string>());
        _sink.Write(e);   // throws: the caller refuses what it was about to do
        if (e.Session.Length > 0)
        {
            lock (_lock)
            {
                _pending.Add(new Pending(Epoch, ++_seq, e));
                // never unbounded: the host's own log keeps everything; the relay's copy loses the oldest
                if (_pending.Count > MaxKept) _pending.RemoveRange(0, _pending.Count - MaxKept);
                Save();
            }
            Queued?.Invoke();
        }
        return e;
    }

    /// <summary>The entries not yet sent on link <paramref name="link"/>, in order; each is marked as sent on it.</summary>
    public List<Pending> ToSend(int link)
    {
        lock (_lock)
        {
            var due = _pending.Where(p => p.SentOn != link).ToList();
            foreach (var p in due) p.SentOn = link;
            return due;
        }
    }

    /// <summary>The relay has saved every entry of <paramref name="epoch"/> up to <paramref name="seq"/>.</summary>
    public void Confirmed(string epoch, long seq)
    {
        lock (_lock)
        {
            if (_pending.RemoveAll(p => p.Epoch == epoch && p.Seq <= seq) > 0) Save();
        }
    }

    void Save()
    {
        if (_path is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_pending, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kept in memory; sent while this run lasts */ }
    }
}

/// <summary>
/// Remote sessions: opened with today's Service code, ended at sign-out, after <see cref="RemoteServiceOptions.SessionIdle"/>
/// without a request, after <see cref="RemoteServiceOptions.SessionMax"/>, or for everyone when the link drops.
/// </summary>
public sealed class RemoteSessions(RemoteServiceOptions options, RemoteLog log, TimeProvider time)
{
    readonly ConcurrentDictionary<string, (RemoteSession Session, DateTimeOffset Seen)> _byToken = new();

    /// <summary>Raised when a session opens or ends (the host shows or hides its "Remote service active" badge).</summary>
    public event Action? Changed;

    /// <summary>True while any session is open: the host's neutral badge. Never who.</summary>
    public bool Active { get { Sweep(); return !_byToken.IsEmpty; } }

    /// <summary>When the oldest open session started, or null.</summary>
    public DateTimeOffset? Since { get { Sweep(); return _byToken.IsEmpty ? null : _byToken.Values.Min(v => v.Session.Started); } }

    /// <summary>
    /// For the host's "Remote service active" badge, from any thread, including one that inspects parts: reads memory only and
    /// never ends a session (ending one writes the host's log). A session past its time stays in the badge until the agent's own
    /// sweep (every 30 s, off the host's threads) ends it.
    /// </summary>
    public (bool Active, DateTimeOffset? Since) Badge
    {
        get
        {
            var open = _byToken.Values.ToArray();
            return (open.Length > 0, open.Length > 0 ? open.Min(v => v.Session.Started) : null);
        }
    }

    public string Open(string user, RemoteRole role, string reason)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var s = new RemoteSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), user, role, reason, time.GetUtcNow());
        log.Write(s, user, role, "session_start", $"Remote service session started: {user} ({Name(role)})",
            new Dictionary<string, string> { ["reason"] = reason });
        _byToken[token] = (s, s.Started);
        Changed?.Invoke();
        return token;
    }

    /// <summary>The session of this cookie, if open; counts as use.</summary>
    public RemoteSession? Find(string? token, string user)
    {
        Sweep();
        if (string.IsNullOrEmpty(token) || !_byToken.TryGetValue(token, out var v)) return null;
        // a cookie copied to another relay account is not that account's session
        if (!string.Equals(v.Session.User, user, StringComparison.Ordinal)) return null;
        _byToken[token] = (v.Session, time.GetUtcNow());
        return v.Session;
    }

    /// <summary>The session of this cookie without counting as use (the host's forwarded pages check it this way too).</summary>
    public RemoteSession? Peek(string? token)
    {
        Sweep();
        return !string.IsNullOrEmpty(token) && _byToken.TryGetValue(token, out var v) ? v.Session : null;
    }

    public void SignOut(string? token)
    {
        if (!string.IsNullOrEmpty(token) && _byToken.TryRemove(token, out var v)) Ended(v.Session, "sign-out");
    }

    /// <summary>Every session ends (the link dropped: nobody can be using them).</summary>
    public void EndAll(string reason)
    {
        foreach (var token in _byToken.Keys)
            if (_byToken.TryRemove(token, out var v)) Ended(v.Session, reason);
    }

    void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (token, v) in _byToken)
        {
            var why = now - v.Session.Started > options.SessionMax ? "max" : now - v.Seen > options.SessionIdle ? "idle" : null;
            if (why is not null && _byToken.TryRemove(token, out _)) Ended(v.Session, why);
        }
    }

    void Ended(RemoteSession s, string reason)
    {
        try
        {
            var data = new Dictionary<string, string> { ["endReason"] = reason };
            // the head covers every entry of the session; kept outside the machine it shows any later rewrite
            if (log.ChainHead is { Length: > 0 } head) data["chainHead"] = head;
            log.Write(s, s.User, s.Role, "session_end", $"Remote service session ended: {s.User} ({reason})", data);
        }
        catch (Exception) { /* the session is over whatever the log says; the host's sink reports its own failure */ }
        Changed?.Invoke();
    }

    public static string Name(RemoteRole r) => r == RemoteRole.Oem ? "oem" : "service";
}
