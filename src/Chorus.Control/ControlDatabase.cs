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
            using var cmd = Command("INSERT INTO records(kind,id,body) VALUES($k,$i,$b) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body", ("$k", kind), ("$i", id), ("$b", JsonSerializer.Serialize(value, Json)));
            cmd.ExecuteNonQuery();
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
