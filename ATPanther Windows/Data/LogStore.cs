using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATPanther.Data;

/// <summary>
/// Pendant zu <c>data/LogEntry.kt</c> (Room-Entity <c>log_entries</c>):
/// id (autogeneriert), timestamp (ms), type ("CHECK" | "BOOKING"),
/// remainingMb (Default 0, -1 = unbekannt), message.
/// </summary>
public sealed class LogEntry
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "CHECK";
    [JsonPropertyName("remainingMb")] public float RemainingMb { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Speicher für den Verlauf. Android nutzt Room/SQLite
/// (<c>data/LogDao.kt</c>, <c>data/AppDatabase.kt</c>); hier eine atomar
/// geschriebene JSON-Datei mit denselben Operationen und denselben
/// Löschregeln – Begründung in PARITY.md 6.2.
/// </summary>
public sealed class LogStore
{
    /// <summary>MON:49 MAX_LOG_ROWS – harte Obergrenze der Tabelle.</summary>
    public const int MaxLogRows = 5000;

    /// <summary>UI:55 LOG_UI_LIMIT – die UI lädt nur die letzten N Einträge.</summary>
    public const int UiLimit = 200;

    /// <summary>MON:347 Aufbewahrung: 7 Tage.</summary>
    public const long MaxAgeMs = 7 * 24 * 3600_000L;

    private readonly object gate = new();
    private readonly string path;
    private List<LogEntry> entries = new();
    private long nextId = 1;

    public LogStore(string path)
    {
        this.path = path;
        Load();
    }

    private sealed class Snapshot
    {
        [JsonPropertyName("nextId")] public long NextId { get; set; } = 1;
        [JsonPropertyName("entries")] public List<LogEntry> Entries { get; set; } = new();
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>@Insert – id und timestamp-Default wie in LogEntry.kt:8–9.</summary>
    public void Insert(string type, string message, float remainingMb = 0f)
    {
        var entry = new LogEntry
        {
            Timestamp = NowMs(),
            Type = type,
            RemainingMb = remainingMb,
            Message = message,
        };
        lock (gate)
        {
            entry.Id = nextId++;
            entries.Add(entry);
            Save();
        }
    }

    /// <summary>LogDao.getRecent(limit): ORDER BY timestamp DESC LIMIT n.</summary>
    public IReadOnlyList<LogEntry> GetRecent(int limit)
    {
        lock (gate)
        {
            return entries.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
        }
    }

    /// <summary>LogDao.getAll(): ORDER BY timestamp DESC.</summary>
    public IReadOnlyList<LogEntry> GetAll()
    {
        lock (gate)
        {
            return entries.OrderByDescending(e => e.Timestamp).ToList();
        }
    }

    /// <summary>LogDao.count().</summary>
    public int Count()
    {
        lock (gate) return entries.Count;
    }

    /// <summary>LogDao.deleteOlderThan(maxAge).</summary>
    public void DeleteOlderThan(long maxAge)
    {
        lock (gate)
        {
            if (entries.RemoveAll(e => e.Timestamp < maxAge) > 0) Save();
        }
    }

    /// <summary>LogDao.deleteBeyondLimit(keep): die neuesten <paramref name="keep"/> bleiben.</summary>
    public void DeleteBeyondLimit(int keep)
    {
        lock (gate)
        {
            if (entries.Count <= keep) return;
            var keepIds = new HashSet<long>(
                entries.OrderByDescending(e => e.Timestamp).Take(keep).Select(e => e.Id));
            entries = entries.Where(e => keepIds.Contains(e.Id)).ToList();
            Save();
        }
    }

    private void Load()
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    Snapshot? snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
                    if (snap != null)
                    {
                        entries = snap.Entries ?? new List<LogEntry>();
                        nextId = snap.NextId;
                    }
                }
            }
            catch
            {
                entries = new List<LogEntry>();
                nextId = 1;
            }

            long highest = 0;
            foreach (LogEntry e in entries) highest = Math.Max(highest, e.Id);
            if (nextId <= highest) nextId = highest + 1;
            if (nextId <= 0) nextId = 1;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp,
                JsonSerializer.Serialize(new Snapshot { NextId = nextId, Entries = entries }));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Der Verlauf darf den Monitor-Loop nicht kippen; Android schreibt
            // die Zeile ebenfalls "fire and forget" in die Room-DB.
        }
    }
}
