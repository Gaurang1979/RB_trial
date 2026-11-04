using System.Security.Claims;
using BarcodeReading;
using BarcodeReading.Dashboard.Services;
using Dashboard.Components;
using Dashboard.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StationGuard;

// Command line: create or update a user, then exit.
//   Dashboard add-user <name> "<full name>" <Operator,Engineer,QA>
if (args.Length >= 1 && args[0] == "add-user")
    return AddUser(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // as a Windows service the working folder is System32: keep data next to the program
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "CodeReaderDashboard"); // no effect when run from a console
// Since .NET 10 the Blazor script is a static web asset: serve those from the build output too (dotnet run,
// a build folder); a published app has them in wwwroot already
builder.WebHost.UseStaticWebAssets();
builder.Services.Configure<StationOptions>(builder.Configuration.GetSection("Station"));
builder.Services.PostConfigure<StationOptions>(o => o.ResolvePaths(AppContext.BaseDirectory));
// The licence content key, installed before any product or model is read (requirements 18.3, ENC-HOST-01)
builder.Services.AddSingleton(sp => new HostLicence(
    VisionCore.Licensing.StoredLicenceKeySource.Install(sp.GetRequiredService<IOptions<StationOptions>>().Value.LicenceFolder)));
builder.Services.AddSingleton(sp => { sp.GetRequiredService<HostLicence>(); return new ProductStore(sp.GetRequiredService<IOptions<StationOptions>>().Value.ProductsFolder); });
builder.Services.AddSingleton(sp => new CaseLog(sp.GetRequiredService<IOptions<StationOptions>>().Value.DataFolder));
builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<IOptions<StationOptions>>().Value.DataFolder));
builder.Services.AddSingleton(sp => new DuplicateGuard(sp.GetRequiredService<CaseLog>(),
    sp.GetRequiredService<IOptions<StationOptions>>().Value.DataFolder));
// Keeps the PC fast and stable while running (memory, leaks, disk), and keeps hard-case images with
// the counts of every case, written off the inspection thread.
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StationOptions>>().Value;
    return new ResourceGuard(new ProcessProbe(o.DataFolder), new GuardOptions { BudgetBytes = (long)Math.Max(0, o.MemoryBudgetMb) << 20 });
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StationOptions>>().Value;
    var guard = sp.GetRequiredService<ResourceGuard>();
    var store = new NgImageStore(new NgImageOptions
    {
        // data/hard-cases/<product>/: the folder the retraining agent reads (ml/retrain.py --hard)
        Folder = Path.Combine(o.DataFolder, "hard-cases"),
        DateFolders = false,
        Mode = o.SaveHardCases ? SaveMode.NgOnly : SaveMode.None,
        MaxFiles = Math.Max(1, o.HardCaseLimit),
        MaxBytes = (long)(Math.Max(0.1, o.HardCasesMaxGb) * (1L << 30)),
    }, () => guard.Level);
    guard.Add(store.DropQueuedReliever()).Add(store.TrimDiskReliever()).Add(new CompactHeapReliever());
    return store;
});
builder.Services.AddSingleton<Station>();
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<SundaramTelemetryService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        // API calls get 401 / 403, not a redirect to the login page
        o.Events.OnRedirectToLogin = ctx => ApiOr(ctx, StatusCodes.Status401Unauthorized);
        o.Events.OnRedirectToAccessDenied = ctx => ApiOr(ctx, StatusCodes.Status403Forbidden);
    });
builder.Services.AddAuthorization();
// API JSON: enums as names ("Replay", "QrCode") both ways
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (app.Services.GetRequiredService<HostLicence>().Keys is { } keys)
    app.Logger.LogInformation("licence content key read from {Folder}: {State}", keys.LicenceFolder, keys.InspectionBlockedReason() ?? "available");
// Build the store before the guard starts, so every clean-up step is registered.
app.Services.GetRequiredService<NgImageStore>();
app.Services.GetRequiredService<ResourceGuard>().Start();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();

// API key for the plant's own software: X-Api-Key header -> a service identity (Operator + Engineer)
app.Use(async (ctx, next) =>
{
    var key = ctx.RequestServices.GetRequiredService<IOptions<StationOptions>>().Value.ApiKey;
    if (!string.IsNullOrEmpty(key) && ctx.Request.Headers.TryGetValue("X-Api-Key", out var given) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(given.ToString()), System.Text.Encoding.UTF8.GetBytes(key)))
    {
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "api"), new Claim("full_name", "API client"),
            new Claim(ClaimTypes.Role, Roles.Operator), new Claim(ClaimTypes.Role, Roles.Engineer),
        }, "ApiKey"));
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- sign in / out (plain form posts from the login page)
app.MapPost("/account/login", async (HttpContext ctx, UserStore users, CaseLog log, [FromForm] string user,
                                     [FromForm] string password, [FromForm] string? returnUrl) =>
{
    var u = users.Verify(user, password);
    if (u is null)
    {
        log.Audit(user, "login-failed", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Redirect("/login?failed=1");
    }
    var claims = new List<Claim> { new(ClaimTypes.Name, u.Name), new("full_name", u.FullName) };
    claims.AddRange(u.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
    await ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    log.Audit(u.Name, "login", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
    return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") ? "/" : returnUrl);
});
app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
});

// ---- REST API (signed-in user or X-Api-Key)
var api = app.MapGroup("/api").RequireAuthorization().DisableAntiforgery();
api.MapGet("/station", (Station s) => StationDto(s.Snapshot()));
// PC health for other software on the PC: the guard's status and limits, and the counts by reason
api.MapGet("/health", (Station s) => new { guard = s.Guard.Status, limits = s.Guard.Options, cases = s.HardCaseImages.Counts });
api.MapPost("/station/start", (Station s, ClaimsPrincipal who, StartRequest req) =>
{
    try
    {
        s.Start(req.Product, req.Source, who.Identity!.Name!, req.ReplayFolder);
    }
    catch (InvalidOperationException e)
    {
        // already running, unknown product, no replay folder, camera not found
        return Results.Conflict(new { error = e.Message });
    }
    return Results.Ok(StationDto(s.Snapshot()));
}).RequireAuthorization(p => p.RequireRole(Roles.Operator));
api.MapPost("/station/stop", (Station s, ClaimsPrincipal who) =>
{
    s.Stop(who.Identity!.Name!);
    return Results.Ok(StationDto(s.Snapshot()));
}).RequireAuthorization(p => p.RequireRole(Roles.Operator));
api.MapGet("/station/preview.jpg", (Station s) =>
    s.PreviewJpeg is { } jpg ? Results.File(jpg, "image/jpeg") : Results.NotFound());
api.MapGet("/station/found.jpg", (Station s) =>
    s.FoundJpeg is { } jpg ? Results.File(jpg, "image/jpeg") : Results.NotFound());
api.MapGet("/settings/duplicates", (DuplicateGuard d) => new
{
    d.Settings.ChildCodes, d.Settings.CaseCodes,
    RememberedChildCodes = d.Remembered.Children, RememberedCaseCodes = d.Remembered.Cases,
});
api.MapPut("/settings/duplicates", (DuplicateGuard d, ClaimsPrincipal who, DuplicateSettings s) =>
{
    try
    {
        d.Update(s, who.Identity!.Name!);
    }
    catch (ArgumentException e)
    {
        return Results.BadRequest(new { error = e.Message });
    }
    return Results.Ok(d.Settings);
}).RequireAuthorization(p => p.RequireRole(Roles.QA));
api.MapGet("/products", (ProductStore p) => p.List());
api.MapGet("/products/{code}", (ProductStore p, string code) => p.Get(code) is { } x ? Results.Ok(x) : Results.NotFound());
api.MapGet("/products/{code}/history", (ProductStore p, string code) => p.History(code));
api.MapPut("/products/{code}", (ProductStore p, CaseLog log, ClaimsPrincipal who, string code, ProductMaster product) =>
{
    if (product.ProductCode != code) return Results.BadRequest("product code in the path and body differ");
    if (product.Mode == LearningMode.Locked || p.Get(code)?.Mode == LearningMode.Locked)
        return Results.BadRequest("locked products change only with a QA e-signature in the dashboard");
    product.ApprovedBy = null;
    var saved = p.Save(product, who.Identity!.Name!);
    log.Audit(who.Identity!.Name!, "product-save", $"{saved.ProductCode} v{saved.Version} (API)");
    return Results.Ok(saved);
}).RequireAuthorization(p => p.RequireRole(Roles.Engineer));
api.MapGet("/cases", (CaseLog log, int? limit, string? product, long? afterId) =>
    log.Recent(limit ?? 100, product, afterId ?? 0));
api.MapGet("/cases/{id:long}/codes", (CaseLog log, long id) => log.Codes(id));
api.MapGet("/trends", (CaseLog log, string? product, int? hours) =>
    log.Trend(product, DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 90))));

api.MapGet("/sundaram/clusters", (SundaramTelemetryService t) => t.GetClusters());
api.MapGet("/sundaram/facilities", (SundaramTelemetryService t) => t.GetFacilities());
api.MapGet("/sundaram/releases", (SundaramTelemetryService t) => t.GetReleases());
api.MapGet("/sundaram/incidents", (SundaramTelemetryService t, int? limit) => t.GetRecentIncidents(limit ?? 50));
api.MapPost("/sundaram/telemetry/errors", (SundaramTelemetryService t, VisionCore.Telemetry.ClaudeDiagnosticPackage pkg) =>
{
    t.IngestIncident(pkg);
    return Results.Accepted($"/api/sundaram/incidents/{pkg.IncidentId}", new { pkg.IncidentId, Signature = pkg.CalculateSignature() });
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
return 0;

static Task ApiOr(RedirectContext<CookieAuthenticationOptions> ctx, int status)
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }
    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

static object StationDto(StationSnapshot s) => new
{
    s.Running, s.Product, Source = s.Source.ToString(), s.StartedBy, s.StartedUtc, s.Error, s.Cases, s.OkCases,
    s.ReadRate, s.ModelLoaded, s.EnhancerLoaded, s.PreviewVersion,
    Health = s.Health is null ? null : new
    {
        Level = s.Health.Level.ToString(), s.Health.Warning, s.Health.ImagesEnabled, s.Health.HardCasesSaved, s.Health.HardCasesSkipped,
        s.Health.RestartRecommended,
    },
    Advice = s.Advice.Select(a => new { a.Code, a.Message, a.SuggestedExposureUs }),
    Last = s.Last is null ? null : new
    {
        s.Last.Id, s.Last.TimeUtc, s.Last.Ok, s.Last.ParentCode, s.Last.Source,
        Read = s.Last.Result.Stats.Codes, Expected = s.Last.Result.Stats.Expected, s.Last.Result.Stats.TotalMs,
        s.Last.Result.Stats.LabelBrightness, Unread = s.Last.Result.Misses.Count,
        Codes = s.Last.Result.Codes.Select(c => new
        {
            c.Text, Format = c.Format.ToString(), Step = c.Step.ToString(), c.Gs1?.Gtin, c.Gs1?.Batch, c.Gs1?.Serial,
            Duplicate = s.Last.Duplicates?.Children.Contains(c.Text) == true,
        }),
        DuplicateCodes = s.Last.Duplicates?.Children.Count ?? 0,
        ParentDuplicate = s.Last.Duplicates?.Parent == true,
    },
};

static int AddUser(string[] a)
{
    if (a.Length < 4)
    {
        Console.Error.WriteLine("usage: Dashboard add-user <name> \"<full name>\" <roles: Operator,Engineer,QA>");
        return 2;
    }
    var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
    var opt = new StationOptions();
    config.GetSection("Station").Bind(opt);
    opt.ResolvePaths(AppContext.BaseDirectory);
    string? pw = Environment.GetEnvironmentVariable("DASHBOARD_PASSWORD");
    if (string.IsNullOrEmpty(pw))
    {
        pw = ReadSecret("password: ");
        if (ReadSecret("again:    ") != pw)
        {
            Console.Error.WriteLine("passwords differ");
            return 1;
        }
    }
    new UserStore(opt.DataFolder).Save(a[1], a[2], pw, a[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    Console.WriteLine($"saved user {a[1]} ({a[3]})");
    return 0;
}

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
        else sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

public sealed record StartRequest(string Product, SourceKind Source, string? ReplayFolder);
