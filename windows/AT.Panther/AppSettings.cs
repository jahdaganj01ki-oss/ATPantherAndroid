using System.Text.Json;

namespace ATPanther;

/// <summary>
/// Nicht-geheime Einstellungen, gespeichert als JSON in %AppData%\ATPanther.
/// Defaults entsprechen der Android-Version: Schwelle 850 MB, Intervall 60 s.
/// </summary>
public sealed class AppSettings
{
    public const float DefaultThresholdMb = 850f;
    public const int DefaultIntervalSec = 60;

    public float ThresholdMb { get; set; } = DefaultThresholdMb;
    public int IntervalSec { get; set; } = DefaultIntervalSec;
    public bool LaunchAtStartup { get; set; }
    public bool AutoStartMonitor { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // Wertebereiche absichern
                    if (settings.ThresholdMb <= 0) settings.ThresholdMb = DefaultThresholdMb;
                    if (settings.IntervalSec < 10) settings.IntervalSec = DefaultIntervalSec;
                    return settings;
                }
            }
        }
        catch
        {
            // Beschädigte Einstellungen ignorieren → Defaults verwenden
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Speichern darf die App nicht zum Absturz bringen
        }
    }
}
