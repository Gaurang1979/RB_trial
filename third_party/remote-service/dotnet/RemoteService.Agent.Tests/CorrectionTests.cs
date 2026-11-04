using Xunit;
using System.Collections.Concurrent;
using Sundaram.RemoteService;

/// <summary>Corrections (DESIGN.md section 3, capability 8): never an edit; a supervisor at the machine approves; the host
/// appends a correcting entry; every step logged with old and new values.</summary>
public sealed class CorrectionTests
{
    sealed class Books : ISupervisorApproval
    {
        public readonly ConcurrentDictionary<string, Dictionary<string, string>> Records = new() { ["B-0457"] = new() { ["lot"] = "LOT-0475", ["line"] = "L1" } };
        public readonly ConcurrentQueue<(Correction C, string Approver)> Appended = new();
        public string? Approver = "Priya (supervisor)";
        public Action? WhileWaiting;
        public readonly ConcurrentQueue<Correction> Asked = new();

        public Task<string?> ApproveAsync(Correction c, CancellationToken ct)
        {
            Asked.Enqueue(c);
            WhileWaiting?.Invoke();
            return Task.FromResult(Approver);
        }

        public CorrectionKind Kind(bool needsStopped = false) => new("batch", "Batch details",
            [new CorrectionField("lot", "Lot number", MaxLength: 12), new CorrectionField("line", "Line", ["L1", "L2"])],
            (id, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(Records.TryGetValue(id, out var r) ? new Dictionary<string, string>(r) : null),
            (c, approver, _) => { Appended.Enqueue((c, approver)); return Task.FromResult("C-" + Appended.Count); },
            Validate: c => c.Changes.TryGetValue("lot", out var l) && !l.New.StartsWith("LOT-", StringComparison.Ordinal) ? "a lot starts with LOT-" : null,
            NeedsStopped: needsStopped);
    }

    static async Task<(Fixture F, Books B)> Start(bool supervisor = true, bool needsStopped = false)
    {
        var books = new Books();
        var f = await Fixture.Start((fx, _) => { fx.Corrections.Add(books.Kind(needsStopped)); if (supervisor) fx.Supervisor = books; });
        return (f, books);
    }

    static object Body(string lot, string reason = "Typed wrong at batch start; label shows LOT-0457", string record = "B-0457") =>
        new { record, changes = new Dictionary<string, string> { ["lot"] = lot }, reason };

    [Fact]
    public async Task Only_oem_may_correct_by_default()
    {
        var (f, _) = await Start();
        await using var _f = f;
        var service = await f.SignIn("ravi", "service");
        Assert.False((await f.Ask("GET", "/_rs/me", service, user: "ravi")).Json.GetProperty("corrections").GetBoolean());
        Assert.Equal(404, (await f.Ask("GET", "/_rs/corrections/batch", service, user: "ravi", query: "?record=B-0457")).Status);
    }

    [Fact]
    public async Task A_correction_is_proposed_approved_by_a_supervisor_and_appended_never_edited()
    {
        var (f, books) = await Start();
        await using var _f = f;
        var oem = await f.SignIn("anita", "oem");
        var rec = (await f.Ask("GET", "/_rs/corrections/batch", oem, role: "oem", query: "?record=B-0457")).Json;
        Assert.Equal("LOT-0475", rec.GetProperty("values").GetProperty("lot").GetString());

        var r = await f.PostJson("/_rs/corrections/batch", Body("LOT-0457"), oem, "oem");
        Assert.Equal(202, r.Status);
        var j = await f.WaitJob(r.Json.GetProperty("job").GetString()!, oem);
        Assert.Equal("done", j.GetProperty("state").GetString());
        Assert.Contains("Priya (supervisor)", j.GetProperty("result").GetString());

        var (c, approver) = Assert.Single(books.Appended);
        Assert.Equal(("LOT-0475", "LOT-0457"), (c.Changes["lot"].Old, c.Changes["lot"].New));
        Assert.Equal(("anita", "Priya (supervisor)"), (c.RequestedBy, approver));
        Assert.Equal("LOT-0475", books.Records["B-0457"]["lot"]);   // the agent never touched the record itself

        var kinds = f.Host.Kinds().Where(k => k.StartsWith("correction_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["correction_proposed", "correction_approved", "correction_applied"], kinds);
        var applied = f.Host.Entries.Single(e => e.Kind == "correction_applied");
        Assert.Equal(("LOT-0475", "LOT-0457", "Priya (supervisor)", "C-1"),
            (applied.Data["old.lot"], applied.Data["new.lot"], applied.Data["approvedBy"], applied.Data["entry"]));
        Assert.Contains("Typed wrong", applied.Data["reason"]);
    }

    [Fact]
    public async Task Bad_proposals_are_refused_before_anyone_is_asked()
    {
        var (f, books) = await Start();
        await using var _f = f;
        var oem = await f.SignIn("anita", "oem");
        Assert.Equal(400, (await f.PostJson("/_rs/corrections/batch", Body("LOT-0457", reason: "short"), oem, "oem")).Status);
        Assert.Equal(400, (await f.PostJson("/_rs/corrections/batch", Body("LOT-0475"), oem, "oem")).Status);              // same value
        Assert.Equal(400, (await f.PostJson("/_rs/corrections/batch", Body("XYZ"), oem, "oem")).Status);                   // host's check
        Assert.Equal(400, (await f.PostJson("/_rs/corrections/batch", Body("LOT-0457-TOO-LONG"), oem, "oem")).Status);    // length
        Assert.Equal(404, (await f.PostJson("/_rs/corrections/batch", Body("LOT-0457", record: "B-9999"), oem, "oem")).Status);
        Assert.Equal(400, (await f.PostJson("/_rs/corrections/batch",
            new { record = "B-0457", changes = new Dictionary<string, string> { ["line"] = "L9" }, reason = "Wrong line was set at start" }, oem, "oem")).Status);   // not a choice
        Assert.Equal(400, (await f.PostJson("/_rs/corrections/batch",
            new { record = "B-0457", changes = new Dictionary<string, string> { ["verdict"] = "OK" }, reason = "Make the NG an OK please" }, oem, "oem")).Status);   // not declared
        Assert.Empty(books.Asked);
        Assert.DoesNotContain("correction_proposed", f.Host.Kinds());
    }

    [Fact]
    public async Task Rejected_or_changed_meanwhile_or_running_means_nothing_is_appended()
    {
        var (f, books) = await Start(needsStopped: true);
        await using var _f = f;
        var oem = await f.SignIn("anita", "oem");

        books.Approver = null;
        var r = await f.PostJson("/_rs/corrections/batch", Body("LOT-0457"), oem, "oem");
        Assert.Equal("refused", (await f.WaitJob(r.Json.GetProperty("job").GetString()!, oem)).GetProperty("state").GetString());
        Assert.Contains("correction_rejected", f.Host.Kinds());

        books.Approver = "Priya (supervisor)";
        books.WhileWaiting = () => books.Records["B-0457"]["lot"] = "LOT-0999";   // someone else changed it
        r = await f.PostJson("/_rs/corrections/batch", Body("LOT-0457"), oem, "oem");
        var j = await f.WaitJob(r.Json.GetProperty("job").GetString()!, oem);
        Assert.Equal("refused", j.GetProperty("state").GetString());
        Assert.Contains("changed", j.GetProperty("message").GetString());

        books.WhileWaiting = null;
        f.Host.Running = true;
        r = await f.PostJson("/_rs/corrections/batch", Body("LOT-0457"), oem, "oem");
        Assert.Equal("refused", (await f.WaitJob(r.Json.GetProperty("job").GetString()!, oem)).GetProperty("state").GetString());
        Assert.Empty(books.Appended);
    }

    [Fact]
    public async Task Without_a_supervisor_there_are_no_corrections()
    {
        var (f, _) = await Start(supervisor: false);
        await using var _f = f;
        var oem = await f.SignIn("anita", "oem");
        Assert.False((await f.Ask("GET", "/_rs/me", oem, role: "oem")).Json.GetProperty("corrections").GetBoolean());
        Assert.Equal(409, (await f.PostJson("/_rs/corrections/batch", Body("LOT-0457"), oem, "oem")).Status);
    }
}
