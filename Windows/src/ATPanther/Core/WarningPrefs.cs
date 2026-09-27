using System.IO;
using System.Text.Json;

namespace ATPanther.Core;

/// <summary>
/// Persistenter Zustand fuer das Guthaben-Warnsystem (No-Spam).
/// Port von WarningPrefs.kt (Ulefone-Variante).
/// - Nach einer angezeigten Warnung: Cooldown 6h bevor erneut gewarnt wird
/// - Nach "Weiterhin nutzen": Snooze 12h (Nutzer hat bewusst entschieden)
/// - Nach "Internet abschalten": Snooze 24h (wird bei aktivem Tarif gecleart)
/// </summary>
public sealed class WarningPrefs
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);
    public static readonly TimeSpan SnoozeContinue = TimeSpan.FromHours(12);
    public static readonly TimeSpan SnoozeDisable = TimeSpan.FromHours(24);

    private readonly string _file;

    public WarningPrefs()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPanther");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "warning_state.json");
    }

    private sealed class State
    {
        public long LastWarningMs { get; set; }
        public long SnoozeUntilMs { get; set; }
        public int ConsecutiveWarnings { get; set; }
        public int DismissCount { get; set; }
        public bool UserDisabledMobile { get; set; }
    }

    private State Load()
    {
        try
        {
            if (!File.Exists(_file)) return new State();
            var json = File.ReadAllText(_file);
            return JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return new State();
        }
    }

    private void Save(State s)
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(s)); }
        catch (Exception ex) { FileLogger.Error(ex); }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool CanShowWarning(TariffStatus status)
    {
        var s = Load();
        var now = NowMs();
        if (now < s.SnoozeUntilMs) return false;
        if (s.LastWarningMs != 0 && now - s.LastWarningMs < (long)Cooldown.TotalMilliseconds) return false;
        return true;
    }

    public void RecordWarningShown(TariffStatus status)
    {
        var s = Load();
        s.LastWarningMs = NowMs();
        s.ConsecutiveWarnings++;
        Save(s);
    }

    public void RecordContinue()
    {
        var s = Load();
        s.SnoozeUntilMs = NowMs() + (long)SnoozeContinue.TotalMilliseconds;
        s.DismissCount++;
        Save(s);
    }

    public void RecordDisableChosen()
    {
        var s = Load();
        s.SnoozeUntilMs = NowMs() + (long)SnoozeDisable.TotalMilliseconds;
        s.UserDisabledMobile = true;
        s.LastWarningMs = NowMs();
        Save(s);
    }

    /// <summary>
    /// Wenn wieder ein aktiver Tarif/Add-on erkannt wird, alle Drosseln zuruecksetzen,
    /// damit bei erneutem Wegfall sofort wieder gewarnt werden kann.
    /// </summary>
    public void ClearIfTariffActive()
    {
        var s = Load();
        if (s.SnoozeUntilMs != 0 || s.ConsecutiveWarnings != 0)
        {
            s.SnoozeUntilMs = 0;
            s.ConsecutiveWarnings = 0;
            s.UserDisabledMobile = false;
            Save(s);
        }
    }

    public string GetStateForDebug()
    {
        var s = Load();
        return $"last={s.LastWarningMs} snoozeUntil={s.SnoozeUntilMs} warnings={s.ConsecutiveWarnings}";
    }
}
