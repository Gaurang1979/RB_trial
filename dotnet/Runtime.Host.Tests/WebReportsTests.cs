using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionReports;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The browser dashboard's Reports tabs (requirements 6.4 WEB-07, UI-UX-SPEC.md 4.11 read only): Shift
/// with Save as PDF and Save as Excel, Find a part, Operator flags, within the role and line limits of WEB-01..03.</summary>
public sealed class WebReportsTests : IAsyncDisposable
{
    const string Pass = "secret-pass";
    RuntimeFixture _f = null!;
    int _web;

    async Task Start(params string[] extra)
    {
        _web = FreePort();
        _f = await RuntimeFixture.StartAsync(f =>
        {
            var s = new AccessStore(Path.Combine(f.Folder, "data"));
            s.SaveUser("opw", "Web Operator", [Role.Operator], Pass, null);
            s.SaveUser("sup", "Line Supervisor", [Role.Supervisor], Pass, null);
            s.SaveUser("far", "Other Line Supervisor", [Role.Supervisor], Pass, null, lines: ["Line 9"]);
        }, [$"--Runtime:WebPort={_web}", "--Plant:Line=L3", .. extra]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null) await _f.DisposeAsync();
    }

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

    static async Task<JsonElement> Json(HttpClient c, string path) => JsonDocument.Parse(await c.GetStringAsync("/web/api/" + path)).RootElement;

    ReportService Reports => _f.App.Services.GetRequiredService<ReportService>();

    static InspectionRow Insp(DateTime t, int part, bool ok, string? text = null, string? image = null, string? partText = null) =>
        new(0, t, "LOT-7", "Labels", 0, part, ok, ok ? null : "Read code", ok ? null : "No code found", text, null, 12.5, partText, image);

    [Fact]
    public async Task Shift_tab_counts_the_current_and_the_three_shifts_before_with_the_top_reasons()
    {
        await Start();
        var log = Reports.Log;
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++) log.AddInspection(Insp(now.AddSeconds(-i), i, i % 4 != 0));
        // the shift before: one good, one bad part, counted from the PLC's part log there
        var prev = Reports.PreviousShift();
        var t = prev.FromLocal.AddMinutes(5).ToUniversalTime();
        log.AddPart(new PartRow(0, t, "LOT-6", "Labels", 1, false, VisionCore.Plc.PartFlags.None, null, null, 1, 0, 1, 1, "OK", "OK"));
        log.AddPart(new PartRow(0, t, "LOT-6", "Labels", 2, true, VisionCore.Plc.PartFlags.None, null, null, 1, 0, 1, 0, "Camera 1: Read code", "NOT OK"));

        using var sup = await SignedIn("sup");
        var shifts = (await Json(sup, "shifts")).GetProperty("shifts").EnumerateArray().ToList();
        Assert.Equal(4, shifts.Count);
        var cur = shifts[0];
        Assert.True(cur.GetProperty("current").GetBoolean());
        Assert.Equal(10, cur.GetProperty("total").GetInt32());
        Assert.Equal(7, cur.GetProperty("good").GetInt32());
        Assert.Equal(3, cur.GetProperty("bad").GetInt32());
        Assert.Equal(70.0, cur.GetProperty("goodPct").GetDouble());
        var reason = cur.GetProperty("reasons").EnumerateArray().Single();
        Assert.Equal("Read code", reason.GetProperty("reason").GetString());   // the plain tool name, never the free text
        Assert.Equal(3, reason.GetProperty("count").GetInt32());

        Assert.Equal(prev.Name, shifts[1].GetProperty("name").GetString());
        Assert.True(shifts[1].GetProperty("fromParts").GetBoolean());
        Assert.Equal(2, shifts[1].GetProperty("total").GetInt32());
        Assert.Equal(1, shifts[1].GetProperty("bad").GetInt32());
        Assert.Equal("Camera 1: Read code", shifts[1].GetProperty("reasons")[0].GetProperty("reason").GetString());
        // a shift with nothing in it has no good % rather than 0 %
        Assert.Equal(JsonValueKind.Null, shifts[3].GetProperty("goodPct").ValueKind);
        // newest first, each shift ending where the next one starts
        Assert.Equal(shifts[1].GetProperty("toUtc").GetDateTime(), cur.GetProperty("fromUtc").GetDateTime());
    }

    [Fact]
    public async Task Shift_saves_as_pdf_and_excel_and_a_finished_shift_gives_the_file_the_line_pc_wrote()
    {
        await Start();
        Reports.Log.AddInspection(Insp(DateTime.UtcNow, 1, false));
        using var sup = await SignedIn("sup");

        var pdf = await sup.GetAsync("/web/api/shift-report?shift=0&format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        var cur = Reports.CurrentShift();
        Assert.Equal($"{cur.FromLocal:yyyy-MM-dd HHmm} {cur.Name}.pdf", pdf.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

        var xlsx = await sup.GetAsync("/web/api/shift-report?shift=0&format=xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync())[..2]));   // a zip: .xlsx

        // two shifts ago: the line PC's own file, as written at the shift's end
        var older = Reports.ShiftAt(Reports.PreviousShift().FromLocal.AddMinutes(-1));
        await File.WriteAllBytesAsync(Path.Combine(Reports.Folder, $"{older.FromLocal:yyyy-MM-dd HHmm} {older.Name}.pdf"), "%PDF-1.4 written"u8.ToArray());
        Assert.Equal("%PDF-1.4 written", await sup.GetStringAsync("/web/api/shift-report?shift=2&format=pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/web/api/shift-report?shift=0&format=docx")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/web/api/shift-report?shift=4&format=pdf")).StatusCode);
    }

    [Fact]
    public async Task Operator_allowed_the_shift_reports_sees_shift_only_never_find_a_part_or_the_flags()
    {
        await Start("--Runtime:WebOperatorShiftReports=true");
        using var op = await SignedIn("opw");
        Assert.False((await Json(op, "me")).GetProperty("allReports").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/web/api/shifts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/web/api/shift-report?shift=1&format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/web/api/find?q=HWN937")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/web/api/flags")).StatusCode);

        using var sup = await SignedIn("sup");
        Assert.True((await Json(sup, "me")).GetProperty("allReports").GetBoolean());
    }

    [Fact]
    public async Task Operator_without_the_shift_reports_and_a_user_of_other_lines_see_no_reports_tab()
    {
        await Start();
        using var op = await SignedIn("opw");
        foreach (var p in new[] { "shifts", "shift-report?shift=0&format=pdf", "find?q=HWN937", "flags" })
            Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/web/api/" + p)).StatusCode);
        using var far = await SignedIn("far");
        Assert.False((await Json(far, "me")).GetProperty("allReports").GetBoolean());
        foreach (var p in new[] { "shifts", "shift-report?shift=0&format=pdf", "find?q=HWN937", "flags" })
            Assert.Equal(HttpStatusCode.Forbidden, (await far.GetAsync("/web/api/" + p)).StatusCode);
    }

    [Fact]
    public async Task Find_a_part_by_code_part_id_image_name_or_time()
    {
        await Start();
        var log = Reports.Log;
        var now = DateTime.UtcNow;
        log.AddInspection(Insp(now.AddMinutes(-30), 1, true, "(01)0890123456789(21)HWN937", "L03_C0_J007_261104-101500_000001", "A12-0042"));
        log.AddInspection(Insp(now.AddMinutes(-20), 2, false, "(01)0890123456789(21)HWN881", "L03_C0_J007_261104-102500_000002"));
        log.AddInspection(Insp(now.AddMinutes(-10), 3, true, "100%_LOT", null));
        log.AddInspection(Insp(now.AddDays(-3), 4, true, "OLD-HWN937", null));
        using var sup = await SignedIn("sup");

        var byCode = (await Json(sup, "find?q=hwn937&days=1")).EnumerateArray().ToList();
        var hit = Assert.Single(byCode);   // any case; the older one is outside the last day
        Assert.Equal("A12-0042", hit.GetProperty("partId").GetString());
        Assert.Equal("L03_C0_J007_261104-101500_000001", hit.GetProperty("imageId").GetString());
        Assert.True(hit.GetProperty("ok").GetBoolean());
        Assert.Equal(2, (await Json(sup, "find?q=HWN937&days=7")).GetArrayLength());

        // newest first, with what failed
        var both = (await Json(sup, "find?q=0890123456789&days=1")).EnumerateArray().ToList();
        Assert.Equal(["HWN881", "HWN937"], both.Select(r => r.GetProperty("text1").GetString()![^6..]));
        Assert.Equal("Read code", both[0].GetProperty("failTool").GetString());
        Assert.Equal("No code found", both[0].GetProperty("reason").GetString());

        Assert.Single((await Json(sup, "find?q=A12-0042")).EnumerateArray());
        Assert.Single((await Json(sup, "find?q=102500_000002")).EnumerateArray());
        // % and _ are the text, not wildcards
        Assert.Single((await Json(sup, "find?q=" + Uri.EscapeDataString("0%_L"))).EnumerateArray());
        Assert.Empty((await Json(sup, "find?q=" + Uri.EscapeDataString("_HWN__"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/web/api/find?q=HW")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/web/api/find")).StatusCode);

        // by time: within a minute of it
        var at = Uri.EscapeDataString(now.AddMinutes(-20).AddSeconds(30).ToString("O"));
        var near = Assert.Single((await Json(sup, "find?at=" + at)).EnumerateArray());
        Assert.Equal("L03_C0_J007_261104-102500_000002", near.GetProperty("imageId").GetString());
    }

    [Fact]
    public async Task Operator_flags_list_the_reviewed_parts_newest_first()
    {
        await Start();
        var reviews = _f.App.Services.GetRequiredService<ReviewStore>();
        var picture = Path.Combine(_f.Folder, "part.bmp");
        BarcodeReading.BmpImage.SaveGray(picture, new byte[64 * 48], 64, 48);
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        reviews.Review("Labels", 0, "Top", "L03_C0_J007_261104-101500_000001", picture, ReviewStore.Kind.Good, "Line Supervisor",
            "label was fine", at, ["Read code|explain.code.not_found"]);
        reviews.Review("Labels", 0, "Top", "L03_C0_J007_261104-102500_000002", picture, ReviewStore.Kind.Bad, "QA Lead", "", at.AddMinutes(1), []);

        using var sup = await SignedIn("sup");
        var flags = (await Json(sup, "flags")).EnumerateArray().ToList();
        Assert.Equal(2, flags.Count);
        Assert.Equal("confirmed_ng", flags[0].GetProperty("decision").GetString());
        Assert.Equal("QA Lead", flags[0].GetProperty("user").GetString());
        var good = flags[1];
        Assert.Equal("accepted", good.GetProperty("decision").GetString());
        Assert.Equal("Labels", good.GetProperty("job").GetString());
        Assert.Equal("Top", good.GetProperty("cameraName").GetString());
        Assert.Equal("L03_C0_J007_261104-101500_000001", good.GetProperty("key").GetString());
        Assert.Equal("label was fine", good.GetProperty("note").GetString());
        Assert.Equal("Read code|explain.code.not_found", good.GetProperty("faults")[0].GetString());
    }

    [Fact]
    public async Task The_page_has_the_tabs_and_print_hides_the_menus()
    {
        await Start();
        using var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };
        var page = await c.GetStringAsync("/");
        foreach (var tab in new[] { "data-tab=\"shift\"", "data-tab=\"find\"", "data-tab=\"flags\"", "data-tab=\"files\"", "Save as PDF", "Save as Excel", "shift-print" })
            Assert.Contains(tab, page);
        Assert.Contains("@media print", await c.GetStringAsync("/web/app.css"));
    }
}
