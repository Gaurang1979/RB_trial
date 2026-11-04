using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The browser dashboard's scope (requirements 6.4 WEB-01..03): the role limits the pages and
/// reports, a line list limits the lines, and the runtime refuses the calls, not only the page.</summary>
public sealed class WebAccessTests : IAsyncDisposable
{
    const string Pass = "secret-pass";
    RuntimeFixture _f = null!;
    int _web;

    async Task Start(params string[] extra)
    {
        _web = FreePort();
        _f = await RuntimeFixture.StartAsync(f =>
        {
            var s = Store(f);
            s.SaveUser("opw", "Web Operator", [Role.Operator], Pass, null);
            s.SaveUser("sup", "Line Supervisor", [Role.Supervisor], Pass, null);
            s.SaveUser("qa", "Quality", [Role.Qa], Pass, null);
            s.SaveUser("far", "Other Line Engineer", [Role.Engineer], Pass, null, lines: ["Line 9"]);
            s.SaveUser("near", "This Line Engineer", [Role.Engineer], Pass, null, lines: ["Line 9", "L3"]);
            s.SaveUser("none", "No Role", [], Pass, null);
        }, [$"--Runtime:WebPort={_web}", "--Plant:Line=L3", .. extra]);
        var reports = _f.App.Services.GetRequiredService<ReportService>();
        await File.WriteAllBytesAsync(Path.Combine(reports.Folder, "2026-11-02 1400 Shift B.pdf"), "%PDF-1.4 shift"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(reports.Folder, "2026-11-02 1512 Batch LOT-7.pdf"), "%PDF-1.4 batch"u8.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null) await _f.DisposeAsync();
    }

    static AccessStore Store(RuntimeFixture f) => new(Path.Combine(f.Folder, "data"));

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    async Task<HttpClient> SignedIn(string user)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };
        var r = await c.PostAsync("/web/sign-in", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = user, ["password"] = Pass }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    static async Task<JsonElement> Me(HttpClient c) => JsonDocument.Parse(await c.GetStringAsync("/web/api/me")).RootElement;

    static string[] Pages(JsonElement me) => me.GetProperty("pages").EnumerateArray().Select(p => p.GetString()!).ToArray();

    static readonly string[] LivePaths = ["status", "counters", "recent", "cameras", "trend?range=shift"];

    [Fact]
    public async Task Operator_watches_live_and_trends_but_not_reports_or_audit()
    {
        await Start();
        using var c = await SignedIn("opw");
        Assert.Equal(["live", "trends"], Pages(await Me(c)));
        foreach (var p in LivePaths) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/web/api/" + p)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/reports/" + Uri.EscapeDataString("2026-11-02 1400 Shift B.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Operator_sees_only_shift_reports_when_the_plant_allows()
    {
        await Start("--Runtime:WebOperatorShiftReports=true");
        using var c = await SignedIn("opw");
        Assert.Equal(["live", "trends", "reports"], Pages(await Me(c)));
        var list = await c.GetStringAsync("/web/api/reports");
        Assert.Contains("Shift B", list);
        Assert.DoesNotContain("Batch", list);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/web/api/reports/" + Uri.EscapeDataString("2026-11-02 1400 Shift B.pdf"))).StatusCode);
        // a batch report is "not found" for the operator, as one that is not there
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/web/api/reports/" + Uri.EscapeDataString("2026-11-02 1512 Batch LOT-7.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Supervisor_reads_every_report_and_only_qa_and_admin_the_audit_trail()
    {
        await Start();
        using var sup = await SignedIn("sup");
        Assert.Equal(["live", "trends", "reports"], Pages(await Me(sup)));
        var list = await sup.GetStringAsync("/web/api/reports");
        Assert.Contains("Shift B", list);
        Assert.Contains("Batch LOT-7", list);
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.GetAsync("/web/api/audit")).StatusCode);

        using var qa = await SignedIn("qa");
        Assert.Equal(["live", "trends", "reports", "audit"], Pages(await Me(qa)));
        Assert.Equal(HttpStatusCode.OK, (await qa.GetAsync("/web/api/audit")).StatusCode);
    }

    [Fact]
    public async Task A_user_limited_to_other_lines_sees_nothing_of_this_one()
    {
        await Start();
        using var far = await SignedIn("far");
        var me = await Me(far);
        Assert.False(me.GetProperty("lineAllowed").GetBoolean());
        Assert.Empty(Pages(me));
        foreach (var p in LivePaths.Append("live").Append("reports").Append("audit").Append("frame/Top"))
            Assert.Equal(HttpStatusCode.Forbidden, (await far.GetAsync("/web/api/" + p)).StatusCode);

        // the line is named by its plant id (Plant:Line) or by its report name
        using var near = await SignedIn("near");
        Assert.True((await Me(near)).GetProperty("lineAllowed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await near.GetAsync("/web/api/reports")).StatusCode);
        Store(_f).SaveUser("far", "Other Line Engineer", [Role.Engineer], null, null, lines: ["line 1"]);
        Assert.Equal(HttpStatusCode.OK, (await far.GetAsync("/web/api/status")).StatusCode);
    }

    [Fact]
    public async Task A_user_without_a_role_sees_no_page()
    {
        await Start();
        using var c = await SignedIn("none");
        Assert.Empty(Pages(await Me(c)));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/status")).StatusCode);
    }

    [Fact]
    public async Task Changed_rights_count_from_the_next_call_and_a_removed_user_is_signed_out()
    {
        await Start();
        using var c = await SignedIn("sup");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/web/api/reports")).StatusCode);
        var store = Store(_f);
        store.SaveUser("sup", "Line Supervisor", [Role.Operator], null, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/reports")).StatusCode);
        store.SaveUser("sup", "Line Supervisor", [Role.Operator], null, null, lines: ["Line 9"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/web/api/status")).StatusCode);

        // taken out of the users file: signed out, and putting the user back does not revive the session
        var path = Path.Combine(_f.Folder, "data", "runtime-users.json");
        var users = JsonSerializer.Deserialize<List<JsonElement>>(await File.ReadAllTextAsync(path))!;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(users.Where(u => u.GetProperty("Name").GetString() != "sup")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/web/api/status")).StatusCode);
        store.SaveUser("sup", "Line Supervisor", [Role.Supervisor], Pass, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/web/api/status")).StatusCode);
    }

    [Fact]
    public void Operator_report_names_follow_the_shift_reporter()
    {
        var settings = new ReportSettings();   // Shift A, B, C
        Role[] op = [Role.Operator];
        Assert.True(WebAccess.MaySeeReport(op, "2026-11-02 1400 Shift B.xlsx", settings));
        Assert.False(WebAccess.MaySeeReport(op, "2026-11-02 1400 Shift D.pdf", settings));
        Assert.False(WebAccess.MaySeeReport(op, "2026-11-02 1512 Batch LOT-7.pdf", settings));
        Assert.False(WebAccess.MaySeeReport(op, "Shift B.pdf", settings));
        Assert.False(WebAccess.MaySeeReport(op, "2026-11-02 1400 shift b.pdf", settings));
        Assert.False(WebAccess.MaySeeReport([], "2026-11-02 1400 Shift B.pdf", settings));
        Assert.True(WebAccess.MaySeeReport([Role.Admin], "anything.pdf", settings));
        Assert.True(WebAccess.MaySeeLine(new RuntimeUser { Lines = null! }, ["L3"]));
        Assert.False(WebAccess.MaySeeLine(new RuntimeUser { Lines = ["L3"] }, []));
    }
}
