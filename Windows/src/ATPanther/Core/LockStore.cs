using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATPanther.Core;

/// <summary>Einstellungen der Monitor-Freigabe. Enthaelt KEINE Zugangsdaten.</summary>
public sealed class LockSettings
{
    /// <summary>URL des Cloudflare Workers, z. B. https://at-panther-lock.name.workers.dev</summary>
    public string WorkerUrl { get; set; } = "";

    /// <summary>Optionaler Token, falls im Worker LOCK_TOKEN gesetzt ist.</summary>
    public string WorkerToken { get; set; } = "";

    /// <summary>true = bei Server-Ausfall weiter abfragen (nicht empfohlen).</summary>
    public bool FailOpen { get; set; }
}

/// <summary>Letzter bekannter Stand der Freigabe (Cache fuer Server-Ausfall).</summary>
public sealed class CachedLock
{
    public string? Owner { get; set; }
    public string? DeviceId { get; set; }
    public long ExpiresAt { get; set; }
    public long At { get; set; }
}

/// <summary>
/// Persistenz fuer Freigabe-Einstellungen und -Cache.
/// Anders als die Zugangsdaten bewusst unverschluesselt: die Datei enthaelt
/// nur eine URL, ein Varianten-Kuerzel und Zeitstempel.
/// </summary>
public sealed class LockStore
{
    private readonly string _dir;
    private readonly string _settingsFile;
    private readonly string _cacheFile;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public LockStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPanther");
        Directory.CreateDirectory(_dir);
        _settingsFile = Path.Combine(_dir, "lock_settings.json");
        _cacheFile = Path.Combine(_dir, "lock_cache.json");
    }

    public LockSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return new LockSettings();
            var s = JsonSerializer.Deserialize<LockSettings>(File.ReadAllText(_settingsFile));
            if (s is null) return new LockSettings();
            s.WorkerUrl = NormalizeUrl(s.WorkerUrl);
            s.WorkerToken = s.WorkerToken.Trim();
            return s;
        }
        catch (Exception ex)
        {
            FileLogger.Warning($"LockStore: settings unlesbar ({ex.Message})");
            return new LockSettings();
        }
    }

    public void SaveSettings(LockSettings s)
    {
        s.WorkerUrl = NormalizeUrl(s.WorkerUrl);
        s.WorkerToken = s.WorkerToken.Trim();
        File.WriteAllText(_settingsFile, JsonSerializer.Serialize(s, Json));
    }

    public CachedLock? LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return null;
            return JsonSerializer.Deserialize<CachedLock>(File.ReadAllText(_cacheFile));
        }
        catch (Exception ex)
        {
            FileLogger.Warning($"LockStore: cache unlesbar ({ex.Message})");
            return null;
        }
    }

    public void SaveCache(CachedLock c) =>
        File.WriteAllText(_cacheFile, JsonSerializer.Serialize(c, Json));

    /// <summary>Nur http(s) und ohne abschliessenden Slash - sonst faellt der Aufruf auf.</summary>
    public static string NormalizeUrl(string? raw)
    {
        var trimmed = (raw ?? "").Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "";
        return trimmed;
    }
}
