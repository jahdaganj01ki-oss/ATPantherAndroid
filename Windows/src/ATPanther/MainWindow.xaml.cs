using System.IO;
using System.Windows;
using ATPanther.Core;
using Microsoft.Win32;

namespace ATPanther;

public partial class MainWindow : Window
{
    private readonly CredentialStore _creds = new();
    private readonly MonitorStateStore _state = new();
    private readonly AppDb _db = new();
    private readonly MonitorService _monitor;
    private bool _pwVisible;
    private bool _uiReady;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            FileLogger.Info("MainWindow initialized.");

            _monitor = new MonitorService(_db, _state);
            _monitor.StatusChanged += (t, _) => Dispatcher.Invoke(() =>
            {
                TvStatus.Text = t;
                App.UpdateTrayTooltip(t);
            });
            _monitor.LogAdded += _ => Dispatcher.Invoke(RefreshLog);
            _monitor.Paused += () => Dispatcher.Invoke(() =>
            {
                MessageBox.Show("⛔ AT Panther pausiert nach wiederholten Fehlern. Zum Fortsetzen zweimal auf Start tippen.",
                    "AT Panther", MessageBoxButton.OK, MessageBoxImage.Warning);
                App.ShowTrayNotification("AT Panther", "Monitor pausiert nach wiederholten Fehlern");
            });

            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    ShowInTaskbar = false;
                    Hide();
                }
            };

            Closing += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    // Already minimized, let tray handle it
                    return;
                }
                // Minimize to tray instead of closing
                WindowState = WindowState.Minimized;
            };

            LoadSettings();
            RefreshLog();
            _uiReady = true;
            App.UpdateTrayTooltip("Gestoppt");
            FileLogger.Info("MainWindow UI ready.");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Initialisieren des Hauptfensters:\n{ex.Message}\n\nDetails wurden in die Logdatei geschrieben.",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private void LoadSettings()
    {
        try
        {
            var c = _creds.Load();
            EtPhone.Text = c.Phone;
            EtPassword.Password = c.Password;
            EtThreshold.Text = c.ThresholdMb > 0 ? c.ThresholdMb.ToString("0") : AppConfig.DefaultThresholdMb.ToString("0");
            EtInterval.Text = c.IntervalSec > 0 ? c.IntervalSec.ToString() : AppConfig.DefaultIntervalSec.ToString();
            ChkAutostart.IsChecked = IsAutostartEnabled();
            FileLogger.Info("Settings loaded.");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Laden der Einstellungen:\n{ex.Message}\n\nDetails wurden in die Logdatei geschrieben.",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshLog()
    {
        RvLog.Items.Clear();
        foreach (var e in _db.GetRecent(AppConfig.LogUiLimit))
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).LocalDateTime.ToString("dd.MM HH:mm:ss");
            var icon = e.Type == "BOOKING" ? "📦" : "📡";
            RvLog.Items.Add($"{time}  {icon}  {e.Message}");
        }
    }

    private void OnEyeClicked(object sender, RoutedEventArgs e)
    {
        _pwVisible = !_pwVisible;
        if (_pwVisible)
        {
            EtPasswordVisible.Text = EtPassword.Password;
            EtPasswordVisible.Visibility = Visibility.Visible;
            EtPassword.Visibility = Visibility.Collapsed;
            BtnEye.Content = "🙈";
        }
        else
        {
            EtPassword.Password = EtPasswordVisible.Text;
            EtPasswordVisible.Visibility = Visibility.Collapsed;
            EtPassword.Visibility = Visibility.Visible;
            BtnEye.Content = "👁";
        }
    }

    private string CurrentPassword() => _pwVisible ? EtPasswordVisible.Text.Trim() : EtPassword.Password.Trim();
    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _creds.Save(new Credentials(
                EtPhone.Text.Trim(), CurrentPassword(),
                float.TryParse(EtThreshold.Text.Trim(), out var t) ? t : AppConfig.DefaultThresholdMb,
                int.TryParse(EtInterval.Text.Trim(), out var i) ? i : AppConfig.DefaultIntervalSec));
            FileLogger.Info("Credentials saved.");
            MessageBox.Show("Anmeldedaten gespeichert.", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Speichern:\n{ex.Message}", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_monitor.IsRunning)
        {
            _monitor.Stop();
            TvStatus.Text = "Gestoppt";
            BtnToggle.Content = "Monitor starten";
            FileLogger.Info("Monitor stopped by user.");
            return;
        }
        var st = _state.Load();
        if (st.PausedAfterFailures)
        {
            _state.Save(new MonitorState(0, false));
            MessageBox.Show("⛔ Pause aufgehoben — klicke erneut auf Start, um den Monitor neu zu starten.",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
            TvStatus.Text = "Pausiert — Start zum Fortsetzen";
            FileLogger.Info("Pause cleared by user.");
            return;
        }
        var phone = EtPhone.Text.Trim();
        var pw = CurrentPassword();
        if (string.IsNullOrEmpty(phone) || string.IsNullOrEmpty(pw))
        {
            MessageBox.Show("Bitte Rufnummer und Passwort eingeben.", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var threshold = float.TryParse(EtThreshold.Text.Trim(), out var t) ? t : AppConfig.DefaultThresholdMb;
        var interval = int.TryParse(EtInterval.Text.Trim(), out var iv) ? iv : AppConfig.DefaultIntervalSec;
        _monitor.Start(phone, pw, threshold, interval);
        BtnToggle.Content = "Monitor stoppen";
        TvStatus.Text = "Starte...";
        Tabs.SelectedIndex = 1;
        FileLogger.Info($"Monitor started: phone={phone}, threshold={threshold}MB, interval={interval}s");
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 0;

    private void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "at-panther.log");
            if (!File.Exists(logPath))
            {
                MessageBox.Show("Keine Logdatei gefunden.", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            FileLogger.Info($"Logdatei geöffnet: {logPath}");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Logdatei konnte nicht geöffnet werden:\n{ex.Message}", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show("Alle zwischengespeicherten Daten löschen?", "AT Panther",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _db.Clear();
                RefreshLog();
                FileLogger.Info("Log database cleared by user.");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Löschen:\n{ex.Message}", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new SaveFileDialog { Filter = "Textdatei (*.txt)|*.txt", FileName = "at-panther-log.txt" };
            if (dlg.ShowDialog() != true) return;
            using var w = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
            var all = _db.GetAll();
            w.WriteLine("AT Panther – Protokoll-Export");
            w.WriteLine($"Exportiert: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            w.WriteLine($"Einträge: {all.Count}");
            w.WriteLine();
            foreach (var en in all)
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(en.Timestamp).LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
                var icon = en.Type == "BOOKING" ? "📦" : "📡";
                w.WriteLine($"{time}  {icon}  {en.Message}  [{en.RemainingMb:F1} MB]");
            }
            FileLogger.Info($"Log exported to: {dlg.FileName}");
            MessageBox.Show($"Exportiert: {dlg.FileName}", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Export:\n{ex.Message}", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("ATPanther") != null;
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        if (ChkAutostart.IsChecked == true)
            key.SetValue("ATPanther", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("ATPanther", false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitor.Stop();
        _db.Dispose();
        base.OnClosed(e);
    }
}
