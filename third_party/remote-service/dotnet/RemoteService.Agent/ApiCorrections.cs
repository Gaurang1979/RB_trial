using System.Security.Cryptography;
using System.Text.Json;

namespace Sundaram.RemoteService;

/// <summary>
/// Corrections of recorded data (DESIGN.md section 3, capability 8). Never an edit: the engineer proposes new values with a
/// reason, a supervisor at the machine approves with their own sign-in, and the host adds a correcting entry next to the
/// original in its ledger. Every step is in the user log with the old and new values.
/// </summary>
public sealed partial class RemoteAgent
{
    static readonly TimeSpan ApprovalWait = TimeSpan.FromMinutes(30);
    const int MinReason = 10;

    sealed record CorrectionBody(string? Record, Dictionary<string, string>? Changes, string? Reason);

    async Task CorrectionsApi(RemoteSession s, RemoteRequest req, string[] segs, bool post, RemoteResponse res, CancellationToken ct)
    {
        var kinds = _host.Corrections.Where(k => k.AllowedFor(s.Role)).ToList();
        if (!post && segs.Length == 1)
        {
            await res.Json(200, kinds.Select(k => new
            {
                name = k.Name, title = k.Title, needsStopped = k.NeedsStopped,
                fields = k.Fields.Select(f => new { name = f.Name, title = f.Title, choices = f.Choices ?? [], maxLength = f.MaxLength }),
            }));
            return;
        }
        if (segs.Length != 2 || kinds.FirstOrDefault(k => k.Name == segs[1]) is not { } kind)
        {
            await res.Error(404, "No such kind of record, or your role may not correct it.", "rs.notFound");
            return;
        }
        if (!post) { await ReadRecord(s, req, kind, res, ct); return; }
        await Propose(s, req, kind, res, ct);
    }

    async Task ReadRecord(RemoteSession s, RemoteRequest req, CorrectionKind kind, RemoteResponse res, CancellationToken ct)
    {
        var record = req.QueryFields().GetValueOrDefault("record", "").Trim();
        if (record.Length is 0 or > 100 || await kind.Find(record, ct) is not { } values)
        {
            await res.Error(404, "No such record.", "rs.noRecord");
            return;
        }
        if (!await Record(s, res, "record_read", $"Opened {kind.Name} {record} to correct", new() { ["kind"] = kind.Name, ["record"] = record }))
            return;
        await res.Json(200, new { record, values = kind.Fields.ToDictionary(f => f.Name, f => values.GetValueOrDefault(f.Name, "")) });
    }

    async Task Propose(RemoteSession s, RemoteRequest req, CorrectionKind kind, RemoteResponse res, CancellationToken ct)
    {
        if (_host.Supervisor is null) { await res.Error(409, "This machine cannot ask a supervisor, so it takes no corrections.", "rs.noSupervisor"); return; }
        CorrectionBody? b;
        try { b = JsonSerializer.Deserialize<CorrectionBody>(req.Body, JsonIn); }
        catch (JsonException) { b = null; }
        var record = (b?.Record ?? "").Trim();
        var reason = (b?.Reason ?? "").Trim();
        if (b?.Changes is not { Count: > 0 } wanted || record.Length is 0 or > 100)
        {
            await res.Error(400, "Name the record and at least one field to correct.", "rs.correction");
            return;
        }
        if (reason.Length < MinReason || reason.Length > 500 || reason.Any(char.IsControl))
        {
            await res.Error(400, $"Give the reason for the correction ({MinReason} to 500 characters); it is kept with the record.", "rs.reason");
            return;
        }
        if (await kind.Find(record, ct) is not { } current) { await res.Error(404, "No such record.", "rs.noRecord"); return; }

        var changes = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        foreach (var (name, value) in wanted)
        {
            var field = kind.Fields.FirstOrDefault(f => f.Name == name);
            var v = (value ?? "").Trim();
            if (field is null) { await res.Error(400, $"\"{name}\" may not be corrected.", "rs.field"); return; }
            if (field.Choices is { Count: > 0 } choices ? !choices.Contains(v) : v.Length > field.MaxLength || v.Any(char.IsControl))
            {
                await res.Error(400, $"\"{field.Title}\" may not take this value.", "rs.value");
                return;
            }
            var old = current.GetValueOrDefault(name, "");
            if (old != v) changes[name] = new FieldChange(old, v);
        }
        if (changes.Count == 0) { await res.Error(400, "The new values are the same as the recorded ones.", "rs.same"); return; }

        var c = new Correction(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), kind.Name, record, changes, reason, s.User, s.Role);
        if (kind.Validate?.Invoke(c) is { } refused) { await res.Error(400, "Not taken: " + refused, "rs.refused"); return; }
        if (!await Record(s, res, "correction_proposed", $"Correction proposed: {kind.Name} {record}: {Describe(changes)}", Data(c)))
            return;

        var job = NewJob(s);
        _ = Task.Run(() => Approve(job, s, kind, c));
        await res.Json(202, new { job = job.Id, correction = c.Id });
    }

    /// <summary>Waits for the supervisor, checks the record has not changed meanwhile and the machine may be changed, then has
    /// the host add the correcting entry.</summary>
    async Task Approve(Job job, RemoteSession s, CorrectionKind kind, Correction c)
    {
        void Note(string kindName, string detail, Dictionary<string, string> data)
        {
            try { Log.Write(s, s.User, s.Role, kindName, detail, data); } catch (Exception) { }
        }
        try
        {
            job.State = "waiting";
            job.Message = "Waiting for a supervisor at the machine to approve";
            string? approver;
            using (var wait = new CancellationTokenSource(ApprovalWait))
            {
                try { approver = await _host.Supervisor!.ApproveAsync(c, wait.Token); }
                catch (OperationCanceledException) { approver = null; }
            }
            if (string.IsNullOrWhiteSpace(approver))
            {
                job.State = "refused";
                job.Message = "No supervisor approved the correction.";
                Note("correction_rejected", $"Correction rejected: {c.Kind} {c.Record}", Data(c));
                return;
            }
            Log.Write(s, s.User, s.Role, "correction_approved", $"Correction approved by {approver}: {c.Kind} {c.Record}", Data(c, approver));

            // the record as approved must still be the record as it is: another change meanwhile means the supervisor approved
            // something else
            var now = await kind.Find(c.Record, CancellationToken.None);
            if (now is null || c.Changes.Any(ch => now.GetValueOrDefault(ch.Key, "") != ch.Value.Old))
            {
                job.State = "refused";
                job.Message = "The record changed while waiting for approval; propose the correction again.";
                Note("correction_refused", $"Correction not applied, the record changed meanwhile: {c.Kind} {c.Record}", Data(c, approver));
                return;
            }
            if (kind.NeedsStopped && (_host.Machine is null || !_host.Machine.SafeToChange(out var why)))
            {
                job.State = "refused";
                job.Message = "Not now: the machine is not stopped.";
                Note("correction_refused", $"Correction not applied, the machine is not stopped: {c.Kind} {c.Record}", Data(c, approver));
                return;
            }
            job.State = "running";
            job.Message = "";
            var reference = await kind.Append(c, approver, CancellationToken.None);
            Log.Write(s, s.User, s.Role, "correction_applied", $"Correction added as {reference}: {c.Kind} {c.Record}: {Describe(c.Changes)}",
                Data(c, approver, reference));
            job.Result = $"Correction added ({reference}), approved by {approver}";
            job.State = "done";
        }
        catch (Exception e)
        {
            job.State = "failed";
            job.Message = e.Message;
            Note("failed", $"Correction failed: {c.Kind} {c.Record}: {e.Message}", Data(c));
        }
    }

    static string Describe(IReadOnlyDictionary<string, FieldChange> changes) =>
        string.Join("; ", changes.Select(ch => $"{ch.Key} \"{ch.Value.Old}\" → \"{ch.Value.New}\""));

    static Dictionary<string, string> Data(Correction c, string? approver = null, string? reference = null)
    {
        var d = new Dictionary<string, string> { ["correction"] = c.Id, ["kind"] = c.Kind, ["record"] = c.Record, ["reason"] = c.Reason };
        foreach (var (field, ch) in c.Changes)
        {
            d["old." + field] = ch.Old;
            d["new." + field] = ch.New;
        }
        if (approver is not null) d["approvedBy"] = approver;
        if (reference is not null) d["entry"] = reference;
        return d;
    }
}
