using System.Diagnostics;
using System.Windows.Forms;

namespace ATPanther.Windows;

public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly PantherApp _form;
    private readonly MonitorController _monitor;
    private bool _disposed;

    public TrayManager(MonitorController monitor, PantherApp form)
    {
        _monitor = monitor;
        _form = form;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Show", null, (_, _) => ShowForm());
        _menu.Items.Add("Resume", null, (_, _) =>
        {
            _monitor.ResumeRequested();
        });
        _menu.Items.Add("Diagnose-Log öffnen", null, (_, _) => OpenDiagLog());
        _menu.Items.Add("Diagnose-Ordner öffnen", null, (_, _) => OpenDiagnoseFolder());
        _menu.Items.Add("-");
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        // The icon is mandatory: Visible = true without an icon crashes startup.
        // AppIcon never throws (system-icon fallback), embedded via ApplicationIcon.
        _icon = new NotifyIcon
        {
            Text = "AT Panther",
            Icon = AppIcon.Load(),
            Visible = true,
            ContextMenuStrip = _menu
        };

        _icon.DoubleClick += (_, _) => ShowForm();
        _monitor.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(string status, float remainingMb)
    {
        // Windows tray tooltips are limited to 63 chars.
        const int maxTooltip = 63;
        var tooltip = $"AT Panther - {status}";
        _icon.Text = tooltip.Length <= maxTooltip ? tooltip : tooltip[..maxTooltip];

        // Android shows pause alerts on a separate IMPORTANCE_HIGH channel that
        // survives the foreground-service stop; mirror that with a balloon tip
        // whenever the monitor enters the permanent failure pause.
        if (status.Contains("pausiert", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _icon.ShowBalloonTip(
                    8000,
                    "AT Panther pausiert",
                    "Login/Verbindung ist wiederholt fehlgeschlagen — der Monitor versucht es " +
                    "nicht weiter automatisch. Zum Fortsetzen App öffnen und Monitor neu starten.",
                    ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                DiagLog.Warn("Tray", "Balloon tip failed.", ex);
            }
        }
    }

    /// <summary>Opens %LocalAppData%\ATPanther\diagnostics.log in the default editor.</summary>
    private void OpenDiagLog()
    {
        try
        {
            DiagLog.Info("Diagnose", "Diagnose log opened by user.");
            Process.Start(new ProcessStartInfo(DiagLog.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagLog.Error("Diagnose", "Could not open diagnostics log.", ex);
        }
    }

    /// <summary>Opens %LocalAppData%\ATPanther in Explorer (logs, state, diagnose packs).</summary>
    private void OpenDiagnoseFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagLog.DataDir);
            Process.Start(new ProcessStartInfo(DiagLog.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagLog.Error("Diagnose", "Could not open diagnostics folder.", ex);
        }
    }

    private void ShowForm()
    {
        if (_form.WindowState == FormWindowState.Minimized)
        {
            _form.WindowState = FormWindowState.Normal;
        }

        _form.Show();
        _form.BringToFront();
        _form.Activate();
    }

    private void Exit()
    {
        _form.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitor.StatusChanged -= OnStatusChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
