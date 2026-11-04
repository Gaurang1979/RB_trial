using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VisionReports;
using Xunit;

namespace VisionCore.Tests;

/// <summary>The remote service user log (REMOTE-SUPPORT.md): its own HMAC chain and head, apart from the results ledger.</summary>
public class ServiceLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vs-servicelog-" + Guid.NewGuid().ToString("N")[..8]);
    readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    ServiceLog Log() => new(_dir, _key);

    static readonly DateTime T0 = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);

    void Fill(ServiceLog log, int n)
    {
        for (var i = 0; i < n; i++)
            log.Append(T0.AddMinutes(i), "s1", "anita", "service", i == 0 ? "session_start" : "file_read", "entry " + i,
                new Dictionary<string, string> { ["path"] = $"logs/{i}.log", ["sha256"] = new string('a', 64) });
    }

    void Sql(string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, ServiceLog.FileName) }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Entries_chain_and_verify_and_the_head_is_the_newest_hash()
    {
        var log = Log();
        Assert.True(log.Created);
        Assert.Equal(0, log.Verify());
        Fill(log, 5);
        Assert.Equal(5, log.Verify());
        var rows = log.Entries(T0, T0.AddHours(1));
        Assert.Equal(5, rows.Count);
        Assert.Equal("logs/3.log", rows[3].Data["path"]);
        var head = log.Append(T0.AddMinutes(9), "s1", "anita", "service", "session_end", "end", new Dictionary<string, string>());
        Assert.Equal(head.Hash, log.Head);
        var again = Log();
        Assert.False(again.Created);
        Assert.Equal(6, again.Verify());   // a second instance (after a restart) reads the same chain
    }

    [Fact]
    public void An_edited_row_is_found()
    {
        var log = Log();
        Fill(log, 4);
        Sql("UPDATE service_log SET detail = 'nothing happened' WHERE id = 2");
        Assert.Throws<DatabaseTamperingException>(() => log.Verify());
    }

    [Fact]
    public void Deleting_the_newest_rows_is_found()
    {
        var log = Log();
        Fill(log, 4);
        Sql("DELETE FROM service_log WHERE id >= 3");
        Assert.Contains("missing", Assert.Throws<DatabaseTamperingException>(() => log.Verify()).Message);
    }

    [Fact]
    public void An_edited_or_missing_head_file_is_found()
    {
        var log = Log();
        Fill(log, 3);
        var path = Path.Combine(_dir, ServiceLog.HeadFileName);
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("service_log 3 ", "service_log 2 "));
        Assert.Throws<DatabaseTamperingException>(() => log.Verify());
        File.Delete(path);
        Assert.Throws<DatabaseTamperingException>(() => log.Verify());
    }

    [Fact]
    public void A_stop_between_the_commit_and_the_head_write_is_accepted()
    {
        var log = Log();
        Fill(log, 3);
        var path = Path.Combine(_dir, ServiceLog.HeadFileName);
        var older = File.ReadAllText(path);
        log.Append(T0.AddMinutes(5), "s1", "anita", "service", "file_read", "late", new Dictionary<string, string>());
        File.WriteAllText(path, older);   // the head still names row 3; row 4 came after it
        Assert.Equal(4, log.Verify());
    }

    [Fact]
    public void Another_key_cannot_read_or_extend_it()
    {
        Fill(Log(), 2);
        Assert.Throws<DatabaseTamperingException>(() => new ServiceLog(_dir, RandomNumberGenerator.GetBytes(32)).Verify());
        Assert.Throws<ArgumentException>(() => new ServiceLog(_dir, new byte[8]));
    }

    [Fact]
    public void The_results_ledger_is_not_touched()
    {
        Fill(Log(), 2);
        Assert.False(File.Exists(Path.Combine(_dir, "results.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "results.ledger-head")));
    }
}
