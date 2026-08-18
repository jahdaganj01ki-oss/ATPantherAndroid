using System.Globalization;
using System.Text;
using ATPanther.Monitor;

namespace ATPanther.UI;

/// <summary>Farbpalette des Black-Theme – identisch zur Android-Version (colors.xml).</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(0x00, 0x00, 0x00);
    public static readonly Color Card = Color.FromArgb(0x0A, 0x0A, 0x0A);
    public static readonly Color CardAlt = Color.FromArgb(0x12, 0x12, 0x12);
    public static readonly Color Primary = Color.FromArgb(0x8E, 0x24, 0xAA);
    public static readonly Color PrimaryDark = Color.FromArgb(0x6A, 0x1B, 0x9A);
    public static readonly Color Accent = Color.FromArgb(0xB3, 0x88, 0xFF);
    public static readonly Color TextPrimary = Color.White;
    public static readonly Color TextSecondary = Color.FromArgb(0xB0, 0xB0, 0xB0);
    public static readonly Color StatusOk = Color.FromArgb(0x66, 0xBB, 0x6A);
    public static readonly Color StatusWarn = Color.FromArgb(0xFF, 0xB7, 0x4D);
    public static readonly Color StatusErr = Color.FromArgb(0xEF, 0x53, 0x50);
}

/// <summary>
/// Hauptfenster der Windows-Version – Port von MainActivity.kt:
/// Login-Daten, Einstellungen (Schwelle/Intervall), Monitor-Steuerung,
/// Wartung (Cache/Log-Export) und Verlauf. Das Fenster schließt ins
/// Tray (NotifyIcon), der Monitor läuft unabhängig davon weiter.
/// </summary>
public sealed class MainForm : Form
{
    private readonly MonitorEngine _engine = new();
    private readonly List<LogEntry> _logEntries = new();
    private bool _allowClose;
    private bool _balloonShown;
    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _trayMenuToggle = null!;

    // Login
    private TextBox _txtPhone = null!;
    private TextBox _txtPassword = null!;
    private CheckBox _chkShowPassword = null!;

    // Einstellungen
    private TextBox _txtThreshold = null!;
    private TextBox _txtInterval = null!;
    private CheckBox _chkLaunchAtStartup = null!;
    private CheckBox _chkAutoStartMonitor = null!;

    // Monitor
    private Label _lblStatus = null!;
    private Button _btnToggle = null!;
    private ListView _lvLog = null!;

    public MainForm()
    {
        Text = "AT Panther";
        Icon = LoadAppIcon();
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(780, 980);
        MinimumSize = new Size(660, 760);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildTray();

        _engine.LogAdded += OnLogAdded;
        _engine.StatusChanged += OnStatusChanged;

        LoadPersistedState();
        RefreshLogList();
        UpdateToggleButton();
    }

    // ── UI-Aufbau ──

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12, 12, 12, 0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Feste Höhe für die Inhalts-Karten (scrollbar), der gesamte Rest gehört dem Verlauf
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 640));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Scrollbarer Bereich mit den Karten – analog zum ScrollView der Android-App
        var contentScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Background,
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Theme.Background,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        content.Controls.Add(BuildLoginCard(), 0, 0);
        content.Controls.Add(BuildSettingsCard(), 0, 1);
        content.Controls.Add(BuildMonitorCard(), 0, 2);
        content.Controls.Add(BuildMaintenanceCard(), 0, 3);

        contentScroll.Controls.Add(content);

        root.Controls.Add(contentScroll, 0, 0);
        root.Controls.Add(BuildLogCard(), 0, 1);

        Controls.Add(root);
    }

    private Panel CreateCard(int height)
    {
        return new Panel
        {
            Dock = DockStyle.Top,
            Height = height,
            BackColor = Theme.Card,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 10),
        };
    }

    private static Label CreateSectionTitle(string text)
    {
        return new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = text,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
    }

    private Panel BuildLoginCard()
    {
        var card = CreateCard(200);

        var title = CreateSectionTitle("Login-Daten");
        card.Controls.Add(title);

        _txtPhone = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            PlaceholderText = "Rufnummer (z.B. 491637805298)",
            BackColor = Theme.CardAlt,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 6, 0, 0),
        };

        _txtPassword = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            PlaceholderText = "Passwort",
            UseSystemPasswordChar = true,
            BackColor = Theme.CardAlt,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 0, 0),
        };

        _chkShowPassword = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 22,
            AutoSize = false,
            Text = "Passwort anzeigen",
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 0),
        };
        _chkShowPassword.CheckedChanged += (_, _) =>
            _txtPassword.UseSystemPasswordChar = !_chkShowPassword.Checked;

        var btnSave = CreatePrimaryButton("Speichern");
        btnSave.Dock = DockStyle.Top;
        btnSave.Height = 34;
        btnSave.Margin = new Padding(0, 8, 0, 0);
        btnSave.Click += (_, _) => SaveCredentials();

        // Reihenfolge: Dock-Controls von unten nach oben hinzufügen
        card.Controls.Add(btnSave);
        card.Controls.Add(_chkShowPassword);
        card.Controls.Add(_txtPassword);
        card.Controls.Add(_txtPhone);
        card.Controls.Add(title);
        return card;
    }

    private Panel BuildSettingsCard()
    {
        var card = CreateCard(156);

        var title = CreateSectionTitle("Einstellungen");
        card.Controls.Add(title);

        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 30,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0),
        };

        var lblThreshold = new Label
        {
            Text = "Schwelle (MB):",
            Location = new Point(0, 6),
            Size = new Size(95, 20),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };

        _txtThreshold = new TextBox
        {
            Location = new Point(100, 2),
            Size = new Size(70, 24),
            TextAlign = HorizontalAlignment.Right,
            BackColor = Theme.CardAlt,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var lblInterval = new Label
        {
            Text = "Intervall (Sek.):",
            Location = new Point(200, 6),
            Size = new Size(100, 20),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };

        _txtInterval = new TextBox
        {
            Location = new Point(306, 2),
            Size = new Size(70, 24),
            TextAlign = HorizontalAlignment.Right,
            BackColor = Theme.CardAlt,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };

        row.Controls.Add(_txtInterval);
        row.Controls.Add(lblInterval);
        row.Controls.Add(_txtThreshold);
        row.Controls.Add(lblThreshold);

        _chkLaunchAtStartup = CreateCheckBox("Mit Windows starten");
        _chkLaunchAtStartup.Margin = new Padding(0, 10, 0, 0);
        _chkLaunchAtStartup.CheckedChanged += (_, _) =>
        {
            var settings = AppSettings.Load();
            settings.LaunchAtStartup = _chkLaunchAtStartup.Checked;
            settings.Save();
            SetLaunchAtStartup(settings.LaunchAtStartup);
        };

        _chkAutoStartMonitor = CreateCheckBox("Monitor beim Programmstart automatisch laufen lassen");
        _chkAutoStartMonitor.Margin = new Padding(0, 4, 0, 0);
        _chkAutoStartMonitor.CheckedChanged += (_, _) =>
        {
            var settings = AppSettings.Load();
            settings.AutoStartMonitor = _chkAutoStartMonitor.Checked;
            settings.Save();
        };

        card.Controls.Add(_chkAutoStartMonitor);
        card.Controls.Add(_chkLaunchAtStartup);
        card.Controls.Add(row);
        card.Controls.Add(title);
        return card;
    }

    private Panel BuildMonitorCard()
    {
        var card = CreateCard(150);

        var title = CreateSectionTitle("Monitor");
        card.Controls.Add(title);

        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            AutoEllipsis = true,
            Text = "Gestoppt",
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0),
        };

        _btnToggle = CreatePrimaryButton("Monitor starten");
        _btnToggle.Dock = DockStyle.Top;
        _btnToggle.Height = 36;
        _btnToggle.Margin = new Padding(0, 8, 0, 0);
        _btnToggle.Click += (_, _) => ToggleMonitor();

        card.Controls.Add(_btnToggle);
        card.Controls.Add(_lblStatus);
        card.Controls.Add(title);
        return card;
    }

    private Panel BuildMaintenanceCard()
    {
        var card = CreateCard(100);

        var title = CreateSectionTitle("Wartung");
        card.Controls.Add(title);

        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0),
        };

        var btnClearCache = CreateSecondaryButton("Cache leeren");
        btnClearCache.Location = new Point(0, 0);
        btnClearCache.Size = new Size(130, 34);
        btnClearCache.Click += (_, _) => ClearCache();

        var btnExportLog = CreateSecondaryButton("Log exportieren");
        btnExportLog.Location = new Point(142, 0);
        btnExportLog.Size = new Size(150, 34);
        btnExportLog.Click += (_, _) => ExportLog();

        row.Controls.Add(btnExportLog);
        row.Controls.Add(btnClearCache);

        card.Controls.Add(row);
        card.Controls.Add(title);
        return card;
    }

    private Control BuildLogCard()
    {
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Padding = new Padding(0, 0, 0, 12),
        };

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            Padding = new Padding(16),
        };

        var title = CreateSectionTitle("Verlauf");

        // Spalten-Header (Label-Zeile, da der ListView-Header systemhell wäre)
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 20,
            BackColor = Color.Transparent,
        };
        var colZeit = new Label { Text = "Zeit", Location = new Point(0, 2), Size = new Size(120, 16), Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = Theme.TextSecondary };
        var colTyp = new Label { Text = "Typ", Location = new Point(126, 2), Size = new Size(60, 16), Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = Theme.TextSecondary };
        var colMsg = new Label { Text = "Meldung", Location = new Point(192, 2), Size = new Size(400, 16), Font = new Font("Segoe UI", 8f, FontStyle.Bold), ForeColor = Theme.TextSecondary };
        header.Controls.Add(colMsg);
        header.Controls.Add(colTyp);
        header.Controls.Add(colZeit);

        _lvLog = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.TextPrimary,
        };
        _lvLog.Columns.Add("Zeit", 120);
        _lvLog.Columns.Add("Typ", 60);
        _lvLog.Columns.Add("Meldung", 400);
        _lvLog.Resize += (_, _) =>
        {
            if (_lvLog.Columns.Count == 3)
                _lvLog.Columns[2].Width = Math.Max(120, _lvLog.ClientSize.Width - 180);
        };

        card.Controls.Add(_lvLog);
        card.Controls.Add(header);
        card.Controls.Add(title);
        outer.Controls.Add(card);
        return outer;
    }

    private static Button CreatePrimaryButton(string text)
    {
        return new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Primary,
            ForeColor = Theme.TextPrimary,
            Font = new Font("Segoe UI", 9.5f),
            FlatAppearance = { BorderSize = 0 },
            Cursor = Cursors.Hand,
        };
    }

    private static Button CreateSecondaryButton(string text)
    {
        return new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardAlt,
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI", 9.5f),
            FlatAppearance = { BorderSize = 1, BorderColor = Theme.PrimaryDark },
            Cursor = Cursors.Hand,
        };
    }

    private static CheckBox CreateCheckBox(string text)
    {
        return new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 22,
            AutoSize = false,
            Text = text,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
    }

    // ── Tray ──

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("AT Panther öffnen", null, (_, _) => ShowWindow());

        _trayMenuToggle = new ToolStripMenuItem("Monitor starten");
        _trayMenuToggle.Click += (_, _) => ToggleMonitor();
        menu.Items.Add(_trayMenuToggle);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) =>
        {
            _allowClose = true;
            Close();
        });

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "AT Panther",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    // ── Persistenz ──

    private void LoadPersistedState()
    {
        var settings = AppSettings.Load();
        var creds = SecureStore.Load();

        _txtPhone.Text = creds?.Phone ?? "";
        _txtPassword.Text = creds?.Password ?? "";
        _txtThreshold.Text = settings.ThresholdMb.ToString("0.#", CultureInfo.InvariantCulture);
        _txtInterval.Text = settings.IntervalSec.ToString(CultureInfo.InvariantCulture);
        _chkLaunchAtStartup.Checked = settings.LaunchAtStartup;
        _chkAutoStartMonitor.Checked = settings.AutoStartMonitor;

        _logEntries.Clear();
        _logEntries.AddRange(LogStore.GetAll());
    }

    private void SaveCredentials()
    {
        var phone = _txtPhone.Text.Trim();
        var password = _txtPassword.Text;
        SecureStore.Save(phone, password);

        var settings = AppSettings.Load();
        settings.ThresholdMb = ParseThreshold();
        settings.IntervalSec = ParseInterval();
        settings.Save();

        SetStatus("Login-Daten und Einstellungen gespeichert", Theme.StatusOk);
    }

    private float ParseThreshold()
    {
        var text = _txtThreshold.Text.Trim().Replace(',', '.');
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : AppSettings.DefaultThresholdMb;
    }

    private int ParseInterval()
    {
        var text = _txtInterval.Text.Trim().Replace(',', '.');
        var parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : AppSettings.DefaultIntervalSec;
        return Math.Max(MonitorEngine.MinIntervalSec, parsed);
    }

    // ── Monitor-Steuerung ──

    private void ToggleMonitor()
    {
        if (_engine.IsRunning) StopMonitor();
        else StartMonitor();
    }

    private void StartMonitor()
    {
        var phone = _txtPhone.Text.Trim();
        var password = _txtPassword.Text;
        if (string.IsNullOrEmpty(phone) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show(this, "Bitte Rufnummer und Passwort eingeben.", "AT Panther",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _engine.Start(phone, password, ParseThreshold(), ParseInterval());
        UpdateToggleButton();
        SetStatus("Starte...", Theme.StatusWarn);
    }

    private void StopMonitor()
    {
        _engine.Stop();
        UpdateToggleButton();
        SetStatus("Gestoppt", Theme.TextSecondary);
    }

    private void UpdateToggleButton()
    {
        var running = _engine.IsRunning;
        _btnToggle.Text = running ? "Monitor stoppen" : "Monitor starten";
        _btnToggle.BackColor = running ? Theme.PrimaryDark : Theme.Primary;
        _trayMenuToggle.Text = running ? "Monitor stoppen" : "Monitor starten";
    }

    private void SetStatus(string text, Color color)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = color;
        UpdateTrayTooltip(text);
    }

    private static Color StatusColor(string text)
    {
        if (text == "Gestoppt") return Theme.TextSecondary;
        if (text.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Fehler:", StringComparison.Ordinal) ||
            text.Contains("gestoppt", StringComparison.OrdinalIgnoreCase))
        {
            return Theme.StatusErr;
        }
        if (text.StartsWith("Anmelden", StringComparison.Ordinal) ||
            text.StartsWith("Buche", StringComparison.Ordinal) ||
            text.StartsWith("Re-Login", StringComparison.Ordinal) ||
            text.StartsWith("Starte", StringComparison.Ordinal) ||
            text.Contains("nicht abgefragt", StringComparison.Ordinal))
        {
            return Theme.StatusWarn;
        }
        return Theme.StatusOk;
    }

    private void UpdateTrayTooltip(string status)
    {
        var tip = "AT Panther — " + status;
        _tray.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    // ── Engine-Events (Worker-Thread → UI marshallen) ──

    private void OnLogAdded(LogEntry entry)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<LogEntry>(OnLogAdded), entry);
            return;
        }
        _logEntries.Insert(0, entry);
        RefreshLogList();
    }

    private void OnStatusChanged(string text, double? remainingMb)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string, double?>(OnStatusChanged), text, remainingMb);
            return;
        }
        var display = remainingMb is >= 0
            ? $"{text}  ({remainingMb.Value.ToString("0.0", CultureInfo.InvariantCulture)} MB)"
            : text;
        SetStatus(display, StatusColor(text));
    }

    // ── Verlauf ──

    private void RefreshLogList()
    {
        if (_lvLog == null) return;
        _lvLog.BeginUpdate();
        _lvLog.Items.Clear();

        var items = _logEntries
            .Take(1000)
            .Select(e => new ListViewItem(new[]
            {
                DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).ToLocalTime().ToString("dd.MM HH:mm:ss"),
                e.IsBooking ? "📦" : "📡",
                e.Message,
            }));
        _lvLog.Items.AddRange(items.ToArray());
        _lvLog.EndUpdate();
    }

    private void ClearCache()
    {
        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ATPanther", "cache");
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
            SetStatus("Cache geleert", Theme.StatusOk);
        }
        catch (Exception ex)
        {
            SetStatus("Cache leeren fehlgeschlagen: " + ex.Message, Theme.StatusErr);
        }
    }

    private void ExportLog()
    {
        var entries = LogStore.GetAll();
        if (entries.Count == 0)
        {
            MessageBox.Show(this, "Kein Log-Verlauf vorhanden.", "AT Panther",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Filter = "Textdatei (*.txt)|*.txt",
            FileName = $"at_panther_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("AT Panther – Log-Export");
            sb.AppendLine("Erstellt am: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
            sb.AppendLine("Anzahl Einträge: " + entries.Count);
            sb.AppendLine("────────────────────────────────────────");
            foreach (var e in entries.OrderBy(e => e.Timestamp))
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
                var icon = e.IsBooking ? "📦" : "📡";
                var remaining = e.RemainingMb >= 0
                    ? $"  [{e.RemainingMb.ToString("0.0", CultureInfo.InvariantCulture)} MB]"
                    : "";
                sb.AppendLine($"{time}  {icon}  {e.Message}{remaining}");
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            MessageBox.Show(this, $"Log exportiert ({entries.Count} Einträge).", "AT Panther",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export fehlgeschlagen:\n" + ex.Message, "AT Panther",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Autostart (HKCU\...\Run – entspricht BOOT_COMPLETED der Android-Version) ──

    private static void SetLaunchAtStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null) return;
            if (enable)
                key.SetValue("ATPanther", "\"" + Application.ExecutablePath + "\"");
            else
                key.DeleteValue("ATPanther", false);
        }
        catch
        {
            // Registry nicht verfügbar → Autostart überspringen
        }
    }

    // ── Icon ──

    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ATPanther.assets.app.ico");
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // Fallback auf System-Icon
        }
        return SystemIcons.Application;
    }

    // ── Lifecycle ──

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Monitor automatisch starten, falls gewünscht (Analogie zu BOOT_COMPLETED)
        if (_chkAutoStartMonitor.Checked &&
            !string.IsNullOrEmpty(_txtPhone.Text.Trim()) &&
            !string.IsNullOrEmpty(_txtPassword.Text))
        {
            StartMonitor();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose)
        {
            // Fenster schließen → ins Tray, Monitor läuft weiter
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(3000, "AT Panther",
                    "Läuft weiter im Hintergrund – ein Doppelklick aufs Tray-Symbol öffnet das Fenster.",
                    ToolTipIcon.Info);
            }
            return;
        }
        _engine.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}
