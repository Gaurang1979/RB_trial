using Microsoft.Data.Sqlite;
using PlantEvents;

namespace StationAgent;

/// <summary>One stored event, ready to send.</summary>
public sealed record QueuedEvent(long Seq, long Ts, string Kind, string Json);

/// <summary>
/// The station's store-and-forward queue: an SQLite file on the line PC.
/// It gives every stored event the next number of the station's gap-free
/// sequence (1, 2, 3 …, kept across restarts), keeps it until the plant
/// server has accepted it, and forgets accepted events after a while.
/// Each queue file has its own id (<see cref="QueueId"/>), so the server
/// can tell a new file whose numbers start again at 1 from old events
/// sent twice. Safe to call from several threads. Every call holds the
/// lock only for a short, bounded piece of work, so a station thread that
/// stores an event never waits long behind the send loop.
/// </summary>
public sealed class EventQueue : IDisposable
{
    const int PruneChunk = 5000;

    readonly SqliteConnection _db;
    readonly object _lock = new();
    long _depth, _lastSeq, _dropped, _newestStored;
    bool _disposed;

    /// <summary>
    /// Opens or creates the queue file. Throws when the file cannot be opened
    /// (corrupt, locked, read-only); the line application catches that and
    /// runs without the plant link, with a station alarm, rather than not start.
    /// </summary>
    public EventQueue(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL");
        // FULL: an event the station has handed over survives a power cut.
        Exec("PRAGMA synchronous=FULL");
        // AUTOINCREMENT never reuses a number, even after old rows are deleted.
        // stored_ms is the agent's own clock when the row was written; ts is the
        // event's time and may come from elsewhere, so retention never uses it.
        Exec("""
            CREATE TABLE IF NOT EXISTS events(
              seq       INTEGER PRIMARY KEY AUTOINCREMENT,
              ts        INTEGER NOT NULL,
              stored_ms INTEGER NOT NULL,
              kind      TEXT    NOT NULL,
              json      TEXT    NOT NULL,
              sent      INTEGER NOT NULL DEFAULT 0)
            """);
        Exec("CREATE INDEX IF NOT EXISTS events_unsent ON events(sent, seq)");
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Exec($"INSERT OR IGNORE INTO meta(key, value) VALUES ('queue_id', '{Guid.NewGuid():N}')");
        QueueId = Scalar("SELECT value FROM meta WHERE key = 'queue_id'") as string ?? throw new InvalidOperationException("queue has no id");
        _depth = (long)Scalar("SELECT COUNT(*) FROM events WHERE sent = 0")!;
        _lastSeq = Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'events'") is long s ? s : 0;
        _dropped = long.TryParse(Scalar("SELECT value FROM meta WHERE key = 'dropped'") as string, out var d) ? d : 0;
        _newestStored = Scalar("SELECT MAX(stored_ms) FROM events") is long n ? n : 0;
    }

    /// <summary>This queue file's id; stamped on every event it stores.</summary>
    public string QueueId { get; }

    /// <summary>
    /// Ties the queue file to one station. The first call stores the station;
    /// a later call with another station throws, so two stations never share
    /// one sequence.
    /// </summary>
    public void BindStation(string station)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using (var c = Cmd("INSERT OR IGNORE INTO meta(key, value) VALUES ('station', $s)"))
            {
                c.Parameters.AddWithValue("$s", station);
                c.ExecuteNonQuery();
            }
            var bound = Scalar("SELECT value FROM meta WHERE key = 'station'") as string;
            if (bound != station) throw new InvalidOperationException($"queue file belongs to station '{bound}', not '{station}'");
        }
    }

    /// <summary>
    /// Stores the event with the next sequence number and this queue's id, and
    /// returns it as stored. The number and the row are written in one
    /// transaction, so a failure or a crash leaves no gap.
    /// </summary>
    public T Enqueue<T>(T e, long storedMs) where T : PlantEvent
    {
        if (e is Heartbeat) throw new ArgumentException("heartbeats are sent live, never stored", nameof(e));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using var ins = Cmd("INSERT INTO events(ts, stored_ms, kind, json) VALUES ($ts, $st, $kind, '')", tx);
            ins.Parameters.AddWithValue("$ts", e.Ts);
            ins.Parameters.AddWithValue("$st", storedMs);
            ins.Parameters.AddWithValue("$kind", e.Kind);
            ins.ExecuteNonQuery();
            using var id = Cmd("SELECT last_insert_rowid()", tx);
            var seq = (long)id.ExecuteScalar()!;
            var stored = e with { Seq = seq, QueueId = QueueId };
            using var upd = Cmd("UPDATE events SET json = $json WHERE seq = $seq", tx);
            upd.Parameters.AddWithValue("$json", PlantEventJson.Serialize(stored));
            upd.Parameters.AddWithValue("$seq", seq);
            upd.ExecuteNonQuery();
            tx.Commit();
            _lastSeq = seq;
            _depth++;
            if (storedMs > _newestStored) _newestStored = storedMs;
            return stored;
        }
    }

    /// <summary>
    /// Stores several events in one transaction (one disk flush for all), each
    /// with the next sequence number; all or none are stored.
    /// </summary>
    public int EnqueueMany(IReadOnlyList<PlantEvent> events, long storedMs)
    {
        if (events.Any(e => e is Heartbeat)) throw new ArgumentException("heartbeats are sent live, never stored", nameof(events));
        if (events.Count == 0) return 0;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _db.BeginTransaction();
            using var ins = Cmd("INSERT INTO events(ts, stored_ms, kind, json) VALUES ($ts, $st, $kind, $json)", tx);
            var pTs = ins.Parameters.Add("$ts", SqliteType.Integer);
            ins.Parameters.AddWithValue("$st", storedMs);
            var pKind = ins.Parameters.Add("$kind", SqliteType.Text);
            var pJson = ins.Parameters.Add("$json", SqliteType.Text);
            // AUTOINCREMENT gives the next numbers in order inside the transaction, so each is known before its row.
            var seq = _lastSeq;
            foreach (var e in events)
            {
                seq++;
                pTs.Value = e.Ts;
                pKind.Value = e.Kind;
                pJson.Value = PlantEventJson.Serialize(e with { Seq = seq, QueueId = QueueId });
                ins.ExecuteNonQuery();
            }
            using var id = Cmd("SELECT last_insert_rowid()", tx);
            if ((long)id.ExecuteScalar()! != seq) throw new InvalidOperationException("the queue's numbering moved during a write");
            tx.Commit();
            _lastSeq = seq;
            _depth += events.Count;
            if (storedMs > _newestStored) _newestStored = storedMs;
            return events.Count;
        }
    }

    /// <summary>The oldest events the server has not accepted, in sequence order.</summary>
    public IReadOnlyList<QueuedEvent> Peek(int max)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var c = Cmd("SELECT seq, ts, kind, json FROM events WHERE sent = 0 ORDER BY seq LIMIT $max");
            c.Parameters.AddWithValue("$max", max);
            using var r = c.ExecuteReader();
            var list = new List<QueuedEvent>();
            while (r.Read()) list.Add(new QueuedEvent(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    /// <summary>The server stored every event up to and including <paramref name="seq"/>.</summary>
    public void MarkSent(long seq)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var c = Cmd("UPDATE events SET sent = 1 WHERE sent = 0 AND seq <= $seq");
            c.Parameters.AddWithValue("$seq", seq);
            _depth -= c.ExecuteNonQuery();
        }
    }

    /// <summary>Events stored and not yet accepted (kept as a running count).</summary>
    public long Depth() { lock (_lock) return _depth; }

    /// <summary>The last sequence number given out (0 before the first event).</summary>
    public long LastSeq() { lock (_lock) return _lastSeq; }

    /// <summary>Unsent events dropped by <see cref="Prune"/>, in the life of this queue file.</summary>
    public long Dropped() { lock (_lock) return _dropped; }

    /// <summary>
    /// Deletes accepted events stored more than <paramref name="keepSent"/>
    /// ago and, when <paramref name="dropUnsent"/>, unsent events stored more
    /// than <paramref name="retention"/> ago. Times are the agent's own clock
    /// at storing; nothing is deleted while the clock reads earlier than the
    /// newest stored row (the clock was set back). Works in small pieces,
    /// releasing the lock between them. Returns the unsent events dropped.
    /// </summary>
    public long Prune(long nowMs, TimeSpan keepSent, TimeSpan retention, bool dropUnsent)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (nowMs < _newestStored) return 0;
        }
        DeleteInChunks("sent = 1", nowMs - (long)keepSent.TotalMilliseconds, counts: false);
        return dropUnsent ? DeleteInChunks("sent = 0", nowMs - (long)retention.TotalMilliseconds, counts: true) : 0;
    }

    long DeleteInChunks(string which, long before, bool counts)
    {
        long total = 0;
        while (true)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var tx = _db.BeginTransaction();
                // Oldest first by seq (the primary key); stored_ms rises with seq.
                using var c = Cmd($"DELETE FROM events WHERE seq IN (SELECT seq FROM events WHERE {which} AND stored_ms < $t ORDER BY seq LIMIT {PruneChunk})", tx);
                c.Parameters.AddWithValue("$t", before);
                var n = c.ExecuteNonQuery();
                if (counts && n > 0)
                {
                    using var m = Cmd("INSERT INTO meta(key, value) VALUES ('dropped', $v) ON CONFLICT(key) DO UPDATE SET value = $v", tx);
                    m.Parameters.AddWithValue("$v", (_dropped + n).ToString());
                    m.ExecuteNonQuery();
                }
                tx.Commit();
                if (counts) { _dropped += n; _depth -= n; }
                total += n;
                if (n < PruneChunk) return total;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }

    void Exec(string sql) { using var c = Cmd(sql); c.ExecuteNonQuery(); }
    object? Scalar(string sql) { using var c = Cmd(sql); return c.ExecuteScalar(); }

    SqliteCommand Cmd(string sql, SqliteTransaction? tx = null)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        c.Transaction = tx;
        return c;
    }
}
