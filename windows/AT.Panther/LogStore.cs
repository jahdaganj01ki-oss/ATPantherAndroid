using System.Text.Json;

namespace ATPanther;

/// <summary>Ein Verlaufseintrag – analog zur Room-Tabelle log_entries der Android-Version.</summary>
public sealed record LogEntry(long Timestamp, string Type, float RemainingMb, string Message)
{
    /// <summary>Neuester Eintrag: hier ist die Chronologie sortiert absteigend gemeint.</summary>
    public bool IsBooking => Type == "BOOKING";
}

/// <summary>
/// Persistiert den Prüf-/Buchungs-Verlauf als JSON in %AppData%\ATPanther\log.json.
/// Entspricht dem Room-Log (CHECK/BOOKING, Restvolumen, Nachricht) inklusive
/// Aufräumen von Einträgen, die älter als 7 Tage sind.
/// </summary>
public static class LogStore
{
    private static readonly object Lock = new();
    private static List<LogEntry>? _cache;

    public static IReadOnlyList<LogEntry> GetAll()
    {
        lock (Lock)
        {
            return _cache ??= LoadInternal();
        }
    }

    public static void Add(string type, float remainingMb, string message)
    {
        lock (Lock)
        {
            var entries = _cache ??= LoadInternal();
            entries.Insert(0, new LogEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), type, remainingMb, message));

            // 7-Tage-Retention wie in der Android-Version (MonitorService)
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
            entries.RemoveAll(e => e.Timestamp < cutoff);

            SaveInternal(entries);
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            _cache = new List<LogEntry>();
            SaveInternal(_cache);
        }
    }

    private static List<LogEntry> LoadInternal()
    {
        try
        {
            if (File.Exists(AppPaths.LogFile))
            {
                var entries = JsonSerializer.Deserialize<List<LogEntry>>(File.ReadAllText(AppPaths.LogFile));
                if (entries != null)
                {
                    // Neueste zuerst sicherstellen
                    entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                    return entries;
                }
            }
        }
        catch
        {
            // Korrupte Log-Datei → mit leerem Log neu beginnen
        }
        return new List<LogEntry>();
    }

    private static void SaveInternal(List<LogEntry> entries)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.LogFile, JsonSerializer.Serialize(entries));
        }
        catch
        {
            // Logging darf den Monitor nicht stoppen
        }
    }
}
