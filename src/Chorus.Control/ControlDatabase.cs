using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Chorus.Control;

/// <summary>Single local SQLite authority database. Clients access it over HTTP, never a network file share.</summary>
public sealed class ControlDatabase
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    public ControlDatabase(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS room_events(room_id TEXT NOT NULL, event_key TEXT NOT NULL, body TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(room_id,event_key));
            CREATE INDEX IF NOT EXISTS ix_room_events_order ON room_events(room_id,at,event_key);
            CREATE TABLE IF NOT EXISTS event_receipts(room_id TEXT NOT NULL, event_key TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(room_id,event_key));
            CREATE INDEX IF NOT EXISTS ix_event_receipts_ttl ON event_receipts(room_id,at);
            CREATE TABLE IF NOT EXISTS room_queue_counts(room_id TEXT PRIMARY KEY, pending INTEGER NOT NULL DEFAULT 0, receipts INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS checkpoints(room_id TEXT PRIMARY KEY, body TEXT NOT NULL, tick_id INTEGER NOT NULL, epoch INTEGER NOT NULL, checksum INTEGER NOT NULL, schema_version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL, detail TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }
    public T Read<T>(Func<Transaction, T> query)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        return query(new Transaction(db, transaction));
    }
    public T Write<T>(Func<Transaction, T> change)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var result = change(new Transaction(db, transaction));
        transaction.Commit();
        return result;
    }
    public sealed class Transaction(SqliteConnection db, SqliteTransaction transaction)
    {
        private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
        {
            var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }
        public T? Get<T>(string kind, string id)
        {
            using var cmd = Command("SELECT body FROM records WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id));
            return cmd.ExecuteScalar() is string body ? JsonSerializer.Deserialize<T>(body, Json) : default;
        }
        public List<T> List<T>(string kind)
        {
            using var cmd = Command("SELECT body FROM records WHERE kind=$k ORDER BY id", ("$k", kind));
            using var reader = cmd.ExecuteReader();
            List<T> result = [];
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
            return result;
        }
        public void Put<T>(string kind, string id, T value)
        {
            if (kind == "room" && value is RoomRecord room)
                value = (T)(object)(room with { Inbox = null, Receipts = null, Snapshot = null });
            using var cmd = Command("INSERT INTO records(kind,id,body) VALUES($k,$i,$b) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body", ("$k", kind), ("$i", id), ("$b", JsonSerializer.Serialize(value, Json)));
            cmd.ExecuteNonQuery();
        }
        public int PendingCount(string roomId)
        {
            using var cmd = Command("SELECT pending FROM room_queue_counts WHERE room_id=$r", ("$r", roomId));
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        public List<Chorus.Protocol.Models.AudienceEvent> PendingEvents(string roomId, int limit)
        {
            using var cmd = Command("SELECT body FROM room_events WHERE room_id=$r ORDER BY at,event_key LIMIT $n", ("$r", roomId), ("$n", Math.Clamp(limit,1,8000)));
            using var reader = cmd.ExecuteReader();
            List<Chorus.Protocol.Models.AudienceEvent> result = [];
            while(reader.Read()) result.Add(JsonSerializer.Deserialize<Chorus.Protocol.Models.AudienceEvent>(reader.GetString(0), Json)!);
            return result;
        }
        public bool AddEvent(string roomId, string key, Chorus.Protocol.Models.AudienceEvent value, long at)
        {
            using var receipt = Command("INSERT OR IGNORE INTO event_receipts(room_id,event_key,at) VALUES($r,$k,$a)", ("$r",roomId), ("$k",key), ("$a",at));
            if(receipt.ExecuteNonQuery() == 0) return false;
            using var bump = Command("INSERT INTO room_queue_counts(room_id,receipts) VALUES($r,1) ON CONFLICT(room_id) DO UPDATE SET receipts=receipts+1",("$r",roomId)); bump.ExecuteNonQuery();
            using var count = Command("SELECT receipts FROM room_queue_counts WHERE room_id=$r", ("$r",roomId));
            if(Convert.ToInt32(count.ExecuteScalar()) > 100000)
            {
                using var undo = Command("DELETE FROM event_receipts WHERE room_id=$r AND event_key=$k", ("$r",roomId), ("$k",key)); undo.ExecuteNonQuery();
                using var dec = Command("UPDATE room_queue_counts SET receipts=receipts-1 WHERE room_id=$r",("$r",roomId)); dec.ExecuteNonQuery();
                throw new ControlConflictException("Room receipt capacity reached; retry after dedup window.");
            }
            using var cmd = Command("INSERT INTO room_events(room_id,event_key,body,at) VALUES($r,$k,$b,$a)", ("$r",roomId), ("$k",key), ("$b",JsonSerializer.Serialize(value,Json)), ("$a",at));
            cmd.ExecuteNonQuery();
            using var inc=Command("UPDATE room_queue_counts SET pending=pending+1 WHERE room_id=$r",("$r",roomId));inc.ExecuteNonQuery(); return true;
        }
        public bool ReceiptExists(string roomId,string key)
        {
            using var cmd=Command("SELECT 1 FROM event_receipts WHERE room_id=$r AND event_key=$k",("$r",roomId),("$k",key)); return cmd.ExecuteScalar() is not null;
        }
        public void PruneReceipts(string roomId,long cutoff)
        {
            using var cmd=Command("DELETE FROM event_receipts WHERE room_id=$r AND at<$c AND NOT EXISTS(SELECT 1 FROM room_events e WHERE e.room_id=event_receipts.room_id AND e.event_key=event_receipts.event_key)",("$r",roomId),("$c",cutoff)); var removed=cmd.ExecuteNonQuery();
            if(removed>0){using var dec=Command("UPDATE room_queue_counts SET receipts=MAX(0,receipts-$n) WHERE room_id=$r",("$n",removed),("$r",roomId));dec.ExecuteNonQuery();}
        }
        public void PutCheckpoint(string roomId, JsonElement state, long tick, long epoch, long checksum, int schema)
        {
            using var cmd=Command("INSERT INTO checkpoints(room_id,body,tick_id,epoch,checksum,schema_version) VALUES($r,$b,$t,$e,$c,$s) ON CONFLICT(room_id) DO UPDATE SET body=excluded.body,tick_id=excluded.tick_id,epoch=excluded.epoch,checksum=excluded.checksum,schema_version=excluded.schema_version",("$r",roomId),("$b",state.GetRawText()),("$t",tick),("$e",epoch),("$c",checksum),("$s",schema)); cmd.ExecuteNonQuery();
        }
        public (JsonElement State,long Tick,long Epoch,long Checksum,int Schema)? GetCheckpoint(string roomId)
        {
            using var cmd=Command("SELECT body,tick_id,epoch,checksum,schema_version FROM checkpoints WHERE room_id=$r",("$r",roomId));
            using var r=cmd.ExecuteReader(); if(!r.Read()) return null;
            return (JsonDocument.Parse(r.GetString(0)).RootElement.Clone(),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3),r.GetInt32(4));
        }
        public void DeleteEvents(string roomId,IReadOnlyList<string> keys)
        {
            var removed=0;
            foreach(var key in keys){using var cmd=Command("DELETE FROM room_events WHERE room_id=$r AND event_key=$k",("$r",roomId),("$k",key));removed+=cmd.ExecuteNonQuery();}
            if(removed>0){using var dec=Command("UPDATE room_queue_counts SET pending=MAX(0,pending-$n) WHERE room_id=$r",("$n",removed),("$r",roomId));dec.ExecuteNonQuery();}
        }
        public void Delete(string kind, string id)
        {
            using var cmd = Command("DELETE FROM records WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id));
            cmd.ExecuteNonQuery();
        }
        public void Audit(long now, string actor, string action, string target, object detail)
        {
            using var cmd = Command("INSERT INTO audit(at,actor,action,target,detail) VALUES($n,$a,$x,$t,$d)",
                ("$n", now), ("$a", actor), ("$x", action), ("$t", target), ("$d", JsonSerializer.Serialize(detail, Json)));
            cmd.ExecuteNonQuery();
        }
        public List<AuditRecord> Audits(int limit)
        {
            using var cmd = Command("SELECT id,at,actor,action,target,detail FROM audit ORDER BY id DESC LIMIT $n", ("$n", Math.Clamp(limit, 1, 500)));
            using var r = cmd.ExecuteReader();
            List<AuditRecord> result = [];
            while (r.Read()) result.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5)));
            return result;
        }
    }
}
