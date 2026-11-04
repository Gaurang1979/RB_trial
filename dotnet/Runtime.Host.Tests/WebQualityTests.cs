using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Remote.V1;
using VisionStudio.Services;
using VisionSystem.Runtime;
using Xunit;

/// <summary>The browser dashboard's idle sign-out and print-quality trend (requirements 6.4 WEB-04, WEB-05;
/// 17.3 PQ-03..05): the drift rule is the plant server's, an ungraded part counts as F, and the data follows
/// the role and line limits of WEB-01..03.</summary>
public sealed class WebQualityTests : IAsyncDisposable
{
    const string Pass = "secret-pass";
    static readonly DateTime Now = new(2026, 11, 3, 12, 30, 0, DateTimeKind.Utc);
    RuntimeFixture? _f;
    int _web;

    public async ValueTask DisposeAsync()
    {
        if (_f is not null) await _f.DisposeAsync();
    }

    static GradeSample S(double? g, double? low = null) => new(Now, g, low);
    static List<GradeSample> Run(int n, Func<int, double?> g) => Enumerable.Range(0, n).Select(i => S(g(i))).ToList();

    [Fact]
    public void Steady_grades_are_ok_and_too_few_are_not_judged()
    {
        Assert.Equal("ok", GradeTrail.DriftOf(Run(20, _ => 4)).State);
        var few = GradeTrail.DriftOf(Run(9, _ => 0));
        Assert.Equal("too_few", few.State);
        Assert.Equal("too_few", GradeTrail.DriftOf([]).State);
    }

    [Fact]
    public void A_mean_near_the_lowest_grade_or_falling_to_it_is_drift()
    {
        // lowest C (2): warn at 2.5; a mean of 2.4 is inside the margin
        Assert.Equal("drift", GradeTrail.DriftOf(Run(20, _ => 2.4)).State);
        Assert.Equal("ok", GradeTrail.DriftOf(Run(20, _ => 3.6)).State);
        // A falling to B over the window: the mean is still above the margin but the line reaches C within 20 more parts
        var falling = GradeTrail.DriftOf(Run(20, i => 4 - i * 0.08));
        Assert.True(falling.Mean > 3.0);
        Assert.True(falling.Slope < 0);
        Assert.Equal("drift", falling.State);
    }

    [Fact]
    public void An_ungraded_part_counts_as_F_never_as_good()
    {
        var d = GradeTrail.DriftOf(Run(20, i => i % 2 == 0 ? 4 : null));   // mean 2.0
        Assert.Equal(10, d.Ungraded);
        Assert.Equal(2.0, d.Mean);
        Assert.Equal("drift", d.State);
        var trail = new GradeTrail();
        trail.Add("QR", Now, 9);        // not a grade (0..4): ungraded
        trail.Add("QR", Now, double.NaN);
        var (buckets, _) = trail.Of("QR", Now.AddHours(-1), Now)!.Value;
        Assert.Equal(2, buckets.Single().Ungraded);
        Assert.Equal(0, buckets.Single().Mean);
    }

    [Fact]
    public void The_tools_own_lowest_grade_wins_over_the_default()
    {
        var samples = Enumerable.Range(0, 20).Select(_ => S(3.2, 3)).ToList();   // lowest B: warn at 3.5
        var d = GradeTrail.DriftOf(samples);
        Assert.Equal("tool", d.LowestFrom);
        Assert.Equal(3, d.Lowest);
        Assert.Equal("drift", d.State);
        Assert.Equal("default", GradeTrail.DriftOf(Run(12, _ => 4)).LowestFrom);
    }

    [Fact]
    public void Only_the_newest_samples_are_kept_and_old_ones_do_not_judge()
    {
        var trail = new GradeTrail();
        for (var i = 0; i < GradeTrail.Keep + 50; i++) trail.Add("QR", Now.AddSeconds(i % 3000), 4);
        var (b, _) = trail.Of("QR", Now.AddDays(-1), Now.AddHours(1))!.Value;
        Assert.Equal(GradeTrail.Keep, b.Sum(x => x.Samples));
        // nothing for 8 h: the status has too few samples, it does not stay "ok" on stale grades
        Assert.Equal("too_few", trail.Of("QR", Now.AddDays(-1), Now.AddHours(9))!.Value.Drift.State);
        Assert.Null(trail.Of("none", Now, Now));
    }

    [Fact]
    public void Buckets_are_hours_with_the_mean_and_the_worst_grade()
    {
        var trail = new GradeTrail();
        trail.Add("QR", new DateTime(2026, 11, 3, 11, 5, 0, DateTimeKind.Utc), 4);
        trail.Add("QR", new DateTime(2026, 11, 3, 11, 50, 0, DateTimeKind.Utc), 2);
        trail.Add("QR", new DateTime(2026, 11, 3, 12, 10, 0, DateTimeKind.Utc), 3);
        var (b, _) = trail.Of("QR", Now.AddHours(-8), Now)!.Value;
        Assert.Equal(2, b.Count);
        Assert.Equal((2, 3.0, 2.0), (b[0].Samples, b[0].Mean, b[0].Worst));
        Assert.Equal("C", GradeTrail.Letter(2));
        Assert.Equal("A", GradeTrail.Letter(4));
    }

    // ---- the idle sign-out lives in the page: what the served script must keep true -------------------

    static string Script()
    {
        using var s = typeof(WebDashboard).Assembly.GetManifestResourceStream("VisionSystem.Runtime.Web.app.js")!;
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void The_page_counts_only_a_persons_input_as_use()
    {
        var js = Script();
        // the only call that resets the idle clock is in armIdle (a new sign-in); every other use is a listener on
        // the person's events, never on the page's own polling, picture refresh or live stream
        Assert.Single(Regex.Matches(js, @"\btouch\(\)"));
        Assert.Matches(@"function armIdle\([^)]*\) \{[^}]*touch\(\);", js);
        Assert.Matches(@"for \(const ev of \[""keydown"", ""pointerdown"", ""pointermove"", ""touchstart"", ""wheel"", ""scroll""\]\)\s+window\.addEventListener\(ev, touch", js);
        foreach (var fn in new[] { "async function api(", "async function refreshFrames(", "function startLive(", "async function loadTrends(", "async function loadQuality(" })
        {
            var start = js.IndexOf(fn, StringComparison.Ordinal);
            Assert.True(start >= 0, fn);
            var end = js.IndexOf("\n}\n", start, StringComparison.Ordinal);
            Assert.DoesNotContain("lastInput", js[start..end]);
            Assert.DoesNotContain("touch", js[start..end]);
        }
        Assert.Contains("15 * 60 * 1000", js);
        // a signed-out page clears its timer and shows the sign-in with the reason
        Assert.Contains("\"web/sign-out\"", js[js.IndexOf("function checkIdle", StringComparison.Ordinal)..]);   // relative: also under /<machine>/ on the support relay
    }

    // ---- HTTP: role, line and idle time ---------------------------------------------------------------

    async Task<HttpClient> Start(string line = "L3")
    {
        _web = FreePort();
        _f = await RuntimeFixture.StartAsync(f =>
        {
            var s = new AccessStore(Path.Combine(f.Folder, "data"));
            s.SaveUser("sup", "Line Supervisor", [Role.Supervisor], Pass, null);
            s.SaveUser("far", "Other Line Engineer", [Role.Engineer], Pass, null, lines: ["Line 9"]);
            s.SaveUser("none", "No Role", [], Pass, null);
        }, [$"--Runtime:WebPort={_web}", $"--Plant:Line={line}"]);
        return await SignedIn("sup");
    }

    async Task<HttpClient> SignedIn(string user)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };
        var r = await c.PostAsync("/web/sign-in", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = user, ["password"] = Pass }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Me_tells_the_page_the_idle_time()
    {
        using var c = await Start();
        var me = JsonDocument.Parse(await c.GetStringAsync("/web/api/me")).RootElement;
        Assert.Equal(900, me.GetProperty("idleSignOutS").GetInt32());
    }

    [Fact]
    public async Task Quality_gives_each_tools_trend_and_drift_status()
    {
        using var c = await Start();
        var empty = JsonDocument.Parse(await c.GetStringAsync("/web/api/quality?range=day")).RootElement;
        Assert.Equal(0, empty.GetProperty("tools").GetArrayLength());

        var grades = _f!.App.Services.GetRequiredService<LineRuntime>().Grades;
        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++) grades.Add("Read QR", now.AddSeconds(-i), i % 4 == 0 ? null : 2.0, 2);
        for (var i = 0; i < 12; i++) grades.Add("Read DM", now.AddSeconds(-i), 4);
        var q = JsonDocument.Parse(await c.GetStringAsync("/web/api/quality?range=shift")).RootElement;
        var tools = q.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("tool").GetString()!);
        Assert.Equal("drift", tools["Read QR"].GetProperty("state").GetString());
        Assert.Equal(5, tools["Read QR"].GetProperty("ungraded").GetInt32());
        Assert.Equal("C", tools["Read QR"].GetProperty("lowestLetter").GetString());
        Assert.Equal("ok", tools["Read DM"].GetProperty("state").GetString());
        Assert.True(tools["Read DM"].GetProperty("buckets").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Quality_is_read_only_and_follows_the_line_and_role_limits()
    {
        using var sup = await Start();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await sup.PostAsync("/web/api/quality", new StringContent("{}"))).StatusCode);
        using var far = await SignedIn("far");   // limited to Line 9, this line is L3
        Assert.Equal(HttpStatusCode.Forbidden, (await far.GetAsync("/web/api/quality")).StatusCode);
        using var none = await SignedIn("none");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/web/api/quality")).StatusCode);
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/web/api/quality")).StatusCode);
    }
}
