using System.IO;
using Microsoft.Data.Sqlite;

namespace ATPanther.Core;

public sealed record LogEntry(long Id, long Timestamp, string Type, float RemainingMb, string Message);

/// <summary>SQLite-Port von AppDatabase/LogDao/LogEntry (Room).</summary>
public sealed class AppDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public AppDb()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPanther");
        Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "logs.db")}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS log_entries (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              timestamp INTEGER NOT NULL,
              type TEXT NOT NULL,
              remainingMb REAL NOT NULL DEFAULT 0,
              message TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_log_timestamp ON log_entries(timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Insert(string type, float remainingMb, string message)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO log_entries (timestamp, type, remainingMb, message) VALUES (@t,@y,@r,@m)";
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@y", type);
        cmd.Parameters.AddWithValue("@r", remainingMb);
        cmd.Parameters.AddWithValue("@m", message);
        cmd.ExecuteNonQuery();
    }

    public List<LogEntry> GetRecent(int limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,timestamp,type,remainingMb,message FROM log_entries ORDER BY timestamp DESC, id DESC LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<LogEntry>();
        while (r.Read()) list.Add(new LogEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), (float)r.GetDouble(3), r.GetString(4)));
        return list;
    }

    public List<LogEntry> GetAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,timestamp,type,remainingMb,message FROM log_entries ORDER BY timestamp ASC, id ASC";
        using var r = cmd.ExecuteReader();
        var list = new List<LogEntry>();
        while (r.Read()) list.Add(new LogEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), (float)r.GetDouble(3), r.GetString(4)));
        return list;
    }

    public void DeleteOlderThan(long maxAgeMs)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM log_entries WHERE timestamp < @m";
        cmd.Parameters.AddWithValue("@m", maxAgeMs);
        cmd.ExecuteNonQuery();
    }

    public void DeleteBeyondLimit(int keep)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM log_entries WHERE id NOT IN (SELECT id FROM log_entries ORDER BY timestamp DESC, id DESC LIMIT @k)";
        cmd.Parameters.AddWithValue("@k", keep);
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM log_entries";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Clear()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM log_entries";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
