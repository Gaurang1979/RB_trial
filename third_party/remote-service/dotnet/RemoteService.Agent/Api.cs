using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sundaram.RemoteService;

/// <summary>The agent's own API under /_rs/ (PROTOCOL.md section 4).</summary>
public sealed partial class RemoteAgent
{
    const int MaxViewRows = 10_000, DefaultViewRows = 1000;
    const int MaxUploadChunk = 1 << 20;
    static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan KeepJobs = TimeSpan.FromHours(1);
    static readonly JsonSerializerOptions JsonIn = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, string> UiTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
    };

    readonly ConcurrentDictionary<string, Job> _jobs = new();
    readonly ConcurrentDictionary<string, Upload> _uploads = new();
    DateTimeOffset _lastHeldEntry = DateTimeOffset.MinValue;

    /// <summary>A piece of work the browser polls (PROTOCOL.md section 4): waiting for the operator, running, then done,
    /// failed or refused.</summary>
    public sealed class Job(string id, string session, DateTimeOffset created)
    {
        public string Id { get; } = id;
        public string Session { get; } = session;
        public DateTimeOffset Created { get; } = created;
        public volatile string State = "running";
        public volatile string Message = "";
        public volatile string Result = "";
    }

    sealed class Upload(string id, RemoteSession session, FileRoot root, string relative, string target, long size, string sha256, string temp)
    {
        public string Id { get; } = id;
        public RemoteSession Session { get; } = session;
        public FileRoot Root { get; } = root;
        public string Relative { get; } = relative;
        public string Target { get; } = target;
        public long Size { get; } = size;
        public string Sha256 { get; } = sha256;
        public string Temp { get; } = temp;
        public long Written;
        public readonly object Lock = new();
        /// <summary>A software package (capability 7): installed with /_rs/packages/&lt;id&gt;/install, never put in a folder.</summary>
        public bool IsPackage { get; init; }
    }

    async Task Api(RemoteRequest req, RemoteResponse res, CancellationToken ct)
    {
        var segs = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(Uri.UnescapeDataString).ToArray();
        var first = segs.FirstOrDefault() ?? "";
        var post = req.Method == "POST";

        // no session needed
        if (first == "ui" && !post) { await Ui(segs.Length > 1 ? segs[1] : "index.html", res); return; }
        if (first == "" && !post)
        {
            await res.Head(302, new Dictionary<string, string[]> { ["Location"] = [req.Path.EndsWith('/') ? "ui/" : "_rs/ui/"] });
            await res.End();
            return;
        }
        if (first == "info" && !post)
        {
            var signed = Sessions.Find(req.Cookie(SessionCookie), req.User) is not null;
            await res.Json(200, new { protocol = ProtocolVersion, product = _host.Product, machine = _options.Machine, signedIn = signed });
            return;
        }
        if (first == "sign-in" && post) { await SignIn(req, res); return; }

        var s = Sessions.Find(req.Cookie(SessionCookie), req.User);
        if (s is null) { await res.Error(401, "Please sign in with today's Service code.", "rs.signIn"); return; }
        Job? job;
        switch (first)
        {
            case "sign-out" when post:
                Sessions.SignOut(req.Cookie(SessionCookie));
                await res.Json(200, new { ok = true }, new Dictionary<string, string[]> { ["Set-Cookie"] = [$"{SessionCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"] });
                return;
            case "me" when !post:
                await Me(s, res);
                return;
            case "files" when !post:
                await Files(s, req, res);
                return;
            case "file" when !post:
                await Download(s, req, res, ct);
                return;
            case "uploads" when post && segs.Length == 1:
                await UploadStart(s, req, res);
                return;
            case "uploads" when post && segs.Length == 2:
                await UploadChunk(s, req, segs[1], res);
                return;
            case "uploads" when post && segs.Length == 3 && segs[2] == "finish":
                await UploadFinish(s, segs[1], res);
                return;
            case "packages":
                await PackagesApi(s, req, segs, post, res);
                return;
            case "corrections":
                await CorrectionsApi(s, req, segs, post, res, ct);
                return;
            case "diagnostics" when !post:
                await Diagnostics(s, res, ct);
                return;
            case "actions" when post && segs.Length == 2:
                await RunAction(s, req, segs[1], res);
                return;
            case "views" when !post && segs.Length == 2:
                await View(s, req, segs[1], res, ct);
                return;
            case "jobs" when !post && segs.Length == 2 && _jobs.TryGetValue(segs[1], out job) && job.Session == s.Id:
                await res.Json(200, new { id = job.Id, state = job.State, message = job.Message, result = job.Result });
                return;
        }
        await res.Error(404, "Not found.", "rs.notFound");
    }

    // ---- console and sign-in ------------------------------------------------------------------------------------------

    static async Task Ui(string file, RemoteResponse res)
    {
        var ext = Path.GetExtension(file);
        var stream = file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && UiTypes.ContainsKey(ext)
            ? Assembly.GetExecutingAssembly().GetManifestResourceStream("Sundaram.RemoteService.Ui." + file) : null;
        if (stream is null) { await res.Error(404, "Not found.", "rs.notFound"); return; }
        using var ms = new MemoryStream();
        await using (stream) await stream.CopyToAsync(ms);
        await res.Head(200, new Dictionary<string, string[]> { ["Content-Type"] = [UiTypes[ext]], ["Cache-Control"] = ["no-cache"] });
        await res.Data(ms.ToArray());
        await res.End();
    }

    async Task SignIn(RemoteRequest req, RemoteResponse res)
    {
        var form = req.FormFields();
        var code = form.GetValueOrDefault("code", "").Trim();
        var reason = form.GetValueOrDefault("reason", "").Trim();
        if (reason.Length is 0 or > 200 || reason.Any(char.IsControl))
        {
            await res.Error(400, "Give a reason (up to 200 characters); it goes into the machine's log.", "rs.reason");
            return;
        }
        var result = _host.Code.Check(code, req.User, req.Role);
        if (result != CodeResult.Accepted)
        {
            // a wrong code is always recorded; while sign-in is held, at most one entry a minute: anyone could otherwise turn
            // tries into a stream of writes to the host's log
            var now = _time.GetUtcNow();
            if (result != CodeResult.Held || now - _lastHeldEntry >= TimeSpan.FromMinutes(1))
            {
                if (result == CodeResult.Held) _lastHeldEntry = now;
                try
                {
                    Log.Write(null, req.User, req.Role, "sign_in_refused", $"Remote service sign-in refused: {req.User}{(result == CodeResult.Held ? " (held)" : "")}");
                }
                catch (Exception) { }
            }
            if (result == CodeResult.Held) await res.Error(429, "Too many wrong codes. Wait 15 minutes, then try again.", "rs.held");
            else await res.Error(401, "Wrong or expired Service code for this machine.", "rs.wrongCode");
            return;
        }
        string token;
        try { token = Sessions.Open(req.User, req.Role, reason); }
        catch (Exception) { await res.Error(503, "The machine cannot record the session, so it cannot start.", "rs.logFailed"); return; }
        await res.Json(200, new { ok = true },
            new Dictionary<string, string[]> { ["Set-Cookie"] = [$"{SessionCookie}={token}; Path=/; HttpOnly; SameSite=Strict"] });
    }

    async Task Me(RemoteSession s, RemoteResponse res)
    {
        var roots = _host.Roots.Select(r => new { name = r.Name, writable = r.Writable && s.MayWrite, extensions = r.Extensions ?? [] });
        var actions = _host.Actions.Where(a => a.AllowedFor(s.Role))
            .Select(a => new { name = a.Name, title = a.Title, description = a.Description, needsStopped = a.NeedsStopped, needsConfirmation = a.NeedsConfirmation });
        var views = _host.Views.Where(v => v.AllowedFor(s.Role)).Select(v => new { name = v.Name, title = v.Title, parameters = v.Parameters });
        await res.Json(200, new
        {
            user = s.User, role = RemoteSessions.Name(s.Role), reason = s.Reason, started = s.Started, product = _host.Product, machine = _options.Machine,
            pages = _options.LocalWeb is not null, diagnostics = _host.Diagnostics is not null, roots, actions, views,
            packages = _host.Packages is not null && s.MayWrite,
            corrections = _host.Supervisor is not null && _host.Corrections.Any(k => k.AllowedFor(s.Role)),
        });
    }

    /// <summary>Writes the entry, or answers that it cannot be recorded (and the caller does nothing).</summary>
    async Task<bool> Record(RemoteSession s, RemoteResponse res, string kind, string detail, Dictionary<string, string> data)
    {
        try { Log.Write(s, s.User, s.Role, kind, detail, data); return true; }
        catch (Exception e)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_log, "Remote service log: {Error}", e.Message);
            await res.Error(503, "The machine cannot record this in its log, so it is not done.", "rs.logFailed");
            return false;
        }
    }

    // ---- files ---------------------------------------------------------------------------------------------------------

    FileRoot? Root(string? name) => _host.Roots.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    async Task Files(RemoteSession s, RemoteRequest req, RemoteResponse res)
    {
        var q = req.QueryFields();
        if (Root(q.GetValueOrDefault("root")) is not { } root || SafePaths.Resolve(root, q.GetValueOrDefault("path")) is not { } dir || !Directory.Exists(dir))
        {
            await res.Error(404, "No such folder.", "rs.notFound");
            return;
        }
        var info = new DirectoryInfo(dir);
        var rows = info.EnumerateFileSystemInfos()
            .Where(f => f.LinkTarget is null && !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && !f.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderBy(f => f is FileInfo).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(5000)
            .Select(f => new { name = f.Name, dir = f is DirectoryInfo, size = f is FileInfo fi ? fi.Length : 0, modifiedUtc = f.LastWriteTimeUtc });
        await res.Json(200, rows);
    }

    async Task Download(RemoteSession s, RemoteRequest req, RemoteResponse res, CancellationToken ct)
    {
        var q = req.QueryFields();
        var rel = q.GetValueOrDefault("path", "");
        if (Root(q.GetValueOrDefault("root")) is not { } root || SafePaths.Resolve(root, rel) is not { } path || !File.Exists(path))
        {
            await res.Error(404, "No such file.", "rs.notFound");
            return;
        }
        var info = new FileInfo(path);
        if (info.Length > root.MaxBytes) { await res.Error(413, "This file is larger than this folder allows.", "rs.tooLarge"); return; }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        if (!await Record(s, res, "file_read", $"Downloaded {root.Name}/{rel}",
                new() { ["root"] = root.Name, ["path"] = rel, ["size"] = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["sha256"] = hash }))
            return;
        file.Position = 0;
        await res.Head(200, new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["application/octet-stream"], ["Content-Disposition"] = [Attachment(info.Name)], ["Cache-Control"] = ["no-store"],
            ["X-RS-SHA256"] = [hash],
        });
        var buf = new byte[RemoteResponse.Chunk];
        int n;
        while ((n = await file.ReadAsync(buf, ct)) > 0) await res.Data(buf.AsMemory(0, n));
        await res.End();
    }

    static string Attachment(string name)
    {
        var ascii = new string(name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    // ---- uploads ------------------------------------------------------------------------------------------------------

    sealed record UploadStartBody(string? Root, string? Path, long Size, string? Sha256);

    async Task UploadStart(RemoteSession s, RemoteRequest req, RemoteResponse res)
    {
        if (!s.MayWrite) { await res.Error(403, "Your role may not upload files.", "rs.role"); return; }
        UploadStartBody? b;
        try { b = JsonSerializer.Deserialize<UploadStartBody>(req.Body, JsonIn); }
        catch (JsonException) { b = null; }
        if (b is null || Root(b.Root) is not { Writable: true } root || SafePaths.Resolve(root, b.Path) is not { } target || (b.Path ?? "").Trim('/').Length == 0
            || Directory.Exists(target) || !Directory.Exists(Path.GetDirectoryName(target)))
        {
            await res.Error(404, "This folder does not take uploads, or the path is not allowed.", "rs.notWritable");
            return;
        }
        if (!SafePaths.ExtensionAllowed(root, target)) { await res.Error(400, "This folder does not take this type of file.", "rs.extension"); return; }
        if (b.Size <= 0 || b.Size > root.MaxBytes) { await res.Error(413, "This file is larger than this folder allows.", "rs.tooLarge"); return; }
        if (b.Sha256 is not { Length: 64 } || !b.Sha256.All(Uri.IsHexDigit)) { await res.Error(400, "The file's SHA-256 is missing.", "rs.hash"); return; }
        if (_uploads.Values.Count(u => u.Session.Id == s.Id) >= 4) { await res.Error(429, "Finish the other uploads first.", "rs.busy"); return; }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var temp = Path.Combine(Path.GetTempPath(), "rs-uploads", id);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        await File.WriteAllBytesAsync(temp, []);
        _uploads[id] = new Upload(id, s, root, (b.Path ?? "").Trim('/'), target, b.Size, b.Sha256.ToLowerInvariant(), temp);
        await res.Json(200, new { id, chunk = MaxUploadChunk });
    }

    async Task UploadChunk(RemoteSession s, RemoteRequest req, string id, RemoteResponse res)
    {
        if (!_uploads.TryGetValue(id, out var u) || u.Session.Id != s.Id) { await res.Error(404, "No such upload.", "rs.notFound"); return; }
        if (!long.TryParse(req.QueryFields().GetValueOrDefault("offset"), out var offset) || req.Body.Length is 0 or > MaxUploadChunk)
        {
            await res.Error(400, "Bad chunk.", "rs.chunk");
            return;
        }
        lock (u.Lock)
        {
            // in order only: a repeated or skipped chunk is refused, so the file is exactly what was sent
            if (offset != u.Written || u.Written + req.Body.Length > u.Size) { offset = -1; }
            else
            {
                using var f = new FileStream(u.Temp, FileMode.Append, FileAccess.Write);
                f.Write(req.Body);
                u.Written += req.Body.Length;
            }
        }
        if (offset < 0) { await res.Error(409, "Chunks must come in order.", "rs.order"); return; }
        await res.Json(200, new { written = u.Written });
    }

    async Task UploadFinish(RemoteSession s, string id, RemoteResponse res)
    {
        if (!_uploads.TryGetValue(id, out var u) || u.Session.Id != s.Id || u.IsPackage) { await res.Error(404, "No such upload.", "rs.notFound"); return; }
        _uploads.TryRemove(id, out _);
        string hash;
        await using (var f = File.OpenRead(u.Temp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
        if (u.Written != u.Size || hash != u.Sha256)
        {
            TryDelete(u.Temp);
            await res.Error(400, "The file arrived damaged (size or SHA-256 differ). Upload it again.", "rs.damaged");
            return;
        }
        if (u.Root.CheckUpload?.Invoke(u.Temp) is { } refused)
        {
            TryDelete(u.Temp);
            if (await Record(s, res, "file_refused", $"Upload refused: {u.Root.Name}/{u.Relative}: {refused}",
                    new() { ["root"] = u.Root.Name, ["path"] = u.Relative, ["sha256"] = hash, ["why"] = refused }))
                await res.Error(400, "The machine refused this file: " + refused, "rs.refused");
            return;
        }
        var job = NewJob(s);
        _ = Task.Run(() => RunJob(job, s, u.Root.NeedsStopped, u.Root.ConfirmWrites,
            new ConfirmRequest("Remote service: put a file in place", $"{u.Root.Name}/{u.Relative} ({u.Size:N0} bytes)", s.User),
            async ct =>
            {
                // recorded first: nothing changes on the machine unrecorded (a failure after this is recorded as "failed")
                Log.Write(s, s.User, s.Role, "file_written", $"Uploaded {u.Root.Name}/{u.Relative}",
                    new Dictionary<string, string> { ["root"] = u.Root.Name, ["path"] = u.Relative, ["size"] = u.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), ["sha256"] = hash });
                // the file before is kept next to it (".previous"), so a wrong upload is undone by putting it back
                if (File.Exists(u.Target)) File.Move(u.Target, u.Target + ".previous", overwrite: true);
                File.Move(u.Temp, u.Target);
                await Task.CompletedTask;
                return "Saved";
            },
            onEnd: () => TryDelete(u.Temp)));
        await res.Json(202, new { job = job.Id });
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    // ---- diagnostics, actions, views ----------------------------------------------------------------------------------

    async Task Diagnostics(RemoteSession s, RemoteResponse res, CancellationToken ct)
    {
        if (_host.Diagnostics is not { } write) { await res.Error(404, "This machine offers no diagnostics bundle.", "rs.notFound"); return; }
        var temp = Path.Combine(Path.GetTempPath(), "rs-diag-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var about = zip.CreateEntry("remote-service.json");
                await using (var w = about.Open())
                    await JsonSerializer.SerializeAsync(w, new { machine = _options.Machine, product = _host.Product, made = _time.GetUtcNow(), by = s.User }, cancellationToken: ct);
                await write(zip, ct);
            }
            var bytes = await File.ReadAllBytesAsync(temp, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!await Record(s, res, "diagnostics", "Diagnostics bundle downloaded",
                    new() { ["size"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["sha256"] = hash }))
                return;
            var name = $"diagnostics-{_options.Machine}-{_time.GetUtcNow():yyyyMMdd-HHmmss}.zip";
            await res.Head(200, new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/zip"], ["Content-Disposition"] = [Attachment(name)], ["Cache-Control"] = ["no-store"], ["X-RS-SHA256"] = [hash],
            });
            await res.Data(bytes);
            await res.End();
        }
        finally { TryDelete(temp); }
    }

    async Task RunAction(RemoteSession s, RemoteRequest req, string name, RemoteResponse res)
    {
        if (_host.Actions.FirstOrDefault(a => a.Name == name) is not { } action || !action.AllowedFor(s.Role))
        {
            await res.Error(404, "No such action.", "rs.notFound");
            return;
        }
        Dictionary<string, string> args;
        try { args = req.Body.Length == 0 ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(req.Body, JsonIn) ?? []; }
        catch (JsonException) { await res.Error(400, "Arguments must be a JSON object of text values.", "rs.args"); return; }
        if (args.Count > 20 || args.Any(a => a.Key.Length > 64 || a.Value.Length > 1000)) { await res.Error(400, "Too many or too long arguments.", "rs.args"); return; }
        var job = NewJob(s);
        _ = Task.Run(() => RunJob(job, s, action.NeedsStopped, action.NeedsConfirmation,
            new ConfirmRequest("Remote service: run an action", action.Title, s.User),
            async ct =>
            {
                Log.Write(s, s.User, s.Role, "action", $"Action started: {action.Name}",
                    new Dictionary<string, string>(args.Select(a => KeyValuePair.Create("arg." + a.Key, a.Value))) { ["action"] = action.Name });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ActionTimeout);
                var result = await action.Run(args, timeout.Token);
                Log.Write(s, s.User, s.Role, "action_done", $"Action done: {action.Name}: {result}",
                    new Dictionary<string, string> { ["action"] = action.Name, ["result"] = result });
                return result;
            }));
        await res.Json(202, new { job = job.Id });
    }

    async Task View(RemoteSession s, RemoteRequest req, string name, RemoteResponse res, CancellationToken ct)
    {
        if (_host.Views.FirstOrDefault(v => v.Name == name) is not { } view || !view.AllowedFor(s.Role))
        {
            await res.Error(404, "No such view.", "rs.notFound");
            return;
        }
        var q = req.QueryFields();
        var args = view.Parameters.Where(q.ContainsKey).ToDictionary(p => p, p => q[p], StringComparer.Ordinal);
        var limit = int.TryParse(q.GetValueOrDefault("limit"), out var l) ? Math.Clamp(l, 1, MaxViewRows) : DefaultViewRows;
        var table = await view.Query(args, limit, ct);
        var rows = table.Rows.Take(limit).ToList();
        if (!await Record(s, res, "view", $"Viewed {view.Name}",
                new Dictionary<string, string>(args.Select(a => KeyValuePair.Create("param." + a.Key, a.Value)))
                { ["view"] = view.Name, ["rows"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            return;
        var truncated = table.Truncated || table.Rows.Count > limit;
        if (q.GetValueOrDefault("format") == "csv")
        {
            await res.Head(200, new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["text/csv; charset=utf-8"], ["Cache-Control"] = ["no-store"],
                ["Content-Disposition"] = [Attachment($"{view.Name}-{_options.Machine}-{_time.GetUtcNow():yyyyMMdd-HHmmss}.csv")],
            });
            await res.Data(Encoding.UTF8.GetBytes(Csv.Write(table with { Rows = rows })));
            await res.End();
            return;
        }
        await res.Json(200, new { columns = table.Columns, rows, truncated });
    }

    // ---- jobs ---------------------------------------------------------------------------------------------------------

    Job NewJob(RemoteSession s)
    {
        foreach (var (k, j) in _jobs)
            if (_time.GetUtcNow() - j.Created > KeepJobs) _jobs.TryRemove(k, out _);
        var job = new Job(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), s.Id, _time.GetUtcNow());
        _jobs[job.Id] = job;
        return job;
    }

    /// <summary>The rules every change follows (DESIGN.md section 3, line safety): the machine must be safe to change when the
    /// work needs it stopped (no machine state: never safe), and the operator confirms on the machine's screen when asked (no
    /// confirmation: never). Each refusal is recorded.</summary>
    async Task RunJob(Job job, RemoteSession s, bool needsStopped, bool needsConfirmation, ConfirmRequest confirm,
        Func<CancellationToken, Task<string>> work, Action? onEnd = null)
    {
        try
        {
            string why = "";
            if (needsStopped && (_host.Machine is null || !_host.Machine.SafeToChange(out why)))
            {
                Refuse(job, s, confirm, "Not now: " + (_host.Machine is null ? "this machine cannot tell whether it is safe to change" : why));
                return;
            }
            if (needsConfirmation)
            {
                if (_host.Operator is null) { Refuse(job, s, confirm, "This machine cannot ask its operator."); return; }
                job.State = "waiting";
                job.Message = "Waiting for the operator to confirm on the machine";
                using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                bool ok;
                try { ok = await _host.Operator.ConfirmAsync(confirm, wait.Token); }
                catch (OperationCanceledException) { ok = false; }
                if (!ok) { Refuse(job, s, confirm, "The operator did not confirm."); return; }
                Log.Write(s, s.User, s.Role, "confirmed", $"Operator confirmed: {confirm.What}", new Dictionary<string, string> { ["what"] = confirm.What });
                // checked again: the line may have started while the operator was asked
                if (needsStopped && !_host.Machine!.SafeToChange(out why)) { Refuse(job, s, confirm, "Not now: " + why); return; }
            }
            job.State = "running";
            job.Message = "";
            job.Result = await work(CancellationToken.None);
            job.State = "done";
        }
        catch (Exception e)
        {
            job.State = "failed";
            job.Message = e is OperationCanceledException ? "Took too long" : e.Message;
            try { Log.Write(s, s.User, s.Role, "failed", $"Failed: {confirm.What}: {job.Message}", new Dictionary<string, string> { ["what"] = confirm.What }); }
            catch (Exception) { }
        }
        finally { onEnd?.Invoke(); }
    }

    void Refuse(Job job, RemoteSession s, ConfirmRequest confirm, string why)
    {
        job.State = "refused";
        job.Message = why;
        try { Log.Write(s, s.User, s.Role, "refused", $"Refused: {confirm.What}: {why}", new Dictionary<string, string> { ["what"] = confirm.What, ["why"] = why }); }
        catch (Exception) { }
    }
}
