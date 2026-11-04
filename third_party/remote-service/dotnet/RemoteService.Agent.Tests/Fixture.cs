using Xunit;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Sundaram.RemoteService;

/// <summary>Fake host parts the tests set as they need.</summary>
sealed class FakeHost : IServiceCodeCheck, IAuditSink, IMachineState, IOperatorConfirmation, IAuditChain
{
    /// <summary>A stand-in for a hash chain: each entry moves the head.</summary>
    public string? Head => "head-" + Entries.Count.ToString("D4");
    public const string Code = "123456";
    public readonly ConcurrentQueue<AuditEntry> Entries = new();
    public volatile bool Held, Running, OperatorSays = true, SinkBroken;
    public readonly ConcurrentQueue<ConfirmRequest> Asked = new();

    public CodeResult Check(string code, string engineer, RemoteRole role) =>
        Held ? CodeResult.Held : code == Code ? CodeResult.Accepted : CodeResult.Wrong;

    public void Write(AuditEntry entry)
    {
        if (SinkBroken) throw new IOException("disk full");
        Entries.Enqueue(entry);
    }

    public bool SafeToChange(out string reason)
    {
        reason = Running ? "the line is running" : "";
        return !Running;
    }

    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        Asked.Enqueue(request);
        return Task.FromResult(OperatorSays);
    }

    public string[] Kinds() => Entries.Select(e => e.Kind).ToArray();
}

/// <summary>
/// One agent dialling a small relay in the test, with a local web server standing in for the host's own pages. Requests are
/// sent down the link exactly as the real relay sends them (PROTOCOL.md section 2).
/// </summary>
sealed class Fixture : IAsyncDisposable
{
    public const string Token = "link-token-for-tests-0123456789abcdef";
    public readonly FakeHost Host = new();
    public readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
    public readonly string Folder;
    Fixture(string? folder) => Folder = folder ?? Path.Combine(Path.GetTempPath(), "rs-test-" + Guid.NewGuid().ToString("N")[..8]);
    public bool KeepFolder;
    public RemoteAgent Agent = null!;
    /// <summary>Set in Start's configure to switch software installs on.</summary>
    public IPackageInstaller? Packages;
    /// <summary>Set in Start's configure to switch corrections on.</summary>
    public List<CorrectionKind> Corrections = [];
    public ISupervisorApproval? Supervisor;
    public readonly ConcurrentQueue<JsonObject> Logs = new();
    /// <summary>Log frames as received (with epoch and seq).</summary>
    public readonly ConcurrentQueue<JsonObject> LogFrames = new();
    /// <summary>The fake relay confirms each log entry (PROTOCOL.md section 5); off to test resending.</summary>
    public volatile bool AutoAck = true;
    public readonly ConcurrentQueue<(string Path, string Cookie)> HostSaw = new();
    public IHeaderDictionary LinkHeaders = null!;
    WebApplication _relay = null!, _pages = null!;
    readonly TaskCompletionSource<(WebSocket Ws, TaskCompletionSource Done)> _link = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<string, System.Threading.Channels.Channel<JsonObject>> _answers = new();
    readonly CancellationTokenSource _stop = new();
    Task _run = Task.CompletedTask;
    int _next;

    public string Logs1 => Path.Combine(Folder, "logs");
    public string Jobs => Path.Combine(Folder, "jobs");
    public string Outside => Path.Combine(Folder, "outside");

    public static async Task<Fixture> Start(Action<Fixture, List<RemoteAction>>? configure = null, bool pages = true, string token = Token,
        string? folder = null, bool dataFolder = true)
    {
        var f = new Fixture(folder);
        Directory.CreateDirectory(f.Logs1);
        Directory.CreateDirectory(Path.Combine(f.Logs1, "2026-11"));
        Directory.CreateDirectory(f.Jobs);
        Directory.CreateDirectory(f.Outside);
        await File.WriteAllTextAsync(Path.Combine(f.Logs1, "today.log"), "line one\nline two\n");
        await File.WriteAllTextAsync(Path.Combine(f.Jobs, "labels.job"), "old");
        await File.WriteAllTextAsync(Path.Combine(f.Outside, "secret.txt"), "not yours");

        var relayPort = FreePort();
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls($"http://127.0.0.1:{relayPort}");
        f._relay = b.Build();
        f._relay.UseWebSockets();
        f._relay.Map("/link", async (HttpContext ctx) =>
        {
            f.LinkHeaders = ctx.Request.Headers;
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var done = new TaskCompletionSource();
            f._link.TrySetResult((ws, done));
            _ = Task.Run(() => f.Receive(ws, done));
            await done.Task;
        });
        await f._relay.StartAsync();

        Uri? local = null;
        if (pages)
        {
            var pagesPort = FreePort();
            var pb = WebApplication.CreateSlimBuilder();
            pb.WebHost.UseUrls($"http://127.0.0.1:{pagesPort}");
            f._pages = pb.Build();
            f._pages.Map("/{**path}", (HttpContext ctx) =>
            {
                f.HostSaw.Enqueue((ctx.Request.Path, ctx.Request.Headers.Cookie.ToString()));   // copied: the request is recycled after
                var s = f.Agent.Forwarded(ctx.Request.Headers[RemoteAgent.KeyHeader], ctx.Request.Headers[RemoteAgent.SessionHeader], ctx.Connection.RemoteIpAddress);
                ctx.Response.Headers["X-Inspection-Id"] = "42";
                return Results.Text(s is null ? "host page: nobody" : "host page: " + s.User);
            });
            await f._pages.StartAsync();
            local = new Uri($"http://127.0.0.1:{pagesPort}");
        }

        var actions = new List<RemoteAction>
        {
            new("restart", "Restart the reader", "Restarts the reader service", (a, _) => Task.FromResult("restarted " + a.GetValueOrDefault("service", "reader"))),
            new("clean", "Clear temp files", "Needs the machine stopped", (_, _) => Task.FromResult("cleaned"), NeedsStopped: true),
            new("factory", "Factory only", "OEM only", (_, _) => Task.FromResult("ok"), Roles: [RemoteRole.Oem]),
        };
        configure?.Invoke(f, actions);
        var host = new RemoteServiceHost
        {
            Code = f.Host, Audit = f.Host, Machine = f.Host, Operator = f.Host, Product = "Test Product", Packages = f.Packages,
            Corrections = f.Corrections, Supervisor = f.Supervisor,
            Roots =
            [
                new FileRoot("logs", f.Logs1),
                new FileRoot("jobs", f.Jobs, Writable: true, Extensions: [".job"],
                    CheckUpload: p => File.ReadAllText(p).Contains("virus") ? "not a job file" : null),
            ],
            Actions = actions,
            Views =
            [
                new DataView("inspections", "Inspections", ["batch"], (p, limit, _) =>
                {
                    var rows = new List<IReadOnlyList<string?>> { new[] { "1", "OK", p.GetValueOrDefault("batch", "-") }, new[] { "2", "=CMD()", "x" } };
                    return Task.FromResult(new DataTable(["id", "result", "batch"], rows, false));
                }),
            ],
            Diagnostics = async (zip, ct) =>
            {
                var e = zip.CreateEntry("versions.txt");
                await using var w = new StreamWriter(e.Open());
                await w.WriteAsync("host 1.2.3");
            },
        };
        f.Agent = new RemoteAgent(new RemoteServiceOptions
        {
            Relay = $"ws://127.0.0.1:{relayPort}/link", Machine = "M-1001", Token = token, LocalWeb = local, AllowInsecure = true,
            DataFolder = dataFolder ? Path.Combine(f.Folder, "agent-state") : null,
        }, host, time: f.Time);
        f._run = Task.Run(() => f.Agent.RunAsync(f._stop.Token));
        return f;
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    async Task Receive(WebSocket ws, TaskCompletionSource done)
    {
        var buf = new byte[1 << 20];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var m = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    m.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (JsonNode.Parse(m.ToArray()) is not JsonObject f) continue;
                if ((string?)f["t"] == "log")
                {
                    Logs.Enqueue((JsonObject)f["entry"]!.DeepClone());
                    LogFrames.Enqueue(f);
                    if (AutoAck)
                        await SendText(ws, new JsonObject { ["t"] = "ack", ["epoch"] = (string?)f["epoch"], ["seq"] = (long)f["seq"]! }.ToJsonString());
                    continue;
                }
                if ((string?)f["id"] is { } id && _answers.TryGetValue(id, out var ch)) ch.Writer.TryWrite(f);
            }
        }
        catch (WebSocketException) { }
        finally { done.TrySetResult(); }
    }

    public async Task<WebSocket> Link() => (await _link.Task.WaitAsync(TimeSpan.FromSeconds(10))).Ws;

    public async Task Raw(string json) => await SendText(await Link(), json);

    readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>One send at a time on a WebSocket: requests and confirmations go out from different threads.</summary>
    public async Task SendText(WebSocket ws, string text)
    {
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    public async Task DropLink()
    {
        var (ws, done) = await _link.Task;
        await _sendLock.WaitAsync();
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
        finally { _sendLock.Release(); }
        done.TrySetResult();
    }

    public sealed record Answer(int Status, Dictionary<string, string[]> Headers, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
        public JsonElement Json => JsonDocument.Parse(Bytes).RootElement;
        public string? Header(string n) => Headers.FirstOrDefault(h => h.Key.Equals(n, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
        public string? Cookie => Header("Set-Cookie")?.Split(';')[0];
    }

    /// <summary>One browser request through the link, as the relay sends it.</summary>
    public async Task<Answer> Ask(string method, string path, string? cookie = null, byte[]? body = null, string? contentType = null,
        string user = "anita", string role = "service", string query = "")
    {
        var ws = await Link();
        var id = "r" + Interlocked.Increment(ref _next);
        var ch = _answers[id] = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
        var headers = new JsonObject { ["accept"] = "*/*" };
        if (cookie is not null) headers["cookie"] = cookie;
        if (contentType is not null) headers["content-type"] = contentType;
        var frame = new JsonObject
        {
            ["t"] = "req", ["id"] = id, ["method"] = method, ["path"] = path, ["query"] = query, ["headers"] = headers, ["user"] = user,
            ["role"] = role, ["body"] = Convert.ToBase64String(body ?? []),
        };
        await SendText(ws, frame.ToJsonString());
        var status = 0;
        var hs = new Dictionary<string, string[]>();
        var data = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var f in ch.Reader.ReadAllAsync(timeout.Token))
        {
            switch ((string?)f["t"])
            {
                case "head":
                    status = (int)f["status"]!;
                    hs = f["headers"]!.Deserialize<Dictionary<string, string[]>>()!;
                    break;
                case "data":
                    data.Write(Convert.FromBase64String((string)f["b64"]!));
                    break;
                case "end":
                    _answers.TryRemove(id, out _);
                    return new Answer(status == 0 ? 502 : status, hs, data.ToArray());
            }
        }
        throw new TimeoutException();
    }

    public Task<Answer> Form(string path, string form, string? cookie = null, string user = "anita", string role = "service") =>
        Ask("POST", path, cookie, Encoding.UTF8.GetBytes(form), "application/x-www-form-urlencoded", user, role);

    public Task<Answer> PostJson(string path, object value, string cookie, string role = "service", string user = "anita") =>
        Ask("POST", path, cookie, JsonSerializer.SerializeToUtf8Bytes(value), "application/json", user, role);

    /// <summary>Signs in and returns the session cookie ("rs_session=...").</summary>
    public async Task<string> SignIn(string user = "anita", string role = "service")
    {
        var r = await Form("/_rs/sign-in", "code=" + FakeHost.Code + "&reason=Call+4711", user: user, role: role);
        Assert.Equal(200, r.Status);
        return r.Cookie!;
    }

    public async Task<JsonElement> WaitJob(string id, string cookie)
    {
        for (var i = 0; i < 100; i++)
        {
            var j = (await Ask("GET", "/_rs/jobs/" + id, cookie)).Json;
            if (j.GetProperty("state").GetString() is "done" or "failed" or "refused") return j;
            await Task.Delay(30);
        }
        throw new TimeoutException();
    }

    public async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        if (_relay is not null) await _relay.DisposeAsync();
        if (_pages is not null) await _pages.DisposeAsync();
        if (!KeepFolder) try { Directory.Delete(Folder, true); } catch (IOException) { }
    }
}
