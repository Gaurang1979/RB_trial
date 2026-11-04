using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using VisionStudio.Remote;
using VisionStudio.Remote.V1;

namespace VisionSystem.Runtime;

/// <summary>A user of the line PC, as stored by the runtime: secrets only as salted hashes.</summary>
public sealed class RuntimeUser
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<Role> Roles { get; set; } = [];
    public string? PasswordHash { get; set; }
    public string? PinHash { get; set; }
    /// <summary>The badge number, hashed with the salt all cards share so a card can be found
    /// with one hash; never kept or written in clear.</summary>
    public string? CardHash { get; set; }
    /// <summary>The lines this user may watch on the browser dashboard (requirements 6.4 WEB-01, PT11-14):
    /// a line's plant id or its report name. Empty: every line.</summary>
    public List<string> Lines { get; set; } = [];
}

/// <summary>A token for plant software (MES, ERP), stored as its SHA-256 hash. With
/// <see cref="Operate"/> it may start, stop, change the job, trigger and set inputs; never setup.</summary>
public sealed class PlantToken
{
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Operate { get; set; }
}

/// <summary>
/// The runtime's users (runtime-users.json) and plant tokens (runtime-tokens.json) in the data
/// folder. The client never reads either file: it asks the runtime to sign in (ARCHITECTURE.md
/// section 2.2). Files are read again when they change, so a user added with
/// <c>vs-runtime add-user</c> can sign in without a restart.
/// </summary>
public sealed class AccessStore
{
    const int Iterations = 100_000;
    const int MinIterations = 10_000;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    readonly string _users, _tokens;
    readonly object _lock = new();
    (DateTime At, List<RuntimeUser> List) _userCache = (DateTime.MinValue, []);
    (DateTime At, List<PlantToken> List) _tokenCache = (DateTime.MinValue, []);

    public AccessStore(string dataFolder)
    {
        _users = Path.Combine(dataFolder, "runtime-users.json");
        _tokens = Path.Combine(dataFolder, "runtime-tokens.json");
    }

    public IReadOnlyList<RuntimeUser> Users() => Read(_users, ref _userCache);
    IReadOnlyList<PlantToken> Tokens() => Read(_tokens, ref _tokenCache);

    List<T> Read<T>(string path, ref (DateTime At, List<T> List) cache)
    {
        lock (_lock)
        {
            var at = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (at != cache.At)
            {
                // A broken file means nobody signs in, never everybody.
                try { cache = (at, at == DateTime.MinValue ? [] : JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Json) ?? []); }
                catch (Exception) { cache = (at, []); }
            }
            return cache.List;
        }
    }

    public RuntimeUser? Find(string name) =>
        Users().FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool Verify(string? hash, string secret)
    {
        if (string.IsNullOrEmpty(secret) || Parse(hash) is not (int n, byte[] salt, byte[] want)) return false;
        return CryptographicOperations.FixedTimeEquals(Derive(secret, salt, n, want.Length), want);
    }

    public static string Hash(string secret) => Hash(secret, RandomNumberGenerator.GetBytes(16), Iterations);

    static string Hash(string secret, byte[] salt, int n) =>
        $"{n}${Convert.ToBase64String(salt)}${Convert.ToBase64String(Derive(secret, salt, n, 32))}";

    static byte[] Derive(string secret, byte[] salt, int n, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, n, HashAlgorithmName.SHA256, length);

    static (int N, byte[] Salt, byte[] Hash)? Parse(string? hash)
    {
        // A broken or cut-short hash matches nothing: an empty hash part would match every secret.
        var parts = (hash ?? "").Split('$');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var n) || n < MinIterations) return null;
        try
        {
            var (salt, h) = (Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2]));
            return salt.Length >= 16 && h.Length >= 32 ? (n, salt, h) : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The user whose badge this is, or null (also when two users hold it). All cards share
    /// one salt, so this costs one hash per salt (normally one), not one per user. Card numbers are
    /// short, so a salt per user would barely slow a copy of the file being guessed: the file's
    /// permissions protect it.</summary>
    public RuntimeUser? FindByCard(string card)
    {
        card = RuntimeAccess.NormalizeCard(card);
        if (card.Length < RuntimeAccess.MinCardLength) return null;
        RuntimeUser? found = null;
        var matches = 0;
        var cards = Users().Select(u => (User: u, Hash: Parse(u.CardHash)))
            .Where(c => c.Hash is not null).Select(c => (c.User, Hash: c.Hash!.Value));
        foreach (var group in cards.GroupBy(c => (c.Hash.N, Salt: Convert.ToBase64String(c.Hash.Salt), c.Hash.Hash.Length)))
        {
            var got = Derive(card, Convert.FromBase64String(group.Key.Salt), group.Key.N, group.Key.Length);
            foreach (var c in group)
                if (CryptographicOperations.FixedTimeEquals(got, c.Hash.Hash)) { found ??= c.User; matches++; }
        }
        return matches == 1 ? found : null;
    }

    /// <summary>Adds or replaces a user (vs-runtime add-user). A null secret keeps the old one; an
    /// empty card removes the user's card. A card that is another user's is refused. Null lines keep
    /// the old ones; an empty list lets the user watch every line.</summary>
    public void SaveUser(string name, string fullName, IEnumerable<Role> roles, string? password, string? pin, string? card = null,
        IEnumerable<string>? lines = null)
    {
        lock (_lock)
        {
            var all = Read(_users, ref _userCache);
            var list = all.Where(u => !string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var old = Find(name);
            var cardHash = old?.CardHash;
            if (card is not null)
            {
                card = RuntimeAccess.NormalizeCard(card);
                if (card.Length == 0) cardHash = null;
                else
                {
                    if (card.Length < RuntimeAccess.MinCardLength)
                        throw new InvalidOperationException($"A card number has at least {RuntimeAccess.MinCardLength} characters.");
                    if (FindByCard(card) is { } holder && !string.Equals(holder.Name, name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"This card is already {holder.Name}'s.");
                    // the salt all cards share, so FindByCard needs one hash
                    var shared = all.Select(u => Parse(u.CardHash)).FirstOrDefault(p => p is not null);
                    cardHash = shared is (int n, byte[] salt, _) ? Hash(card, salt, n) : Hash(card);
                }
            }
            list.Add(new RuntimeUser
            {
                Name = name,
                FullName = fullName,
                Roles = roles.Distinct().ToList(),
                PasswordHash = password is null ? old?.PasswordHash : Hash(password),
                PinHash = pin is null ? old?.PinHash : Hash(pin),
                CardHash = cardHash,
                Lines = lines?.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    ?? old?.Lines ?? [],
            });
            Write(_users, list);
            _userCache = (DateTime.MinValue, []);
        }
    }

    /// <summary>Makes a new plant token and returns it; only its hash is kept, so it is shown once.</summary>
    public string AddToken(string name, bool operate)
    {
        var token = "vst_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (_lock)
        {
            var list = Read(_tokens, ref _tokenCache).Where(t => t.Name != name).ToList();
            list.Add(new PlantToken { Name = name, Hash = TokenHash(token), Operate = operate });
            Write(_tokens, list);
            _tokenCache = (DateTime.MinValue, []);
        }
        return token;
    }

    public PlantToken? FindToken(string token)
    {
        var h = Encoding.ASCII.GetBytes(TokenHash(token));
        return Tokens().FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t.Hash), h));
    }

    static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Who is calling: the client kind, the user (none for a plant token) and the rights. <paramref name="Service"/>:
/// a Sundaram engineer whose request the remote-service agent forwarded with its session (REQ-11; RemoteServiceRunner); the
/// user is not in the users file and the call only reads.</summary>
public sealed record Caller(ClientKind Client, RuntimeUser? User, bool OperateToken, bool Local, string? Token, bool Service = false)
{
    public IReadOnlyCollection<Role> Roles => User?.Roles ?? [];
    public bool Anonymous => Token is null;
    public bool MayOperate => RuntimeAccess.MayOperate(Client, Roles, OperateToken);
    public bool MayChangeSetup => RuntimeAccess.MayChangeSetup(Client, Roles);
    public bool MayDesignJobs => RuntimeAccess.MayDesignJobs(Client, Roles);
    public bool MayTestPlc => RuntimeAccess.MayTestPlc(Client, Roles);
    public bool MayLearn => RuntimeAccess.MayLearn(Client, Roles);
    /// <summary>Signed in to Vision Studio as Support or OEM with today's code (AS-04h): each of their actions is audited.</summary>
    public bool Support => Client == ClientKind.Studio && Roles.Any(RuntimeAccess.IsSupport);
    /// <summary>Who, for the audit trail: "name (OEM)".</summary>
    public string Who => User is null ? "" : Support ? $"{User.Name} ({string.Join(", ", Roles.Where(RuntimeAccess.IsSupport).Select(SupportCodes.Name))})" : User.Name;
}

/// <summary>
/// Today's Service / OEM code at a Vision Studio sign-in (AS-04h; Req 17.10 MRG-09): the Service role opens with the
/// service OTP, the OEM role with the factory OTP, for this PC's licence key. Wrong codes are counted per user in
/// <see cref="OtpAttempts"/> (on disk, held when the file is tampered with), apart from the remote support count.
/// No licence key or no secret: no code opens a role. <paramref name="audit"/> writes the line's audit trail.
/// </summary>
public sealed class SupportCodes(VisionStudio.Services.OtpAttempts attempts, Func<string?> licenceKey, Func<string?> serviceSecret,
    Func<string?> factorySecret, Action<string>? audit = null)
{
    public VisionStudio.Services.OemSignIn.Outcome Decide(string user, IEnumerable<Role> roles, string? code) =>
        attempts.Decide("studio:" + user, roles.Select(Name), code, licenceKey(), serviceSecret(), factorySecret());

    public void Audit(string detail)
    {
        try { audit?.Invoke(detail); } catch (Exception) { /* the audit trail reports its own faults */ }
    }

    public static string Name(Role r) => r == Role.Oem ? VisionStudio.Services.Roles.OEM : VisionStudio.Services.Roles.Service;
}

/// <summary>
/// Signed-in sessions and the access rules of every call (ARCHITECTURE.md section 2.2, PR #88):
/// <list type="bullet">
/// <item>No token on the local endpoint (named pipe or Unix socket) is the line PC's screen with
/// nobody signed in: it may read, never change. Over the network a token is always needed.</item>
/// <item>A changing call needs a Vision Studio session on the local endpoint with a role, or a plant
/// token with the operate right. A browser session never changes anything.</item>
/// <item>A user signed in over the network is a browser session, whatever the client says.</item>
/// <item>Sessions live in memory: after a restart every token is unknown (UNAUTHENTICATED) and the
/// client signs in again.</item>
/// </list>
/// </summary>
public sealed class Sessions(AccessStore store, RuntimeOptions options, TimeProvider? time = null, SupportCodes? codes = null)
{
    const int MaxFailures = 5;
    static readonly TimeSpan LockFor = TimeSpan.FromMinutes(1);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (Caller Caller, DateTimeOffset Seen)> _sessions = new();
    readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Until)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public AccessStore Store => store;

    /// <summary>True when the call came over the named pipe or the Unix socket, not over TCP.</summary>
    public static bool IsLocal(ServerCallContext ctx)
    {
        // A pipe or socket connection has no IP address; a TCP one always has one.
        var c = ctx.GetHttpContext().Connection;
        if (c.LocalIpAddress is not null || c.LocalPort != 0) return false;
        var ep = ctx.GetHttpContext().Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionEndPointFeature>()?.LocalEndPoint;
        return ep is not IPEndPoint;
    }

    static string? BearerToken(ServerCallContext ctx)
    {
        var h = ctx.RequestHeaders.GetValue("authorization");
        return h is not null && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && h.Length > 7 ? h[7..].Trim() : null;
    }

    /// <summary>The caller of a reading call. Throws UNAUTHENTICATED for no token over the network
    /// and for an unknown or expired token anywhere.</summary>
    public Caller Read(ServerCallContext ctx)
    {
        var local = IsLocal(ctx);
        var token = BearerToken(ctx);
        if (token is null)
        {
            if (!local) throw Unauthenticated("A token is needed over the network.");
            return new Caller(ClientKind.Studio, null, false, true, null);
        }
        if (_sessions.TryGetValue(token, out var s))
        {
            var now = _time.GetUtcNow();
            if (now - s.Seen > options.SessionIdle)
            {
                if (_sessions.TryRemove(token, out _) && s.Caller.Support)
                    codes?.Audit($"Signed out of Vision Studio after a long pause: {s.Caller.Who}");
                throw Unauthenticated("Signed out after a long pause. Please sign in again.");
            }
            // a session made on one endpoint is not taken to the other
            if (s.Caller.Local != local) throw Unauthenticated("Please sign in again.");
            _sessions[token] = (s.Caller, now);
            return s.Caller;
        }
        if (store.FindToken(token) is { } plant)
            return new Caller(ClientKind.PlantSoftware, null, plant.Operate, local, token);
        throw Unauthenticated("Please sign in again.");
    }

    /// <summary>The caller of a changing call (start, stop, change job, trigger, inputs).</summary>
    public Caller Operate(ServerCallContext ctx)
    {
        var c = Read(ctx);
        if (c.Anonymous) throw Unauthenticated("Sign in first.");
        if (!c.MayOperate)
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, c.Client == ClientKind.Browser
                ? "The browser dashboard only shows the line. Open Vision Studio on the line PC to change this."
                : "You may not change this. Ask your supervisor."));
        return c;
    }

    /// <summary>The caller of an NG review or learning call: Supervisor, QA, Engineer or Admin in Vision Studio
    /// (owner, 2026-10-31: the supervisor decides each NG and the job learns from it).</summary>
    public Caller Review(ServerCallContext ctx)
    {
        var c = Read(ctx);
        if (c.Anonymous) throw Unauthenticated("Sign in first.");
        if (!c.MayChangeSetup)
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, c.Client == ClientKind.Browser
                ? "The browser dashboard only shows the line. Open Vision Studio on the line PC to change this."
                : "Only a supervisor may decide bad parts. Ask your supervisor."));
        return c;
    }

    /// <summary>The caller of a learning session call (AS-04c, AS-04h): list and sort pictures, the reviews, start and stop
    /// learning, the setting. As <see cref="Review"/>, and Support or OEM opened with today's code.</summary>
    public Caller Learn(ServerCallContext ctx)
    {
        var c = Read(ctx);
        if (c.Anonymous) throw Unauthenticated("Sign in first.");
        if (!c.MayLearn)
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, c.Client == ClientKind.Browser
                ? "The browser dashboard only shows the line. Open Vision Studio on the line PC to change this."
                : "Only a supervisor may sort pictures and learn. Ask your supervisor."));
        return c;
    }

    /// <summary>Writes a Support or OEM session's action into the audit trail (AS-04h); nothing for other callers.</summary>
    public void AuditSupport(Caller c, string action)
    {
        if (c.Support) codes?.Audit($"{action}, by {c.Who}");
    }

    /// <summary>The caller of a job designer call (GetJob, SaveJob, ListPictures, TryJob, GrabPicture, FindTools, SuggestBlobTolerance).</summary>
    public Caller Design(ServerCallContext ctx)
    {
        var c = Read(ctx);
        if (c.Anonymous) throw Unauthenticated("Sign in first.");
        if (!c.MayDesignJobs)
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, c.Client == ClientKind.Browser
                ? "The browser dashboard only shows the line. Open Vision Studio on the line PC to change this."
                : "Only an engineer may change jobs. Ask your engineer."));
        return c;
    }

    /// <summary>The caller of the PLC test signal (PlcTestSignal): an engineer in Vision Studio.</summary>
    public Caller TestPlc(ServerCallContext ctx)
    {
        var c = Read(ctx);
        if (c.Anonymous) throw Unauthenticated("Sign in first.");
        if (!c.MayTestPlc)
            throw new RpcException(new Grpc.Core.Status(StatusCode.PermissionDenied, c.Client == ClientKind.Browser
                ? "The browser dashboard only shows the line. Open Vision Studio on the line PC to change this."
                : "Only an engineer may send PLC test signals. Ask your engineer."));
        return c;
    }

    static RpcException Unauthenticated(string text) => new(new Grpc.Core.Status(StatusCode.Unauthenticated, text));

    public SignInResponse SignIn(SignInRequest req, ServerCallContext ctx) => SignIn(req, IsLocal(ctx));

    /// <summary>A sign-in on the browser dashboard (Hybrid 4): always over the network, so always a
    /// browser session that only reads, counted with the same wrong tries as any network sign-in.
    /// Password only: a PIN is short, fine at the line PC, too easy to guess over the network.</summary>
    public SignInResponse SignInBrowser(string user, string password) =>
        SignIn(new SignInRequest { User = user ?? "", Password = password ?? "", Client = ClientKind.Browser }, local: false);

    /// <summary>The browser session of this token, or null (unknown, expired, or not a browser session:
    /// a Vision Studio or plant token never opens the web pages).</summary>
    public Caller? Browser(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var s)) return null;
        if (s.Caller.Client != ClientKind.Browser || s.Caller.Local) return null;
        var now = _time.GetUtcNow();
        if (now - s.Seen > options.SessionIdle)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        _sessions[token] = (s.Caller, now);
        return s.Caller;
    }

    /// <summary>True while this browser session is signed in and not idle, without counting this as use
    /// (the live stream's heartbeat checks it, so a stream ends at sign-out or expiry).</summary>
    public bool BrowserAlive(string? token) =>
        !string.IsNullOrEmpty(token) && _sessions.TryGetValue(token, out var s)
        && s.Caller.Client == ClientKind.Browser && !s.Caller.Local && _time.GetUtcNow() - s.Seen <= options.SessionIdle;

    public void SignOut(string token) => _sessions.TryRemove(token, out _);

    SignInResponse SignIn(SignInRequest req, bool local)
    {
        var kind = req.Client == ClientKind.Unspecified ? ClientKind.Studio : req.Client;
        if (kind == ClientKind.PlantSoftware)
            return Refused(400, "Plant software signs in with a token, not a user.", "runtime.signIn.useToken");
        // Vision Studio changes the line only from the line PC itself.
        if (!local) kind = ClientKind.Browser;
        var card = req.SecretCase == SignInRequest.SecretOneofCase.Card;
        // A badge is read by the reader at the line PC; over the network a card number would only
        // be something to guess.
        if (card && !local)
            return Refused(403, "Sign in with a card only at the line PC.", "runtime.signIn.cardLocalOnly");
        var now = _time.GetUtcNow();
        // counted per endpoint: wrong tries over the network never lock the operator out at the line;
        // unknown cards are counted together, whoever's card it was meant to be
        var key = card ? (local ? "card:local" : "card:net") : (local ? "local:" : "net:") + (req.User ?? "");
        if (_failures.TryGetValue(key, out var f) && f.Count >= MaxFailures && now < f.Until)
            return Refused(429, "Too many wrong tries. Wait a minute, then try again.", "runtime.signIn.locked");
        var user = card ? store.FindByCard(req.Card) : store.Find(req.User ?? "");
        var ok = user is not null && req.SecretCase switch
        {
            SignInRequest.SecretOneofCase.Password => AccessStore.Verify(user.PasswordHash, req.Password),
            SignInRequest.SecretOneofCase.Pin => AccessStore.Verify(user.PinHash, req.Pin),
            SignInRequest.SecretOneofCase.Card => true,
            _ => false,
        };
        if (!ok)
        {
            _failures.AddOrUpdate(key, _ => (1, now + LockFor), (_, o) => (o.Until < now ? 1 : o.Count + 1, now + LockFor));
            return card
                ? Refused(401, "This card is not known. Sign in with your PIN or call your supervisor.", "runtime.signIn.cardUnknown")
                : Refused(401, "Wrong name or password. Try again.", "runtime.signIn.wrong");
        }
        // A good card never clears the count of unknown ones: else own card, four guesses, own card...
        if (!card) _failures.TryRemove(key, out _);
        if (user!.Roles.Any(RuntimeAccess.IsSupport))
        {
            var (opened, refused) = OpenSupport(user, req, local && kind == ClientKind.Studio);
            if (refused is not null) return refused;
            user = opened;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _sessions[token] = (new Caller(kind, user, false, local, token), now);
        return new SignInResponse { Ok = true, Token = token, User = Info(user) };
    }

    /// <summary>
    /// A user with the Service or OEM role (AS-04h): those roles only with today's code, given with the password in
    /// Vision Studio on the line PC; never over the network (the code cannot be tried from the plant's network, PROT-32),
    /// never with a PIN or a card. The user signs in with their other roles when the code is missing or wrong, and is
    /// refused when they have none. Returns the user as the session holds them (only the roles opened).
    /// </summary>
    (RuntimeUser User, SignInResponse? Refused) OpenSupport(RuntimeUser user, SignInRequest req, bool studioAtLine)
    {
        var support = user.Roles.Where(RuntimeAccess.IsSupport).Distinct().ToList();
        var given = !string.IsNullOrWhiteSpace(req.Code);
        var opened = new List<Role>();
        string? why = null;
        SignInResponse? no = null;
        if (!studioAtLine)
            (why, no) = ("not at the line PC", Refused(403, "Sign in as Support or OEM only in Vision Studio at the line PC.", "runtime.signIn.codeLocalOnly"));
        else if (req.SecretCase != SignInRequest.SecretOneofCase.Password)
            (why, no) = ("no password", Refused(403, "Support and OEM sign in with the password and today's code.", "runtime.signIn.codePassword"));
        else if (!given)
            (why, no) = ("no code", Refused(401, "Enter today's code with your password.", "runtime.signIn.codeNeeded"));
        else if (codes is null)
            (why, no) = ("codes not set up", Refused(401, "Wrong or expired code for this machine.", "runtime.signIn.codeWrong"));
        else
        {
            var o = codes.Decide(user.Name, support, req.Code);
            opened = support.Where(r => o.Granted.Contains(SupportCodes.Name(r))).ToList();
            if (o.Locked)
                (why, no) = ("held after too many wrong codes", Refused(429, "Too many wrong codes. Wait 15 minutes, then try again.", "runtime.signIn.codeLocked"));
            else if (opened.Count < support.Count)
                (why, no) = ("wrong or expired code", Refused(401, "Wrong or expired code for this machine.", "runtime.signIn.codeWrong"));
        }
        var kept = user.Roles.Where(r => !RuntimeAccess.IsSupport(r)).Concat(opened).Distinct().ToList();
        // the code itself is never written anywhere; a customer role's daily sign-in without a code is not noted
        if (opened.Count > 0) codes?.Audit($"Vision Studio sign-in as {string.Join(", ", opened.Select(SupportCodes.Name))} with today's code: {user.Name}");
        if (why is not null && (given || kept.Count == 0))
            codes?.Audit($"Vision Studio sign-in as {string.Join(", ", support.Except(opened).Select(SupportCodes.Name))} refused ({why}): {user.Name}");
        if (kept.Count == 0) return (user, no);
        return (new RuntimeUser { Name = user.Name, FullName = user.FullName, Roles = kept, Lines = user.Lines,
                                  PasswordHash = user.PasswordHash, PinHash = user.PinHash, CardHash = user.CardHash }, null);
    }

    /// <summary>
    /// The second person who accepts a changeover at the line PC (CHANGEOVER-VERIFY.md): their own PIN, password or card,
    /// checked like a sign-in and counted with the same wrong tries, only on the line PC itself, without starting a session.
    /// Returns the user, or the refusal.
    /// </summary>
    public (RuntimeUser? User, AcceptChangeoverResponse? Refused) SecondPerson(AcceptChangeoverRequest req, ServerCallContext ctx)
    {
        static AcceptChangeoverResponse No(uint code, string text, string key) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
        if (!IsLocal(ctx)) return (null, No(403, "Accept a changeover only at the line PC.", "runtime.changeover.localOnly"));
        var card = req.SecretCase == AcceptChangeoverRequest.SecretOneofCase.Card;
        var now = _time.GetUtcNow();
        var key = card ? "card:local" : "local:" + (req.User ?? "");
        if (_failures.TryGetValue(key, out var f) && f.Count >= MaxFailures && now < f.Until)
            return (null, No(429, "Too many wrong tries. Wait a minute, then try again.", "runtime.signIn.locked"));
        var user = card ? store.FindByCard(req.Card) : store.Find(req.User ?? "");
        var ok = user is not null && req.SecretCase switch
        {
            AcceptChangeoverRequest.SecretOneofCase.Password => AccessStore.Verify(user.PasswordHash, req.Password),
            AcceptChangeoverRequest.SecretOneofCase.Pin => AccessStore.Verify(user.PinHash, req.Pin),
            AcceptChangeoverRequest.SecretOneofCase.Card => true,
            _ => false,
        };
        if (!ok)
        {
            _failures.AddOrUpdate(key, _ => (1, now + LockFor), (_, o) => (o.Until < now ? 1 : o.Count + 1, now + LockFor));
            return (null, No(401, "Wrong name or PIN. Try again.", "runtime.signIn.wrong"));
        }
        if (!card) _failures.TryRemove(key, out _);
        return (user, null);
    }

    public void SignOut(ServerCallContext ctx)
    {
        if (BearerToken(ctx) is { } t && _sessions.TryRemove(t, out var s) && s.Caller.Support)
            codes?.Audit($"Signed out of Vision Studio: {s.Caller.Who}");
    }

    /// <summary>The users for the sign-in tiles: on the local endpoint without a token, over the
    /// network only with one.</summary>
    public ListUsersResponse ListUsers(ServerCallContext ctx)
    {
        Read(ctx);
        var r = new ListUsersResponse();
        r.Users.AddRange(store.Users().Select(Info));
        return r;
    }

    public WhoAmIResponse WhoAmI(Caller c)
    {
        var r = new WhoAmIResponse { Client = c.Client, MayOperate = c.MayOperate, MayChangeSetup = c.MayChangeSetup, MayDesignJobs = c.MayDesignJobs,
                                     MayTestPlc = c.MayTestPlc, MayLearn = c.MayLearn };
        // 0 (not set) lets the app use its 10 minutes; a zero or negative setting is not "at once"
        if (c.Client == ClientKind.Studio && c.User is not null && options.StudioIdleSignOut > TimeSpan.Zero)
            r.IdleSignOutSeconds = (uint)Math.Clamp(Math.Ceiling(options.StudioIdleSignOut.TotalSeconds), 1, uint.MaxValue);
        if (c.User is not null) r.User = Info(c.User);
        return r;
    }

    public static UserInfo Info(RuntimeUser u)
    {
        var i = new UserInfo { Name = u.Name, FullName = u.FullName, HasPin = u.PinHash is not null, HasPassword = u.PasswordHash is not null, HasCard = u.CardHash is not null };
        i.Roles.AddRange(u.Roles);
        return i;
    }

    static SignInResponse Refused(uint code, string text, string key) => new() { ErrorCode = code, ErrorText = text, ErrorKey = key };
}
