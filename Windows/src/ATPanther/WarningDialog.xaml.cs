using System.Windows;
using ATPanther.Core;

namespace ATPanther;

/// <summary>
/// On-Screen Warn-Dialog bei fehlendem Datentarif (Port von WarningDialogActivity.kt).
/// Ergebnis: Continue (Snooze 12h) oder Disable (Snooze 24h).
/// </summary>
public partial class WarningDialog : Window
{
    public enum WarningChoice { Continue, Disable }

    public WarningChoice Choice { get; private set; } = WarningChoice.Continue;

    public WarningDialog(string debugInfo)
    {
        InitializeComponent();
        TxtMessage.Text = "Aktuell ist kein Datentarif gebucht. Verbrauch von Datenvolumen kostet jetzt direkt Guthaben. " +
            "Möchten Sie weiterhin Internet nutzen oder das Internet abschalten?";
        TxtDebug.Text = string.IsNullOrWhiteSpace(debugInfo) ? "" : debugInfo.Substring(0, Math.Min(220, debugInfo.Length));
    }

    private void OnContinueClicked(object sender, RoutedEventArgs e)
    {
        Choice = WarningChoice.Continue;
        DialogResult = true;
        Close();
    }

    private void OnDisableClicked(object sender, RoutedEventArgs e)
    {
        Choice = WarningChoice.Disable;
        DialogResult = true;
        Close();
    }
}
