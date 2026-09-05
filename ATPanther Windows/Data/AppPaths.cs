namespace ATPanther.Data;

/// <summary>
/// Ablageorte der Windows-Version. Pendant zum privaten App-Verzeichnis von
/// Android (SharedPreferences + Room-DB + cacheDir).
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "AT Panther";

    /// <summary>%APPDATA%\AT Panther – Einstellungen, Log, Monitor-Zustand.</summary>
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>%LOCALAPPDATA%\AT Panther\cache – Pendant zu Android cacheDir.</summary>
    public static string Cache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     AppFolderName, "cache");

    /// <summary>Pendant zu SharedPreferences "at_panther_secure" (UI:184).</summary>
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Pendant zu SharedPreferences "at_panther_monitor_state" (MON:500).</summary>
    public static string MonitorState => Path.Combine(Root, "monitor_state.json");

    /// <summary>Pendant zur Room-DB "at_panther_db", Tabelle log_entries.</summary>
    public static string LogStore => Path.Combine(Root, "log_entries.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
    }
}
