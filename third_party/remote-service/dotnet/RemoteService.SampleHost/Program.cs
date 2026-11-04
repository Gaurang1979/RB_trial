// A sample host: how an application uses the remote service agent. Not for a real machine: the Service code is fixed and
// the "operator" answers on this console.
//
//   RS_TOKEN=<link token> RS_SERVICE_CODE=<6 digits> [RS_PACKAGE_KEY=<public.pem>] dotnet run -- <relay address> <machine> <data folder> [--insecure]
//   With RS_PACKAGE_KEY the machine takes "Sample machine" packages signed with that key (installed under <data folder>/program).
using Microsoft.Extensions.Logging;
using Sundaram.RemoteService;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: RemoteService.SampleHost <relay address> <machine> <data folder> [--insecure]");
    return 2;
}
var folder = Path.GetFullPath(args[2]);
foreach (var d in new[] { "logs", "jobs", "images" }) Directory.CreateDirectory(Path.Combine(folder, d));
File.WriteAllText(Path.Combine(folder, "logs", "machine.log"), $"{DateTime.Now:s} sample machine started\n");

var sample = new SampleMachine(Environment.GetEnvironmentVariable("RS_SERVICE_CODE") ?? "");
using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var agent = new RemoteAgent(new RemoteServiceOptions
{
    Relay = args[0], Machine = args[1], Token = Environment.GetEnvironmentVariable("RS_TOKEN"), AllowInsecure = args.Contains("--insecure"),
    DataFolder = Path.Combine(folder, "agent"),
}, new RemoteServiceHost
{
    Product = "Sample machine", Code = sample, Audit = sample, Machine = sample, Operator = sample, Supervisor = sample,
    Corrections =
    [
        new CorrectionKind("batch", "Batch details", [new CorrectionField("lot", "Lot number", MaxLength: 30), new CorrectionField("expiry", "Expiry (YYYY-MM)", MaxLength: 7)],
            (id, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(sample.Batches.TryGetValue(id, out var b) ? new Dictionary<string, string>(b) : null),
            (c, approver, _) => Task.FromResult(sample.AppendCorrection(c, approver)),
            Validate: c => c.Changes.TryGetValue("expiry", out var e) && !System.Text.RegularExpressions.Regex.IsMatch(e.New, @"^\d{4}-\d{2}$") ? "expiry must be YYYY-MM" : null),
    ],
    Roots =
    [
        new FileRoot("logs", Path.Combine(folder, "logs")),
        new FileRoot("images", Path.Combine(folder, "images")),
        new FileRoot("jobs", Path.Combine(folder, "jobs"), Writable: true, Extensions: [".job", ".json"]),
    ],
    Actions =
    [
        new RemoteAction("restart-reader", "Restart the reader service", "Stops and starts the reader; the line holds meanwhile.",
            async (_, ct) => { await Task.Delay(500, ct); return "Reader restarted"; }),
        new RemoteAction("clear-temp", "Clear temporary files", "Removes temporary files; only while the machine is stopped.",
            (_, _) => Task.FromResult("0 files removed"), NeedsStopped: true, NeedsConfirmation: true),
    ],
    Views =
    [
        new DataView("alarms", "Alarms today", ["level"], (p, limit, _) => Task.FromResult(new DataTable(["time", "level", "text"],
            [[DateTime.Now.ToString("HH:mm"), p.GetValueOrDefault("level", "warning"), "Camera temperature high"]], false))),
    ],
    Packages = Environment.GetEnvironmentVariable("RS_PACKAGE_KEY") is { Length: > 0 } key
        ? new FolderPackageInstaller(Path.Combine(folder, "program"), "Sample machine", [File.ReadAllText(key)],
            (dir, _) => { Console.WriteLine($"[program] restarted from {dir}"); return Task.CompletedTask; })
        : null,
    Diagnostics = async (zip, ct) =>
    {
        await using var w = new StreamWriter(zip.CreateEntry("versions.txt").Open());
        await w.WriteAsync($"sample host {typeof(RemoteAgent).Assembly.GetName().Version}\n");
    },
}, logs.CreateLogger("RemoteService"));
agent.Sessions.Changed += () => Console.WriteLine(agent.Sessions.Active ? "[screen] Remote service active" : "[screen] (badge off)");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await agent.RunAsync(stop.Token);
return 0;

sealed class SampleMachine(string code) : IServiceCodeCheck, IAuditSink, IMachineState, IOperatorConfirmation, ISupervisorApproval, IAuditChain
{
    /// <summary>The log as a SHA-256 chain: each entry's hash covers the one before (a real host uses an HMAC with its key).</summary>
    string _head = new('0', 64);
    public string? Head => _head;

    /// <summary>Recorded batches; a correction is appended next to them, never written over them.</summary>
    public readonly Dictionary<string, Dictionary<string, string>> Batches = new() { ["B-0457"] = new() { ["lot"] = "LOT-0475", ["expiry"] = "2027-10" } };
    public readonly List<string> Ledger = [];

    public string AppendCorrection(Correction c, string approver)
    {
        Ledger.Add($"correction of {c.Kind} {c.Record}: {string.Join(", ", c.Changes.Select(x => $"{x.Key} {x.Value.Old} -> {x.Value.New}"))}; reason: {c.Reason}; by {c.RequestedBy}, approved by {approver}");
        foreach (var (field, ch) in c.Changes) Batches[c.Record][field] = ch.New;   // the current view; the original stays in the ledger
        Console.WriteLine("[ledger] " + Ledger[^1]);
        return "C-" + Ledger.Count.ToString("D4");
    }

    public Task<string?> ApproveAsync(Correction c, CancellationToken ct)
    {
        Console.WriteLine($"[screen] Supervisor approval: {c.Kind} {c.Record}, reason: {c.Reason} - approved by the sample supervisor");
        return Task.FromResult<string?>("Supervisor (sample)");
    }

    public CodeResult Check(string candidate, string engineer, RemoteRole role) =>
        code.Length == 6 && candidate == code ? CodeResult.Accepted : CodeResult.Wrong;

    public void Write(AuditEntry e)
    {
        _head = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{_head}|{e.Time:O}|{e.User}|{e.Kind}|{e.Detail}"))).ToLowerInvariant();
        Console.WriteLine($"[log] {e.Time:HH:mm:ss} {e.User} {e.Kind}: {e.Detail}  #{_head[..12]}");
    }

    public bool SafeToChange(out string reason) { reason = ""; return true; }

    public Task<bool> ConfirmAsync(ConfirmRequest r, CancellationToken ct)
    {
        Console.WriteLine($"[screen] {r.Title}: {r.What} (asked by {r.RequestedBy}) - approved by the sample operator");
        return Task.FromResult(true);
    }
}
