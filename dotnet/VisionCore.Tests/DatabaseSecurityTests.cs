using Microsoft.Data.Sqlite;
using VisionCore.Plc;
using VisionReports;
using Xunit;

namespace VisionCore.Tests;

public class DatabaseSecurityTests : IDisposable
{
    private readonly string _testDir;

    public DatabaseSecurityTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "vs_db_sec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    // Test-only key (32 bytes). The real key is set per installation at commissioning, never in the repository.
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void KeyMustBeLongEnough()
    {
        Assert.Equal(Key, DatabaseSecurity.ParseKey(Convert.ToBase64String(Key)));
        Assert.Null(DatabaseSecurity.ParseKey(Convert.ToBase64String(new byte[16])));
        Assert.Null(DatabaseSecurity.ParseKey("not base64!"));
        Assert.Null(DatabaseSecurity.ParseKey(null));
    }

    [Fact]
    public void WithoutAKeyRowsAreKeptButNotChained()
    {
        var log = new ResultLog(_testDir, null);
        var now = DateTime.UtcNow;
        log.AddInspection(new InspectionRow(0, now, "B001", "Job1", 0, 101, true, null, null, null, null, 10.0));

        Assert.False(log.LedgerOn);
        Assert.False(log.VerifyLedgerIntegrity());
        Assert.Single(log.Inspections(now.AddMinutes(-1), now.AddMinutes(1)));
        Assert.Contains(log.Events(now.AddMinutes(-1), now.AddMinutes(1)), e => e.Kind == "ledger_off");
    }

    [Fact]
    public void CleanLogVerifiesSuccessfully()
    {
        var log = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;

        log.AddInspection(new InspectionRow(0, now, "B001", "Job1", 0, 101, true, null, null, "HWN937", null, 12.5));
        log.AddInspection(new InspectionRow(0, now.AddMilliseconds(20), "B001", "Job1", 0, 102, false, "2D Code", "Damaged code", null, null, 14.1));
        log.AddPart(new PartRow(0, now.AddMilliseconds(50), "B001", "Job1", 101, false, PartFlags.None, 80, 25, 1, 0, 1, 1, "Pass", "Good"));
        log.AddPart(new PartRow(0, now.AddMilliseconds(70), "B001", "Job1", 102, true, PartFlags.CameraNg, 80, 25, 1, 0, 1, 0, "Camera 1", "Damaged code"));

        Assert.True(log.VerifyLedgerIntegrity());
    }

    [Fact]
    public void TamperingInspectionVerdictFailsCryptographicChain()
    {
        var log = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;

        log.AddInspection(new InspectionRow(0, now, "B001", "Job1", 0, 101, true, null, null, null, null, 10.0));
        log.AddInspection(new InspectionRow(0, now.AddMilliseconds(20), "B001", "Job1", 0, 102, false, "2D Code", "Unreadable", null, null, 12.0));

        Assert.True(log.VerifyLedgerIntegrity());

        // Simulate operator bypassing app and modifying results.db via SQLite directly
        using (var conn = new SqliteConnection($"Data Source={log.Path_}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE inspections SET ok = 1 WHERE part_id = 102"; // Falsifying reject to pass
            cmd.ExecuteNonQuery();
        }

        // Verification must detect the tampering and fail closed
        var ex = Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedgerIntegrity());
        Assert.Contains("Database tampering detected", ex.Message);
    }

    [Fact]
    public void TamperingPartRejectionFailsCryptographicChain()
    {
        var log = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;

        log.AddPart(new PartRow(0, now, "B001", "Job1", 101, false, PartFlags.None, 80, 20, 1, 0, 1, 1, "Pass", "Good"));
        log.AddPart(new PartRow(0, now.AddMilliseconds(50), "B001", "Job1", 102, true, PartFlags.CameraNg, 80, 20, 1, 0, 1, 0, "NG", "Defect"));

        Assert.True(log.VerifyLedgerIntegrity());

        // Simulate operator changing rejected from 1 to 0 in parts table
        using (var conn = new SqliteConnection($"Data Source={log.Path_}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE parts SET rejected = 0 WHERE part_id = 102";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedgerIntegrity());
        Assert.Contains("Database tampering detected", ex.Message);
    }

    // ---- chain v2 (Req 17.10 MRG-08)

    private static void Sql(ResultLog log, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={log.Path_}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static InspectionRow Insp(DateTime t, int part, bool ok, string? tool = null, string? reason = null) =>
        new(0, t, "B001", "Job1", 0, part, ok, tool, reason, "HWN937", null, 12.5, $"A-{part}", $"L01_C1_{part}");

    private static PartRow Part(DateTime t, int part, bool rejected, string category = "Pass", string reason = "Good") =>
        new(0, t, "B001", "Job1", part, rejected, rejected ? PartFlags.CameraNg : PartFlags.None, 80.25, 0.1, 1, 0, 1, rejected ? 0 : 1, category, reason);

    [Theory]
    [InlineData("UPDATE inspections SET fail_tool = 'Label' WHERE part_id = 102")]
    [InlineData("UPDATE inspections SET reason = 'Dust on lens' WHERE part_id = 102")]
    [InlineData("UPDATE inspections SET fail_tool = NULL WHERE part_id = 102")]
    [InlineData("UPDATE inspections SET text1 = 'HWN881' WHERE part_id = 101")]
    [InlineData("UPDATE inspections SET image_id = 'other' WHERE part_id = 101")]
    [InlineData("UPDATE inspections SET ms = 9.0 WHERE part_id = 101")]
    [InlineData("UPDATE parts SET reason = 'Good' WHERE part_id = 102")]
    [InlineData("UPDATE parts SET category = 'Pass' WHERE part_id = 102")]
    [InlineData("UPDATE parts SET flags = 0 WHERE part_id = 102")]
    [InlineData("UPDATE parts SET length_mm = 80.3 WHERE part_id = 101")]
    public void TamperingAnyRecordedFieldFailsTheChain(string tamper)
    {
        var log = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;
        log.AddInspection(Insp(now, 101, true));
        log.AddInspection(Insp(now.AddMilliseconds(20), 102, false, "2D Code", "Damaged code"));
        log.AddInspection(Insp(now.AddMilliseconds(40), 103, true));
        log.AddPart(Part(now, 101, false));
        log.AddPart(Part(now.AddMilliseconds(50), 102, true, "Camera 1", "Damaged code"));
        log.AddPart(Part(now.AddMilliseconds(90), 103, false));
        Assert.Equal(new LedgerReport(0, 6, 0), log.VerifyLedger());

        Sql(log, tamper);

        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());
    }

    [Fact]
    public void NewRowsAreChainedV2()
    {
        var log = new ResultLog(_testDir, Key);
        log.AddInspection(Insp(DateTime.UtcNow, 101, true));
        using var conn = new SqliteConnection($"Data Source={log.Path_}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chain_ver FROM inspections";
        Assert.Equal(DatabaseSecurity.ChainV2, cmd.ExecuteScalar());
    }

    [Fact]
    public void FieldsCannotShiftIntoTheirNeighbour()
    {
        var a = DatabaseSecurity.ComputeRowHashV2("", "inspections", ["ab", "c"], Key);
        var b = DatabaseSecurity.ComputeRowHashV2("", "inspections", ["a", "bc"], Key);
        var c = DatabaseSecurity.ComputeRowHashV2("", "inspections", ["a|1:b", null], Key);
        var d = DatabaseSecurity.ComputeRowHashV2("", "inspections", ["a", "b"], Key);
        Assert.Equal(4, new[] { a, b, c, d }.Distinct().Count());
        Assert.NotEqual(DatabaseSecurity.ComputeRowHashV2("", "inspections", [null], Key),
                        DatabaseSecurity.ComputeRowHashV2("", "inspections", ["-"], Key));
        Assert.NotEqual(DatabaseSecurity.ComputeRowHashV2("", "inspections", ["x"], Key),
                        DatabaseSecurity.ComputeRowHashV2("", "parts", ["x"], Key));
    }

    // Writes rows the way the first ledger (chain v1) did: no chain_ver, the v1 formula.
    private static void WriteV1Rows(ResultLog log, DateTime now)
    {
        var prev = DatabaseSecurity.GenesisHash;
        for (var p = 1; p <= 3; p++)
        {
            var t = now.AddMilliseconds(p).ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            prev = DatabaseSecurity.ComputeInspectionHash(prev, t, "B000", "Job1", 0, p, p != 2, Key);
            Sql(log, $"INSERT INTO inspections (time_utc,batch,job,camera,part_id,ok,fail_tool,reason,ms,chain_hash) " +
                     $"VALUES ('{t}','B000','Job1',0,{p},{(p != 2 ? 1 : 0)},'2D Code','old',10.0,'{prev}')");
        }
    }

    [Fact]
    public void OldV1RowsStillVerifyAndTheChainContinuesInV2()
    {
        var log = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;
        WriteV1Rows(log, now);
        log.AddInspection(Insp(now.AddSeconds(1), 101, false, "2D Code", "Damaged code"));
        log.AddInspection(Insp(now.AddSeconds(2), 102, true));

        Assert.Equal(new LedgerReport(3, 2, 0), log.VerifyLedger());

        // A v1 row is still checked in what v1 covered
        Sql(log, "UPDATE inspections SET ok = 1 WHERE part_id = 2");
        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());
    }

    [Fact]
    public void AV2RowSetBackToV1FailsTheChain()
    {
        var log = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;
        WriteV1Rows(log, now);
        log.AddInspection(Insp(now.AddSeconds(1), 101, false, "2D Code", "Damaged code"));
        log.AddInspection(Insp(now.AddSeconds(2), 102, true));

        Sql(log, "UPDATE inspections SET chain_ver = NULL WHERE part_id = 102");
        var ex = Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());
        Assert.Contains("v2 to v1", ex.Message);

        Sql(log, "UPDATE inspections SET chain_ver = '9' WHERE part_id = 102");
        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());
    }

    [Fact]
    public void RowsWithoutAKeyAreCountedAndTheChainLinksOverThem()
    {
        var keyed = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;
        keyed.AddInspection(Insp(now, 101, true));
        new ResultLog(_testDir, null).AddInspection(Insp(now.AddMilliseconds(10), 102, true)); // key missing for a while
        keyed.AddInspection(Insp(now.AddMilliseconds(20), 103, false, "2D Code", "Damaged code"));

        Assert.Equal(new LedgerReport(0, 2, 1), keyed.VerifyLedger());
    }

    [Fact]
    public void TwoWritersTakingTurnsKeepOneChain()
    {
        // e.g. the runtime and Vision Studio, each with its own log on the same results.db
        var a = new ResultLog(_testDir, Key);
        var b = new ResultLog(_testDir, Key);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            var w = i % 2 == 0 ? a : b;
            w.AddInspection(Insp(now.AddMilliseconds(i), 100 + i, i % 3 != 0, i % 3 == 0 ? "2D Code" : null, i % 3 == 0 ? "Damaged code" : null));
            w.AddPart(Part(now.AddMilliseconds(i), 100 + i, i % 3 == 0));
        }

        Assert.Equal(new LedgerReport(0, 20, 0), a.VerifyLedger());
        Assert.Equal(new LedgerReport(0, 20, 0), b.VerifyLedger());
    }

    [Fact]
    public void TwoWritersAtTheSameTimeKeepOneChain()
    {
        var logs = new[] { new ResultLog(_testDir, Key), new ResultLog(_testDir, Key) };
        var now = DateTime.UtcNow;
        const int Threads = 4, Rows = 50;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, th =>
        {
            var log = logs[th % 2];
            for (var i = 0; i < Rows; i++)
            {
                var part = th * 1000 + i;
                log.AddInspection(Insp(now.AddMilliseconds(i), part, i % 5 != 0, i % 5 == 0 ? "2D Code" : null, i % 5 == 0 ? $"writer {th}" : null));
                log.AddPart(Part(now.AddMilliseconds(i), part, i % 5 == 0));
            }
        });

        var report = logs[0].VerifyLedger();
        Assert.Equal(new LedgerReport(0, 2 * Threads * Rows, 0), report);
        Assert.Equal(Threads * Rows, logs[1].Inspections(now.AddMinutes(-1), now.AddMinutes(1)).Count);
    }

    // ---- chain head kept outside results.db (Req 17.10 MRG-08 item 5)

    private static ResultLog FourRows(string dir, DateTime now)
    {
        var log = new ResultLog(dir, Key);
        log.AddInspection(Insp(now, 101, true));
        log.AddInspection(Insp(now.AddMilliseconds(10), 102, false, "2D Code", "Damaged code"));
        log.AddPart(Part(now.AddMilliseconds(20), 101, false));
        log.AddPart(Part(now.AddMilliseconds(30), 102, true, "Camera 1", "Damaged code"));
        return log;
    }

    [Fact]
    public void AFreshLogStartsItsChainHeadAndACleanLogVerifies()
    {
        var log = new ResultLog(_testDir, Key);
        Assert.True(File.Exists(log.HeadPath));
        Assert.Equal(new LedgerReport(0, 0, 0), log.VerifyLedger());

        FourRows(_testDir, DateTime.UtcNow);
        Assert.Equal(new LedgerReport(0, 4, 0), log.VerifyLedger());
        Assert.DoesNotContain(".tmp", string.Join(",", Directory.GetFiles(_testDir)));
    }

    [Theory]
    [InlineData("DELETE FROM inspections WHERE id = (SELECT MAX(id) FROM inspections)")]
    [InlineData("DELETE FROM parts WHERE id = (SELECT MAX(id) FROM parts)")]
    [InlineData("DELETE FROM inspections")]
    [InlineData("UPDATE parts SET chain_hash = NULL WHERE id = (SELECT MAX(id) FROM parts)")]
    [InlineData("UPDATE inspections SET chain_hash = '' WHERE id = (SELECT MAX(id) FROM inspections)")]
    public void DeletingTheNewestRowsOrBlankingTheNewestHashIsSeen(string tamper)
    {
        var log = FourRows(_testDir, DateTime.UtcNow);
        Sql(log, tamper);
        var ex = Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());
        Assert.Contains("chain head", ex.Message);
    }

    [Fact]
    public void AMissingOrChangedChainHeadIsSeen()
    {
        var log = FourRows(_testDir, DateTime.UtcNow);
        var good = File.ReadAllText(log.HeadPath);

        // the newest row deleted and the head rolled back to name the row before it, without the key
        File.WriteAllText(log.HeadPath, good.Replace("inspections 2 ", "inspections 1 "));
        Assert.Contains("changed", Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger()).Message);

        File.WriteAllText(log.HeadPath, "nonsense");
        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());

        File.Delete(log.HeadPath);
        Assert.Contains("missing", Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger()).Message);
        // a missing head is not made again by the next row: the evidence stays
        log.AddInspection(Insp(DateTime.UtcNow, 103, true));
        Assert.False(File.Exists(log.HeadPath));
        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());

        File.WriteAllText(log.HeadPath, good);
        Assert.Equal(new LedgerReport(0, 5, 0), log.VerifyLedger());
    }

    [Fact]
    public void AHeadLeftOneRowBehindStillVerifies()
    {
        // a crash after the commit and before the head moved: the file is ahead of its head, and every row is chained
        var log = FourRows(_testDir, DateTime.UtcNow);
        var behind = File.ReadAllText(log.HeadPath);
        log.AddPart(Part(DateTime.UtcNow, 103, false));
        File.WriteAllText(log.HeadPath, behind);
        Assert.Equal(new LedgerReport(0, 5, 0), log.VerifyLedger());
    }

    [Fact]
    public void ALogChainedBeforeTheHeadIsAnchoredOnlyWhenItVerifies()
    {
        var log = FourRows(_testDir, DateTime.UtcNow);
        File.Delete(log.HeadPath);   // as a results.db chained before the head was kept
        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());

        Sql(log, "UPDATE inspections SET reason = 'edited' WHERE part_id = 102");
        Assert.Throws<DatabaseTamperingException>(() => log.AnchorHead());
        Assert.False(File.Exists(log.HeadPath));

        Sql(log, "UPDATE inspections SET reason = 'Damaged code' WHERE part_id = 102");
        Assert.Equal(new LedgerReport(0, 4, 0), log.AnchorHead());
        Assert.Equal(new LedgerReport(0, 4, 0), log.VerifyLedger());
        Sql(log, "DELETE FROM parts WHERE part_id = 102");
        Assert.Throws<DatabaseTamperingException>(() => log.VerifyLedger());
    }
}
