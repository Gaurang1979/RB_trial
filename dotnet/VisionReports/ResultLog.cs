using System.Globalization;
using Microsoft.Data.Sqlite;
using VisionCore.Plc;

namespace VisionReports;

/// <summary>One camera inspection (one image).
/// PartId is the PLC's part number; PartText the part id as the PLC sent it (text, e.g. "A12-0042"); ImageId the
/// image's name (L01_C2_J045_261031-104512_000123, owner 2026-10-31), the same as its file and in the plant events.</summary>
public sealed record InspectionRow(long Id, DateTime TimeUtc, string Batch, string Job, int Camera, int? PartId, bool Ok,
                                   string? FailTool, string? Reason, string? Text1, string? Text2, double Ms, string? PartText = null,
                                   string? ImageId = null);

/// <summary>One product leaving the line, as the PLC reported it, with the reason note. ImageId: the image name of
/// the first camera that inspected it (any camera's name finds the part's images).</summary>
public sealed record PartRow(long Id, DateTime TimeUtc, string Batch, string Job, int PartId, bool Rejected, PartFlags Flags,
                             double? LengthMm, double? GapMm, int GroupSize, int GroupIndex, int CamAnswered, int CamOk,
                             string Category, string Reason, string? ImageId = null);

/// <summary>A line event: a condition that starts and ends (jam, line stop, alarm) or a one-off (batch start, settings).</summary>
public sealed record EventRow(long Id, DateTime TimeUtc, string Kind, bool Active, string Detail);

/// <summary>Parts in one UTC hour, for trends, with the inspections' average and longest time.</summary>
public sealed record HourTrend(DateTime HourUtc, int Count, int Ok, double AvgMs, double MaxMs);

/// <summary>What a ledger check saw (Req 17.10 MRG-08): chained rows by chain version, and rows written without a
/// ledger key, which are counted, not checked.</summary>
public sealed record LedgerReport(int V1Rows, int V2Rows, int Unchained);

public sealed record BatchRow(long Id, string Name, string Job, DateTime StartedUtc, DateTime? EndedUtc, string User, string Fields);

/// <summary>
/// Results log in SQLite (data/results.db): every inspection, every product leaving the line, line
/// events and batches. Feeds the reports. Times are UTC; reports show local time.
/// </summary>
public sealed class ResultLog
{
    private readonly string _cs;
    private readonly object _write = new();
    private readonly byte[]? _ledgerKey;

    // The chained columns in hash order (chain v2, Req 17.10 MRG-08): every field a row records, in the order the
    // writer binds them and the verifier reads them. Appending a column here changes the formula: that is a new
    // chain version, never an edit of v2.
    private static readonly string[] InspectionColumns =
        ["time_utc", "batch", "job", "camera", "part_id", "ok", "fail_tool", "reason", "text1", "text2", "ms", "part_text", "image_id"];
    private static readonly string[] PartColumns =
        ["time_utc", "batch", "job", "part_id", "rejected", "flags", "length_mm", "gap_mm", "group_size", "group_index",
         "cam_answered", "cam_ok", "category", "reason", "image_id"];

    /// <summary>Opens the log with the ledger key of this installation (<see cref="DatabaseSecurity.KeyFromEnvironment"/>).</summary>
    public ResultLog(string dataFolder) : this(dataFolder, DatabaseSecurity.KeyFromEnvironment()) { }

    /// <summary>Opens the log. Without a ledger key, rows are still written (no result is lost, the line is not
    /// stopped) but not chained, and a "ledger_off" event says so (Req 17.10 REQ-16).</summary>
    public ResultLog(string dataFolder, byte[]? ledgerKey)
    {
        Directory.CreateDirectory(dataFolder);
        Path_ = System.IO.Path.Combine(dataFolder, "results.db");
        _cs = new SqliteConnectionStringBuilder { DataSource = Path_ }.ToString();
        _ledgerKey = ledgerKey is { Length: >= DatabaseSecurity.MinKeyBytes } ? ledgerKey : null;
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS inspections (
                id INTEGER PRIMARY KEY AUTOINCREMENT, time_utc TEXT NOT NULL, batch TEXT NOT NULL, job TEXT NOT NULL,
                camera INTEGER NOT NULL, part_id INTEGER, ok INTEGER NOT NULL, fail_tool TEXT, reason TEXT,
                text1 TEXT, text2 TEXT, ms REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_insp_time ON inspections(time_utc);
            CREATE TABLE IF NOT EXISTS parts (
                id INTEGER PRIMARY KEY AUTOINCREMENT, time_utc TEXT NOT NULL, batch TEXT NOT NULL, job TEXT NOT NULL,
                part_id INTEGER NOT NULL, rejected INTEGER NOT NULL, flags INTEGER NOT NULL, length_mm REAL, gap_mm REAL,
                group_size INTEGER NOT NULL, group_index INTEGER NOT NULL, cam_answered INTEGER NOT NULL, cam_ok INTEGER NOT NULL,
                category TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_parts_time ON parts(time_utc);
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, time_utc TEXT NOT NULL, kind TEXT NOT NULL, active INTEGER NOT NULL, detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events_time ON events(time_utc);
            CREATE TABLE IF NOT EXISTS batches (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, job TEXT NOT NULL, started_utc TEXT NOT NULL,
                ended_utc TEXT, user TEXT NOT NULL, fields TEXT NOT NULL);
            """);
        // added later (a log made before has the columns added, empty): the PLC's part id as text, and the image name
        AddColumn(c, "inspections", "part_text");
        AddColumn(c, "inspections", "image_id");
        AddColumn(c, "inspections", "chain_hash");
        AddColumn(c, "parts", "image_id");
        AddColumn(c, "parts", "chain_hash");
        AddColumn(c, "inspections", "chain_ver");
        AddColumn(c, "parts", "chain_ver");

        if (_ledgerKey is null) AddEvent(DateTime.UtcNow, "ledger_off", false, "Results ledger key not set: rows are not chained");
        // a ledger with no chained row yet starts its chain head here (Req 17.10 MRG-08 item 5); a missing head while
        // chained rows exist is left missing, so the next check reports it
        else if (!File.Exists(HeadPath) && !HasChainedRows(c)) WriteHead(ChainHead.Empty);
    }

    /// <summary>The chain head file (Req 17.10 MRG-08 item 5): the newest chained row of each table, kept outside
    /// results.db and signed with the ledger key, so deleting the newest rows or blanking the newest hash is seen.</summary>
    public string HeadPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path_)!, "results.ledger-head");

    private static bool HasChainedRows(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM inspections WHERE chain_hash IS NOT NULL AND chain_hash != '') " +
                          "OR EXISTS(SELECT 1 FROM parts WHERE chain_hash IS NOT NULL AND chain_hash != '')";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>True when rows are chained (a ledger key is set).</summary>
    public bool LedgerOn => _ledgerKey is not null;

    /// <summary>The newest chained row (id and hash), read inside the reader's or writer's transaction (never kept in
    /// memory, so a second program writing the same file cannot leave it stale).</summary>
    private static HeadEntry GetLatest(SqliteConnection c, SqliteTransaction tx, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT id, chain_hash FROM {table} WHERE chain_hash IS NOT NULL AND chain_hash != '' ORDER BY id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? new HeadEntry(r.GetInt64(0), r.GetString(1).ToLowerInvariant()) : HeadEntry.None;
    }

    private static string Other(string table) => table == "inspections" ? "parts" : "inspections";

    private static void AddColumn(SqliteConnection c, string table, string column)
    {
        using var cols = c.CreateCommand();
        cols.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(cols.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            try { Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} TEXT"); }
            catch (SqliteException e) when (e.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { /* another program added it first */ }
    }

    public string Path_ { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string T(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    private static DateTime P(string s) => DateTime.SpecifyKind(DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private void Insert(string sql, params (string, object?)[] args)
    {
        lock (_write)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddInspection(InspectionRow r) => Append("inspections", InspectionColumns,
        [T(r.TimeUtc), r.Batch, r.Job, r.Camera, r.PartId, r.Ok ? 1 : 0, r.FailTool, r.Reason, r.Text1, r.Text2, r.Ms, r.PartText, r.ImageId]);

    public void AddPart(PartRow r) => Append("parts", PartColumns,
        [T(r.TimeUtc), r.Batch, r.Job, r.PartId, r.Rejected ? 1 : 0, (int)r.Flags, r.LengthMm, r.GapMm, r.GroupSize, r.GroupIndex,
         r.CamAnswered, r.CamOk, r.Category, r.Reason, r.ImageId]);

    /// <summary>Appends one row, chained (v2) when a ledger key is set (Req 17.10 MRG-08). The write transaction
    /// (BEGIN IMMEDIATE) holds the file's write lock from reading the chain head to the commit, so every program
    /// writing this results.db (runtime, Vision Studio, a second station) appends to the one chain in row order.</summary>
    private void Append(string table, string[] columns, object?[] values)
    {
        lock (_write)
        {
            using var c = Open();
            using var tx = c.BeginTransaction(deferred: false);
            string? hash = null, ver = null;
            HeadEntry prev = HeadEntry.None, other = HeadEntry.None;
            if (_ledgerKey is not null)
            {
                prev = GetLatest(c, tx, table);
                other = GetLatest(c, tx, Other(table));
                hash = DatabaseSecurity.ComputeRowHashV2(prev.Hash, table, values, _ledgerKey);
                ver = DatabaseSecurity.ChainV2;
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {table} ({string.Join(',', columns)},chain_hash,chain_ver) VALUES " +
                              $"({string.Join(',', columns.Select((_, i) => "$v" + i))},$hash,$ver)";
            for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$v" + i, values[i] ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", (object?)ver ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            long id;
            using (var last = c.CreateCommand()) { last.Transaction = tx; last.CommandText = "SELECT last_insert_rowid()"; id = (long)last.ExecuteScalar()!; }
            tx.Commit();
            // the head moves after the commit, so it never names a row the file does not have; a head left behind
            // (a crash here, a failed write) is an older row of the chain, which the check accepts
            if (hash is not null) MoveHead(table, new HeadEntry(id, hash), other, prev.Id > 0 || other.Id > 0);
        }
    }

    /// <summary>Moves the chain head on to this append's row, keeping the newer entry where another program wrote
    /// the head meanwhile. A head that is missing while chained rows existed, or that this key did not write, is
    /// left as found: the next check reports it (Req 17.10 MRG-08 item 5).</summary>
    private void MoveHead(string table, HeadEntry mine, HeadEntry other, bool hadChained)
    {
        ChainHead head;
        try
        {
            if (File.Exists(HeadPath))
            {
                var old = DatabaseSecurity.ParseHead(File.ReadAllText(HeadPath), _ledgerKey!);
                head = old.With(table, Newer(old.For(table), mine)).With(Other(table), Newer(old.For(Other(table)), other));
            }
            else if (hadChained) return;
            else head = ChainHead.Empty.With(table, mine).With(Other(table), other);
        }
        catch (Exception e) when (e is DatabaseTamperingException or IOException or UnauthorizedAccessException) { return; }
        WriteHead(head);
    }

    private static HeadEntry Newer(HeadEntry a, HeadEntry b) => b.Id >= a.Id ? b : a;

    // written whole and renamed into place, so a reader never sees half a head; a failed write leaves the old head
    private void WriteHead(ChainHead head)
    {
        var tmp = HeadPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, DatabaseSecurity.FormatHead(head, _ledgerKey!));
            File.Move(tmp, HeadPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Audits the entire cryptographic ledger chain (REQ-16 / PROT-31).
    /// Detects out-of-band record tampering, row modifications, or unauthorized deletions.
    /// Throws <see cref="DatabaseTamperingException"/> if any hash verification fails. False when no ledger key is
    /// set, so nothing can be checked.
    /// </summary>
    public bool VerifyLedgerIntegrity() => VerifyLedger() is not null;

    /// <summary>Checks both chains row by row in one read snapshot, each row by its own chain version (Req 17.10
    /// MRG-08), against the chain head kept outside the file (MRG-08 item 5), and says what it saw. Null when no
    /// ledger key is set. Throws <see cref="DatabaseTamperingException"/> on a hash that does not match, a v1 row
    /// after a v2 row, an unknown version, a chain head that is missing or not this key's, or a head row that is
    /// gone or whose hash was removed or changed (the newest rows deleted).</summary>
    public LedgerReport? VerifyLedger()
    {
        if (_ledgerKey is null) return null;
        // the head is read before the snapshot: it only ever names committed rows, so the snapshot has them
        ChainHead? head = File.Exists(HeadPath) ? DatabaseSecurity.ParseHead(File.ReadAllText(HeadPath), _ledgerKey) : null;
        var report = Verify(head);
        if (head is null && report.V1Rows + report.V2Rows > 0)
            throw new DatabaseTamperingException($"Database tampering detected: the results ledger's chain head ({System.IO.Path.GetFileName(HeadPath)}) is missing.");
        return report;
    }

    /// <summary>Checks the chain without the head and then writes the head from the file's newest chained rows: for
    /// a results.db chained before the head was kept (commissioning, after an upgrade). Never for a log whose check
    /// failed: a chain that does not verify throws and the head is not written. Null when no ledger key is set.</summary>
    public LedgerReport? AnchorHead()
    {
        if (_ledgerKey is null) return null;
        lock (_write)
        {
            using var c = Open();
            using var tx = c.BeginTransaction(deferred: false);   // no writer appends between the check and the head
            var report = Verify(null, c, tx);
            WriteHead(new ChainHead(GetLatest(c, tx, "inspections"), GetLatest(c, tx, "parts")));
            tx.Commit();
            return report;
        }
    }

    private LedgerReport Verify(ChainHead? head)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: true);
        return Verify(head, c, tx);
    }

    private LedgerReport Verify(ChainHead? head, SqliteConnection c, SqliteTransaction tx)
    {
        var (v1a, v2a, ua) = VerifyChain(c, tx, "inspections", "inspection", InspectionColumns, _ledgerKey!, head?.Inspections);
        var (v1b, v2b, ub) = VerifyChain(c, tx, "parts", "part", PartColumns, _ledgerKey!, head?.Parts);
        return new LedgerReport(v1a + v1b, v2a + v2b, ua + ub);
    }

    private static (int V1, int V2, int Unchained) VerifyChain(SqliteConnection c, SqliteTransaction tx, string table, string what,
                                                               string[] columns, byte[] key, HeadEntry? head)
    {
        // the head's row must still be in the chain with the head's hash; rows after it are chained by the key
        var headSeen = head is null || head.Id == 0;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT id,chain_hash,chain_ver,{string.Join(',', columns)} FROM {table} ORDER BY id ASC";
        using var r = cmd.ExecuteReader();
        var prev = DatabaseSecurity.GenesisHash;
        int v1 = 0, v2 = 0, unchained = 0;
        var values = new object?[columns.Length];
        while (r.Read())
        {
            var id = r.GetInt64(0);
            var stored = r.IsDBNull(1) ? "" : r.GetString(1);
            if (head is not null && id == head.Id) headSeen = string.Equals(stored, head.Hash, StringComparison.OrdinalIgnoreCase);
            if (stored.Length == 0) { unchained++; continue; } // written without a ledger key (MRG-04): the chain links over it
            var ver = r.IsDBNull(2) ? "" : r.GetString(2);
            for (var i = 0; i < columns.Length; i++) values[i] = r.IsDBNull(3 + i) ? null : r.GetValue(3 + i);
            string expected;
            if (ver == DatabaseSecurity.ChainV2)
            {
                expected = DatabaseSecurity.ComputeRowHashV2(prev, table, values, key);
                v2++;
            }
            else if (ver.Length == 0)
            {
                if (v2 > 0) throw new DatabaseTamperingException($"Database tampering detected at {what} row ID {id}. Chain version went back from v2 to v1!");
                expected = table == "inspections"
                    ? DatabaseSecurity.ComputeInspectionHash(prev, (string)values[0]!, (string)values[1]!, (string)values[2]!,
                        Convert.ToInt32(values[3], CultureInfo.InvariantCulture),
                        values[4] is null ? null : Convert.ToInt32(values[4], CultureInfo.InvariantCulture),
                        Convert.ToInt64(values[5], CultureInfo.InvariantCulture) == 1, key)
                    : DatabaseSecurity.ComputePartHash(prev, (string)values[0]!, (string)values[1]!, (string)values[2]!,
                        Convert.ToInt32(values[3], CultureInfo.InvariantCulture), Convert.ToInt64(values[4], CultureInfo.InvariantCulture) == 1, key);
                v1++;
            }
            else throw new DatabaseTamperingException($"Database tampering detected at {what} row ID {id}. Unknown chain version!");
            if (!string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase))
                throw new DatabaseTamperingException($"Database tampering detected at {what} row ID {id}. Cryptographic hash chain broken!");
            prev = stored;
        }
        if (!headSeen)
            throw new DatabaseTamperingException($"Database tampering detected at {what} row ID {head!.Id}: the newest row the chain head names is missing or its hash was removed or changed (newest rows deleted).");
        return (v1, v2, unchained);
    }

    public void AddEvent(DateTime timeUtc, string kind, bool active, string detail) => Insert(
        "INSERT INTO events (time_utc,kind,active,detail) VALUES ($t,$k,$a,$d)",
        ("$t", T(timeUtc)), ("$k", kind), ("$a", active ? 1 : 0), ("$d", detail));

    public void StartBatch(string name, string job, string user, string fields, DateTime timeUtc)
    {
        lock (_write)
        {
            using var c = Open();
            using var end = c.CreateCommand();
            end.CommandText = "UPDATE batches SET ended_utc = $t WHERE ended_utc IS NULL";
            end.Parameters.AddWithValue("$t", T(timeUtc));
            end.ExecuteNonQuery();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO batches (name,job,started_utc,user,fields) VALUES ($n,$j,$t,$u,$f)";
            cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$j", job);
            cmd.Parameters.AddWithValue("$t", T(timeUtc)); cmd.Parameters.AddWithValue("$u", user); cmd.Parameters.AddWithValue("$f", fields);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- queries (time range [from, to), optional batch name)

    private List<TRow> Query<TRow>(string sql, DateTime from, DateTime to, string? batch, Func<SqliteDataReader, TRow> map, int limit = int.MaxValue)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql + " WHERE time_utc >= $f AND time_utc < $t" + (batch is null ? "" : " AND batch = $b") + " ORDER BY time_utc LIMIT $n";
        cmd.Parameters.AddWithValue("$f", T(from)); cmd.Parameters.AddWithValue("$t", T(to)); cmd.Parameters.AddWithValue("$n", limit);
        if (batch is not null) cmd.Parameters.AddWithValue("$b", batch);
        using var rd = cmd.ExecuteReader();
        var list = new List<TRow>();
        while (rd.Read()) list.Add(map(rd));
        return list;
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public List<InspectionRow> Inspections(DateTime from, DateTime to, string? batch = null, int limit = int.MaxValue) => Query(
        "SELECT id,time_utc,batch,job,camera,part_id,ok,fail_tool,reason,text1,text2,ms,part_text,image_id FROM inspections", from, to, batch,
        r => new InspectionRow(r.GetInt64(0), P(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt32(6) != 0, S(r, 7), S(r, 8), S(r, 9), S(r, 10), r.GetDouble(11), S(r, 12), S(r, 13)), limit);

    public List<PartRow> Parts(DateTime from, DateTime to, string? batch = null, int limit = int.MaxValue) => Query(
        "SELECT id,time_utc,batch,job,part_id,rejected,flags,length_mm,gap_mm,group_size,group_index,cam_answered,cam_ok,category,reason,image_id FROM parts",
        from, to, batch,
        r => new PartRow(r.GetInt64(0), P(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5) != 0,
            (PartFlags)r.GetInt32(6), D(r, 7), D(r, 8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12),
            r.GetString(13), r.GetString(14), S(r, 15)), limit);

    public List<EventRow> Events(DateTime from, DateTime to) => Query(
        "SELECT id,time_utc,kind,active,detail FROM events", from, to, null,
        r => new EventRow(r.GetInt64(0), P(r.GetString(1)), r.GetString(2), r.GetInt32(3) != 0, r.GetString(4)));

    /// <summary>Parts per UTC hour in [from, to), for the browser dashboard's trends, counted as the shift
    /// report counts them (<see cref="ReportBuilder.Build"/>): from the PLC's part log when it has parts in the
    /// range, else one part per inspection (no PLC). Times are the inspections' average and longest. Aggregated
    /// in SQLite, so a week at line speed is a few hundred rows. FromParts says which was counted.</summary>
    public (List<HourTrend> Hours, bool FromParts) HourTrend(DateTime from, DateTime to)
    {
        using var c = Open();
        var fromParts = HasParts(c, from, to);
        var counts = Hours(c, fromParts
            ? "SELECT substr(time_utc,1,13), COUNT(*), COUNT(*) - SUM(rejected) FROM parts"
            : "SELECT substr(time_utc,1,13), COUNT(*), SUM(ok) FROM inspections", from, to,
            rd => (rd.GetInt32(1), rd.GetInt32(2)));
        var times = Hours(c, "SELECT substr(time_utc,1,13), AVG(ms), MAX(ms) FROM inspections", from, to,
            rd => (rd.GetDouble(1), rd.GetDouble(2)));
        var list = counts.Select(h => times.TryGetValue(h.Key, out var t)
                ? new HourTrend(h.Key, h.Value.Item1, h.Value.Item2, t.Item1, t.Item2)
                : new HourTrend(h.Key, h.Value.Item1, h.Value.Item2, 0, 0))
            .OrderBy(h => h.HourUtc).ToList();
        return (list, fromParts);
    }

    /// <summary>The most frequent NG reasons in [from, to), most first: the part log's categories (as in the
    /// shift report), else the failing tool of each NG inspection. Never the free reason text, which differs
    /// from part to part ("expected HWN937, found HWN881").</summary>
    public List<(string Reason, int Count)> NgReasons(DateTime from, DateTime to, int top = 10)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HasParts(c, from, to)
            ? "SELECT COALESCE(NULLIF(category,''), 'Not known'), COUNT(*) FROM parts WHERE time_utc >= $f AND time_utc < $t AND rejected = 1 GROUP BY 1 ORDER BY 2 DESC LIMIT $n"
            : "SELECT COALESCE(NULLIF(fail_tool,''), 'NOT OK'), COUNT(*) FROM inspections WHERE time_utc >= $f AND time_utc < $t AND ok = 0 GROUP BY 1 ORDER BY 2 DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$f", T(from)); cmd.Parameters.AddWithValue("$t", T(to)); cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, int)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetInt32(1)));
        return list;
    }

    /// <summary>Inspections in [from, to) whose read text, PLC part id or image name holds this text (any case), newest
    /// first, for the browser dashboard's "Find a part". The time index bounds the scan; the text is matched as written
    /// (% and _ are not wildcards).</summary>
    public List<InspectionRow> FindInspections(string text, DateTime from, DateTime to, int limit = 50)
    {
        var like = "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,time_utc,batch,job,camera,part_id,ok,fail_tool,reason,text1,text2,ms,part_text,image_id FROM inspections" +
            " WHERE time_utc >= $f AND time_utc < $t AND (text1 LIKE $q ESCAPE '\\' OR text2 LIKE $q ESCAPE '\\'" +
            " OR part_text LIKE $q ESCAPE '\\' OR image_id LIKE $q ESCAPE '\\') ORDER BY time_utc DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$f", T(from)); cmd.Parameters.AddWithValue("$t", T(to));
        cmd.Parameters.AddWithValue("$q", like); cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<InspectionRow>();
        while (r.Read())
            list.Add(new InspectionRow(r.GetInt64(0), P(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt32(6) != 0, S(r, 7), S(r, 8), S(r, 9), S(r, 10), r.GetDouble(11), S(r, 12), S(r, 13)));
        return list;
    }

    private static bool HasParts(SqliteConnection c, DateTime from, DateTime to)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM parts WHERE time_utc >= $f AND time_utc < $t)";
        cmd.Parameters.AddWithValue("$f", T(from)); cmd.Parameters.AddWithValue("$t", T(to));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static Dictionary<DateTime, TV> Hours<TV>(SqliteConnection c, string select, DateTime from, DateTime to, Func<SqliteDataReader, TV> map)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = select + " WHERE time_utc >= $f AND time_utc < $t GROUP BY 1";
        cmd.Parameters.AddWithValue("$f", T(from)); cmd.Parameters.AddWithValue("$t", T(to));
        using var rd = cmd.ExecuteReader();
        var d = new Dictionary<DateTime, TV>();
        while (rd.Read())
            d[DateTime.SpecifyKind(DateTime.ParseExact(rd.GetString(0), "yyyy-MM-dd HH", CultureInfo.InvariantCulture), DateTimeKind.Utc)] = map(rd);
        return d;
    }

    /// <summary>Batches that ran (partly) inside the range, newest first; all batches when no range.</summary>
    public List<BatchRow> Batches(DateTime? from = null, DateTime? to = null, int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,job,started_utc,ended_utc,user,fields FROM batches" +
            (from is null ? "" : " WHERE started_utc < $t AND (ended_utc IS NULL OR ended_utc >= $f)") + " ORDER BY started_utc DESC LIMIT $n";
        if (from is not null) { cmd.Parameters.AddWithValue("$f", T(from.Value)); cmd.Parameters.AddWithValue("$t", T(to!.Value)); }
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<BatchRow>();
        while (rd.Read())
            list.Add(new BatchRow(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), P(rd.GetString(3)),
                rd.IsDBNull(4) ? null : P(rd.GetString(4)), rd.GetString(5), rd.GetString(6)));
        return list;
    }

    /// <summary>The last state of a condition event before a time (to know a jam was already on at the start).</summary>
    public bool ActiveAt(string kind, DateTime timeUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT active FROM events WHERE kind = $k AND time_utc < $t ORDER BY time_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$t", T(timeUtc));
        return cmd.ExecuteScalar() is long v && v != 0;
    }
}
