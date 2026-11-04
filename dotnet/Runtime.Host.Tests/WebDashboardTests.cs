using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using VisionReports;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The browser dashboard (Hybrid 4): view-only pages on their own port, a password sign-in
/// that is always a browser session, no changing route, and the data of the Live, Trends, Reports and
/// Audit trail pages.</summary>
public sealed class WebDashboardTests : IAsyncLifetime
{
    RuntimeFixture _f = null!;
    int _web;
    HttpClient _http = null!;
    CookieContainer _cookies = new();

    public async Task InitializeAsync()
    {
        _web = FreePort();
        _f = await RuntimeFixture.StartAsync(null, $"--Runtime:WebPort={_web}");
        _http = Client(_cookies);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _f.DisposeAsync();
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    HttpClient Client(CookieContainer cookies) =>
        new(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };

    Task<HttpResponseMessage> SignIn(HttpClient c, string user, string password) =>
        c.PostAsync("/web/sign-in", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = user, ["password"] = password }));

    [Fact]
    public async Task Page_is_served_and_the_data_needs_a_sign_in()
    {
        var page = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Vision System", await page.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/web/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/web/tokens.g.css")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/web/vs-runtime.dll")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/web/api/status")).StatusCode);
    }

    [Fact]
    public async Task Password_sign_in_reads_the_line()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(_http, "eng", "wrong")).StatusCode);
        var ok = await SignIn(_http, "eng", "secret-pass");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await _http.GetStringAsync("/web/api/me");
        Assert.Contains("Vision Engineer", me);
        var status = await _http.GetStringAsync("/web/api/status");
        Assert.Contains("\"state\"", status);
        Assert.Contains("\"inspections\"", status);
        Assert.Contains("\"yieldPercent\"", await _http.GetStringAsync("/web/api/counters"));
        Assert.StartsWith("[", await _http.GetStringAsync("/web/api/recent"));

        await _http.PostAsync("/web/sign-out", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/web/api/status")).StatusCode);
    }

    [Fact]
    public async Task A_pin_is_not_enough_over_the_network()
    {
        // the operator has only a PIN: fine at the line PC, never in the browser
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(_http, "op", RuntimeFixture.OperatorPin)).StatusCode);
    }

    [Fact]
    public async Task Only_a_browser_session_opens_the_pages()
    {
        // a Vision Studio session token or a plant token put in the cookie opens nothing
        var studio = await _f.Sessions().SignInAsync(new SignInRequest { User = "op", Pin = RuntimeFixture.OperatorPin, Client = ClientKind.Studio });
        var plant = _f.AddToken("mes", operate: true);
        foreach (var token in new[] { studio.Token, plant })
        {
            var jar = new CookieContainer();
            jar.Add(new Uri($"http://127.0.0.1:{_web}"), new Cookie(WebDashboard.Cookie, token));
            using var c = Client(jar);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/web/api/status")).StatusCode);
        }
    }

    [Fact]
    public async Task There_is_no_changing_route_and_no_cross_site_post()
    {
        await SignIn(_http, "eng", "secret-pass");
        foreach (var path in new[] { "/web/api/status", "/web/api/start", "/web/api/stop", "/web/api/change-job", "/" })
        {
            var r = await _http.PostAsync(path, new StringContent("{}"));
            Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{path}: {r.StatusCode}");
        }
        var cross = new HttpRequestMessage(HttpMethod.Post, "/web/sign-in")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "eng", ["password"] = "secret-pass" }),
        };
        cross.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(cross)).StatusCode);
    }

    [Fact]
    public async Task Reports_list_and_download_only_report_files()
    {
        var reports = _f.App.Services.GetRequiredService<ReportService>();
        await File.WriteAllBytesAsync(Path.Combine(reports.Folder, "Shift A 2026-11-03.pdf"), "%PDF-1.4 test"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(reports.Folder, "notes.txt"), "not a report");
        await SignIn(_http, "eng", "secret-pass");
        var list = await _http.GetStringAsync("/web/api/reports");
        Assert.Contains("Shift A 2026-11-03.pdf", list);
        Assert.DoesNotContain("notes.txt", list);
        var pdf = await _http.GetAsync("/web/api/reports/" + Uri.EscapeDataString("Shift A 2026-11-03.pdf"));
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/web/api/reports/notes.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/web/api/reports/..%2Fruntime-users.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/web/api/reports/..%2F..%2Fx.pdf")).StatusCode);
    }

    [Fact]
    public async Task Trend_counts_inspections_per_hour_with_the_ng_reasons()
    {
        var log = _f.App.Services.GetRequiredService<ReportService>().Log;
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
            log.AddInspection(new InspectionRow(0, now.AddMinutes(-i), "B1", "Labels", 1, i, i % 5 != 0, i % 5 == 0 ? "Read code" : null,
                i % 5 == 0 ? "No code found" : null, null, null, 10 + i));
        log.AddInspection(new InspectionRow(0, now.AddDays(-3), "B0", "Labels", 1, 99, false, "Read code", "Old", null, null, 5));
        await SignIn(_http, "eng", "secret-pass");
        var day = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync("/web/api/trend?range=day")).RootElement;
        var hours = day.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(10, hours.Sum(h => h.GetProperty("count").GetInt32()));
        Assert.Equal(8, hours.Sum(h => h.GetProperty("ok").GetInt32()));
        Assert.False(day.GetProperty("fromParts").GetBoolean());
        var reasons = day.GetProperty("reasons").EnumerateArray().ToList();
        // grouped by the failing tool, never by the free reason text
        Assert.Equal("Read code", reasons.Single().GetProperty("reason").GetString());
        Assert.Equal(2, reasons.Single().GetProperty("count").GetInt32());
        var week = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync("/web/api/trend?range=week")).RootElement;
        Assert.Equal(11, week.GetProperty("hours").EnumerateArray().Sum(h => h.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Trend_counts_the_plcs_parts_when_there_are_any()
    {
        // two cameras per part: the part log, not the inspections, says how many parts and which were rejected
        var log = _f.App.Services.GetRequiredService<ReportService>().Log;
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            var bad = i == 0;
            log.AddInspection(new InspectionRow(0, now.AddMinutes(-i), "B1", "Labels", 0, i, true, null, null, null, null, 20));
            log.AddInspection(new InspectionRow(0, now.AddMinutes(-i), "B1", "Labels", 1, i, !bad, bad ? "Read text" : null, bad ? "expected A1, found A7" : null, null, null, 30));
            log.AddPart(new PartRow(0, now.AddMinutes(-i), "B1", "Labels", i, bad, VisionCore.Plc.PartFlags.None, null, null, 1, 0, 3, bad ? 1 : 3,
                bad ? "Camera 2: Read text" : "OK", bad ? "Camera 2 NOT OK" : "OK"));
        }
        await SignIn(_http, "eng", "secret-pass");
        var t = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync("/web/api/trend?range=shift")).RootElement;
        Assert.True(t.GetProperty("fromParts").GetBoolean());
        var hours = t.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(4, hours.Sum(h => h.GetProperty("count").GetInt32()));
        Assert.Equal(3, hours.Sum(h => h.GetProperty("ok").GetInt32()));
        Assert.Equal("Camera 2: Read text", t.GetProperty("reasons").EnumerateArray().Single().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_dashboard_port_never_serves_the_runtime_contract()
    {
        // a gRPC call on the dashboard port is not found, whatever token it carries
        var stop = new HttpRequestMessage(HttpMethod.Post, "/visionstudio.remote.v1.VisionRuntime/Stop") { Content = new ByteArrayContent([0, 0, 0, 0, 0]) };
        stop.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        stop.Headers.Add("authorization", "Bearer " + _f.AddToken("mes", operate: true));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(stop)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/visionstudio.remote.v1.Session/ListUsers")).StatusCode);

        // and the local endpoint never serves the dashboard
        using var socket = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(_f.Endpoint.SocketPath!), ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        })
        { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        foreach (var path in new[] { "/", "/web/app.js", "/web/api/status" })
            Assert.Equal(HttpStatusCode.NotFound, (await socket.GetAsync("http://localhost" + path)).StatusCode);
    }

    [Fact]
    public async Task Audit_shows_batches_and_line_events()
    {
        var log = _f.App.Services.GetRequiredService<ReportService>().Log;
        log.StartBatch("LOT-42", "Labels", "Line Operator", "{}", DateTime.UtcNow.AddMinutes(-5));
        log.AddEvent(DateTime.UtcNow.AddMinutes(-4), "jam", true, "Conveyor jam at the reject gate");
        // the audit trail is QA's and Admin's (audit.view, requirements 6.4 WEB-02)
        new AccessStore(Path.Combine(_f.Folder, "data")).SaveUser("qa", "Quality", [Role.Qa], "secret-pass", null);
        await SignIn(_http, "qa", "secret-pass");
        var a = await _http.GetStringAsync("/web/api/audit?days=1");
        Assert.Contains("LOT-42", a);
        Assert.Contains("Conveyor jam at the reject gate", a);
    }

    [Fact]
    public async Task Live_frame_and_events_stream()
    {
        var frames = _f.App.Services.GetRequiredService<WebFrames>();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        frames.Put(new LiveFrame { Camera = "Top", InspectionId = 12, Pass = false, Jpeg = ByteString.CopyFrom(jpeg), Width = 2, Height = 2 });
        await SignIn(_http, "eng", "secret-pass");
        Assert.Contains("Top", await _http.GetStringAsync("/web/api/cameras"));
        var frame = await _http.GetAsync("/web/api/frame/Top");
        Assert.Equal(jpeg, await frame.Content.ReadAsByteArrayAsync());
        Assert.Equal("12", frame.Headers.GetValues("X-Inspection-Id").Single());
        Assert.Equal("0", frame.Headers.GetValues("X-Pass").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/web/api/frame/Side")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/web/api/image/..%2Fx")).StatusCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/web/api/live");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        var first = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("event: status", first);
    }
}
