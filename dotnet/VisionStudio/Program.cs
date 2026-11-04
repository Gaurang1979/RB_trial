using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StationGuard;
using VisionCore;
using VisionStudio.Components;
using VisionStudio.Services;

// Command line: create or update a user, then exit.
//   VisionStudio add-user <name> "<full name>" <Operator,Engineer,QA> <password>
if (args.Length >= 1 && args[0] == "add-user")
{
    if (args.Length < 5) { Console.Error.WriteLine("add-user <name> \"<full name>\" <roles> <password>"); return 2; }
    var o = new StudioOptions();
    o.ResolvePaths(AppContext.BaseDirectory);
    new UserStore(o.DataFolder).Save(args[1], args[2], args[4], args[3].Split(",", StringSplitOptions.RemoveEmptyEntries));
    Console.WriteLine($"user {args[1]} saved");
    return 0;
}

// Commissioning secrets (Req 17.10 MRG-09): stored protected (DPAPI) in the data folder, never printed.
//   VisionStudio secrets status
//   VisionStudio secrets set <VS_SERVICE_OTP_SECRET|VS_FACTORY_OTP_SECRET|VS_LEDGER_KEY|VS_SUPPORT_LINK_TOKEN> [--replace]   (value on standard input)
//   VisionStudio secrets new-ledger-key [--replace]
if (args.Length >= 1 && args[0] == "secrets")
{
    var o = new StudioOptions();
    o.ResolvePaths(AppContext.BaseDirectory);
    return SecretsCommand.Run(args.Skip(1).ToArray(), VisionCore.Licensing.CommissioningSecrets.ForThisPc(o.DataFolder), Console.In, Console.Out, Console.Error);
}

// Results ledger (Req 17.10 MRG-08 item 8): check it, anchor a log chained before the head was kept (commissioning),
// or start a new ledger after alarm 801. Each change needs the person's name and a typed confirmation.
//   VisionStudio ledger check | ledger anchor --by <name> | ledger new --by <name> --reason "<why>"   [--data <folder>]
if (args.Length >= 1 && args[0] == "ledger")
{
    var o = new StudioOptions();
    o.ResolvePaths(AppContext.BaseDirectory);
    return LedgerCommand.Run(args.Skip(1).ToArray(), o.DataFolder,
        data => VisionReports.DatabaseSecurity.ParseKey(VisionCore.Licensing.CommissioningSecrets.ForThisPc(data).LedgerKeyBase64),
        Console.In, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,   // as a Windows service the working folder is System32
});
builder.Host.UseWindowsService(o => o.ServiceName = "VisionStudio");
// static files from the build output too (dotnet run); a published app has them in wwwroot already
builder.WebHost.UseStaticWebAssets();
builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection("Studio"));
builder.Services.PostConfigure<StudioOptions>(o => o.ResolvePaths(AppContext.BaseDirectory));
builder.Services.AddSingleton(sp => new JobStore(sp.GetRequiredService<IOptions<StudioOptions>>().Value.JobsFolder, sp.GetRequiredService<ModelPaths>()));
builder.Services.AddSingleton(sp => new ModelPaths(sp.GetRequiredService<IOptions<StudioOptions>>().Value.ModelsFolder));
builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder));
// OTP secrets and the ledger key, from the protected store or the service account's environment (Req 17.10 MRG-09)
builder.Services.AddSingleton<OtpAttempts>();
builder.Services.AddSingleton(sp =>
{
    var secrets = VisionCore.Licensing.CommissioningSecrets.ForThisPc(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder);
    if (secrets.Error is { } e) sp.GetRequiredService<ILoggerFactory>().CreateLogger("Secrets").LogError("{Error}", e);
    return secrets;
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
    return new Texts(Path.Combine(AppContext.BaseDirectory, "i18n"), o.Language);
});
builder.Services.AddSingleton(_ =>
{
    var d = new CameraDrivers();
    d.Add(new CameraDriverC1());
    return d;
});
builder.Services.AddSingleton<ImageCache>();
builder.Services.AddSingleton<LicenceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LicenceService>());
// Anti-tamper gate (docs/vision-system/PACKAGE-PROTECTION.md): the Station reads it before running production.
builder.Services.AddSingleton<PackageIntegrityService>();
// Keeps the PC fast and stable while running (memory, leaks, disk), and keeps NG images with counts.
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
    var guard = new ResourceGuard(new ProcessProbe(o.DataFolder), new GuardOptions { BudgetBytes = (long)o.MemoryBudgetMb << 20 });
    guard.Add(new Reliever("trim-image-cache", Pressure.Elevated, TimeSpan.FromMilliseconds(5), sp.GetRequiredService<ImageCache>().TrimNonLive));
    return guard;
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
    var guard = sp.GetRequiredService<ResourceGuard>();
    var store = new NgImageStore(new NgImageOptions
    {
        Folder = Path.Combine(o.DataFolder, "ng-images"),
        Mode = Enum.TryParse<SaveMode>(o.SaveImages, true, out var m) ? m : SaveMode.NgOnly,
        EveryNth = o.SaveEveryNth,
        MaxFiles = o.ImagesMaxFiles,
        MaxBytes = (long)(o.ImagesMaxGb * (1L << 30)),
    }, () => guard.Level);
    guard.Add(store.DropQueuedReliever()).Add(store.TrimDiskReliever()).Add(new CompactHeapReliever());
    return store;
});
// The plant link (station agent): off unless the "Plant" section names a server. The token lives in
// a file (TokenFile) or VS_PLANT_TOKEN, never in appsettings.json.
builder.Services.AddSingleton(sp =>
{
    var o = builder.Configuration.GetSection("Plant").Get<StationAgent.PlantLinkOptions>() ?? new();
    var studio = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
    if (string.IsNullOrWhiteSpace(builder.Configuration["Plant:QueueFile"])) o.QueueFile = Path.Combine(studio.DataFolder, "plant-queue.db");
    var p = StationAgent.StationPublisher.Start(o, AppContext.BaseDirectory);
    if (p.Error is not null) sp.GetRequiredService<ILoggerFactory>().CreateLogger("Plant").LogWarning("{Error}", p.Error);
    return p;
});
builder.Services.AddSingleton<Station>();
builder.Services.AddSingleton<PlcService>();
builder.Services.AddSingleton(sp => new VisionReports.ResultLog(sp.GetRequiredService<IOptions<StudioOptions>>().Value.DataFolder,
    VisionReports.DatabaseSecurity.ParseKey(sp.GetRequiredService<VisionCore.Licensing.CommissioningSecrets>().LedgerKeyBase64)));
builder.Services.AddSingleton(sp => new LedgerGate(sp.GetRequiredService<VisionReports.ResultLog>()));
builder.Services.AddSingleton<Recorder>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddHostedService<ShiftReporter>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1 << 20);

var app = builder.Build();
// Build the store before the guard starts, so every clean-up step is registered.
app.Services.GetRequiredService<NgImageStore>();
app.Services.GetRequiredService<ResourceGuard>().Start();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseStaticFiles();

// open mode (bench and development PCs): everyone acts as an engineer, no sign-in
app.Use(async (ctx, next) =>
{
    if (ctx.RequestServices.GetRequiredService<IOptions<StudioOptions>>().Value.OpenMode && ctx.User.Identity?.IsAuthenticated != true)
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "bench"), new Claim("full_name", "Bench PC"),
            new Claim(ClaimTypes.Role, Roles.Operator), new Claim(ClaimTypes.Role, Roles.Engineer), new Claim(ClaimTypes.Role, Roles.QA),
        }, "Open"));
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/account/login", async (HttpContext ctx, UserStore users, LicenceService licence, VisionCore.Licensing.CommissioningSecrets secrets,
    OtpAttempts attempts, ILogger<UserStore> log, [FromForm] string user, [FromForm] string password, [FromForm] string? otp) =>
{
    var u = users.Verify(user, password);
    if (u is null) return Results.Redirect("/login?failed=1");
    // Service and OEM roles only with today's OTP for this PC's licence (Req 17.10 MRG-09 item 4); the code is never logged
    var roles = attempts.Decide(u.Name, u.Roles, otp, licence.Settings.LicenceKey, secrets.ServiceOtp, secrets.FactoryOtp);
    if (roles.Locked) log.LogWarning("OTP sign-in locked after {Count} wrong codes: {User}", OtpAttempts.MaxFailures, u.Name);
    foreach (var r in roles.Granted) log.LogWarning("OTP sign-in: {User} as {Role}", u.Name, r);
    if (!string.IsNullOrWhiteSpace(otp) || roles.Roles.Count == 0)
        foreach (var r in roles.Refused) log.LogWarning("OTP sign-in refused: {User} as {Role}", u.Name, r);
    if (roles.Roles.Count == 0) return Results.Redirect(roles.Locked ? "/login?failed=3" : "/login?failed=2");
    var claims = new List<Claim> { new(ClaimTypes.Name, u.Name), new("full_name", u.FullName) };
    claims.AddRange(roles.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
    await ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.LocalRedirect("/");
});
app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
});

// rendered images (live cameras, teach and test views)
app.MapGet("/img/{key}.jpg", (string key, ImageCache images) =>
    images.Get(key) is { } f ? Results.File(f.Jpeg, "image/jpeg") : Results.NotFound());

app.Services.GetRequiredService<PlcService>();   // the PLC link starts with the program, not with the first page
// reports: made on request (PDF / Excel), and the stored automatic ones
app.MapGet("/reports/make", (string format, string from, string to, string? batch, string? title, int? sections, ReportService reports) =>
{
    var f = DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    var t = DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    var q = new VisionReports.ReportQuery(f, t, string.IsNullOrWhiteSpace(batch) ? null : batch, string.IsNullOrWhiteSpace(title) ? "Production report" : title)
    {
        Sections = sections is > 0 ? (VisionReports.ReportSections)sections.Value : VisionReports.ReportSections.All,
    };
    var name = $"{q.Title} {f.ToLocalTime():yyyy-MM-dd HHmm}";
    return format == "xlsx"
        ? Results.File(reports.Excel(q), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx")
        : Results.File(reports.Pdf(q), "application/pdf", name + ".pdf");
}).RequireAuthorization();
app.MapGet("/reports/file/{name}", (string name, ReportService reports) =>
    reports.FilePath(name) is { } p
        ? Results.File(p, name.EndsWith(".pdf") ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name)
        : Results.NotFound()).RequireAuthorization();

app.Services.GetRequiredService<Recorder>();   // start recording results from the first inspection
// grade reports written by the Test page (plain names only, from the reports folder)
app.MapGet("/reports/{name}", (string name, IOptions<StudioOptions> o) =>
{
    if (!VisionCore.Reports.ReportFiles.IsSafe(name)) return Results.NotFound();
    var path = Path.Combine(VisionCore.Reports.ReportFiles.Folder(o.Value.DataFolder), name);
    // shown in the browser (no download name), to engineers and QA only: reports hold code data and images
    return File.Exists(path) ? Results.File(Path.GetFullPath(path), "application/pdf") : Results.NotFound();
}).RequireAuthorization(p => p.RequireRole(Roles.Engineer, Roles.QA));

// Station health and counts for the Run screen and for other software on the PC.
app.MapGet("/api/health", (Station s) => new
{
    guard = s.Guard.Status,
    ng = s.NgImages.Counts,
    cycles = s.Cycles,
    cycles_ok = s.CyclesOk,
    plant = s.Plant,
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
return 0;
