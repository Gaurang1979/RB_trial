using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sundaram.RemoteService;

/// <summary>
/// The remote service agent (DESIGN.md, PROTOCOL.md). The host creates one and runs <see cref="RunAsync"/> for its whole life:
/// the agent dials the relay (outbound only), serves its own API and console under /_rs/, forwards every other request to the
/// host's local web pages, and writes every remote event to the host's audit sink and to the relay.
/// <para>Nothing the relay sends can stop the host: any failure drops the link, which is dialled again.</para>
/// </summary>
public sealed partial class RemoteAgent
{
    public const int ProtocolVersion = 1;
    public const string SessionCookie = "rs_session";
    /// <summary>Headers the agent adds to requests it forwards to the host's pages (<see cref="Forwarded"/>).</summary>
    public const string KeyHeader = "X-RS-Key", SessionHeader = "X-RS-Session";
    const int MaxRelayMessage = (1 << 20) * 2;
    const int MaxOpen = 32;
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
    static readonly string[] ForwardHeaders = ["accept", "accept-language", "content-type", "last-event-id", "if-none-match"];
    static readonly string[] ReturnHeaders = ["content-type", "content-disposition", "cache-control", "set-cookie", "etag"];

    readonly RemoteServiceOptions _options;
    readonly RemoteServiceHost _host;
    readonly ILogger _log;
    readonly TimeProvider _time;
    readonly ConcurrentDictionary<string, CancellationTokenSource> _open = new();
    readonly SemaphoreSlim _send = new(1, 1);
    readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    readonly SemaphoreSlim _logSignal = new(0, int.MaxValue);
    ClientWebSocket? _ws;
    readonly AgentState? _state;
    readonly string? _stateError;

    public RemoteAgent(RemoteServiceOptions options, RemoteServiceHost host, ILogger? log = null, TimeProvider? time = null)
    {
        _options = options;
        _host = host;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        if (options.DataFolder is not null) Directory.CreateDirectory(options.DataFolder);
        Log = new RemoteLog(host.Audit, _time, options.DataFolder);
        Sessions = new RemoteSessions(options, Log, _time);
        Log.Queued += () => _logSignal.Release();
        Key = Convert.ToHexString(_key);
        if (options.DataFolder is null) _stateError = "this machine has no data folder to remember installed versions, so it takes no packages";
        else
        {
            try { _state = AgentState.Load(options.DataFolder); }
            catch (Exception e)
            {
                // never an empty state in its place (that would let an old package back in), and never a stopped host

                _stateError = $"{AgentState.FileName} cannot be read ({e.Message}), so this machine takes no packages";
                _log.LogError("Remote service: {Error}", _stateError);
            }
        }
    }

    public RemoteLog Log { get; }
    public RemoteSessions Sessions { get; }
    public bool Connected { get; private set; }

    /// <summary>Marks requests the agent forwards to the host's pages; held in memory only, sent only over loopback.</summary>
    public string Key { get; }

    /// <summary>
    /// For the host's own web pages: the remote session of a request the agent forwarded, or null for any other request. Checks
    /// the key (fixed-time) and that the request came from this PC. A host page that sees a session here treats the request as a
    /// remote engineer's: read-only, and never asks for a password.
    /// </summary>
    public RemoteSession? Forwarded(string? keyHeader, string? sessionHeader, IPAddress? remote)
    {
        if (remote is null || !IPAddress.IsLoopback(remote) || keyHeader is not { Length: 64 }) return null;
        try { if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(keyHeader), _key)) return null; }
        catch (FormatException) { return null; }
        return Sessions.Peek(sessionHeader);
    }

    /// <summary>Runs until <paramref name="stop"/>. Returns at once (after logging why) when the options are not usable.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        if (_options.Problem() is { } why)
        {
            _log.LogWarning("Remote service off: {Why}", why);
            return;
        }
        using var http = LocalWebClient();
        using var sweep = new Timer(_ => _ = Sessions.Active, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            ws.Options.SetRequestHeader("X-Machine-Id", _options.Machine);
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _options.Token);
            ws.Options.SetRequestHeader("X-RS-Protocol", ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var up = DateTimeOffset.MinValue;
            using var linkDone = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                await ws.ConnectAsync(new Uri(_options.Relay), stop);
                _ws = ws;
                Connected = true;
                up = _time.GetUtcNow();
                _log.LogInformation("Remote service link up: {Relay} as {Machine}", _options.Relay, _options.Machine);
                var logs = Task.Run(() => SendLogs(ws, linkDone.Token), CancellationToken.None);
                try { await Serve(ws, http, stop); }
                finally
                {
                    linkDone.Cancel();
                    try { await logs; } catch (Exception) { /* the link is over; queued entries wait for the next one */ }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogWarning("Remote service link: {Error}", e.Message);
            }
            finally
            {
                _ws = null;
                foreach (var id in _open.Keys) Abandon(id);
                if (Connected)
                {
                    Connected = false;
                    Sessions.EndAll("link");
                }
            }
            if (up != DateTimeOffset.MinValue && _time.GetUtcNow() - up >= StableAfter) backoff = TimeSpan.FromSeconds(1);
            try { await Task.Delay(backoff, stop); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    int _linkNumber;

    /// <summary>Sends the user log to the relay in order (PROTOCOL.md section 5). Everything not confirmed is sent again on
    /// each new link; the relay's "ack" drops what it has saved.</summary>
    async Task SendLogs(ClientWebSocket ws, CancellationToken ct)
    {
        var link = Interlocked.Increment(ref _linkNumber);
        _logSignal.Release();   // anything queued or unconfirmed while the link was down
        while (!ct.IsCancellationRequested)
        {
            await _logSignal.WaitAsync(ct);
            foreach (var p in Log.ToSend(link))
            {
                var e = p.Entry;
                await Send(ws, new
                {
                    t = "log", epoch = p.Epoch, seq = p.Seq,
                    entry = new
                    {
                        time = e.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                        session = e.Session, user = e.User, role = RemoteSessions.Name(e.Role), kind = e.Kind, detail = e.Detail, data = e.Data,
                    },
                });
            }
        }
    }

    HttpClient? LocalWebClient()
    {
        if (_options.LocalWeb is null) return null;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1) };
        if (_options.LocalWebCertificateSha256 is { } mine)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, _) =>
                c is not null && CryptographicOperations.FixedTimeEquals(c.GetCertHash(HashAlgorithmName.SHA256), mine);
        return new HttpClient(handler) { BaseAddress = _options.LocalWeb, Timeout = Timeout.InfiniteTimeSpan };
    }

    async Task Serve(ClientWebSocket ws, HttpClient? http, CancellationToken stop)
    {
        var buffer = new byte[64 << 10];
        while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, stop);
                if (r.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, r.Count);
                if (message.Length > MaxRelayMessage) throw new InvalidDataException("relay message too large");
            } while (!r.EndOfMessage);
            if (JsonNode.Parse(message.ToArray()) is not JsonObject m) continue;
            var id = Text(m, "id");
            switch (Text(m, "t"))
            {
                case "req" when id.Length is > 0 and <= 64:
                    if (_open.Count >= MaxOpen) { await Send(ws, new { t = "end", id, error = "busy" }); break; }
                    var req = Parse(m, id);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    if (!_open.TryAdd(id, cts)) { cts.Dispose(); break; }
                    _ = Task.Run(() => Handle(ws, http, req, cts), CancellationToken.None);
                    break;
                case "cancel":
                    Abandon(id);
                    break;
                case "ping":
                    await Send(ws, new { t = "pong" });
                    break;
                case "ack" when m["seq"] is JsonValue sv && sv.TryGetValue<long>(out var seq):
                    Log.Confirmed(Text(m, "epoch"), seq);
                    break;
            }
        }
    }

    static RemoteRequest Parse(JsonObject m, string id)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (m["headers"] is JsonObject hs)
            foreach (var (k, v) in hs)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) headers[k.ToLowerInvariant()] = s;
        byte[] body;
        try { body = Text(m, "body") is { Length: > 0 } b ? Convert.FromBase64String(b) : []; }
        catch (FormatException) { body = []; }
        var path = Text(m, "path");
        return new RemoteRequest(id, Text(m, "method") == "POST" ? "POST" : "GET", path.Length == 0 ? "/" : path, Text(m, "query"),
            headers, Text(m, "user"), Text(m, "role") == "oem" ? RemoteRole.Oem : RemoteRole.Service, body);
    }

    /// <summary>A string field of a relay frame; anything else (a number, an object) is "".</summary>
    static string Text(JsonObject m, string name) => m[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    void Abandon(string id)
    {
        if (!_open.TryRemove(id, out var cts)) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    async Task Handle(ClientWebSocket ws, HttpClient? http, RemoteRequest req, CancellationTokenSource cts)
    {
        var res = new RemoteResponse(req.Id, f => Send(ws, f));
        try
        {
            // only a path of this machine, never a full address that would leave this PC
            if (!req.Path.StartsWith('/') || req.Path.StartsWith("//", StringComparison.Ordinal) || req.Path.Contains('\\')
                || (req.Query.Length > 0 && !req.Query.StartsWith('?')) || !UserOk(req.User))
                await res.Error(404, "Not found.", "rs.notFound");
            else if (req.Path == "/_rs" || req.Path.StartsWith("/_rs/", StringComparison.Ordinal))
                await Api(req, res, cts.Token);
            else if (http is null && req.Path == "/")
            {
                // a machine without pages of its own opens on the service console
                await res.Head(302, new Dictionary<string, string[]> { ["Location"] = ["_rs/ui/"] });
                await res.End();
            }
            else if (http is null)
                await res.Error(404, "This machine has no web pages. Open the service console.", "rs.noPages");
            else
                await ForwardToHost(http, req, res, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _log.LogWarning("Remote service request failed: {Error}", e.Message);
            try
            {
                if (!res.Started) await res.Error(500, "The machine could not answer.", "rs.failed");
                else await res.End("the machine could not answer");
            }
            catch (Exception) { }
        }
        finally
        {
            _open.TryRemove(req.Id, out _);
            cts.Dispose();
        }
    }

    static bool UserOk(string u) => u.Length is > 0 and <= 64 && u.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@');

    async Task ForwardToHost(HttpClient http, RemoteRequest req, RemoteResponse res, CancellationToken ct)
    {
        var method = req.Method == "POST" ? HttpMethod.Post : HttpMethod.Get;
        using var msg = new HttpRequestMessage(method, req.Path + req.Query);
        if (method == HttpMethod.Post) msg.Content = new ByteArrayContent(req.Body);
        foreach (var (name, value) in req.Headers)
        {
            if (name == "cookie")
            {
                // the host's own cookies only; the agent's session goes as its own header
                var cookies = value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
                var cookie = string.Join("; ", cookies);
                if (cookie.Length > 0) msg.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            else if (name == "content-type" && msg.Content is not null)
                msg.Content.Headers.TryAddWithoutValidation("Content-Type", value);
            else if (ForwardHeaders.Contains(name) && name != "content-type")
                msg.Headers.TryAddWithoutValidation(name, value);
        }
        msg.Headers.TryAddWithoutValidation(KeyHeader, Key);
        // only a session of this relay account; the host decides what an engineer without one may see (usually: the sign-in)
        if (req.Cookie(SessionCookie) is { } token && Sessions.Find(token, req.User) is not null)
            msg.Headers.TryAddWithoutValidation(SessionHeader, token);

        using var answer = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        var back = new Dictionary<string, string[]>();
        foreach (var h in answer.Headers.Concat(answer.Content.Headers))
            if (ReturnHeaders.Contains(h.Key.ToLowerInvariant()) || _options.ExtraReturnHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                back[h.Key] = h.Value.ToArray();
        await res.Head((int)answer.StatusCode, back);
        await using var stream = await answer.Content.ReadAsStreamAsync(ct);
        var buf = new byte[RemoteResponse.Chunk];
        int n;
        while ((n = await stream.ReadAsync(buf, ct)) > 0)
            await res.Data(buf.AsMemory(0, n));
        await res.End();
    }

    async Task Send(ClientWebSocket ws, object frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        await _send.WaitAsync();
        try
        {
            // never cancelled part-way: a cancelled send aborts the whole socket, and every other request with it
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            else
                throw new WebSocketException("link closed");
        }
        finally { _send.Release(); }
    }
}
