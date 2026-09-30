using System.IO;
using System.Windows;
using ATPanther.Core;
using Microsoft.Win32;
using System.ComponentModel;

namespace ATPanther;

public partial class MainWindow : Window
{
    private readonly CredentialStore _creds = new();
    private readonly MonitorStateStore _state = new();
    private readonly WarningPrefs _warnings = new();
    private readonly AppDb _db = new();
    private readonly MonitorGate _gate = new();
    private readonly MonitorService _monitor;
    private bool _pwVisible;
    private bool _uiReady;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            DarkTitleBar.Enable(this);
            FileLogger.Info("MainWindow initialized.");

            _monitor = new MonitorService(_db, _state);
            _monitor.StatusChanged += (t, _) => Dispatcher.Invoke(() =>
            {
                TvStatus.Text = t;
                App.UpdateTrayTooltip(t);
            });
            _monitor.LogAdded += _ => Dispatcher.Invoke(RefreshLog);
            _monitor.NoTariffWarning += tariff => Dispatcher.Invoke(() => ShowNoTariffWarning(tariff));
            _monitor.Paused += () => Dispatcher.Invoke(() =>
            {
                MessageBox.Show("⛔ AT Panther pausiert nach wiederholten Fehlern. Zum Fortsetzen zweimal auf Start tippen.",
                    "AT Panther", MessageBoxButton.OK, MessageBoxImage.Warning);
                App.ShowTrayNotification("AT Panther", "Monitor pausiert nach wiederholten Fehlern");
            });
            _monitor.Standby += r => Dispatcher.Invoke(() =>
            {
                ShowLockStatus(r);
                BtnToggle.Content = "Monitor starten";
                MessageBox.Show(
                    $"⏸ Bereitschaftsmodus\n\n{r.Detail}\n\n" +
                    "Diese Variante fragt das ALDI-Talk-Portal NICHT ab. " +
                    "Mit „Übernehmen“ holst du die Überwachung auf diesen Rechner.",
                    "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
            });

            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    // Fix 27.09.2026: beim Minimieren komplett ins Tray – aber NUR wenn
                    // das Tray-Icon wirklich registriert ist. Sonst bleibt der
                    // Taskleisten-Button (kein "unsichtbarer Prozess" mehr).
                    if (App.IsTrayReady)
                    {
                        ShowInTaskbar = false;
                        Hide();
                        FileLogger.Info("Fenster minimiert -> Tray (Taskleiste aus).");
                    }
                    else
                    {
                        FileLogger.Warning("Minimiert, aber kein Tray-Icon – Taskleiste bleibt an.");
                    }
                    App.UpdateTrayTooltip(TvStatus.Text);
                }
            };

            // Fix 27.09.2026: auch bei direktem Minimieren-Button sicher ins Tray.
            IsVisibleChanged += (_, _) =>
            {
                if (!IsVisible && WindowState == WindowState.Minimized && App.IsTrayReady)
                {
                    ShowInTaskbar = false;
                    App.UpdateTrayTooltip(TvStatus.Text);
                }
            };

            Closing += (_, e) =>
            {
                // Fix 27.09.2026: X-Button geht jetzt IMMER ins Tray (vorher nur wenn
                // nicht minimiert). Echtes Beenden nur ueber Tray-Menue "Beenden".
                if (App.IsTrayReady)
                {
                    e.Cancel = true;
                    WindowState = WindowState.Minimized;
                    ShowInTaskbar = false;
                    Hide();
                    FileLogger.Info("X gedrueckt -> ins Tray minimiert.");
                }
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
            var lockSettings = _gate.LoadSettings();
            EtLockUrl.Text = lockSettings.WorkerUrl;
            EtLockToken.Text = lockSettings.WorkerToken;
            ChkFailOpen.IsChecked = lockSettings.FailOpen;
            ShowLockStatus(_gate.CachedStatus());
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
        // Ohne Freigabe-URL startet der Monitor nicht: der Schutz gegen
        // parallele Abfragen mehrerer Varianten ist bewusst fail-closed.
        if (!_gate.IsConfigured())
        {
            MessageBox.Show(
                "Ohne Freigabe-URL startet der Monitor nicht.\n\n" +
                "Bitte unten bei „Monitor-Freigabe“ die Worker-URL eintragen und speichern.",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    // ── Monitor-Freigabe (nur eine Variante darf das Portal abfragen) ──────

    private void ShowLockStatus(GateResult r)
    {
        var headline = r.Status switch
        {
            GateStatus.Allowed => "✅ Freigabe aktiv: " + r.Owner,
            GateStatus.NotOwner => "⏸ Bereitschaft: " + r.Owner + " fragt ab",
            GateStatus.Free => "⚪ Freigabe frei – Übernehmen tippen",
            GateStatus.NotConfigured => "⚠ Freigabe nicht konfiguriert",
            GateStatus.Unreachable => "⛔ Freigabe-Server nicht erreichbar",
            _ => "⚠ Freigabe unsicher",
        };
        TvLock.Text = headline + Environment.NewLine + r.Detail;
    }

    private async void OnLockSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _gate.SaveSettings(EtLockUrl.Text, EtLockToken.Text, ChkFailOpen.IsChecked == true);
            // Ungueltige Eingaben zurueckspiegeln, damit klar ist, was gespeichert ist
            var saved = _gate.LoadSettings();
            EtLockUrl.Text = saved.WorkerUrl;
            EtLockToken.Text = saved.WorkerToken;
            FileLogger.Info($"Freigabe gespeichert: url={saved.WorkerUrl} failOpen={saved.FailOpen}");
            ShowLockStatus(await _gate.EvaluateAsync(autoClaim: false));
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Speichern der Freigabe:\n{ex.Message}",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnLockClaimClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_gate.IsConfigured())
            {
                MessageBox.Show("Bitte zuerst die URL speichern.", "AT Panther",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var answer = MessageBox.Show(
                "Übernehmen?\n\nDie andere Variante wird dadurch beim nächsten Check selbstständig " +
                "in den Bereitschaftsmodus geschaltet und fragt das Portal nicht mehr ab.",
                "AT Panther", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            ShowLockStatus(await _gate.ClaimAsync());
            FileLogger.Info("Freigabe übernommen.");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Übernehmen:\n{ex.Message}",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnLockReleaseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowLockStatus(await _gate.ReleaseAsync());
            FileLogger.Info("Freigabe abgegeben.");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Freigeben:\n{ex.Message}",
                "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Guthaben-Warnung: Tray-Balloon + modaler Dialog mit zwei Aktionen.
    /// Port von NoTariffWarningManager.maybeWarn (Android) – der WLAN-Guard entfaellt
    /// auf Windows (kein mobiles Datenrisiko am Desktop), Cooldown/Snooze bleibt.
    /// </summary>
    private void ShowNoTariffWarning(TariffStatus tariff)
    {
        try
        {
            FileLogger.Warning($"Warnung: kein Datentarif ({tariff.DebugInfo})");
            App.ShowTrayNotification("⚠ Kein Datentarif aktiv", "Datenverbrauch kostet jetzt direkt Guthaben!");
            // Fenster ggf. aus dem Tray holen, damit der Dialog sichtbar ist
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
                Activate();
            }
            var dlg = new WarningDialog(tariff.DebugInfo) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (dlg.Choice == WarningDialog.WarningChoice.Continue)
            {
                _warnings.RecordContinue();
                FileLogger.Info("Warnung bestaetigt: Weiterhin nutzen (Snooze 12h)");
                _db.Insert("WARN", tariff.RemainingMb >= 0 ? (float)tariff.RemainingMb : -1f,
                    "Warnung bestaetigt: Weiterhin nutzen (Snooze 12h)");
            }
            else
            {
                _warnings.RecordDisableChosen();
                FileLogger.Info("Warnung: Nutzer waehlte 'Internet abschalten' (Snooze 24h)");
                _db.Insert("WARN", tariff.RemainingMb >= 0 ? (float)tariff.RemainingMb : -1f,
                    "Warnung: Nutzer waehlte 'Internet abschalten' (Snooze 24h)");
            }
            RefreshLog();
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
        }
    }

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
        // Fix 27.09.2026: _monitor/_db koennen null sein, wenn der Konstruktor
        // vor der Zuweisung geworfen hat (z.B. XAML-Parse-Fehler) – dann gab es
        // eine NullReferenceException beim Schliessen obendrauf.
        try { _monitor?.Stop(); } catch { }
        try { _db?.Dispose(); } catch { }
        base.OnClosed(e);
    }
}
