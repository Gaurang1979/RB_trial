using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VisionReports;

/// <summary>One entry of the remote service user log.</summary>
public sealed record ServiceLogRow(long Id, DateTime TimeUtc, string Session, string User, string Role, string Kind, string Detail,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// The remote service user log (owner, 2026-11-04: every remote action in the line's tamper-evident log;
/// docs/vision-system/REMOTE-SUPPORT.md). Its own file, service-log.db, and its own HMAC-SHA256 chain with the results ledger's
/// key and row hash (<see cref="DatabaseSecurity.ComputeRowHashV2"/>), with a head file of its own (service.ledger-head) that only
/// the key can write. Kept apart from results.db on purpose: nothing about the results ledger or its check at start changes.
/// <para>No ledger key: no service log, and the runtime offers no remote service (nothing remote may happen unrecorded).</para>
/// </summary>
public sealed class ServiceLog
{
    public const string FileName = "service-log.db", HeadFileName = "service.ledger-head";
    const string Table = "service_log", HeadMagic = "vs-service-head-v1";
    static readonly string[] Columns = ["time_utc", "session", "user", "role", "kind", "detail", "data"];
    readonly string _cs;
    readonly byte[] _key;
    readonly object _write = new();

    public ServiceLog(string dataFolder, byte[] ledgerKey)
    {
        if (ledgerKey is not { Length: >= DatabaseSecurity.MinKeyBytes }) throw new ArgumentException("the service log needs the ledger key");
        Directory.CreateDirectory(dataFolder);
        Path_ = Path.Combine(dataFolder, FileName);
        HeadPath = Path.Combine(dataFolder, HeadFileName);
        _key = ledgerKey;
        _cs = new SqliteConnectionStringBuilder { DataSource = Path_ }.ToString();
        using var c = Open();
        Exec(c, $"""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS {Table} (
                id INTEGER PRIMARY KEY AUTOINCREMENT, time_utc TEXT NOT NULL, session TEXT NOT NULL, user TEXT NOT NULL, role TEXT NOT NULL,
                kind TEXT NOT NULL, detail TEXT NOT NULL, data TEXT NOT NULL, chain_hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_service_time ON {Table}(time_utc);
            """);
        if (!File.Exists(HeadPath) && Latest(c, null).Id == 0)
        {
            Created = true;
            WriteHead(HeadEntry.None);
        }
    }

    /// <summary>True when this start found no service log and began a new one (the host records it: an earlier log deleted
    /// together with its head shows up as this).</summary>
    public bool Created { get; }

    public string Path_ { get; }
    public string HeadPath { get; }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string T(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>The values a row's hash covers, in column order (data as canonical JSON: keys sorted).</summary>
    static object?[] Values(DateTime timeUtc, string session, string user, string role, string kind, string detail, IReadOnlyDictionary<string, string> data) =>
        [T(timeUtc), session, user, role, kind, detail, JsonSerializer.Serialize(new SortedDictionary<string, string>(data.ToDictionary(), StringComparer.Ordinal))];

    /// <summary>Appends one entry to the chain and moves the head. Throws when it cannot be written: the caller then does not do
    /// what it was about to record.</summary>
    public HeadEntry Append(DateTime timeUtc, string session, string user, string role, string kind, string detail, IReadOnlyDictionary<string, string> data)
    {
        lock (_write)
        {
            using var c = Open();
            using var tx = c.BeginTransaction(deferred: false);
            var prev = Latest(c, tx);
            var values = Values(timeUtc, session, user, role, kind, detail, data);
            var hash = DatabaseSecurity.ComputeRowHashV2(prev.Hash, Table, values, _key);
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {Table} ({string.Join(',', Columns)},chain_hash) VALUES ({string.Join(',', Columns.Select((_, i) => "$v" + i))},$h)";
            for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$v" + i, values[i] ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.ExecuteNonQuery();
            long id;
            using (var last = c.CreateCommand()) { last.Transaction = tx; last.CommandText = "SELECT last_insert_rowid()"; id = (long)last.ExecuteScalar()!; }
            tx.Commit();
            var head = new HeadEntry(id, hash);
            WriteHead(head);
            return head;
        }
    }

    static HeadEntry Latest(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT id, chain_hash FROM {Table} ORDER BY id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? new HeadEntry(r.GetInt64(0), r.GetString(1)) : HeadEntry.None;
    }

    /// <summary>The newest entry's hash, the chain head the agent keeps outside the machine (relay, ERPNext).</summary>
    public string Head
    {
        get
        {
            using var c = Open();
            return Latest(c, null).Hash;
        }
    }

    // ---- head file: "vs-service-head-v1\nservice_log <id> <hash>\n<hmac>", written whole and renamed into place ----

    void WriteHead(HeadEntry head)
    {
        var body = string.Create(CultureInfo.InvariantCulture, $"{HeadMagic}\n{Table} {head.Id} {head.Hash}");
        var tmp = HeadPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            // flushed to the disk before the rename: after a power cut the head is the old one or the new one, never empty
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                f.Write(Encoding.UTF8.GetBytes(body + "\n" + Mac(body) + "\n"));
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, HeadPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    string Mac(string body) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    HeadEntry ReadHead()
    {
        var lines = File.ReadAllText(HeadPath).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 3 || lines[0] != HeadMagic)
            throw new DatabaseTamperingException("Service log tampering: its chain head file is not readable.");
        var p = lines[1].Split(' ');
        if (p.Length != 3 || p[0] != Table || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || p[2].Length != 64)
            throw new DatabaseTamperingException("Service log tampering: its chain head file is not readable.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac($"{lines[0]}\n{lines[1]}")), Encoding.ASCII.GetBytes(lines[2].Trim().ToLowerInvariant())))
            throw new DatabaseTamperingException("Service log tampering: its chain head file was changed.");
        return new HeadEntry(id, p[2].ToLowerInvariant());
    }

    /// <summary>
    /// Checks the whole chain: every row's hash from the one before, and the head file (written only with the key) naming a row
    /// that is there with that hash. A row after the head is accepted (a stop between the commit and the head write); a head
    /// row that is missing or changed means rows were deleted or edited. Returns the number of rows; throws
    /// <see cref="DatabaseTamperingException"/> otherwise.
    /// </summary>
    public int Verify()
    {
        if (!File.Exists(HeadPath)) throw new DatabaseTamperingException("Service log tampering: its chain head file is missing.");
        var head = ReadHead();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id,chain_hash,{string.Join(',', Columns)} FROM {Table} ORDER BY id ASC";
        using var r = cmd.ExecuteReader();
        var prev = DatabaseSecurity.GenesisHash;
        var rows = 0;
        var headSeen = head.Id == 0;
        while (r.Read())
        {
            var id = r.GetInt64(0);
            var stored = r.GetString(1);
            var values = Enumerable.Range(0, Columns.Length).Select(i => (object?)r.GetString(2 + i)).ToArray();
            var expected = DatabaseSecurity.ComputeRowHashV2(prev, Table, values, _key);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(stored)))
                throw new DatabaseTamperingException($"Service log tampering at row {id}: the row or one before it was changed or deleted.");
            if (id == head.Id)
            {
                if (stored != head.Hash) throw new DatabaseTamperingException($"Service log tampering at row {id}: it differs from the chain head.");
                headSeen = true;
            }
            prev = stored;
            rows++;
        }
        if (!headSeen) throw new DatabaseTamperingException($"Service log tampering: row {head.Id}, which the chain head names, is missing (newest rows deleted).");
        return rows;
    }

    /// <summary>The kind of the entry a batch correction is kept as (Runtime.Host BatchCorrections).</summary>
    public const string BatchCorrectionKind = "batch_correction";

    /// <summary>One approved correction of a batch's values.</summary>
    public sealed record BatchCorrection(DateTime TimeUtc, string Batch, IReadOnlyDictionary<string, (string Old, string New)> Changes, string By,
        string ApprovedBy, string Reason, string Entry);

    /// <summary>The approved corrections of one batch (or of every batch when null), oldest first (reports, current values).</summary>
    public List<BatchCorrection> BatchCorrections(string? batch = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id,time_utc,data FROM {Table} WHERE kind = $k ORDER BY id ASC";
        cmd.Parameters.AddWithValue("$k", BatchCorrectionKind);
        using var r = cmd.ExecuteReader();
        var list = new List<BatchCorrection>();
        while (r.Read())
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [];
            var name = d.GetValueOrDefault("batch", "");
            if (batch is not null && name != batch) continue;
            var changes = d.Keys.Where(k => k.StartsWith("new.", StringComparison.Ordinal)).Select(k => k[4..])
                .ToDictionary(f => f, f => (d.GetValueOrDefault("old." + f, ""), d["new." + f]));
            list.Add(new BatchCorrection(DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), name, changes,
                d.GetValueOrDefault("by", ""), d.GetValueOrDefault("approvedBy", ""), d.GetValueOrDefault("reason", ""),
                "S-" + r.GetInt64(0).ToString(CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>Entries between two times, oldest first (the Audit trail page, reports).</summary>
    public List<ServiceLogRow> Entries(DateTime fromUtc, DateTime toUtc, int limit = 2000)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id,{string.Join(',', Columns)} FROM {Table} WHERE time_utc >= $f AND time_utc <= $t ORDER BY id ASC LIMIT $n";
        cmd.Parameters.AddWithValue("$f", T(fromUtc));
        cmd.Parameters.AddWithValue("$t", T(toUtc));
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ServiceLogRow>();
        while (r.Read())
            list.Add(new ServiceLogRow(r.GetInt64(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7)) ?? []));
        return list;
    }
}
