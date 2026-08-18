namespace ATPanther;

/// <summary>
/// Zentrale Pfade der Windows-Version.
///
/// Die EXE selbst bleibt eine einzige portable Datei – alle Nutzdaten
/// (Einstellungen, verschlüsselte Zugangsdaten, Log) liegen im
/// per-User-AppData-Ordner, damit die App auch aus geschützten Ordnern
/// (z. B. Programme) heraus lauffähig ist.
/// </summary>
public static class AppPaths
{
    /// <summary>%AppData%\ATPanther – Datenverzeichnis des Benutzers.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ATPanther");

    /// <summary>Einstellungen (nicht geheim).</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>DPAPI-verschlüsselte Zugangsdaten (Rufnummer + Passwort).</summary>
    public static string CredentialsFile => Path.Combine(DataDir, "credentials.dat");

    /// <summary>Log-Verlauf (JSON).</summary>
    public static string LogFile => Path.Combine(DataDir, "log.json");

    /// <summary>Stellt sicher, dass das Datenverzeichnis existiert.</summary>
    public static void EnsureCreated() => Directory.CreateDirectory(DataDir);
}
