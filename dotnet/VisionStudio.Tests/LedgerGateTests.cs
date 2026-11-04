using Microsoft.Data.Sqlite;
using VisionCore.Plc;
using VisionReports;
using VisionStudio.Services;
using Xunit;

namespace VisionStudio.Tests;

/// <summary>The results ledger check before production (Req 17.10 REQ-16 item 5): a clean log lets the line start,
/// a changed one or one whose newest rows were deleted refuses it with alarm 801.</summary>
public sealed class LedgerGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_ledger_gate_" + Guid.NewGuid().ToString("N"));

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

    private static void Sql(ResultLog log, string sql)
    {
        using var c = new SqliteConnection($"Data Source={log.Path_}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ACleanLogLetsTheLineStart()
    {
        var gate = new LedgerGate(Log());
        Assert.Null(gate.Check());
        Assert.Null(gate.Failure);
    }

    [Fact]
    public void WithoutAKeyNothingIsCheckedAndTheLineStarts()
    {
        Assert.Null(new LedgerGate(new ResultLog(_dir, null)).Check());
        Assert.Null(new LedgerGate(null).Check());
    }

    [Fact]
    public void AChangedRowRefusesTheStart()
    {
        var log = Log();
        var gate = new LedgerGate(log);
        Sql(log, "UPDATE inspections SET ok = 1, fail_tool = NULL WHERE part_id = 102");
        Assert.Contains("tampering", gate.Check());
        Assert.NotNull(gate.Failure);
        Assert.Equal(801u, LedgerGate.AlarmCode);
    }

    [Fact]
    public void DeletingTheNewestRowRefusesTheStart()
    {
        var log = Log();
        var gate = new LedgerGate(log);
        Sql(log, "DELETE FROM inspections WHERE part_id = 102");
        Assert.Contains("newest", gate.Check());
    }

    [Fact]
    public void AChangedChainHeadRefusesTheStartAndPuttingItBackClearsIt()
    {
        var log = Log();
        var gate = new LedgerGate(log);
        var head = File.ReadAllText(log.HeadPath);
        File.WriteAllText(log.HeadPath, "x");
        Assert.NotNull(gate.Check());
        File.WriteAllText(log.HeadPath, head);   // the engineer put the head back
        Assert.Null(gate.Check());
        Assert.Null(gate.Failure);
    }
}
