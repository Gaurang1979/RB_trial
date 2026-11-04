using Microsoft.Data.Sqlite;
using VisionCore.Plc;
using VisionReports;
using VisionStudio.Services;
using Xunit;

namespace VisionStudio.Tests;

/// <summary>"VisionStudio ledger" (Req 17.10 MRG-08 item 8): anchoring a log chained before the chain head was kept,
/// and the guided new ledger after alarm 801, which keeps the old files and is recorded. Neither runs without a
/// person's name and typed confirmation, so an alarm 801 never clears by itself.</summary>
public sealed class LedgerCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_ledger_cmd_" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new(), _err = new();
    private static readonly DateTime Now = new(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);

    // Test-only key (32 bytes). The real key is set per installation at commissioning, never in the repository.
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ResultLog Log()
    {
        var log = new ResultLog(_dir, Key);
        var now = DateTime.UtcNow;
        log.AddInspection(new InspectionRow(0, now, "B001", "Job1", 0, 101, true, null, null, "HWN937", null, 12.5));
        log.AddInspection(new InspectionRow(0, now.AddMilliseconds(20), "B001", "Job1", 0, 102, false, "2D Code", "Damaged code", null, null, 14.1));
        log.AddPart(new PartRow(0, now.AddMilliseconds(50), "B001", "Job1", 101, false, PartFlags.None, 80, 25, 1, 0, 1, 1, "Pass", "Good"));
        return log;
    }

    /// <summary>A results.db chained before PR #303: chained rows, no head file.</summary>
    private ResultLog OlderChain()
    {
        var log = Log();
        File.Delete(log.HeadPath);
        return log;
    }

    private static void Sql(ResultLog log, string sql)
    {
        using var c = new SqliteConnection($"Data Source={log.Path_}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private int Run(string stdin, params string[] args) =>
        LedgerCommand.Run(args, _dir, _ => Key, new StringReader(stdin), _out, _err, Now);

    private string? Gate() => new LedgerGate(new ResultLog(_dir, Key)).Check();

    private List<EventRow> Events(string kind) =>
        new ResultLog(_dir, Key).Events(Now.AddYears(-1), DateTime.UtcNow.AddYears(1)).Where(e => e.Kind == kind).ToList();

    [Fact]
    public void AnOlderChainIsAnchoredAndTheStartCheckThenPasses()
    {
        OlderChain();
        Assert.Contains("missing", Gate());
        Assert.Equal(0, Run("anchor\n", "anchor", "--by", "Ravi"));
        Assert.Null(Gate());
        var e = Assert.Single(Events("ledger_anchored"));
        Assert.Contains("Ravi", e.Detail);
        Assert.Contains("0 v1 and 3 v2 chained rows", e.Detail);
    }

    [Fact]
    public void AChangedChainIsNotAnchored()
    {
        var log = OlderChain();
        Sql(log, "UPDATE inspections SET reason = 'Good' WHERE part_id = 102");
        Assert.Equal(1, Run("anchor\n", "anchor", "--by", "Ravi"));
        Assert.Contains("not anchored", _err.ToString());
        Assert.False(File.Exists(log.HeadPath));
        Assert.NotNull(Gate());
    }

    [Fact]
    public void AnExistingHeadIsNeverOverwritten()
    {
        var log = Log();
        Sql(log, "DELETE FROM parts");   // newest rows deleted: the head is the evidence
        var head = File.ReadAllText(log.HeadPath);
        Assert.Equal(1, Run("anchor\n", "anchor", "--by", "Ravi"));
        Assert.Equal(head, File.ReadAllText(log.HeadPath));
        Assert.NotNull(Gate());
    }

    [Fact]
    public void AnchoringIsOnceOnly()
    {
        var log = OlderChain();
        Assert.Equal(0, Run("anchor\n", "anchor", "--by", "Ravi"));
        File.Delete(log.HeadPath);   // later someone removes the head
        Assert.Equal(1, Run("anchor\n", "anchor", "--by", "Ravi"));
        Assert.Contains("anchored before", _err.ToString());
        Assert.False(File.Exists(log.HeadPath));
        Assert.NotNull(Gate());
    }

    [Fact]
    public void AnchorNeedsANameAndTheTypedWord()
    {
        var log = OlderChain();
        Assert.Equal(2, Run("anchor\n", "anchor"));
        Assert.Equal(1, Run("yes\n", "anchor", "--by", "Ravi"));
        Assert.Equal(1, Run("", "anchor", "--by", "Ravi"));
        Assert.False(File.Exists(log.HeadPath));
        Assert.Empty(Events("ledger_anchored"));
    }

    [Fact]
    public void AGoodLedgerIsNeverSetAside()
    {
        Log();
        Assert.Equal(1, Run("new ledger\n", "new", "--by", "Ravi", "--reason", "test"));
        Assert.Contains("verifies", _err.ToString());
        Assert.False(Directory.Exists(Path.Combine(_dir, LedgerCommand.ArchiveFolder)));
        Assert.Equal(2, new ResultLog(_dir, Key).Inspections(Now.AddYears(-1), DateTime.UtcNow.AddDays(1)).Count);
    }

    [Fact]
    public void AnAlarmStaysUntilAPersonStartsANewLedger()
    {
        var log = Log();
        Sql(log, "UPDATE inspections SET ok = 1 WHERE part_id = 102");
        Assert.NotNull(Gate());
        Assert.NotNull(Gate());   // checking again never clears it
        Assert.Equal(2, Run("new ledger\n", "new", "--by", "Ravi"));                       // no reason
        Assert.Equal(2, Run("new ledger\n", "new", "--reason", "row changed"));            // no name
        Assert.Equal(1, Run("new\n", "new", "--by", "Ravi", "--reason", "row changed"));   // not the typed words
        Assert.False(Directory.Exists(Path.Combine(_dir, LedgerCommand.ArchiveFolder)));
        Assert.NotNull(Gate());
    }

    [Fact]
    public void ANewLedgerKeepsTheOldFilesAndIsRecorded()
    {
        var log = Log();
        Sql(log, "UPDATE inspections SET ok = 1 WHERE part_id = 102");
        var why = Gate();
        Assert.NotNull(why);

        Assert.Equal(0, Run("New Ledger\n", "new", "--by", "Ravi", "--reason", "row 2 changed by hand"));

        var archive = Path.Combine(_dir, LedgerCommand.ArchiveFolder, "20261103-180000Z");
        Assert.True(File.Exists(Path.Combine(archive, "results.db")));
        Assert.True(File.Exists(Path.Combine(archive, "results.ledger-head")));
        var note = File.ReadAllText(Path.Combine(archive, "why.txt"));
        Assert.Contains("by: Ravi", note);
        Assert.Contains("reason: row 2 changed by hand", note);
        Assert.Contains(why!, note);
        // the old rows are kept as they were, with the change that raised the alarm
        using (var c = new SqliteConnection($"Data Source={Path.Combine(archive, "results.db")};Mode=ReadOnly"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM inspections WHERE part_id = 102 AND ok = 1";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        Assert.Null(Gate());   // the next start's check passes on the new ledger
        var e = Assert.Single(Events("ledger_new"));
        Assert.Contains("Ravi", e.Detail);
        Assert.Contains("row 2 changed by hand", e.Detail);
        Assert.Contains(Path.Combine(LedgerCommand.ArchiveFolder, "20261103-180000Z"), e.Detail);
        Assert.Empty(new ResultLog(_dir, Key).Inspections(Now.AddYears(-1), DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public void CheckSaysWhatItSawAndChangesNothing()
    {
        var log = Log();
        Assert.Equal(0, Run("", "check"));
        Assert.Contains("0 v1 and 3 v2 chained rows", _out.ToString());
        Sql(log, "DELETE FROM parts");
        Assert.Equal(1, Run("", "check"));
        Assert.Contains("ALM-801", _err.ToString());
        Assert.NotNull(Gate());
    }

    [Fact]
    public void WithoutAKeyNothingIsAnchoredOrReplaced()
    {
        Log();
        Assert.Equal(0, LedgerCommand.Run(["check"], _dir, _ => null, new StringReader(""), _out, _err, Now));
        Assert.Equal(1, LedgerCommand.Run(["anchor", "--by", "Ravi"], _dir, _ => null, new StringReader("anchor\n"), _out, _err, Now));
        Assert.Equal(1, LedgerCommand.Run(["new", "--by", "Ravi", "--reason", "x"], _dir, _ => null, new StringReader("new ledger\n"), _out, _err, Now));
        Assert.False(Directory.Exists(Path.Combine(_dir, LedgerCommand.ArchiveFolder)));
    }
}
