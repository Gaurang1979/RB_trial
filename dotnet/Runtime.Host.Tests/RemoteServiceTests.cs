using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VisionCore.Licensing;
using VisionReports;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;
using Xunit;

/// <summary>
/// Remote service step 4a (REQ-11, PROT-26; REMOTE-SUPPORT.md): vs-runtime hosts the remote-service agent. A small relay in the
/// test plays support.sundaramtech.com and sends browser requests down the link as the real one does.
/// </summary>
[Collection("support-link")]   // the secrets come from the environment: never at the same time as another class that sets them
public sealed class RemoteServiceTests : IAsyncDisposable
{
    const string Licence = "LIC-TEST-0042";
    const string ServiceSecret = "service-secret-for-tests-0001";
    const string FactorySecret = "factory-secret-for-tests-0002";
    const string LinkToken = "link-token-for-tests-0123456789abcdef";
    static readonly string LedgerKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    RuntimeFixture _f = null!;
    WebApplication _relay = null!;
    int _web;
    readonly TaskCompletionSource<(WebSocket Ws, IHeaderDictionary Headers, TaskCompletionSource Done)> _link = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<string, System.Threading.Channels.Channel<JsonObject>> _answers = new();
    readonly ConcurrentQueue<JsonObject> _logs = new();
    readonly SemaphoreSlim _send = new(1, 1);
    int _next;

    async Task Start(bool ledgerKey = true, Action<RuntimeFixture>? prepare = null)
    {
        Environment.SetEnvironmentVariable(CommissioningSecrets.SupportLinkToken, LinkToken);
        Environment.SetEnvironmentVariable(CommissioningSecrets.ServiceOtpSecret, ServiceSecret);
        Environment.SetEnvironmentVariable(CommissioningSecrets.FactoryOtpSecret, FactorySecret);
        Environment.SetEnvironmentVariable(CommissioningSecrets.LedgerKey, ledgerKey ? LedgerKey : null);
        var relayPort = FreePort();
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls($"http://127.0.0.1:{relayPort}");
        _relay = b.Build();
        _relay.UseWebSockets();
        _relay.Map("/link", async (HttpContext ctx) =>
        {
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var done = new TaskCompletionSource();
            _link.TrySetResult((ws, ctx.Request.Headers, done));
            _ = Task.Run(() => Receive(ws, done));
            await done.Task;
        });
        await _relay.StartAsync();
        _web = FreePort();
        _f = await RuntimeFixture.StartAsync(f =>
        {
            File.WriteAllText(Path.Combine(f.Folder, "data", "licence.json"), JsonSerializer.Serialize(new { LicenceKey = Licence }));
            prepare?.Invoke(f);
        }, [$"--Runtime:WebPort={_web}", $"--Runtime:SupportRelay=ws://127.0.0.1:{relayPort}/link", "--Runtime:SupportMachine=M-1001"]);
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
                    _logs.Enqueue((JsonObject)f["entry"]!.DeepClone());
                    await SendText(ws, new JsonObject { ["t"] = "ack", ["epoch"] = (string?)f["epoch"], ["seq"] = (long)f["seq"]! }.ToJsonString());
                }
                else if ((string?)f["id"] is { } id && _answers.TryGetValue(id, out var ch)) ch.Writer.TryWrite(f);
            }
        }
        catch (WebSocketException) { }
        finally { done.TrySetResult(); }
    }

    async Task SendText(WebSocket ws, string text)
    {
        await _send.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _send.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null) await _f.DisposeAsync();
        if (_relay is not null) await _relay.DisposeAsync();
        foreach (var n in new[] { CommissioningSecrets.SupportLinkToken, CommissioningSecrets.ServiceOtpSecret, CommissioningSecrets.FactoryOtpSecret, CommissioningSecrets.LedgerKey })
            Environment.SetEnvironmentVariable(n, null);
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    async Task<WebSocket> Link() => (await _link.Task.WaitAsync(TimeSpan.FromSeconds(10))).Ws;

    sealed record Answer(int Status, Dictionary<string, string[]> Headers, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
        public JsonElement Json => JsonDocument.Parse(Bytes).RootElement;
        public string? Cookie => Headers.FirstOrDefault(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault()?.Split(';')[0];
    }

    /// <summary>One browser request through the link, as the relay sends it.</summary>
    async Task<Answer> Ask(string method, string path, string? cookie = null, string? form = null, string user = "anita", string role = "service", string query = "")
    {
        var ws = await Link();
        var id = "r" + Interlocked.Increment(ref _next);
        var ch = _answers[id] = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
        var headers = new JsonObject { ["accept"] = "*/*" };
        if (cookie is not null) headers["cookie"] = cookie;
        if (form is not null) headers["content-type"] = "application/x-www-form-urlencoded";
        var frame = new JsonObject
        {
            ["t"] = "req", ["id"] = id, ["method"] = method, ["path"] = path, ["query"] = query, ["headers"] = headers, ["user"] = user, ["role"] = role,
            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(form ?? "")),
        };
        await SendText(ws, frame.ToJsonString());
        var status = 0;
        var hs = new Dictionary<string, string[]>();
        var body = new MemoryStream();
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
                    body.Write(Convert.FromBase64String((string)f["b64"]!));
                    break;
                case "end":
                    _answers.TryRemove(id, out _);
                    return new Answer(status == 0 ? 502 : status, hs, body.ToArray());
            }
        }
        throw new TimeoutException();
    }

    static string Service() => OemOtpVerifier.CalculateDailyOtp(Licence, null, ServiceSecret)!;
    static string Factory() => OemOtpVerifier.CalculateFactoryOtp(Licence, null, FactorySecret)!;

    async Task<string> SignIn(string code, string role = "service")
    {
        var r = await Ask("POST", "/_rs/sign-in", form: $"code={code}&reason=Call+4711+no-reads", role: role);
        Assert.Equal(200, r.Status);
        return r.Cookie!;
    }

    async Task<RemoteService?> Badge() => (await _f.Runtime().GetStatusAsync(new GetStatusRequest())).RemoteService;

    ServiceLog Log() => new(Path.Combine(_f.Folder, "data"), Convert.FromBase64String(LedgerKey));

    HttpClient Direct() => new(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };

    [Fact]
    public async Task The_runtime_dials_the_relay_with_its_machine_number_token_and_protocol()
    {
        await Start();
        var (_, headers, _) = await _link.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("M-1001", headers["X-Machine-Id"].ToString());
        Assert.Equal("Bearer " + LinkToken, headers.Authorization.ToString());
        Assert.Equal("1", headers["X-RS-Protocol"].ToString());
    }

    [Fact]
    public async Task Without_the_ledger_key_there_is_no_remote_service()
    {
        await Start(ledgerKey: false);
        var runner = _f.App.Services.GetRequiredService<RemoteServiceRunner>();
        await runner.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(runner.Agent);
        Assert.Contains("ledger key", runner.Off);
        await Task.Delay(300);
        Assert.False(_link.Task.IsCompleted);
    }

    [Fact]
    public async Task A_tampered_service_log_keeps_remote_service_off_and_the_line_running()
    {
        await Start(prepare: f =>
        {
            var log = new ServiceLog(Path.Combine(f.Folder, "data"), Convert.FromBase64String(LedgerKey));
            log.Append(DateTime.UtcNow, "s1", "anita", "service", "session_start", "x", new Dictionary<string, string>());
            File.WriteAllText(log.HeadPath, "vs-service-head-v1\nservice_log 9 " + new string('a', 64) + "\n" + new string('b', 64) + "\n");
        });
        var runner = _f.App.Services.GetRequiredService<RemoteServiceRunner>();
        await runner.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(runner.Agent);
        Assert.Contains("tampering", runner.Off, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await _f.Runtime().GetStatusAsync(new GetStatusRequest()));   // the line runs and answers
    }

    [Fact]
    public async Task Through_the_relay_the_dashboard_offers_only_the_console_and_never_takes_a_password()
    {
        await Start();
        var options = (await Ask("GET", "/web/sign-in-options")).Json;
        Assert.True(options.GetProperty("service").GetBoolean());
        Assert.False(options.GetProperty("password").GetBoolean());
        Assert.Equal("_rs/ui/", options.GetProperty("console").GetString());
        Assert.Equal(403, (await Ask("POST", "/web/sign-in", form: "user=eng&password=secret-pass")).Status);
        var me = await Ask("GET", "/web/api/me");
        Assert.Equal(401, me.Status);
        Assert.Equal("web.signIn.console", me.Json.GetProperty("key").GetString());
        // a plant user's dashboard cookie through the relay is not a session either
        using (var plant = Direct())
        {
            var signIn = await plant.PostAsync("/web/sign-in", new FormUrlEncodedContent([new("user", "eng"), new("password", "secret-pass")]));
            var plantCookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            Assert.Equal(401, (await Ask("GET", "/web/api/me", plantCookie)).Status);
        }

        // on the plant's network: the password sign-in, the old code route is gone, and a made-up key changes nothing
        using var c = Direct();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/web/sign-in-options");
        req.Headers.Add("X-RS-Key", new string('A', 64));
        Assert.True(JsonDocument.Parse(await (await c.SendAsync(req)).Content.ReadAsStringAsync()).RootElement.GetProperty("password").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/web/sign-in-service", new FormUrlEncodedContent([new("code", Service())]))).StatusCode);
    }

    [Fact]
    public async Task The_service_code_opens_the_console_and_the_dashboard_with_one_session_recorded_in_the_chained_log()
    {
        await Start();
        Assert.Null(await Badge());
        Assert.Equal(401, (await Ask("POST", "/_rs/sign-in", form: "code=000000&reason=Call+4711")).Status);
        var cookie = await SignIn(Service());

        var me = (await Ask("GET", "/web/api/me", cookie)).Json;
        Assert.Equal(["Service"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(["live", "trends", "reports", "audit"], me.GetProperty("pages").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(200, (await Ask("GET", "/web/api/status", cookie)).Status);
        Assert.True((await Badge())?.Active);

        var rs = (await Ask("GET", "/_rs/me", cookie)).Json;
        Assert.Equal(["reports", "ng-images", "jobs"], rs.GetProperty("roots").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.All(rs.GetProperty("roots").EnumerateArray(), r => Assert.False(r.GetProperty("writable").GetBoolean()));   // nothing changes the line in 4a
        Assert.False(rs.GetProperty("packages").GetBoolean());
        Assert.False(rs.GetProperty("corrections").GetBoolean());

        var log = Log();
        Assert.True(log.Verify() >= 2);   // the refused code and the session start, chained
        Assert.Contains("Service log started", (await Ask("GET", "/web/api/audit", cookie)).Text);   // a new log is noted
        var kinds = log.Entries(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Select(e => e.Kind).ToArray();
        Assert.Contains("sign_in_refused", kinds);
        Assert.Contains("session_start", kinds);
        // and a copy on the Audit trail page
        Assert.Contains("Remote service session started: anita", (await Ask("GET", "/web/api/audit", cookie)).Text);

        // signing out on the dashboard ends the agent's session, with the chain head kept outside
        await Ask("POST", "/web/sign-out", cookie);
        Assert.Equal(401, (await Ask("GET", "/_rs/me", cookie)).Status);
        Assert.Null(await Badge());
        for (var i = 0; i < 100 && !_logs.Any(l => (string?)l["kind"] == "session_end"); i++) await Task.Delay(30);
        var end = _logs.Single(l => (string?)l["kind"] == "session_end");
        Assert.Equal(64, ((string?)((JsonObject)end["data"]!)["chainHead"])?.Length);
        log.Verify();
    }

    [Fact]
    public async Task The_oem_role_needs_todays_factory_code_not_the_service_code()
    {
        await Start();
        Assert.Equal(401, (await Ask("POST", "/_rs/sign-in", form: $"code={Service()}&reason=Factory+settings+check", role: "oem")).Status);
        var cookie = await SignIn(Factory(), "oem");
        Assert.Equal(["OEM"], (await Ask("GET", "/web/api/me", cookie, role: "oem")).Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Views_diagnostics_and_the_ledger_check_work_and_are_recorded()
    {
        await Start();
        var cookie = await SignIn(Service());
        var events = await Ask("GET", "/_rs/views/events", cookie, query: "?hours=1");
        Assert.Equal(200, events.Status);
        Assert.Equal(["time (UTC)", "kind", "active", "detail"], events.Json.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(200, (await Ask("GET", "/_rs/views/inspections", cookie, query: "?hours=24&format=csv")).Status);

        var zip = await Ask("GET", "/_rs/diagnostics", cookie);
        using (var z = new ZipArchive(new MemoryStream(zip.Bytes)))
            Assert.Superset(new HashSet<string> { "versions.txt", "status.json", "runtime-settings.json", "events-last-day.csv" }, z.Entries.Select(e => e.FullName).ToHashSet());

        var job = await Ask("POST", "/_rs/actions/verify-ledgers", cookie);
        Assert.Equal(202, job.Status);
        JsonElement j = default;
        for (var i = 0; i < 100; i++)
        {
            j = (await Ask("GET", "/_rs/jobs/" + job.Json.GetProperty("job").GetString(), cookie)).Json;
            if (j.GetProperty("state").GetString() != "running") break;
            await Task.Delay(30);
        }
        Assert.Contains("service log good", j.GetProperty("result").GetString());
        var kinds = Log().Entries(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Select(e => e.Kind).ToArray();
        Assert.Contains("view", kinds);
        Assert.Contains("diagnostics", kinds);
        Assert.Contains("action_done", kinds);
    }

    // ---- 4b: the line PC answers ----------------------------------------------------------------------------------------

    async Task<RemoteRequest> WaitRequest()
    {
        for (var i = 0; i < 200; i++)
        {
            if ((await _f.Runtime().GetStatusAsync(new GetStatusRequest())).RemoteService?.Requests.FirstOrDefault() is { } r) return r;
            await Task.Delay(25);
        }
        throw new TimeoutException("no remote request in the status");
    }

    async Task<JsonElement> Job(string id, string cookie, string role = "oem")
    {
        for (var i = 0; i < 200; i++)
        {
            var j = (await Ask("GET", "/_rs/jobs/" + id, cookie, role: role)).Json;
            if (j.GetProperty("state").GetString() is "done" or "failed" or "refused") return j;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    async Task<Answer> Post(string path, object body, string cookie) =>
        await AskBytes("POST", path, cookie, JsonSerializer.SerializeToUtf8Bytes(body), "application/json");

    /// <summary>A request with a raw body (uploads, JSON).</summary>
    async Task<Answer> AskBytes(string method, string path, string cookie, byte[] body, string contentType, string query = "")
    {
        var ws = await Link();
        var id = "r" + Interlocked.Increment(ref _next);
        var ch = _answers[id] = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
        var frame = new JsonObject
        {
            ["t"] = "req", ["id"] = id, ["method"] = method, ["path"] = path, ["query"] = query,
            ["headers"] = new JsonObject { ["accept"] = "*/*", ["cookie"] = cookie, ["content-type"] = contentType },
            ["user"] = "anita", ["role"] = "oem", ["body"] = Convert.ToBase64String(body),
        };
        await SendText(ws, frame.ToJsonString());
        var status = 0;
        var data = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var f in ch.Reader.ReadAllAsync(timeout.Token))
        {
            switch ((string?)f["t"])
            {
                case "head": status = (int)f["status"]!; break;
                case "data": data.Write(Convert.FromBase64String((string)f["b64"]!)); break;
                case "end": _answers.TryRemove(id, out _); return new Answer(status == 0 ? 502 : status, [], data.ToArray());
            }
        }
        throw new TimeoutException();
    }

    /// <summary>The fixture's Labels job as a new job file with its own name and PLC number.</summary>
    byte[] NewJob(string name, int number)
    {
        var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_f.Folder, "jobs", "Labels.json")))!.AsObject();
        foreach (var key in json.Select(p => p.Key).ToList())
        {
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) json[key] = name;
            if (key.Equals("number", StringComparison.OrdinalIgnoreCase)) json[key] = number;
        }
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    /// <summary>Uploads a job file; returns the finish answer (202 with a job, or the refusal).</summary>
    async Task<Answer> UploadJob(string cookie, string file, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var start = await Post("/_rs/uploads", new { root = "jobs", path = file, size = bytes.Length, sha256 = hash }, cookie);
        Assert.Equal(200, start.Status);
        var up = start.Json.GetProperty("id").GetString()!;
        Assert.Equal(200, (await AskBytes("POST", $"/_rs/uploads/{up}", cookie, bytes, "application/octet-stream", "?offset=0")).Status);
        return await AskBytes("POST", $"/_rs/uploads/{up}/finish", cookie, [], "application/octet-stream");
    }

    [Fact]
    public async Task A_job_file_goes_in_place_only_after_the_operator_confirms_at_the_line_PC()
    {
        await Start();
        var oem = await SignIn(Factory(), "oem");
        var jobs = Path.Combine(_f.Folder, "jobs");
        var bytes = NewJob("Labels copy", 42);
        var fin = await UploadJob(oem, "Labels copy.json", bytes);
        Assert.Equal(202, fin.Status);

        var request = await WaitRequest();
        Assert.Equal(RemoteRequest.Types.Kind.Confirm, request.Kind);
        Assert.Contains("jobs/Labels copy.json", request.What);
        Assert.DoesNotContain("anita", request.What + request.Title);   // never who asks (REQ-08)
        Assert.False(File.Exists(Path.Combine(jobs, "Labels copy.json")));   // nothing before the operator says yes

        var answer = await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = true });
        Assert.True(answer.Ok, answer.ErrorText);
        // a second answer (a double tap, another screen) finds it gone and records nothing more
        var twice = await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = false });
        Assert.Equal(404u, twice.ErrorCode);
        var j = await Job(fin.Json.GetProperty("job").GetString()!, oem);
        Assert.Equal("done", j.GetProperty("state").GetString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(jobs, "Labels copy.json")));
        var kinds = Log().Entries(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Select(e => e.Kind).ToArray();
        Assert.Contains("confirmed", kinds);
        Assert.Contains("file_written", kinds);
        Assert.Empty((await _f.Runtime().GetStatusAsync(new GetStatusRequest())).RemoteService?.Requests ?? []);
    }

    [Fact]
    public async Task A_batch_correction_needs_a_supervisor_at_the_line_PC_and_is_appended_never_an_edit()
    {
        await Start(prepare: f => new AccessStore(Path.Combine(f.Folder, "data")).SaveUser("sup", "Priya Supervisor", [Role.Supervisor], null, "8642"));
        var reports = _f.App.Services.GetRequiredService<VisionStudio.Services.ReportService>();
        reports.Log.StartBatch("B-0457", "Labels", "op", JsonSerializer.Serialize(new Dictionary<string, string> { ["lot"] = "LOT-0475", ["mrp"] = "125.00" }), DateTime.UtcNow);
        var oem = await SignIn(Factory(), "oem");

        var rec = await Ask("GET", "/_rs/corrections/batch", oem, role: "oem", query: "?record=B-0457");
        Assert.Equal("LOT-0475", rec.Json.GetProperty("values").GetProperty("lot").GetString());
        var r = await Post("/_rs/corrections/batch", new { record = "B-0457", changes = new Dictionary<string, string> { ["lot"] = "LOT-0457" },
            reason = "Lot typed wrong at batch start; label shows LOT-0457" }, oem);
        Assert.Equal(202, r.Status);

        var request = await WaitRequest();
        Assert.Equal(RemoteRequest.Types.Kind.Approve, request.Kind);
        Assert.Equal(("lot", "LOT-0475", "LOT-0457"), (request.Changes[0].Field, request.Changes[0].OldValue, request.Changes[0].NewValue));

        // over the network: never; an operator's PIN or a wrong PIN: no; nothing approved yet
        var token = _f.AddToken("mes", operate: true);
        var remote = await _f.Runtime(network: true).AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = true, User = "sup", Pin = "8642" },
            RuntimeFixture.Bearer(token));
        Assert.Equal(403u, remote.ErrorCode);
        var op = await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = true, User = "op", Pin = RuntimeFixture.OperatorPin });
        Assert.Equal("runtime.remote.notSupervisor", op.ErrorKey);
        var wrong = await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = true, User = "sup", Pin = "0000" });
        Assert.False(wrong.Ok);

        var ok = await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = true, User = "sup", Pin = "8642" });
        Assert.True(ok.Ok, ok.ErrorText);
        var j = await Job(r.Json.GetProperty("job").GetString()!, oem);
        Assert.Equal("done", j.GetProperty("state").GetString());
        Assert.Contains("Priya Supervisor", j.GetProperty("result").GetString());

        // the batch row is as entered; the correction sits next to it in the chained service log, and reports show both
        Assert.Contains("LOT-0475", reports.Log.Batches(limit: 5).Single(b => b.Name == "B-0457").Fields);
        var corrections = Log().BatchCorrections("B-0457");
        var c = Assert.Single(corrections);
        Assert.Equal(("LOT-0475", "LOT-0457", "Priya Supervisor"), (c.Changes["lot"].Old, c.Changes["lot"].New, c.ApprovedBy));
        Log().Verify();
        var data = reports.Build(new VisionReports.ReportQuery(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), "B-0457", "Batch B-0457"));
        Assert.Single(data.Corrections);
        Assert.NotEmpty(VisionReports.ExcelReport.Create(data));
        Assert.NotEmpty(VisionReports.PdfReport.Create(data));
        // and the next correction starts from the corrected value
        var again = await Ask("GET", "/_rs/corrections/batch", oem, role: "oem", query: "?record=B-0457");
        Assert.Equal("LOT-0457", again.Json.GetProperty("values").GetProperty("lot").GetString());
    }

    [Fact]
    public async Task No_at_the_line_PC_refuses_without_any_sign_in()
    {
        await Start();
        var oem = await SignIn(Factory(), "oem");
        var fin = await UploadJob(oem, "Other.json", NewJob("Other", 43));
        var request = await WaitRequest();
        Assert.True((await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = false })).Ok);
        Assert.Equal("refused", (await Job(fin.Json.GetProperty("job").GetString()!, oem)).GetProperty("state").GetString());
        Assert.False(File.Exists(Path.Combine(_f.Folder, "jobs", "Other.json")));
    }

    [Fact]
    public async Task A_job_file_with_another_jobs_PLC_number_is_refused_before_anyone_is_asked()
    {
        await Start();
        var oem = await SignIn(Factory(), "oem");
        var fin = await UploadJob(oem, "Copy.json", NewJob("Copy", 7));   // 7 is the Labels job
        Assert.Equal(400, fin.Status);
        Assert.Contains("job number 7", fin.Text);
        Assert.Empty((await _f.Runtime().GetStatusAsync(new GetStatusRequest())).RemoteService?.Requests ?? []);
    }

    [Fact]
    public async Task Nothing_goes_in_place_while_the_line_runs_even_after_the_operator_said_yes()
    {
        await Start();
        var oem = await SignIn(Factory(), "oem");
        var fin = await UploadJob(oem, "Running.json", NewJob("Running", 44));
        var request = await WaitRequest();
        // the line starts while the operator is being asked: the change is checked again after the answer
        var op = await _f.SignInOperator();
        Assert.True((await _f.Runtime().ChangeJobAsync(new ChangeJobRequest { Name = "Labels" }, op)).Ok);
        Assert.True((await _f.Runtime().StartAsync(new StartRequest(), op)).Ok);
        Assert.True((await _f.Runtime().AnswerRemoteRequestAsync(new AnswerRemoteRequestRequest { Id = request.Id, Approve = true })).Ok);
        var j = await Job(fin.Json.GetProperty("job").GetString()!, oem);
        Assert.Equal("refused", j.GetProperty("state").GetString());
        Assert.Contains("running", j.GetProperty("message").GetString());
        Assert.False(File.Exists(Path.Combine(_f.Folder, "jobs", "Running.json")));

        // and while it runs nothing is even asked
        var again = await UploadJob(oem, "Running.json", NewJob("Running", 44));
        Assert.Equal("refused", (await Job(again.Json.GetProperty("job").GetString()!, oem)).GetProperty("state").GetString());
        await _f.Runtime().StopAsync(new StopRequest(), op);
    }

    [Fact]
    public async Task A_batch_name_started_twice_is_not_correctable()
    {
        await Start();
        var reports = _f.App.Services.GetRequiredService<VisionStudio.Services.ReportService>();
        reports.Log.StartBatch("B-1", "Labels", "op", "{\"lot\":\"L1\"}", DateTime.UtcNow.AddHours(-2));
        reports.Log.StartBatch("B-1", "Labels", "op", "{\"lot\":\"L2\"}", DateTime.UtcNow);
        var oem = await SignIn(Factory(), "oem");
        Assert.Equal(404, (await Ask("GET", "/_rs/corrections/batch", oem, role: "oem", query: "?record=B-1")).Status);
    }
}
