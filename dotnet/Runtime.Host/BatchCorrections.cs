using System.Collections;
using System.Text.Json;
using Sundaram.RemoteService;
using VisionReports;

namespace VisionSystem.Runtime;

/// <summary>
/// Remote corrections of batch values (owner, 2026-11-04: batch values only; REMOTE-SUPPORT.md 4b). The values entered at a
/// batch's start (lot, expiry, MRP…) stay in results.db as they were; a correction is a chained entry in the service log next
/// to them, made after a supervisor at the line approves. Reports show the corrected value with the original, who corrected,
/// who approved and why. A correction never reaches the running station: no inspection, OK or NG decision depends on it.
/// </summary>
public static class BatchCorrections
{
    public static CorrectionKind Kind(ResultLog log, ServiceLog service) => new(
        "batch", "Batch values", new FieldList(log),
        (batch, _) => Task.FromResult(Current(log, service, batch)),
        (c, approver, _) => Task.FromResult(Append(log, service, c, approver)),
        Validate: c =>
            c.Changes.Values.Any(ch => ch.New.Length == 0) ? "a batch value cannot be made empty"
            : Current(log, service, c.Record) is not { } now ? "no such batch, or the name is used by more than one batch"
            : c.Changes.Keys.FirstOrDefault(f => !now.ContainsKey(f)) is { } missing ? $"the batch has no value \"{missing}\""
            : null);

    static readonly object Lock = new();

    /// <summary>A batch's values as they stand: entered at its start, with every approved correction applied in order.</summary>
    public static IReadOnlyDictionary<string, string>? Current(ResultLog log, ServiceLog service, string batch)
    {
        // corrections are kept by batch name: a name started more than once would be ambiguous, so it is not correctable here
        var rows = log.Batches(limit: 2000).Where(b => b.Name == batch).Take(2).ToList();
        if (rows.Count != 1) return null;
        var row = rows[0];
        var values = Parse(row.Fields);
        foreach (var c in service.BatchCorrections(batch))
            foreach (var (field, (_, now)) in c.Changes)
                values[field] = now;
        return values;
    }

    static Dictionary<string, string> Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    static string Append(ResultLog log, ServiceLog service, Correction c, string approver)
    {
        lock (Lock)
        {
            // checked again under the lock: two corrections of one value approved at once must not both start from the same "old"
            var now = Current(log, service, c.Record) ?? throw new InvalidOperationException("the batch is no longer correctable");
            if (c.Changes.Any(ch => now.GetValueOrDefault(ch.Key, "") != ch.Value.Old))
                throw new InvalidOperationException("the batch was corrected meanwhile; propose the correction again");
            return AppendLocked(service, c, approver);
        }
    }

    static string AppendLocked(ServiceLog service, Correction c, string approver)
    {
        var data = new Dictionary<string, string>
        {
            ["batch"] = c.Record, ["reason"] = c.Reason, ["by"] = c.RequestedBy, ["approvedBy"] = approver, ["correction"] = c.Id,
        };
        foreach (var (field, ch) in c.Changes)
        {
            data["old." + field] = ch.Old;
            data["new." + field] = ch.New;
        }
        var head = service.Append(DateTime.UtcNow, "correction:" + c.Id, c.RequestedBy, c.Role == RemoteRole.Oem ? "oem" : "service",
            ServiceLog.BatchCorrectionKind, $"Batch {c.Record} corrected: " + string.Join("; ", c.Changes.Select(x => $"{x.Key} \"{x.Value.Old}\" → \"{x.Value.New}\"")),
            data);
        return "S-" + head.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The batch fields this line uses, read from its recent batches each time (they come from its jobs).</summary>
    sealed class FieldList(ResultLog log) : IReadOnlyList<CorrectionField>
    {
        (DateTime At, IReadOnlyList<CorrectionField> Fields) _snap = (DateTime.MinValue, []);

        // one read of the batches serves a request's calls (count, index, each field), and stays the same between them
        IReadOnlyList<CorrectionField> Now()
        {
            var snap = _snap;
            if (DateTime.UtcNow - snap.At < TimeSpan.FromSeconds(2)) return snap.Fields;
            var fields = log.Batches(limit: 200).SelectMany(b => Parse(b.Fields).Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).Select(k => new CorrectionField(k, k, MaxLength: 100)).ToList();
            _snap = (DateTime.UtcNow, fields);
            return fields;
        }

        public CorrectionField this[int index] => Now()[index];
        public int Count => Now().Count;
        public IEnumerator<CorrectionField> GetEnumerator() => Now().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
