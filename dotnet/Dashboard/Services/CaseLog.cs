using Microsoft.Data.Sqlite;

namespace BarcodeReading.Dashboard.Services;

/// <summary>One case (one top-camera frame) as logged.</summary>
public sealed record CaseRow(
    long Id, DateTime TimeUtc, string Product, int Expected, int Read, bool Complete,
    float TotalMs, float LabelBrightness, int Misses, string? ParentCode, string? User,
    bool Ok = false, int Duplicates = 0, bool ParentDuplicate = false);

/// <summary>One hour of one product, for the trends page.</summary>
public sealed record TrendRow(DateTime HourUtc, int Cases, int Complete, long Expected, long Read, double MedianMs);

/// <summary>An audit entry: who did what, when (product saves, approvals, starts, model promotions).</summary>
public sealed record AuditRow(long Id, DateTime TimeUtc, string User, string Action, string Detail);

/// <summary>
/// Case log and audit trail in SQLite (data/caselog.db). The line's own database still gets
/// the codes through your software; this log feeds the dashboard's trends and audit pages.
/// </summary>
public sealed class CaseLog
{
    private readonly string _cs;

    public CaseLog(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataFolder, "caselog.db") }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS cases (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                time_utc TEXT NOT NULL, product TEXT NOT NULL, expected INTEGER NOT NULL, read INTEGER NOT NULL,
                complete INTEGER NOT NULL, total_ms REAL NOT NULL, label_brightness REAL NOT NULL,
                misses INTEGER NOT NULL, parent_code TEXT, user TEXT);
            CREATE INDEX IF NOT EXISTS ix_cases_time ON cases(time_utc);
            CREATE TABLE IF NOT EXISTS codes (
                case_id INTEGER NOT NULL, text TEXT NOT NULL, step INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_codes_case ON codes(case_id);
            CREATE INDEX IF NOT EXISTS ix_codes_text ON codes(text);
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                time_utc TEXT NOT NULL, user TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL);
            """);
        // added for the duplicate check: accepted case, duplicate child codes, duplicate parent
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(cases)";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) have.Add(rd.GetString(1));
        }
        if (!have.Contains("ok")) Exec(c, "ALTER TABLE cases ADD COLUMN ok INTEGER");
        if (!have.Contains("duplicates")) Exec(c, "ALTER TABLE cases ADD COLUMN duplicates INTEGER NOT NULL DEFAULT 0");
        if (!have.Contains("parent_duplicate")) Exec(c, "ALTER TABLE cases ADD COLUMN parent_duplicate INTEGER NOT NULL DEFAULT 0");
    }

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

    public long AddCase(string product, DecodeResult r, string? parentCode, string? user,
                        bool ok = false, int duplicates = 0, bool parentDuplicate = false)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cases (time_utc, product, expected, read, complete, total_ms, label_brightness, misses, parent_code, user,
                               ok, duplicates, parent_duplicate)
            VALUES ($t, $p, $e, $r, $c, $ms, $lb, $m, $pc, $u, $ok, $dup, $pdup);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$e", r.Stats.Expected);
        cmd.Parameters.AddWithValue("$r", r.Stats.Codes);
        cmd.Parameters.AddWithValue("$c", r.Stats.Complete ? 1 : 0);
        cmd.Parameters.AddWithValue("$ms", r.Stats.TotalMs);
        cmd.Parameters.AddWithValue("$lb", r.Stats.LabelBrightness);
        cmd.Parameters.AddWithValue("$m", r.Misses.Count);
        cmd.Parameters.AddWithValue("$pc", (object?)parentCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", (object?)user ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$dup", duplicates);
        cmd.Parameters.AddWithValue("$pdup", parentDuplicate ? 1 : 0);
        long id = (long)cmd.ExecuteScalar()!;

        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO codes (case_id, text, step) VALUES ($id, $t, $s)";
        var pId = ins.Parameters.Add("$id", SqliteType.Integer);
        var pT = ins.Parameters.Add("$t", SqliteType.Text);
        var pS = ins.Parameters.Add("$s", SqliteType.Integer);
        foreach (var code in r.Codes)
        {
            pId.Value = id;
            pT.Value = code.Text;
            pS.Value = (int)code.Step;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    public IReadOnlyList<CaseRow> Recent(int limit = 100, string? product = null, long afterId = 0)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, time_utc, product, expected, read, complete, total_ms, label_brightness, misses, parent_code, user,
                   COALESCE(ok, complete), duplicates, parent_duplicate
            FROM cases WHERE id > $after AND ($p IS NULL OR product = $p) ORDER BY id DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$p", (object?)product ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 10_000));
        var list = new List<CaseRow>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new CaseRow(rd.GetInt64(0), DateTime.Parse(rd.GetString(1)).ToUniversalTime(), rd.GetString(2),
                rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5) == 1, rd.GetFloat(6), rd.GetFloat(7), rd.GetInt32(8),
                rd.IsDBNull(9) ? null : rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10),
                rd.GetInt32(11) == 1, rd.GetInt32(12), rd.GetInt32(13) == 1));
        }
        return list;
    }

    /// <summary>
    /// Child codes of the last accepted cases, oldest first, at most <paramref name="limit"/>:
    /// the duplicate memory is rebuilt from them at start. Cases logged before the duplicate
    /// check count as accepted when they were complete.
    /// </summary>
    public IReadOnlyList<string> RecentAcceptedChildCodes(int limit) =>
        Strings("""
            SELECT codes.text FROM codes JOIN cases ON cases.id = codes.case_id
            WHERE COALESCE(cases.ok, cases.complete) = 1 ORDER BY codes.rowid DESC LIMIT $n
            """, limit);

    /// <summary>Parent (case) codes of the last accepted cases, oldest first.</summary>
    public IReadOnlyList<string> RecentAcceptedParentCodes(int limit) =>
        Strings("""
            SELECT parent_code FROM cases
            WHERE COALESCE(ok, complete) = 1 AND parent_code IS NOT NULL ORDER BY id DESC LIMIT $n
            """, limit);

    private IReadOnlyList<string> Strings(string sql, int limit)
    {
        if (limit <= 0) return Array.Empty<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<string>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        list.Reverse();
        return list;
    }

    public IReadOnlyList<string> Codes(long caseId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT text FROM codes WHERE case_id = $id";
        cmd.Parameters.AddWithValue("$id", caseId);
        var list = new List<string>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    /// <summary>Per hour: cases, complete cases, codes expected and read, median decode time.</summary>
    public IReadOnlyList<TrendRow> Trend(string? product, DateTime fromUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT substr(time_utc, 1, 13), complete, expected, read, total_ms FROM cases
            WHERE time_utc >= $from AND ($p IS NULL OR product = $p) ORDER BY time_utc
            """;
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$p", (object?)product ?? DBNull.Value);
        var groups = new SortedDictionary<string, List<(bool Complete, int Expected, int Read, float Ms)>>();
        using (var rd = cmd.ExecuteReader())
        {
            while (rd.Read())
            {
                string hour = rd.GetString(0);
                if (!groups.TryGetValue(hour, out var g)) groups[hour] = g = new();
                g.Add((rd.GetInt32(1) == 1, rd.GetInt32(2), rd.GetInt32(3), rd.GetFloat(4)));
            }
        }
        return groups.Select(kv =>
        {
            var ms = kv.Value.Select(v => v.Ms).OrderBy(v => v).ToList();
            return new TrendRow(DateTime.Parse(kv.Key + ":00:00Z").ToUniversalTime(), kv.Value.Count,
                kv.Value.Count(v => v.Complete), kv.Value.Sum(v => (long)v.Expected),
                kv.Value.Sum(v => (long)(v.Expected > 0 ? Math.Min(v.Read, v.Expected) : v.Read)), ms[ms.Count / 2]);
        }).ToList();
    }

    public void Audit(string user, string action, string detail)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit (time_utc, user, action, detail) VALUES ($t, $u, $a, $d)";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$d", detail);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditRow> AuditTrail(int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, time_utc, user, action, detail FROM audit ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 10_000));
        var list = new List<AuditRow>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new AuditRow(rd.GetInt64(0), DateTime.Parse(rd.GetString(1)).ToUniversalTime(), rd.GetString(2), rd.GetString(3), rd.GetString(4)));
        return list;
    }
}
