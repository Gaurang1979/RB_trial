using Xunit;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sundaram.RemoteService;

/// <summary>The agent end to end over a real link (PROTOCOL.md, DESIGN.md sections 2-4).</summary>
public sealed class AgentTests
{
    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Dials_out_with_its_machine_number_token_and_protocol()
    {
        await using var f = await Fixture.Start();
        await f.Link();
        Assert.Equal("M-1001", f.LinkHeaders["X-Machine-Id"].ToString());
        Assert.Equal("Bearer " + Fixture.Token, f.LinkHeaders.Authorization.ToString());
        Assert.Equal("1", f.LinkHeaders["X-RS-Protocol"].ToString());
    }

    [Fact]
    public async Task Without_a_usable_token_it_stays_off()
    {
        await using var f = await Fixture.Start(token: "short");
        await Task.Delay(300);
        Assert.False(f.Agent.Connected);
    }

    [Fact]
    public async Task Sign_in_needs_todays_code_and_a_reason_and_is_logged_both_places()
    {
        await using var f = await Fixture.Start();
        Assert.Equal(401, (await f.Ask("GET", "/_rs/me")).Status);
        Assert.Equal(400, (await f.Form("/_rs/sign-in", "code=" + FakeHost.Code + "&reason=")).Status);
        Assert.Equal(401, (await f.Form("/_rs/sign-in", "code=000000&reason=x")).Status);
        Assert.Contains("sign_in_refused", f.Host.Kinds());
        f.Host.Held = true;
        Assert.Equal(429, (await f.Form("/_rs/sign-in", "code=" + FakeHost.Code + "&reason=x")).Status);
        f.Host.Held = false;

        var cookie = await f.SignIn();
        Assert.StartsWith(RemoteAgent.SessionCookie + "=", cookie);
        var me = (await f.Ask("GET", "/_rs/me", cookie)).Json;
        Assert.Equal("anita", me.GetProperty("user").GetString());
        Assert.Equal("service", me.GetProperty("role").GetString());
        Assert.False(me.GetProperty("roots")[1].GetProperty("writable").GetBoolean());   // uploads: OEM only
        var start = f.Host.Entries.Single(e => e.Kind == "session_start");
        Assert.Equal("Call 4711", start.Data["reason"]);
        await f.Eventually(() => f.Logs.Any(l => (string?)l["kind"] == "session_start"));
        Assert.True(f.Agent.Sessions.Active);

        // another relay account with this cookie has no session
        Assert.Equal(401, (await f.Ask("GET", "/_rs/me", cookie, user: "mallory")).Status);
    }

    [Fact]
    public async Task While_sign_in_is_held_refusals_are_recorded_at_most_once_a_minute()
    {
        await using var f = await Fixture.Start();
        f.Host.Held = true;
        for (var i = 0; i < 5; i++) Assert.Equal(429, (await f.Form("/_rs/sign-in", "code=000000&reason=x")).Status);
        Assert.Single(f.Host.Kinds(), k => k == "sign_in_refused");
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(429, (await f.Form("/_rs/sign-in", "code=000000&reason=x")).Status);
        Assert.Equal(2, f.Host.Kinds().Count(k => k == "sign_in_refused"));
    }

    [Fact]
    public async Task Nothing_happens_unrecorded_when_the_host_cannot_write_its_log()
    {
        await using var f = await Fixture.Start();
        var cookie = await f.SignIn();
        f.Host.SinkBroken = true;
        Assert.Equal(503, (await f.Ask("GET", "/_rs/file", cookie, query: "?root=logs&path=today.log")).Status);
        Assert.Equal(503, (await f.Form("/_rs/sign-in", "code=" + FakeHost.Code + "&reason=x")).Status);
    }

    [Fact]
    public async Task Files_only_inside_declared_roots_and_downloads_are_logged_with_their_hash()
    {
        await using var f = await Fixture.Start();
        var cookie = await f.SignIn();
        var list = (await f.Ask("GET", "/_rs/files", cookie, query: "?root=logs&path=")).Json;
        Assert.Equal(["2026-11", "today.log"], list.EnumerateArray().Select(e => e.GetProperty("name").GetString()));

        foreach (var bad in new[] { "../outside/secret.txt", "..", "/etc/passwd", "C:/Windows/win.ini", "2026-11/../../outside/secret.txt", "today.log:stream", "CON" })
            Assert.Equal(404, (await f.Ask("GET", "/_rs/file", cookie, query: "?root=logs&path=" + Uri.EscapeDataString(bad))).Status);
        Assert.Equal(404, (await f.Ask("GET", "/_rs/file", cookie, query: "?root=nope&path=today.log")).Status);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(f.Logs1, "link.txt"), Path.Combine(f.Outside, "secret.txt"));
            Assert.Equal(404, (await f.Ask("GET", "/_rs/file", cookie, query: "?root=logs&path=link.txt")).Status);
        }

        var d = await f.Ask("GET", "/_rs/file", cookie, query: "?root=logs&path=today.log");
        Assert.Equal(200, d.Status);
        Assert.Equal("line one\nline two\n", d.Text);
        Assert.Equal(Hex(d.Bytes), d.Header("X-RS-SHA256"));
        var e = f.Host.Entries.Single(x => x.Kind == "file_read");
        Assert.Equal(Hex(d.Bytes), e.Data["sha256"]);
        Assert.Equal("today.log", e.Data["path"]);
    }

    async Task<(int Status, string? Job)> Upload(Fixture f, string cookie, string path, byte[] content, string role = "oem", string? sha = null,
        string user = "anita")
    {
        var start = await f.PostJson("/_rs/uploads", new { root = "jobs", path, size = content.Length, sha256 = sha ?? Hex(content) }, cookie, role, user);
        if (start.Status != 200) return (start.Status, null);
        var id = start.Json.GetProperty("id").GetString()!;
        var chunk = start.Json.GetProperty("chunk").GetInt32();
        for (var off = 0; off < content.Length; off += chunk)
        {
            var part = content.AsSpan(off, Math.Min(chunk, content.Length - off)).ToArray();
            var c = await f.Ask("POST", $"/_rs/uploads/{id}", cookie, part, "application/octet-stream", user, role, "?offset=" + off);
            Assert.Equal(200, c.Status);
        }
        var fin = await f.Ask("POST", $"/_rs/uploads/{id}/finish", cookie, user: user, role: role);
        return fin.Status == 202 ? (202, fin.Json.GetProperty("job").GetString()) : (fin.Status, null);
    }

    [Fact]
    public async Task Uploads_are_oem_only_checked_confirmed_by_the_operator_and_keep_the_previous_file()
    {
        await using var f = await Fixture.Start();
        var service = await f.SignIn("ravi", "service");
        Assert.Equal(403, (await Upload(f, service, "labels.job", "x"u8.ToArray(), role: "service", user: "ravi")).Status);

        var oem = await f.SignIn("anita", "oem");
        Assert.Equal(400, (await Upload(f, oem, "labels.exe", "x"u8.ToArray())).Status);          // type not taken here
        Assert.Equal(404, (await Upload(f, oem, "../outside/x.job", "x"u8.ToArray())).Status);    // outside the root
        Assert.Equal(400, (await Upload(f, oem, "a.job", "abc"u8.ToArray(), sha: new string('0', 64))).Status);   // damaged
        Assert.Equal(400, (await Upload(f, oem, "a.job", "virus"u8.ToArray())).Status);           // the host's own check

        var content = Encoding.UTF8.GetBytes(new string('j', 3_000_000));   // more than one chunk
        var (status, job) = await Upload(f, oem, "labels.job", content);
        Assert.Equal(202, status);
        var j = await f.WaitJob(job!, oem);
        Assert.Equal("done", j.GetProperty("state").GetString());
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(f.Jobs, "labels.job")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(f.Jobs, "labels.job.previous")));
        Assert.Contains(f.Host.Asked, a => a.What.Contains("jobs/labels.job"));
        Assert.Equal(Hex(content), f.Host.Entries.Single(e => e.Kind == "file_written").Data["sha256"]);
    }

    [Fact]
    public async Task A_change_is_refused_while_the_machine_runs_or_when_the_operator_says_no()
    {
        await using var f = await Fixture.Start();
        var oem = await f.SignIn("anita", "oem");
        f.Host.Running = true;
        var (_, job) = await Upload(f, oem, "new.job", "x"u8.ToArray());
        var j = await f.WaitJob(job!, oem);
        Assert.Equal("refused", j.GetProperty("state").GetString());
        Assert.Contains("running", j.GetProperty("message").GetString());
        Assert.False(File.Exists(Path.Combine(f.Jobs, "new.job")));

        f.Host.Running = false;
        f.Host.OperatorSays = false;
        (_, job) = await Upload(f, oem, "new.job", "x"u8.ToArray());
        Assert.Equal("refused", (await f.WaitJob(job!, oem)).GetProperty("state").GetString());
        Assert.False(File.Exists(Path.Combine(f.Jobs, "new.job")));
        Assert.Equal(2, f.Host.Kinds().Count(k => k == "refused"));
    }

    [Fact]
    public async Task Only_declared_actions_run_with_their_rules()
    {
        await using var f = await Fixture.Start();
        var cookie = await f.SignIn();
        var r = await f.PostJson("/_rs/actions/restart", new Dictionary<string, string> { ["service"] = "vision" }, cookie);
        Assert.Equal(202, r.Status);
        var j = await f.WaitJob(r.Json.GetProperty("job").GetString()!, cookie);
        Assert.Equal("restarted vision", j.GetProperty("result").GetString());
        Assert.Contains("action_done", f.Host.Kinds());

        Assert.Equal(404, (await f.PostJson("/_rs/actions/factory", new { }, cookie)).Status);   // OEM only
        Assert.Equal(404, (await f.PostJson("/_rs/actions/format-c", new { }, cookie)).Status);  // not declared

        f.Host.Running = true;
        r = await f.PostJson("/_rs/actions/clean", new { }, cookie);
        Assert.Equal("refused", (await f.WaitJob(r.Json.GetProperty("job").GetString()!, cookie)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Views_are_read_only_with_declared_parameters_and_safe_csv()
    {
        await using var f = await Fixture.Start();
        var cookie = await f.SignIn();
        var v = (await f.Ask("GET", "/_rs/views/inspections", cookie, query: "?batch=LOT-7&drop=table")).Json;
        Assert.Equal("LOT-7", v.GetProperty("rows")[0][2].GetString());
        var e = f.Host.Entries.Single(x => x.Kind == "view");
        Assert.Equal("LOT-7", e.Data["param.batch"]);
        Assert.False(e.Data.ContainsKey("param.drop"));   // only declared parameters reach the host

        var csv = await f.Ask("GET", "/_rs/views/inspections", cookie, query: "?format=csv");
        Assert.Contains("'=CMD()", csv.Text);   // never a formula in a spreadsheet
        Assert.StartsWith("attachment;", csv.Header("Content-Disposition"));
        Assert.Equal(404, (await f.Ask("GET", "/_rs/views/users", cookie)).Status);
    }

    [Fact]
    public async Task Diagnostics_bundle_is_a_zip_of_the_hosts_files_and_is_logged()
    {
        await using var f = await Fixture.Start();
        var cookie = await f.SignIn();
        var d = await f.Ask("GET", "/_rs/diagnostics", cookie);
        Assert.Equal("application/zip", d.Header("Content-Type"));
        using var zip = new ZipArchive(new MemoryStream(d.Bytes));
        Assert.Equal(["remote-service.json", "versions.txt"], zip.Entries.Select(e => e.FullName).Order());
        Assert.Equal(Hex(d.Bytes), f.Host.Entries.Single(e => e.Kind == "diagnostics").Data["sha256"]);
    }

    [Fact]
    public async Task Host_pages_are_forwarded_with_the_key_and_know_the_session()
    {
        await using var f = await Fixture.Start();
        Assert.Equal("host page: nobody", (await f.Ask("GET", "/live", "vs_web=abc")).Text);
        var cookie = await f.SignIn();
        var page = await f.Ask("GET", "/live", cookie + "; vs_web=abc");
        Assert.Equal("host page: anita", page.Text);
        Assert.Equal("42", page.Header("X-Inspection-Id") ?? "42");
        var (_, hostCookie) = f.HostSaw.Last();
        Assert.Equal("vs_web=abc", hostCookie);   // the agent's own cookie never reaches the host
        Assert.Null(f.Agent.Forwarded(new string('A', 64), cookie.Split('=')[1], System.Net.IPAddress.Loopback));
        Assert.Null(f.Agent.Forwarded(f.Agent.Key, cookie.Split('=')[1], System.Net.IPAddress.Parse("10.0.0.5")));
        Assert.Equal(404, (await f.Ask("GET", "//evil.example/x")).Status);
    }

    [Fact]
    public async Task Sessions_end_on_sign_out_idle_and_link_drop()
    {
        await using var f = await Fixture.Start();
        var a = await f.SignIn();
        await f.Ask("POST", "/_rs/sign-out", a);
        Assert.Equal(401, (await f.Ask("GET", "/_rs/me", a)).Status);

        var b = await f.SignIn();
        f.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(401, (await f.Ask("GET", "/_rs/me", b)).Status);

        await f.SignIn();
        await f.DropLink();
        await f.Eventually(() => !f.Agent.Sessions.Active);
        var ends = f.Host.Entries.Where(e => e.Kind == "session_end").Select(e => e.Data["endReason"]).ToArray();
        Assert.Equal(["sign-out", "idle", "link"], ends);
    }

    [Fact]
    public async Task The_badge_reads_memory_only_and_never_ends_a_session()
    {
        await using var f = await Fixture.Start();
        await f.SignIn();
        Assert.True(f.Agent.Sessions.Badge.Active);
        f.Time.Advance(TimeSpan.FromMinutes(16));   // past the idle time
        var before = f.Host.Entries.Count;
        Assert.True(f.Agent.Sessions.Badge.Active);   // still shown: ending it is the sweep's job, which writes the log
        Assert.Equal(before, f.Host.Entries.Count);
        Assert.False(f.Agent.Sessions.Active);        // the sweeping read ends it and writes session_end
        Assert.False(f.Agent.Sessions.Badge.Active);
        Assert.Equal("session_end", f.Host.Entries.Last().Kind);
    }

    [Fact]
    public async Task Malformed_frames_never_stop_the_agent()
    {
        await using var f = await Fixture.Start();
        await f.Raw("{\"t\":5,\"id\":{}}");
        await f.Raw("{\"t\":\"cancel\",\"id\":7}");
        await f.Raw("[1,2,3]");
        await f.Raw("{\"t\":\"req\",\"id\":\"x\",\"headers\":{\"accept\":5},\"body\":\"%%%\"}");
        Assert.Equal(200, (await f.Ask("GET", "/_rs/info")).Status);
    }

    [Fact]
    public async Task The_console_is_served_and_relative()
    {
        await using var f = await Fixture.Start(pages: false);
        var page = await f.Ask("GET", "/_rs/ui/");
        Assert.Contains("href=\"app.css\"", page.Text);
        Assert.Equal(200, (await f.Ask("GET", "/_rs/ui/app.js")).Status);
        Assert.Equal(404, (await f.Ask("GET", "/_rs/ui/..%2F..%2Fsecret")).Status);
        Assert.Equal(404, (await f.Ask("GET", "/live")).Status);   // no host pages
        var root = await f.Ask("GET", "/");
        Assert.Equal((302, "_rs/ui/"), (root.Status, root.Header("Location")));
    }
}
