using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATPanther.Data;

/// <summary>
/// Pendant zu den App-Einstellungen in SharedPreferences
/// <c>at_panther_secure</c> (<c>MainActivity.kt:184–211</c>).
///
/// Wichtig aus dem Original übernommen: Schwelle und Intervall werden als
/// <b>String</b> gespeichert, damit exakt der getippte Wert zurückkommt
/// (MainActivity.kt:186–189). Defaults beim ersten Start: 850 / 60.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultThresholdMb = "850";
    public const string DefaultIntervalSec = "60";

    public string Phone { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ThresholdMb { get; set; } = DefaultThresholdMb;
    public string IntervalSec { get; set; } = DefaultIntervalSec;
    public bool AutoStart { get; set; }

    private sealed class Dto
    {
        [JsonPropertyName("phone")] public string? Phone { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
        [JsonPropertyName("passwordProtected")] public bool PasswordProtected { get; set; }
        [JsonPropertyName("threshold_mb")] public string? ThresholdMb { get; set; }
        [JsonPropertyName("interval_sec")] public string? IntervalSec { get; set; }
        [JsonPropertyName("autostart")] public bool AutoStart { get; set; }
    }

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            string path = AppPaths.Settings;
            if (!File.Exists(path)) return settings;

            Dto? dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (dto == null) return settings;

            settings.Phone = dto.Phone ?? string.Empty;
            settings.ThresholdMb = string.IsNullOrEmpty(dto.ThresholdMb) ? DefaultThresholdMb : dto.ThresholdMb!;
            settings.IntervalSec = string.IsNullOrEmpty(dto.IntervalSec) ? DefaultIntervalSec : dto.IntervalSec!;
            settings.AutoStart = dto.AutoStart;

            if (!string.IsNullOrEmpty(dto.Password))
            {
                if (dto.PasswordProtected)
                {
                    try { settings.Password = Dpapi.Unprotect(dto.Password!); }
                    catch { settings.Password = string.Empty; }
                }
                else
                {
                    settings.Password = dto.Password!;
                }
            }
        }
        catch
        {
            // Defekte Datei => Defaults wie bei frisch installierter App.
        }
        return settings;
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();

        var dto = new Dto
        {
            Phone = Phone,
            ThresholdMb = ThresholdMb,
            IntervalSec = IntervalSec,
            AutoStart = AutoStart,
        };

        if (Password.Length == 0)
        {
            dto.Password = string.Empty;
            dto.PasswordProtected = false;
        }
        else
        {
            try
            {
                dto.Password = Dpapi.Protect(Password);
                dto.PasswordProtected = true;
            }
            catch
            {
                // DPAPI nicht verfuegbar (z. B. fremdes Profil): Speicherung schlägt
                // nicht fehl, der Wert bleibt aber ungeschuetzt – in PARITY.md 6.4 vermerkt.
                dto.Password = Password;
                dto.PasswordProtected = false;
            }
        }

        string temp = AppPaths.Settings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto));
        File.Move(temp, AppPaths.Settings, overwrite: true);
    }
}

/// <summary>
/// Persistierter Monitor-Zustand, Pendant zu SharedPreferences
/// <c>at_panther_monitor_state</c> (<c>MonitorService.kt:50–52, 500–521</c>).
/// Die Schlüsselnamen entsprechen dem Original.
/// </summary>
public sealed class MonitorStateStore
{
    /// <summary>MON:41 MAX_CONSECUTIVE_CONNECTION_FAILURES.</summary>
    public const int MaxConsecutiveConnectionFailures = 3;

    private readonly object gate = new();
    private int failures;
    private bool paused;

    public MonitorStateStore() => Load();

    public bool IsPaused
    {
        get { lock (gate) return paused; }
    }

    public int Failures
    {
        get { lock (gate) return failures; }
    }

    /// <summary>
    /// MonitorService.kt:505–512: Zähler erhöhen und das Pause-Flag automatisch
    /// setzen, sobald die Obergrenze erreicht ist. Gibt den neuen Zähler zurück.
    /// </summary>
    public int RecordFailure()
    {
        lock (gate)
        {
            failures += 1;
            paused = failures >= MaxConsecutiveConnectionFailures;
            Save();
            return failures;
        }
    }

    /// <summary>MonitorService.kt:514–519 (clearConnectionFailures / clearConnectionPause).</summary>
    public void Clear()
    {
        lock (gate)
        {
            failures = 0;
            paused = false;
            Save();
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("consecutive_connection_failures")] public int Failures { get; set; }
        [JsonPropertyName("paused_after_connection_failures")] public bool Paused { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.MonitorState)) return;
            Dto? dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(AppPaths.MonitorState));
            if (dto == null) return;
            failures = dto.Failures;
            paused = dto.Paused;
        }
        catch
        {
            failures = 0;
            paused = false;
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureDirectories();
            string temp = AppPaths.MonitorState + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Dto
            {
                Failures = failures,
                Paused = paused,
            }));
            File.Move(temp, AppPaths.MonitorState, overwrite: true);
        }
        catch
        {
            // Zustand bestmöglich halten; ein Schreibfehler darf den Loop nicht beenden.
        }
    }
}
