using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ATPanther.Data;
using ATPanther.Monitor;

namespace ATPanther.Ui;

/// <summary>
/// Hauptfenster – Nachbau von <c>MainActivity.kt</c> plus
/// <c>res/layout/activity_main.xml</c>. Kartenreihenfolge, Beschriftungen,
/// Defaultwerte und Zustandswechsel sind übernommen. Die Android-Foreground-
/// Notification liegt als Tray-Icon vor, weil Windows kein Pendant zu
/// <c>startForeground()</c> hat (PARITY.md 3.11/3.12).
///
/// Layout bewusst mit festen Koordinaten statt AutoSize/AutoLayout: der einzige
/// Build läuft in GitHub Actions, ein visuell kaputtes AutoLayout wäre dort nicht
/// sichtbar. Die Android-App ist ebenfalls einspaltig (ScrollView).
/// </summary>
public sealed class MainForm : Form
{
    private const string AppTitle = "AT Panther";

    // Einspaltige Spalte wie das ScrollView-Layout (padding 16dp)
    private const int Margin = 16;
    private const int CardWidth = 388;
    private const int FieldWidth = CardWidth - 32;
    private const int RowHeight = 26;
    private const int ButtonHeight = 32;
    private const int Gap = 8;

    private readonly LogStore log = new(AppPaths.LogStore);
    private readonly MonitorStateStore monitorState = new();
    private readonly MonitorEngine engine;
    private readonly Container components = new();

    private AppSettings settings = AppSettings.Load();

    private readonly TextBox phoneBox = new();
    private readonly TextBox passwordBox = new();
    private readonly TextBox thresholdBox = new();
    private readonly TextBox intervalBox = new();
    private readonly Label statusLabel = new();
    private readonly Button toggleButton = new();
    private readonly Button passwordToggleButton = new();
    private readonly CheckBox autoStartCheck = new();
    private readonly ListBox logList = new();
    private readonly NotifyIcon tray = new();
    private readonly Panel canvas = new();
    private readonly Panel scroll = new();

    /// <summary>UI:74 – reine UI-Markierung, wird (wie im Original) nur per Klick geändert.</summary>
    private bool isServiceRunning;

    private bool passwordVisible;
    private bool exitRequested;
    private bool loading;

    public MainForm()
    {
        engine = new MonitorEngine(log, monitorState);
        engine.Notification += OnNotification;
        engine.StatusBroadcast += OnStatusBroadcast;
        engine.PausedAlert += OnPausedAlert;
        engine.PausedAlertCancelled += OnPausedAlertCancelled;
        engine.LogChanged += OnLogChanged;

        Text = AppTitle;
        BackColor = Theme.BgBlack;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(CardWidth + Margin * 2, 700);
        try { Icon = IconLoader.Load(); } catch { /* Default-Icon */ }
        Margin = new Padding(0);

        BuildLayout();
        LoadCredentials();
        SetupTray();
    }

    // ── Layout ────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        scroll.Dock = DockStyle.Fill;
        scroll.AutoScroll = true;
        scroll.BackColor = Theme.BgBlack;

        canvas.AutoSize = false;
        canvas.BackColor = Theme.BgBlack;
        canvas.Location = new Point(0, 0);
        canvas.Width = CardWidth + Margin * 2;
        scroll.Controls.Add(canvas);
        Controls.Add(scroll);

        int y = Margin;

        var title = new Label
        {
            Text = AppTitle,
            Font = Theme.TitleFont,
            ForeColor = Theme.Accent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(Margin, y),
            Size = new Size(FieldWidth, 36),
        };
        canvas.Controls.Add(title);
        y += 36 + Margin;

        y = BuildLoginCard(y);
        y = BuildSettingsCard(y);
        y = BuildMonitorCard(y);
        y = BuildMaintenanceCard(y);
        y = BuildHistoryCard(y);

        canvas.Height = y + Margin;
    }

    /// <summary>Legt eine Karte mit festen Zeilen an und liefert das neue y.</summary>
    private int Card(string title, int y, params Action<Panel, int>[] rows)
    {
        int inner = 0;
        foreach (Action<Panel, int> row in rows) inner += 0; // Platzhalter, Höhen stehen in rows

        var panel = new CardPanel
        {
            Location = new Point(Margin, y),
            Size = new Size(CardWidth, 10),
            Padding = new Padding(Margin),
        };
        canvas.Controls.Add(panel);

        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.CardTitleFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = false,
            Location = new Point(Margin, Margin),
            Size = new Size(FieldWidth - 32, 22),
        };
        panel.Controls.Add(titleLabel);

        int rowY = Margin + 22 + Gap;
        foreach (Action<Panel, int> row in rows)
        {
            row(panel, rowY);
            rowY += Gap; // Zeilenhöhen werden in den row-Delegaten über tag gespeichert
            if (panel.Tag is int h && h > rowY) rowY = h;
        }

        panel.Height = rowY + Margin;
        return y + panel.Height + 12;
    }

    /// <summary>Eine Zeile mit fester Höhe: setzt panel.Tag auf das nächste y innerhalb der Karte.</summary>
    private static Action<Panel, int> Row(int height, Action<Panel, int> place) =>
        (panel, y) =>
        {
            place(panel, y);
            panel.Tag = y + height;
        };

    private static TextBox StyleBox(TextBox box)
    {
        box.AutoSize = false;
        box.BackColor = Theme.CardBgAlt;
        box.ForeColor = Theme.TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
        return box;
    }

    private int BuildLoginCard(int y) => Card("Login-Daten", y,
        Row(RowHeight, (panel, rowY) =>
        {
            StyleBox(phoneBox).SetBounds(Margin, rowY, FieldWidth - 32, RowHeight);
            Placeholder.Set(phoneBox, "Rufnummer (z.B. 491637805298)");
            panel.Controls.Add(phoneBox);
        }),
        Row(RowHeight, (panel, rowY) =>
        {
            StyleBox(passwordBox).SetBounds(Margin, rowY, FieldWidth - 32 - 42, RowHeight);
            passwordBox.UseSystemPasswordChar = true;
            Placeholder.Set(passwordBox, "Passwort");
            panel.Controls.Add(passwordBox);

            Button eye = Buttons.Outlined("👁");
            eye.SetBounds(Margin + FieldWidth - 32 - 34, rowY, 34, RowHeight);
            eye.Click += (_, _) =>
            {
                passwordVisible = !passwordVisible;
                passwordBox.UseSystemPasswordChar = !passwordVisible;
                eye.Text = passwordVisible ? "🙈" : "👁";
            };
            panel.Controls.Add(eye);
        }),
        Row(ButtonHeight, (panel, rowY) =>
        {
            Button save = Buttons.Filled("Speichern");
            save.SetBounds(Margin, rowY, FieldWidth - 32, ButtonHeight);
            save.Click += OnSave;
            panel.Controls.Add(save);
        }));

    private int BuildSettingsCard(int y) => Card("Einstellungen", y,
        Row(RowHeight, (panel, rowY) =>
        {
            AddLabel(panel, "Schwelle (MB):", Margin, rowY + 4, 200);
            StyleBox(thresholdBox).SetBounds(Margin + FieldWidth - 32 - 104, rowY, 104, RowHeight);
            thresholdBox.TextAlign = HorizontalAlignment.Right;
            NumberInput.Attach(thresholdBox);
            panel.Controls.Add(thresholdBox);
        }),
        Row(RowHeight, (panel, rowY) =>
        {
            AddLabel(panel, "Intervall (Sek.):", Margin, rowY + 4, 200);
            StyleBox(intervalBox).SetBounds(Margin + FieldWidth - 32 - 104, rowY, 104, RowHeight);
            intervalBox.TextAlign = HorizontalAlignment.Right;
            NumberInput.Attach(intervalBox);
            panel.Controls.Add(intervalBox);
        }));

    private int BuildMonitorCard(int y) => Card("Monitor", y,
        Row(40, (panel, rowY) =>
        {
            statusLabel.Text = "Gestoppt";
            statusLabel.ForeColor = Theme.TextSecondary;
            statusLabel.AutoSize = false;
            statusLabel.SetBounds(Margin, rowY, FieldWidth - 32, 40);
            panel.Controls.Add(statusLabel);
        }),
        Row(ButtonHeight, (panel, rowY) =>
        {
            Button start = Buttons.Filled("Monitor starten");
            start.SetBounds(Margin, rowY, FieldWidth - 32, ButtonHeight);
            start.Click += OnToggle;
            panel.Controls.Add(start);
            toggleButton = start;
        }),
        Row(ButtonHeight, (panel, rowY) =>
        {
            Button battery = Buttons.Outlined("Batterie-Optimierung ignorieren");
            battery.SetBounds(Margin, rowY, FieldWidth - 32, ButtonHeight);
            battery.Click += OnBatteryOptimization;
            panel.Controls.Add(battery);
        }),
        Row(22, (panel, rowY) =>
        {
            // Windows-Pendant zu RECEIVE_BOOT_COMPLETED (PARITY.md 6.3)
            autoStartCheck.Text = "Autostart (Windows)";
            autoStartCheck.ForeColor = Theme.TextSecondary;
            autoStartCheck.AutoSize = false;
            autoStartCheck.SetBounds(Margin, rowY, FieldWidth - 32, 22);
            autoStartCheck.CheckedChanged += (_, _) =>
            {
                if (loading) return;
                AutoStart.Set(autoStartCheck.Checked);
                settings.AutoStart = autoStartCheck.Checked;
                TrySaveSettings();
            };
            panel.Controls.Add(autoStartCheck);
        }));

    private int BuildMaintenanceCard(int y) => Card("Wartung", y,
        Row(ButtonHeight, (panel, rowY) =>
        {
            Button cache = Buttons.Outlined("Cache leeren");
            cache.SetBounds(Margin, rowY, FieldWidth - 32, ButtonHeight);
            cache.Click += OnClearCache;
            panel.Controls.Add(cache);
        }),
        Row(ButtonHeight, (panel, rowY) =>
        {
            Button export = Buttons.Outlined("Log exportieren");
            export.SetBounds(Margin, rowY, FieldWidth - 32, ButtonHeight);
            export.Click += OnExportLog;
            panel.Controls.Add(export);
        }));

    private int BuildHistoryCard(int y) => Card("Verlauf", y,
        // activity_main.xml:287–290: feste Höhe statt mitwachsender Liste
        Row(260, (panel, rowY) =>
        {
            logList.BorderStyle = BorderStyle.None;
            logList.IntegralHeight = false;
            logList.BackColor = Theme.CardBg;
            logList.ForeColor = Theme.TextSecondary;
            logList.Font = Theme.LogFont;
            logList.HorizontalScrollbar = true;
            logList.SetBounds(Margin, rowY, FieldWidth - 32, 260);
            panel.Controls.Add(logList);
        }));

    private static void AddLabel(Panel panel, string text, int x, int y, int width)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            ForeColor = Theme.TextSecondary,
            AutoSize = false,
            Location = new Point(x, y),
            Size = new Size(width, 18),
        });
    }

    // ── Tray ──────────────────────────────────────────────────────────────

    private void SetupTray()
    {
        tray.Icon = IconLoader.Load();
        tray.Text = AppTitle;

        var menu = new ContextMenuStrip(components);
        menu.Items.Add("Öffnen", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) =>
        {
            exitRequested = true;
            Close();
        });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowFromTray();
        tray.Visible = true;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
    }

    private static string Clamp(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max);

    private void Toast(string message) => OnUi(() =>
    {
        try
        {
            tray.BalloonTipTitle = AppTitle;
            tray.BalloonTipText = Clamp(message, 200);
            tray.BalloonTipIcon = ToolTipIcon.Info;
            tray.ShowBalloonTip(3000);
        }
        catch { /* Tray noch nicht bereit */ }
    });

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException)
        {
            // Fenster wird gerade abgebaut
        }
    }

    // ── Engine-Ereignisse ─────────────────────────────────────────────────

    private void OnNotification(string? text) => OnUi(() =>
    {
        try { tray.Text = Clamp(text == null ? AppTitle : AppTitle + ": " + text, 63); }
        catch { /* ignorieren */ }
    });

    private void OnStatusBroadcast(string status, float remainingMb) => OnUi(() =>
    {
        // MainActivity.kt:97 – MB nur anhängen, wenn >= 0
        statusLabel.Text = remainingMb >= 0
            ? $"{status}  ({Format(remainingMb)} MB)"
            : status;
    });

    private void OnPausedAlert() => OnUi(() =>
    {
        try
        {
            tray.BalloonTipTitle = "AT Panther pausiert";
            tray.BalloonTipText = "Login/Verbindung ist 3x hintereinander fehlgeschlagen — " +
                                  "der Monitor versucht es NICHT weiter automatisch. " +
                                  "Zum Fortsetzen App öffnen und Monitor neu starten.";
            tray.BalloonTipIcon = ToolTipIcon.Warning;
            tray.ShowBalloonTip(10000);
            tray.Text = Clamp("AT Panther: ⛔ pausiert", 63);
        }
        catch { /* ignorieren */ }
    });

    private void OnPausedAlertCancelled() => OnUi(() =>
    {
        try { tray.Text = Clamp(AppTitle, 63); } catch { /* ignorieren */ }
    });

    private void OnLogChanged() => OnUi(RefreshLogList);

    // ── Aktionen ──────────────────────────────────────────────────────────

    private void OnSave(object? sender, EventArgs e)
    {
        settings.Phone = phoneBox.Text.Trim();
        settings.Password = passwordBox.Text.Trim();
        settings.ThresholdMb = thresholdBox.Text.Trim();
        settings.IntervalSec = intervalBox.Text.Trim();
        try
        {
            AppPaths.EnsureDirectories();
            settings.Save();
            Toast("Login-Daten und Einstellungen gespeichert");
        }
        catch
        {
            Toast("Speichern fehlgeschlagen");
        }
    }

    private void TrySaveSettings()
    {
        try { settings.Save(); } catch { /* best effort */ }
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        if (isServiceRunning) StopMonitor();
        else StartMonitor();
    }

    /// <summary>MainActivity.kt:339–381 – inklusive Zwei-Tipp-Regel nach einer Pause.</summary>
    private void StartMonitor()
    {
        if (monitorState.IsPaused)
        {
            monitorState.Clear();
            Toast("⛔ Pause aufgehoben — tippe erneut auf Start, um den Monitor neu zu starten");
            statusLabel.Text = "Pausiert — Start zum Fortsetzen";
            statusLabel.ForeColor = Theme.StatusWarn;
            return;
        }

        string phone = phoneBox.Text.Trim();
        string password = passwordBox.Text.Trim();
        if (phone.Length == 0 || password.Length == 0)
        {
            Toast("Bitte Rufnummer und Passwort eingeben");
            return;
        }

        engine.Start(phone, password, ParseThreshold(), ParseInterval());
        isServiceRunning = true;
        toggleButton.Text = "Monitor stoppen";
        statusLabel.Text = "Starte...";
        statusLabel.ForeColor = Theme.StatusWarn;
    }

    /// <summary>MainActivity.kt:383–392 (ACTION_STOP).</summary>
    private void StopMonitor()
    {
        engine.Stop();
        isServiceRunning = false;
        toggleButton.Text = "Monitor starten";
        statusLabel.Text = "Gestoppt";
        statusLabel.ForeColor = Theme.TextSecondary;
    }

    private float ParseThreshold()
    {
        string text = thresholdBox.Text.Trim();
        return float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float value)
            ? value
            : MonitorEngine.DefaultThresholdMb;
    }

    private int ParseInterval()
    {
        string text = intervalBox.Text.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
            ? value
            : MonitorEngine.DefaultIntervalSec;
    }

    private void OnClearCache(object? sender, EventArgs e)
    {
        try
        {
            string cache = AppPaths.Cache;
            if (Directory.Exists(cache))
            {
                foreach (string dir in Directory.GetDirectories(cache))
                {
                    try { Directory.Delete(dir, true); } catch { /* belegte Datei */ }
                }
                foreach (string file in Directory.GetFiles(cache))
                {
                    try { File.Delete(file); } catch { /* belegte Datei */ }
                }
            }
            Directory.CreateDirectory(cache);
            Toast("Cache geleert");
        }
        catch
        {
            Toast("Cache leeren teilweise fehlgeschlagen");
        }
    }

    /// <summary>
    /// MainActivity.kt:145–148 und 250–300: SAF-Dialog ⇒ SaveFileDialog,
    /// gleicher Dateiname, gleicher Inhalt, Einträge älteste zuerst.
    /// </summary>
    private void OnExportLog(object? sender, EventArgs e)
    {
        IReadOnlyList<LogEntry> entries = log.GetRecent(LogStore.UiLimit);
        if (entries.Count == 0)
        {
            Toast("Kein Log-Verlauf vorhanden");
            return;
        }

        string suggested = "at_panther_log_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";

        using var dialog = new SaveFileDialog
        {
            FileName = suggested,
            Filter = "Textdatei (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
            Title = "Log exportieren",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            Toast("Export abgebrochen");
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BuildExport(entries), new UTF8Encoding(false));
            Toast($"Log exportiert ({entries.Count} Einträge)");
        }
        catch
        {
            Toast("Export fehlgeschlagen");
        }
    }

    internal static string BuildExport(IReadOnlyList<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AT Panther \u2013 Log-Export");
        sb.AppendLine($"Erstellt am: {FormatFull(DateTime.Now)}");
        sb.AppendLine($"Anzahl Einträge: {entries.Count}");
        sb.AppendLine(new string('\u2500', 40));

        foreach (LogEntry entry in entries.OrderBy(e => e.Timestamp))
        {
            string icon = entry.Type == "BOOKING" ? "\U0001F4E6" : "\U0001F4E1";
            string remaining = entry.RemainingMb >= 0
                ? $"  [{Format(entry.RemainingMb)} MB]"
                : string.Empty;
            sb.AppendLine($"{FormatFull(FromMs(entry.Timestamp))}  {icon}  {entry.Message}{remaining}");
        }
        return sb.ToString();
    }

    /// <summary>MainActivity.kt:309–335 – Systemdialog statt EMUI-Whitelist.</summary>
    private void OnBatteryOptimization(object? sender, EventArgs e)
    {
        if (KeepAwake.IsActive)
        {
            Toast("App ist bereits auf der Whitelist");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "ms-settings:energy-sleepsettings")
            { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("powercfg.cpl") { UseShellExecute = true });
            }
            catch
            {
                Toast("Bitte manuell unter Einstellungen > Batterie hinzufügen");
            }
        }
    }

    // ── Verlaufsanzeige ───────────────────────────────────────────────────

    private void RefreshLogList()
    {
        int top = logList.TopIndex;
        logList.BeginUpdate();
        logList.Items.Clear();
        foreach (LogEntry entry in log.GetRecent(LogStore.UiLimit))
        {
            string icon = entry.Type == "BOOKING" ? "\U0001F4E6" : "\U0001F4E1";
            logList.Items.Add($"{FormatShort(FromMs(entry.Timestamp))}  {icon}  {entry.Message}");
        }
        logList.EndUpdate();
        try { logList.TopIndex = Math.Min(top, Math.Max(0, logList.Items.Count - 1)); }
        catch { /* Liste noch leer */ }
    }

    private static DateTime FromMs(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;

    private static string FormatFull(DateTime value) =>
        value.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture);

    private static string FormatShort(DateTime value) =>
        value.ToString("dd.MM HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>"%.1f" mit Geräte-Locale (MainActivity.kt:419) ⇒ CurrentCulture.</summary>
    private static string Format(float value) => value.ToString("F1", CultureInfo.CurrentCulture);

    // ── Fenster ───────────────────────────────────────────────────────────

    private void LoadCredentials()
    {
        loading = true;
        phoneBox.Text = settings.Phone;
        passwordBox.Text = settings.Password;
        thresholdBox.Text = settings.ThresholdMb;
        intervalBox.Text = settings.IntervalSec;
        autoStartCheck.Checked = AutoStart.IsEnabled();
        loading = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshLogList();
    }

    /// <summary>
    /// Schließen minimiert nur ins Tray – der Monitor läuft weiter, wie der
    /// Android-Foreground-Service nach dem Wischen der App weiterläuft.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        engine.Dispose();
        tray.Visible = false;
        tray.Dispose();
        components.Dispose();
        base.OnFormClosing(e);
    }
}
