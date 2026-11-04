using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using VisionStudio.Remote.V1;
using VisionStudio.Services;

namespace VisionSystem.Runtime;

/// <summary>
/// The newest live frame of each camera, kept for the browser dashboard's Live page. One watcher for
/// all browsers, so ten open dashboards cost the line what one does; frames are the JPEGs the runtime
/// already makes for Vision Studio, never encoded again.
/// </summary>
public sealed class WebFrames(LineRuntime line, IHostApplicationLifetime life)
{
    readonly ConcurrentDictionary<string, LiveFrame> _last = new(StringComparer.Ordinal);
    int _started;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var reader = line.WatchFrames(life.ApplicationStopping);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in reader.ReadAllAsync(life.ApplicationStopping))
                    _last[f.Camera] = f;
            }
            catch (OperationCanceledException) { }
        });
    }

    public LiveFrame? Last(string camera) => _last.TryGetValue(camera, out var f) ? f : null;

    /// <summary>The cameras with a frame; only the current job's when the status names its cameras, so a
    /// camera removed by a job change does not keep showing its last picture.</summary>
    public IReadOnlyCollection<string> Cameras(IEnumerable<string> current)
    {
        var job = current.ToHashSet(StringComparer.Ordinal);
        return _last.Keys.Where(k => job.Count == 0 || job.Contains(k)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>For tests: as if the runtime had sent this frame.</summary>
    internal void Put(LiveFrame f) => _last[f.Camera] = f;
}

/// <summary>
/// The browser dashboard (Hybrid 4, UI-UX-SPEC.md 4.17, ARCHITECTURE.md 2.2): view-only pages on their
/// own port, served by the runtime. Live, Trends, Reports and Audit trail; sign-in with a user's
/// password. The web surface has no changing call at all: every route is a GET except sign-in and
/// sign-out, so the rule "the browser only watches" does not rest on hidden buttons.
/// </summary>
public static partial class WebDashboard
{
    public const string Cookie = "vs_web";
    /// <summary>The page signs the user out after this long without input in the browser (WEB-04); its own
    /// polling never counts.</summary>
    public static readonly TimeSpan IdleSignOut = TimeSpan.FromMinutes(15);
    const string Prefix = "VisionSystem.Runtime.Web.";
    static readonly JsonFormatter Proto = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".svg"] = "image/svg+xml",
    };

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex SafeId();

    public static void Map(WebApplication app, int port)
    {
        // Only on the dashboard's own connection: never on the pipe, the socket or the plant-software port
        // (the Host header can be anything a client writes; the local port cannot).
        app.Use(async (ctx, next) =>
        {
            var web = ctx.Connection.LocalPort == port && ctx.Connection.LocalIpAddress is not null;
            if (web)
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
                if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
                // a POST from another site's page is refused (the cookie is SameSite=Strict as well)
                if (HttpMethods.IsPost(ctx.Request.Method) && !SameOrigin(ctx))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            else if (ctx.Request.Path.StartsWithSegments("/web") || ctx.Request.Path == "/")
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            // the dashboard port serves the dashboard and nothing else: never the gRPC runtime contract
            // (ARCHITECTURE.md 2.4: the gRPC transport is the named pipe; plant software has its own port)
            if (web && !(ctx.Request.Path == "/" || ctx.Request.Path.StartsWithSegments("/web")))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });

        app.MapGet("/", () => Asset("index.html"));
        app.MapGet("/web/{file}", (string file) => Asset(file));

        // which sign-in the page shows: a user's password on the plant's network; through remote service (REQ-11) only the
        // service console's sign-in, never a password, so a customer's password never goes through the relay
        app.MapGet("/web/sign-in-options", (HttpContext ctx, RemoteServiceRunner remote) =>
        {
            var via = remote.ViaAgent(ctx);
            return Results.Json(new { password = !via, service = via, console = via ? "_rs/ui/" : null }, Json);
        });

        app.MapPost("/web/sign-in", async (HttpContext ctx, Sessions sessions, RemoteServiceRunner remote) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest();
            if (remote.ViaAgent(ctx))
                return Results.Json(new { ok = false, error = "Through remote service, sign in on the service console.", key = "web.signIn.serviceOnly" }, Json,
                    statusCode: StatusCodes.Status403Forbidden);
            var form = await ctx.Request.ReadFormAsync();
            var r = sessions.SignInBrowser(form["user"].ToString(), form["password"].ToString());
            if (!r.Ok) return Results.Json(new { ok = false, error = r.ErrorText, key = r.ErrorKey }, Json, statusCode: (int)r.ErrorCode);
            SetCookie(ctx, r.Token);
            return Results.Json(new { ok = true, user = r.User.FullName }, Json);
        }).DisableAntiforgery();

        app.MapPost("/web/sign-out", (HttpContext ctx, Sessions sessions, RemoteServiceRunner remote) =>
        {
            // through remote service the engineer's session is the agent's: signing out here ends it (and its badge)
            if (remote.ViaAgent(ctx)) remote.SignOut(ctx);
            else if (ctx.Request.Cookies[Cookie] is { Length: > 0 } t) sessions.SignOut(t);
            ctx.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
            return Results.NoContent();
        }).DisableAntiforgery();

        // Every data call is checked here, not only hidden in the page (requirements 6.4 WEB-01..03, PT11-12):
        // the user as the users file has them now, this line in their lines, and the route's page in their
        // role's pages. A route that names no page is refused, so a new route is closed until it says one.
        var api = app.MapGroup("/web/api").AddEndpointFilter(async (ic, next) =>
        {
            var ctx = ic.HttpContext;
            var sessions = ctx.RequestServices.GetRequiredService<Sessions>();
            var remote = ctx.RequestServices.GetRequiredService<RemoteServiceRunner>();
            var token = ctx.Request.Cookies[Cookie];
            Caller caller;
            if (remote.ViaAgent(ctx))
            {
                // through remote service: the agent's session only (signed in with today's code on the service console), never a
                // plant user's cookie; read-only like every browser session
                if (remote.Session(ctx) is not { } rs)
                    return Results.Json(new { error = "Please sign in on the service console.", key = "web.signIn.console" }, Json, statusCode: StatusCodes.Status401Unauthorized);
                caller = RemoteCaller(rs);
            }
            else if (sessions.Browser(token) is not { } local)
                return Results.Json(new { error = "Please sign in." }, Json, statusCode: StatusCodes.Status401Unauthorized);
            else
            {
                caller = local;
                // a user taken out of the users file is signed out at once, and changed roles or lines count from this call
                if (sessions.Store.Find(caller.User!.Name) is not { } user)
                {
                    sessions.SignOut(token!);
                    return Results.Json(new { error = "Please sign in." }, Json, statusCode: StatusCodes.Status401Unauthorized);
                }
                caller = caller with { User = user };
            }
            ctx.Items["caller"] = caller;
            ctx.Response.Headers.CacheControl = "no-store";
            var need = ctx.GetEndpoint()?.Metadata.GetMetadata<WebAccess.Needs>();
            if (need is null) return Forbidden("You may not see this.", "web.notAllowed");
            if (need.Page is { } page)
            {
                var scope = WebAccess.Scope.Of(ctx.RequestServices);
                if (!WebAccess.MaySeeLine(caller, scope.Lines))
                    return Forbidden("You may not watch this line.", "web.lineNotAllowed");
                if (!WebAccess.MaySee(caller, page, scope.OperatorShiftReports))
                    return Forbidden("Your role may not see this page.", "web.pageNotAllowed");
                if (need.AllReports && !WebAccess.SeesAllReports(caller))
                    return Forbidden("Your role may not see this tab.", "web.tabNotAllowed");
            }
            return await next(ic);
        });

        api.MapGet("/me", (HttpContext ctx) =>
        {
            var c = (Caller)ctx.Items["caller"]!;
            var scope = WebAccess.Scope.Of(ctx.RequestServices);
            var lineOk = WebAccess.MaySeeLine(c, scope.Lines);
            var pages = lineOk ? Enum.GetValues<WebAccess.Page>().Where(p => WebAccess.MaySee(c, p, scope.OperatorShiftReports)) : [];
            return Results.Json(new
            {
                name = c.User!.Name, fullName = c.User.FullName,
                roles = c.Service ? [c.User.Name.StartsWith("oem:", StringComparison.Ordinal) ? "OEM" : "Service"] : c.Roles.Select(r => r.ToString()),
                line = scope.Lines.FirstOrDefault() ?? "", idleSignOutS = (int)IdleSignOut.TotalSeconds, lineAllowed = lineOk, pages = pages.Select(p => p.ToString().ToLowerInvariant()),
                // Find a part and Operator flags (WEB-07); an Operator allowed the shift reports sees only Shift and Files
                allReports = lineOk && WebAccess.SeesAllReports(c),
            }, Json);
        }).WithMetadata(new WebAccess.Needs(null));
        api.MapGet("/status", (LineRuntime line) => ProtoJson(line.Status())).WithMetadata(WebAccess.Live);
        api.MapGet("/counters", (LineRuntime line) => ProtoJson(line.Counters())).WithMetadata(WebAccess.Live);
        api.MapGet("/recent", (LineRuntime line) =>
            Results.Text("[" + string.Join(",", line.RecentResults().Select(r => Proto.Format(Slim(r)))) + "]", "application/json"))
            .WithMetadata(WebAccess.Live);
        api.MapGet("/cameras", (WebFrames frames, LineRuntime line) =>
            Results.Json(frames.Cameras(line.Status().Cameras.Select(c => c.Name)), Json)).WithMetadata(WebAccess.Live);
        // the frame and what it shows in one answer, so the caption always belongs to the picture
        api.MapGet("/frame/{camera}", (string camera, HttpContext ctx, WebFrames frames) =>
        {
            if (frames.Last(camera) is not { } f) return Results.NotFound();
            ctx.Response.Headers["X-Inspection-Id"] = f.InspectionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Pass"] = f.Pass ? "1" : "0";
            return Results.File(f.Jpeg.ToByteArray(), "image/jpeg");
        }).WithMetadata(WebAccess.Live);
        api.MapGet("/image/{id}", (string id, HttpContext ctx, LineRuntime line) =>
        {
            if (!SafeId().IsMatch(id)) return Results.NotFound();
            if (line.Image(id, GetImageRequest.Types.Encoding.Jpeg, withGraphics: true) is not { } img) return Results.NotFound();
            // an image id never names another picture: the browser keeps it instead of asking again
            ctx.Response.Headers.CacheControl = "private, max-age=600";
            return Results.File(img.Data, img.Format == "png" ? "image/png" : "image/jpeg");
        }).WithMetadata(WebAccess.Live);
        api.MapGet("/live", (HttpContext ctx, LineRuntime line, Sessions sessions, IHostApplicationLifetime life) => Live(ctx, line, sessions, life))
            .WithMetadata(WebAccess.Live);
        api.MapGet("/trend", (string? range, ReportService reports) => Trend(range, reports, DateTime.UtcNow)).WithMetadata(WebAccess.Trends);
        api.MapGet("/quality", (string? range, LineRuntime line) => Quality(range, line.Grades, DateTime.UtcNow)).WithMetadata(WebAccess.Trends);
        api.MapGet("/reports", (HttpContext ctx, ReportService reports) =>
        {
            var c = (Caller)ctx.Items["caller"]!;
            return Results.Json(reports.Files().Where(f => WebAccess.MaySeeReport(c, f.Name, reports.Settings)).Take(500)
                .Select(f => new { name = f.Name, size = f.Length, timeUtc = f.LastWriteTimeUtc }), Json);
        }).WithMetadata(WebAccess.Reports);
        api.MapGet("/reports/{name}", (string name, HttpContext ctx, ReportService reports) =>
        {
            var c = (Caller)ctx.Items["caller"]!;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            // a report the role may not see is "not found", as one that is not there: its name is not told
            if (ext is not (".pdf" or ".xlsx") || !WebAccess.MaySeeReport(c, name, reports.Settings)
                || reports.FilePath(name) is not { } path) return Results.NotFound();
            var type = ext == ".pdf" ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            return Results.File(path, type, fileDownloadName: name);
        }).WithMetadata(WebAccess.Reports);
        // the Reports tabs of UI-UX-SPEC.md 4.11, read only (WEB-07)
        api.MapGet("/shifts", (ReportService reports) => Shifts(reports, DateTime.Now)).WithMetadata(WebAccess.Reports);
        api.MapGet("/shift-report", (int? shift, string? format, HttpContext ctx, ReportService reports) =>
            ShiftReport(shift ?? 0, format, (Caller)ctx.Items["caller"]!, reports, DateTime.Now, ctx.RequestAborted)).WithMetadata(WebAccess.Reports);
        api.MapGet("/find", (string? q, int? days, DateTime? at, ReportService reports) => Find(q, days, at, reports, DateTime.UtcNow))
            .WithMetadata(WebAccess.ReportsAll);
        api.MapGet("/flags", (ReviewStore reviews) => Results.Json(reviews.Recent(200).Select(d => new
        {
            atUtc = d.At.UtcDateTime, job = d.Job, camera = d.Camera, cameraName = d.CameraName, key = d.Key,
            decision = d.Kind, user = d.User, note = d.Note, faults = d.Faults,
        }), Json)).WithMetadata(WebAccess.ReportsAll);
        api.MapGet("/audit", (int? days, ReportService reports) =>
        {
            var to = DateTime.UtcNow;
            var from = to.AddDays(-Math.Clamp(days ?? 1, 1, 31));
            var events = reports.Log.Events(from, to).TakeLast(2000)
                .Select(e => new { timeUtc = e.TimeUtc, kind = e.Kind, active = e.Active, detail = e.Detail });
            var batches = reports.Log.Batches(from, to, 200)
                .Select(b => new { name = b.Name, job = b.Job, startedUtc = b.StartedUtc, endedUtc = b.EndedUtc, user = b.User });
            return Results.Json(new { events, batches }, Json);
        }).WithMetadata(WebAccess.Audit);
    }

    /// <summary>A remote engineer as a browser caller: no user of the users file, read-only, every page of this line.</summary>
    internal static Caller RemoteCaller(Sundaram.RemoteService.RemoteSession s) =>
        new(ClientKind.Browser, new RuntimeUser
        {
            Name = (s.Role == Sundaram.RemoteService.RemoteRole.Oem ? "oem:" : "service:") + s.User,
            FullName = s.User + " (Sundaram " + (s.Role == Sundaram.RemoteService.RemoteRole.Oem ? "OEM" : "service") + ")",
        }, false, false, null, Service: true);

    static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true,
        });

    /// <summary>Into the line's own audit trail (the Audit trail page and the reports), next to the relay's.</summary>
    static void Audit(Recorder recorder, string detail)
    {
        try { recorder.Note("remote_support", detail); } catch (Exception) { }
    }

    static IResult Forbidden(string text, string key) =>
        Results.Json(new { error = text, key }, Json, statusCode: StatusCodes.Status403Forbidden);

    static bool SameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;   // not a browser's cross-site form (browsers always send Origin on POST)
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    static IResult ProtoJson(IMessage m) => Results.Text(Proto.Format(m), "application/json");

    // The NG strip needs the verdict and the images, not every code's corners.
    static InspectionResult Slim(InspectionResult r)
    {
        var s = new InspectionResult { InspectionId = r.InspectionId, Time = r.Time, Job = r.Job, PartId = r.PartId, Pass = r.Pass, CycleMs = r.CycleMs, Variant = r.Variant };
        s.ImageIds.AddRange(r.ImageIds);
        s.Tools.AddRange(r.Tools.Where(t => !t.Pass).Select(t => new VisionStudio.Remote.V1.ToolResult { Name = t.Name, Kind = t.Kind, Pass = false, ReasonText = t.ReasonText }));
        return s;
    }

    static IResult Asset(string file)
    {
        if (!SafeId().IsMatch(file) || !Types.TryGetValue(Path.GetExtension(file), out var type)) return Results.NotFound();
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + file);
        return s is null ? Results.NotFound() : Results.Stream(s, type);
    }

    const int MaxStreamsPerSession = 4;
    static readonly ConcurrentDictionary<string, int> Streams = new(StringComparer.Ordinal);

    /// <summary>Pushes the status, the counters and each part's result as server-sent events, so the
    /// page updates without a reload. Each browser gets its own watchers; a slow one drops old results
    /// (the watchers keep the newest), it never slows the line. The stream ends when the browser goes,
    /// the session is signed out or expires (checked every 15 s), or the service stops; at most four
    /// streams per session.</summary>
    static async Task Live(HttpContext ctx, LineRuntime line, Sessions sessions, IHostApplicationLifetime life)
    {
        var caller = (Caller)ctx.Items["caller"]!;
        var remote = ctx.RequestServices.GetRequiredService<RemoteServiceRunner>();
        // a remote engineer has no dashboard cookie: their streams are counted, and end, with the agent's session
        var token = caller.Service ? "rs:" + ctx.Request.Headers[Sundaram.RemoteService.RemoteAgent.SessionHeader] : ctx.Request.Cookies[Cookie]!;
        bool Alive() => caller.Service ? remote.Session(ctx) is not null : sessions.BrowserAlive(token) && StillLive(ctx, sessions);
        if (Streams.AddOrUpdate(token, 1, (_, n) => n + 1) > MaxStreamsPerSession)
        {
            Release(token);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, life.ApplicationStopping);
        var ct = cts.Token;
        try
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var gate = new SemaphoreSlim(1, 1);
            async Task Write(string text)
            {
                await gate.WaitAsync(ct);
                try
                {
                    await ctx.Response.WriteAsync(text, Encoding.UTF8, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                finally { gate.Release(); }
            }
            var status = line.WatchStatus(ct);
            var results = line.WatchResults(ct);
            var tasks = new[]
            {
                Task.Run(async () =>
                {
                    await foreach (var s in status.ReadAllAsync(ct))
                        await Write($"event: status\ndata: {Proto.Format(s)}\n\nevent: counters\ndata: {Proto.Format(line.Counters())}\n\n");
                }, ct),
                Task.Run(async () =>
                {
                    await foreach (var r in results.ReadAllAsync(ct)) await Write($"event: result\ndata: {Proto.Format(Slim(r))}\n\n");
                }, ct),
                Task.Run(async () =>
                {
                    // a comment line every 15 s keeps proxies from closing a quiet stream; a signed-out session ends it
                    while (!ct.IsCancellationRequested && Alive())
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), ct);
                        await Write(": beat\n\n");
                    }
                }, ct),
            };
            await Task.WhenAny(tasks);
            // whichever ended first (browser gone, a write failed, signed out), the others stop before the
            // request ends: nothing writes to a response Kestrel has handed to the next request
            cts.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (Exception) { /* cancelled, or the browser went: the stream is over either way */ }
        }
        finally { Release(token); }
    }

    /// <summary>The stream's user may still watch Live on this line (WEB-03): a role or line taken away, or the
    /// user taken out of the users file, ends the stream at the next beat.</summary>
    static bool StillLive(HttpContext ctx, Sessions sessions)
    {
        var caller = (Caller)ctx.Items["caller"]!;
        var name = caller.User!.Name;
        var scope = WebAccess.Scope.Of(ctx.RequestServices);
        return sessions.Store.Find(name) is { } u && WebAccess.MaySeeLine(u, scope.Lines)
            && WebAccess.MaySee(u.Roles, WebAccess.Page.Live, scope.OperatorShiftReports);
    }

    static void Release(string token)
    {
        if (Streams.AddOrUpdate(token, 0, (_, n) => n - 1) <= 0) Streams.TryRemove(new KeyValuePair<string, int>(token, 0));
    }

    /// <summary>The Trends page: parts per hour over the last shift (8 h), day or week, and the
    /// top NG reasons. Hours are UTC; the page shows them in the browser's time.</summary>
    static readonly ConcurrentDictionary<(string Log, int Hours), (DateTime At, IResult Result)> TrendCache = new();

    /// <summary>A week is a scan of up to millions of rows on the line PC: each range is worked out at
    /// most once a minute, however many browsers ask.</summary>
    internal static IResult Trend(string? range, ReportService reports, DateTime nowUtc)
    {
        var hours = (range ?? "shift").ToLowerInvariant() switch { "day" => 24, "week" => 168, _ => 8 };
        var key = (reports.Log.Path_, hours);
        if (TrendCache.TryGetValue(key, out var c) && nowUtc - c.At < TimeSpan.FromMinutes(1)) return c.Result;
        var r = TrendOf(hours, reports, nowUtc);
        TrendCache[key] = (nowUtc, r);
        return r;
    }

    /// <summary>The print-quality trend and drift status of each graded Read code tool (WEB-05): small, kept
    /// across a restart (WEB-06), worked out per call.</summary>
    internal static IResult Quality(string? range, GradeTrail grades, DateTime nowUtc)
    {
        var hours = (range ?? "shift").ToLowerInvariant() switch { "day" => 24, "week" => 168, _ => 8 };
        var from = nowUtc.AddHours(-hours);
        var tools = grades.Tools().Select(t => (Tool: t, R: grades.Of(t, from, nowUtc))).Where(x => x.R is not null)
            .Select(x =>
            {
                var (buckets, d) = x.R!.Value;
                return new
                {
                    tool = x.Tool, state = d.State, samples = d.Samples, ungraded = d.Ungraded,
                    lowest = d.Lowest, lowestLetter = GradeTrail.Letter(d.Lowest), lowestFrom = d.LowestFrom, warnAt = d.WarnAt,
                    mean = d.Mean, slope = d.Slope, projected = d.Projected,
                    buckets = buckets.Select(b => new { hourUtc = b.HourUtc, samples = b.Samples, mean = Math.Round(b.Mean, 3), worst = b.Worst, ungraded = b.Ungraded }),
                };
            });
        return Results.Json(new { fromUtc = from, toUtc = nowUtc, tools }, Json);
    }

    static IResult TrendOf(int hours, ReportService reports, DateTime nowUtc)
    {
        var to = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        var from = to.AddHours(-hours);
        var (rows, fromParts) = reports.Log.HourTrend(from, to);
        var reasons = reports.Log.NgReasons(from, to);
        return Results.Json(new
        {
            fromUtc = from, toUtc = to, fromParts,
            hours = rows.Select(h => new { hourUtc = h.HourUtc, count = h.Count, ok = h.Ok, avgMs = Math.Round(h.AvgMs, 2), maxMs = Math.Round(h.MaxMs, 2) }),
            reasons = reasons.Select(r => new { reason = r.Reason, count = r.Count }),
        }, Json);
    }

    /// <summary>The current shift and the three before it, newest first, by the report settings' shifts.</summary>
    internal static List<ShiftSpan> LastShifts(ReportService reports, DateTime nowLocal)
    {
        var list = new List<ShiftSpan> { reports.ShiftAt(nowLocal) };
        while (list.Count < 4) list.Add(reports.ShiftAt(list[^1].FromLocal.AddMinutes(-1)));
        return list;
    }

    static readonly ConcurrentDictionary<(string Log, DateTime Shift), (DateTime At, IResult Result)> ShiftCache = new();

    /// <summary>The Shift tab (WEB-07): good, bad, total and good % of the current and last 3 shifts, counted as the
    /// shift report counts them, with the top 5 reasons for bad parts. Worked out at most once a minute.</summary>
    internal static IResult Shifts(ReportService reports, DateTime nowLocal)
    {
        var spans = LastShifts(reports, nowLocal);
        var key = (reports.Log.Path_, spans[0].FromLocal);
        if (ShiftCache.TryGetValue(key, out var c) && nowLocal.ToUniversalTime() - c.At < TimeSpan.FromMinutes(1)) return c.Result;
        var rows = spans.Select((s, i) =>
        {
            var (from, to) = (s.FromLocal.ToUniversalTime(), s.ToLocal.ToUniversalTime());
            var (hours, fromParts) = reports.Log.HourTrend(from, to);
            int total = hours.Sum(h => h.Count), good = hours.Sum(h => h.Ok);
            return new
            {
                index = i, name = s.Name, current = i == 0, fromUtc = from, toUtc = to, fromParts,
                total, good, bad = total - good, goodPct = total == 0 ? (double?)null : Math.Round(100.0 * good / total, 1),
                reasons = reports.Log.NgReasons(from, to, 5).Select(r => new { reason = r.Reason, count = r.Count }),
            };
        }).ToList();
        IResult result = Results.Json(new { line = reports.Settings.LineName, shifts = rows }, Json);
        foreach (var old in ShiftCache.Keys.Where(k => k.Log == key.Path_ && k.Shift != key.FromLocal)) ShiftCache.TryRemove(old, out _);
        ShiftCache[key] = (nowLocal.ToUniversalTime(), result);
        return result;
    }

    /// <summary>One report is made at a time on the line PC; a second request is told "busy", never queued.</summary>
    static readonly SemaphoreSlim ReportGate = new(1, 1);

    /// <summary>"Save as PDF" / "Save as Excel" of a shift (WEB-07): the file the line PC wrote at the shift's end
    /// when it has, else the same report made now. Named as the shift reporter names it, so an Operator allowed the
    /// shift reports may have it and nothing else.</summary>
    internal static async Task<IResult> ShiftReport(int index, string? format, Caller caller, ReportService reports, DateTime nowLocal, CancellationToken ct)
    {
        var ext = format?.ToLowerInvariant() switch { "pdf" => ".pdf", "xlsx" or "excel" => ".xlsx", _ => null };
        if (ext is null || index is < 0 or > 3) return Results.BadRequest();
        var span = LastShifts(reports, nowLocal)[index];
        var name = $"{span.FromLocal:yyyy-MM-dd HHmm} {span.Name}{ext}";
        if (!WebAccess.MaySeeReport(caller, name, reports.Settings)) return Results.NotFound();
        var type = ext == ".pdf" ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        if (index > 0 && reports.FilePath(name) is { } written) return Results.File(written, type, fileDownloadName: name);
        if (!await ReportGate.WaitAsync(0, ct))
            return Results.Json(new { error = "A report is being made. Try again in a moment.", key = "web.reports.busy" }, Json, statusCode: StatusCodes.Status429TooManyRequests);
        try
        {
            var q = new VisionReports.ReportQuery(span.FromLocal.ToUniversalTime(), span.ToLocal.ToUniversalTime(), null, span.Title);
            var bytes = await Task.Run(() => ext == ".pdf" ? reports.Pdf(q) : reports.Excel(q), ct);
            return Results.File(bytes, type, fileDownloadName: name);
        }
        finally { ReportGate.Release(); }
    }

    /// <summary>"Find a part" (WEB-07): by code or text (read text, PLC part id, image name) in the last day, 7 or
    /// 31 days, or by time (within a minute). At most 50 inspections, newest first.</summary>
    internal static IResult Find(string? q, int? days, DateTime? at, ReportService reports, DateTime nowUtc)
    {
        List<VisionReports.InspectionRow> rows;
        if (at is { } t)
        {
            var u = t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
            rows = reports.Log.Inspections(u.AddMinutes(-1), u.AddMinutes(1), limit: 50);
            rows.Reverse();
        }
        else
        {
            var text = (q ?? "").Trim();
            if (text.Length is < 3 or > 128)
                return Results.Json(new { error = "Type at least 3 characters of the code.", key = "web.find.short" }, Json, statusCode: StatusCodes.Status400BadRequest);
            var d = (days ?? 7) switch { <= 1 => 1, <= 7 => 7, _ => 31 };
            rows = reports.Log.FindInspections(text, nowUtc.AddDays(-d), nowUtc.AddMinutes(1), 50);
        }
        return Results.Json(rows.Select(r => new
        {
            timeUtc = r.TimeUtc, batch = r.Batch, job = r.Job, camera = r.Camera, partId = r.PartText ?? r.PartId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ok = r.Ok, failTool = r.FailTool, reason = r.Reason, text = r.Text2 ?? r.Text1, text1 = r.Text1, text2 = r.Text2,
            ms = Math.Round(r.Ms, 1), imageId = r.ImageId,
        }), Json);
    }
}

/// <summary>
/// What a browser dashboard user may see (requirements 6.4 WEB-01..03; UI-UX-SPEC.md 4.17: "the role
/// only limits which lines and reports they see"). The pages by role are the default rights of
/// PART11-DESIGN.md section 4: Live and Trends for every role (run.view, result.view), Reports for
/// Supervisor and above (Operator: the shift reports when the plant allows, UI-UX-SPEC.md 4.11), the
/// audit trail for QA and Admin (audit.view). A user without a role sees nothing.
/// </summary>
public static class WebAccess
{
    public enum Page { Live, Trends, Reports, Audit }

    /// <summary>Endpoint metadata: the page a web route belongs to; null = any signed-in user (/me). AllReports: a
    /// Reports tab only for the roles that see every report (Find a part, Operator flags; WEB-07).</summary>
    public sealed record Needs(Page? Page, bool AllReports = false);

    public static readonly Needs Live = new(Page.Live), Trends = new(Page.Trends), Reports = new(Page.Reports), Audit = new(Page.Audit),
        ReportsAll = new(Page.Reports, AllReports: true);

    /// <summary>This line's names (its plant id, then its report name) and the plant's Operator setting.</summary>
    public sealed record Scope(IReadOnlyList<string> Lines, bool OperatorShiftReports)
    {
        public static Scope Of(IServiceProvider sp)
        {
            var plantLine = sp.GetRequiredService<IConfiguration>()["Plant:Line"];
            var reportLine = sp.GetRequiredService<ReportService>().Settings.LineName;
            var lines = new[] { plantLine, reportLine }.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).ToList();
            return new Scope(lines, sp.GetRequiredService<IOptions<RuntimeOptions>>().Value.WebOperatorShiftReports);
        }
    }

    /// <summary>A user with no line list watches every line; one with a list only a line it names.</summary>
    public static bool MaySeeLine(RuntimeUser user, IReadOnlyList<string> line) =>
        user.Lines is not { Count: > 0 } mine
        || mine.Any(m => line.Any(l => string.Equals(m.Trim(), l, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A remote service engineer (REQ-11, PROT-32) watches every page of this line, read-only like everyone.</summary>
    public static bool MaySee(Caller c, Page page, bool operatorShiftReports) => c.Service || MaySee(c.Roles, page, operatorShiftReports);
    public static bool MaySeeLine(Caller c, IReadOnlyList<string> line) => c.Service || MaySeeLine(c.User!, line);
    public static bool MaySeeReport(Caller c, string file, ReportSettings settings) => c.Service || MaySeeReport(c.Roles, file, settings);

    public static bool MaySee(IReadOnlyCollection<Role> roles, Page page, bool operatorShiftReports) => page switch
    {
        Page.Live or Page.Trends => roles.Any(r => r is Role.Operator or Role.Supervisor or Role.Qa or Role.Engineer or Role.Admin),
        Page.Reports => SeesAllReports(roles) || (operatorShiftReports && roles.Contains(Role.Operator)),
        Page.Audit => roles.Any(r => r is Role.Qa or Role.Admin),
        _ => false,
    };

    public static bool SeesAllReports(Caller c) => c.Service || SeesAllReports(c.Roles);

    static bool SeesAllReports(IReadOnlyCollection<Role> roles) =>
        roles.Any(r => r is Role.Supervisor or Role.Qa or Role.Engineer or Role.Admin);

    /// <summary>Supervisor and above see every report file; an Operator (when allowed at all) only the shift
    /// reports, named as ShiftReporter writes them: "yyyy-MM-dd HHmm &lt;shift&gt;.pdf|.xlsx".</summary>
    public static bool MaySeeReport(IReadOnlyCollection<Role> roles, string file, ReportSettings settings)
    {
        if (SeesAllReports(roles)) return true;
        if (!roles.Contains(Role.Operator)) return false;
        var name = Path.GetFileNameWithoutExtension(file);
        if (name.Length < 17 || !DateTime.TryParseExact(name[..15], "yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _) || name[15] != ' ') return false;
        var shift = name[16..];
        var shifts = settings.Shifts.Count > 0 ? settings.Shifts.Select(s => s.Name) : ["Day"];
        return shifts.Any(s => string.Equals(s, shift, StringComparison.Ordinal));
    }
}
