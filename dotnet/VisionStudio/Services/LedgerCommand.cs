using System.Globalization;
using Microsoft.Data.Sqlite;
using VisionReports;

namespace VisionStudio.Services;

/// <summary>
/// "VisionStudio ledger ..." (Req 17.10 MRG-08 item 8): the one-time anchoring of a results.db chained before the
/// chain head was kept, and the guided "start a new ledger" after alarm 801. Nothing else runs these: an alarm 801
/// clears only when a person runs one, names themself and types the confirmation, and the next start's check passes.
/// A new ledger never deletes: the old files are moved into ledger-archive/ with why.txt.
/// </summary>
public static class LedgerCommand
{
    public const string ArchiveFolder = "ledger-archive";
    public const string AnchorConfirm = "anchor";
    public const string NewConfirm = "new ledger";

    // results.db's companions that belong to the same ledger; moved together, so the archive verifies on its own
    private static readonly string[] Files = ["results.db", "results.db-wal", "results.db-shm", "results.ledger-head"];

    private const string Usage =
        "ledger check [--data <folder>] | ledger anchor --by <name> [--data <folder>] | " +
        "ledger new --by <name> --reason \"<why>\" [--data <folder>]   (confirmation on standard input)";

    /// <param name="defaultDataFolder">Vision Studio's data folder, used without --data.</param>
    /// <param name="keyFor">The ledger key for a data folder (its commissioning store, MRG-09), or null.</param>
    public static int Run(string[] args, string defaultDataFolder, Func<string, byte[]?> keyFor,
                          TextReader stdin, TextWriter stdout, TextWriter stderr, DateTime? nowUtc = null)
    {
        var verb = args.FirstOrDefault();
        var data = Option(args, "--data") ?? defaultDataFolder;
        var by = Option(args, "--by")?.Trim();
        var reason = Option(args, "--reason")?.Trim();
        var now = nowUtc ?? DateTime.UtcNow;
        try
        {
            switch (verb)
            {
                case "check": return Check(data, keyFor(data), stdout, stderr);
                case "anchor" when !string.IsNullOrEmpty(by): return Anchor(data, keyFor(data), by, stdin, stdout, stderr, now);
                case "new" when !string.IsNullOrEmpty(by) && !string.IsNullOrEmpty(reason):
                    return New(data, keyFor(data), by, reason, stdin, stdout, stderr, now);
                default:
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Check(string data, byte[]? key, TextWriter stdout, TextWriter stderr)
    {
        if (key is null) { stdout.WriteLine("No ledger key set: rows are not chained, nothing to check."); return 0; }
        var why = Failure(new ResultLog(data, key), out var report);
        if (why is not null) { stderr.WriteLine($"ALM-{LedgerGate.AlarmCode}: {why}"); return 1; }
        stdout.WriteLine($"Results ledger verifies: {Counts(report!)}.");
        return 0;
    }

    private static int Anchor(string data, byte[]? key, string by, TextReader stdin, TextWriter stdout, TextWriter stderr, DateTime now)
    {
        if (key is null) { stderr.WriteLine("No ledger key set: nothing is chained, nothing to anchor."); return 1; }
        var log = new ResultLog(data, key);
        // once only, and never over a head: a head that names deleted rows is the evidence of alarm 801
        if (File.Exists(log.HeadPath))
        {
            stderr.WriteLine($"{Path.GetFileName(log.HeadPath)} exists: this ledger is anchored already; nothing changed. Run \"ledger check\".");
            return 1;
        }
        if (AnchoredBefore(log))
        {
            stderr.WriteLine($"This ledger was anchored before and its {Path.GetFileName(log.HeadPath)} is now missing: that is alarm {LedgerGate.AlarmCode}, " +
                             "not a commissioning step. Nothing changed; use \"ledger new\" after checking the results log.");
            return 1;
        }
        stdout.WriteLine($"Anchoring the results ledger in {log.Path_} for {by}: the whole chain is checked and its newest rows are written to " +
                         $"{Path.GetFileName(log.HeadPath)}. Do this once, at commissioning, for a log chained before the head was kept.");
        stdout.WriteLine($"Type \"{AnchorConfirm}\" to go on:");
        if (!Confirmed(stdin, AnchorConfirm)) { stderr.WriteLine("Not confirmed; nothing changed."); return 1; }
        LedgerReport report;
        try { report = log.AnchorHead()!; }
        catch (DatabaseTamperingException e)
        {
            stderr.WriteLine($"ALM-{LedgerGate.AlarmCode}: {e.Message} The chain does not verify, so it is not anchored; nothing changed.");
            return 1;
        }
        log.AddEvent(now, "ledger_anchored", false, $"Results ledger anchored by {by}: {Counts(report)}");
        stdout.WriteLine($"Results ledger anchored: {Counts(report)}.");
        return 0;
    }

    private static int New(string data, byte[]? key, string by, string reason, TextReader stdin, TextWriter stdout, TextWriter stderr, DateTime now)
    {
        if (key is null) { stderr.WriteLine("No ledger key set: there is no ledger to replace; nothing changed."); return 1; }
        var why = Failure(new ResultLog(data, key), out _);
        if (why is null)
        {
            stderr.WriteLine("The results ledger verifies: a good ledger is never set aside. Nothing changed.");
            return 1;
        }
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z";
        var archive = Path.Combine(data, ArchiveFolder, stamp);
        for (var n = 2; Directory.Exists(archive); n++) archive = Path.Combine(data, ArchiveFolder, $"{stamp}-{n}");
        stdout.WriteLine($"The results ledger does not verify: {why}");
        stdout.WriteLine($"A new ledger moves results.db and its chain head into {archive} (nothing is deleted) and starts an empty " +
                         $"results.db, recorded as started by {by} because: {reason}. Close Vision Studio and the line runtime first.");
        stdout.WriteLine($"Type \"{NewConfirm}\" to go on:");
        if (!Confirmed(stdin, NewConfirm)) { stderr.WriteLine("Not confirmed; nothing changed."); return 1; }

        SqliteConnection.ClearAllPools();   // this process's connections hold the file open
        Directory.CreateDirectory(archive);
        var moved = new List<string>();
        try
        {
            foreach (var f in Files)
            {
                var from = Path.Combine(data, f);
                if (!File.Exists(from)) continue;
                File.Move(from, Path.Combine(archive, f));
                moved.Add(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var f in moved)
                try { File.Move(Path.Combine(archive, f), Path.Combine(data, f)); }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                { stderr.WriteLine($"{f} could not be put back from {archive}: {back.Message}"); }
            stderr.WriteLine($"{e.Message} Is Vision Studio or the line runtime still running? The old ledger was kept where it was.");
            return 1;
        }
        var rel = Path.GetRelativePath(data, archive);
        File.WriteAllText(Path.Combine(archive, "why.txt"),
            $"time_utc: {now.ToUniversalTime():yyyy-MM-dd HH:mm:ss}\nby: {by}\nreason: {reason}\nfailure: {why}\nfiles: {string.Join(", ", moved)}\n");
        var fresh = new ResultLog(data, key);
        fresh.AddEvent(now, "ledger_new", false, $"New results ledger started by {by} after alarm {LedgerGate.AlarmCode}: {reason}. Old ledger kept in {rel} ({why})");
        stdout.WriteLine($"New results ledger started in {fresh.Path_}; the old one is in {archive}.");
        return 0;
    }

    // the start's own check (LedgerGate), so the command and the line never disagree
    private static string? Failure(ResultLog log, out LedgerReport? report)
    {
        report = null;
        try { report = log.VerifyLedger(); return null; }
        catch (DatabaseTamperingException e) { return e.Message; }
        catch (Exception e) { return "The results ledger could not be checked: " + e.Message; }
    }

    private static bool AnchoredBefore(ResultLog log) =>
        log.Events(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.AddYears(100)).Any(e => e.Kind == "ledger_anchored");

    private static bool Confirmed(TextReader stdin, string words) =>
        string.Equals(stdin.ReadLine()?.Trim(), words, StringComparison.OrdinalIgnoreCase);

    private static string Counts(LedgerReport r) =>
        $"{r.V1Rows} v1 and {r.V2Rows} v2 chained rows, {r.Unchained} unchained";
}
