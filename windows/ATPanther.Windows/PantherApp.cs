using System.Globalization;
using System.Windows.Forms;

namespace ATPanther.Windows;

public sealed class PantherApp : Form
{
    private readonly MonitorController _monitor;
    private readonly TrayManager _tray;

    private readonly TextBox _statusBox = null!;
    private readonly TextBox _phoneBox = null!;
    private readonly TextBox _passwordBox = null!;
    private readonly NumericUpDown _thresholdBox = null!;
    private readonly NumericUpDown _intervalBox = null!;
    private readonly TextBox _logBox = null!;
    private readonly Button _startButton = null!;
    private readonly Button _stopButton = null!;

    private const int MaxLogChars = 300_000;

    public PantherApp()
    {
        _monitor = new MonitorController();
        _tray = new TrayManager(_monitor, this);

        _statusBox = StatusBox();
        _phoneBox = PhoneBox();
        _passwordBox = PasswordBox();
        _thresholdBox = ThresholdBox();
        _intervalBox = IntervalBox();
        _logBox = LogBox();
        _startButton = Button("Start", System.Drawing.Color.White, System.Drawing.Color.Black);
        _stopButton = Button("Stop", System.Drawing.Color.FromArgb(120, 120, 120), System.Drawing.Color.White);

        InitializeForm();

        _monitor.StatusChanged += OnMonitorStatus;
        _monitor.LogAdded += OnMonitorLog;

        LoadSavedCredentials();
        LoadRecentLog();
        ShowInitialStatus();
    }

    /// <summary>
    /// Android loads the last LOG_UI_LIMIT (200) log rows into the UI on start;
    /// mirror that by pre-filling the log box (oldest first) on launch.
    /// </summary>
    private void LoadRecentLog()
    {
        var recent = _monitor.RecentLogs(200);
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            AppendLogLine(recent[i].Timestamp, recent[i].Type, recent[i].Message);
        }
    }

    private void ShowInitialStatus()
    {
        if (_monitor.IsPaused)
        {
            SetStatus("⛔ Verbindung pausiert — Start hebt die Pause auf");
        }
        else
        {
            SetStatus("Bereit");
        }
    }

    private void InitializeForm()
    {
        Text = "AT Panther";
        Size = new System.Drawing.Size(600, 480);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        TopMost = false;
        try { Icon = AppIcon.Load(); }
        catch (Exception ex) { DiagLog.Warn("Icon", "Window fell back to default icon.", ex); }

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = System.Drawing.Color.FromArgb(20, 20, 20)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 0)
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // Row 0: status
        grid.Controls.Add(RowLabel("Status"), 0, 0);
        grid.Controls.Add(_statusBox, 1, 0);

        // Rows 1-4: phone, password, threshold, interval
        grid.Controls.Add(RowLabel("Phone number"), 0, 1);
        grid.Controls.Add(_phoneBox, 1, 1);

        grid.Controls.Add(RowLabel("Password"), 0, 2);
        var passwordPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        passwordPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        passwordPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        passwordPanel.Controls.Add(_passwordBox, 0, 0);
        var showPassword = new CheckBox
        {
            Text = "Anzeigen",
            Dock = DockStyle.Fill,
            ForeColor = System.Drawing.Color.FromArgb(190, 190, 190),
        };
        showPassword.CheckedChanged += (_, _) =>
        {
            _passwordBox.UseSystemPasswordChar = !showPassword.Checked;
        };
        passwordPanel.Controls.Add(showPassword, 1, 0);
        grid.Controls.Add(passwordPanel, 1, 2);

        grid.Controls.Add(RowLabel("Threshold (MB)"), 0, 3);
        grid.Controls.Add(_thresholdBox, 1, 3);

        grid.Controls.Add(RowLabel("Check interval (s)"), 0, 4);
        grid.Controls.Add(_intervalBox, 1, 4);

        // Row 5: log section header (both columns)
        grid.Controls.Add(RowLabel("Log"), 0, 5);
        grid.SetColumnSpan(grid.Controls[grid.Controls.Count - 1], 2);

        // Row 6: log (both columns, fills remaining vertical space)
        grid.Controls.Add(_logBox, 0, 6);
        grid.SetColumnSpan(_logBox, 2);

        // Row 7: buttons (both columns)
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            Margin = new Padding(0, 10, 0, 0)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));

        _startButton.Click += (_, _) => OnStart();
        _stopButton.Click += (_, _) => OnStop();
        var saveButton = Button("Save",
            System.Drawing.Color.FromArgb(120, 120, 120), System.Drawing.Color.White);
        saveButton.Click += (_, _) => OnSave();
        var exportButton = Button("Export log",
            System.Drawing.Color.FromArgb(120, 120, 120), System.Drawing.Color.White);
        exportButton.Click += (_, _) => OnExportLog();

        buttons.Controls.Add(_startButton, 0, 0);
        buttons.Controls.Add(_stopButton, 1, 0);
        buttons.Controls.Add(saveButton, 2, 0);
        buttons.Controls.Add(exportButton, 3, 0);

        grid.Controls.Add(buttons, 0, 7);
        grid.SetColumnSpan(buttons, 2);

        // Row 8: hint (both columns)
        var hint = new Label
        {
            Text = "AT Panther monitors ALDI Talk data volume and books 1 GB below the threshold. Credentials are stored DPAPI-protected for this Windows user.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = System.Drawing.Color.FromArgb(190, 190, 190),
            Padding = new Padding(0, 10, 0, 0)
        };
        grid.Controls.Add(hint, 0, 8);
        grid.SetColumnSpan(hint, 2);

        // Row heights: input rows fixed, log row stretches.
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));  // status
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));  // phone
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));  // password
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));  // threshold
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));  // interval
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));  // log header
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // log
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));  // buttons
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));  // hint

        panel.Controls.Add(grid);
        Controls.Add(panel);
    }

    private void OnStart()
    {
        var phone = _phoneBox.Text.Trim();
        var password = _passwordBox.Text;
        var threshold = (double)_thresholdBox.Value;
        var intervalSeconds = (int)_intervalBox.Value;

        if (string.IsNullOrWhiteSpace(phone) || password.Length == 0)
        {
            SetStatus("Bitte Rufnummer und Passwort eingeben");
            return;
        }

        var started = _monitor.Start(phone, password, threshold, intervalSeconds);
        if (started)
        {
            SetStatus("Monitor gestartet");
            SaveCredentials(phone, password, threshold, intervalSeconds);
            AppendLogLine(DateTime.Now, "CHECK",
                $"Monitor gestartet (Schwelle {threshold.ToString("0.#", CultureInfo.InvariantCulture)} MB, Intervall {intervalSeconds} s)");
        }
    }

    private void OnStop()
    {
        _monitor.Stop();
        SetStatus("Monitor gestoppt");
    }

    /// <summary>
    /// Saves phone/password/threshold/interval (DPAPI-protected) without
    /// starting the monitor — port of the Android "Save credentials" button.
    /// </summary>
    private void OnSave()
    {
        var phone = _phoneBox.Text.Trim();
        var password = _passwordBox.Text;
        var threshold = (double)_thresholdBox.Value;
        var intervalSeconds = (int)_intervalBox.Value;

        if (SaveCredentials(phone, password, threshold, intervalSeconds))
        {
            SetStatus("Login-Daten und Einstellungen gespeichert");
            AppendLogLine(DateTime.Now, "CHECK", "Einstellungen gespeichert");
        }
        else
        {
            SetStatus("Speichern fehlgeschlagen (siehe Diagnose-Log)");
        }
    }

    private async void OnExportLog()
    {
        // Ulefone v1.2 parity (Freeze-Fix #13): the export formats up to 200
        // rows — do it off the UI thread so the window never hangs on click.
        SetStatus("Exportiere Log...");
        var ok = false;
        var path = string.Empty;
        try
        {
            (ok, path) = await Task.Run(() =>
            {
                var success = _monitor.TryExportLog(out var p);
                return (success, p);
            });
        }
        catch (Exception ex)
        {
            DiagLog.Warn("Export", "Log export failed.", ex);
            ok = false;
        }

        if (ok)
        {
            SetStatus($"Log exportiert: {path}");
        }
        else
        {
            SetStatus("Kein Log-Verlauf vorhanden");
        }
    }

    // ── Monitor event → UI thread marshaling ──

    private void OnMonitorStatus(string text, float remainingMb)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => OnMonitorStatus(text, remainingMb)));
            return;
        }

        // Mirror the Android status line: the activity appends the current volume
        // when the broadcast carries one ("status  (X.X MB)").
        SetStatus(remainingMb >= 0
            ? $"{text}  ({remainingMb.ToString("0.0", CultureInfo.InvariantCulture)} MB)"
            : text);
    }

    private void OnMonitorLog(LogEntry entry)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => OnMonitorLog(entry)));
            return;
        }

        AppendLogLine(entry.Timestamp, entry.Type, entry.Message);
    }

    private void SetStatus(string message)
    {
        _statusBox.Text = message;
    }

    private void AppendLogLine(DateTime timestamp, string type, string message)
    {
        var time = timestamp.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);
        var icon = type == "BOOKING" ? "📦" : "📡";
        _logBox.AppendText($"{time}  {icon}  {message}{Environment.NewLine}");

        // Bound the on-screen buffer so a very long session cannot grow without limit.
        if (_logBox.TextLength > MaxLogChars)
        {
            _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - MaxLogChars);
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    // ── DPAPI credential persistence (Android SharedPreferences equivalent) ──

    private bool SaveCredentials(string phone, string password, double thresholdMb, int intervalSeconds)
    {
        try
        {
            Credentials.Store(AppConfig.CredentialPhoneKey, phone);
            Credentials.Store(AppConfig.CredentialPasswordKey, password);
            Credentials.Store(AppConfig.CredentialThresholdMbKey,
                thresholdMb.ToString(CultureInfo.InvariantCulture));
            Credentials.Store(AppConfig.CredentialIntervalSecondsKey,
                intervalSeconds.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex)
        {
            // Storing is best-effort — the monitor itself keeps running.
            DiagLog.Warn("Setup", "Could not store credentials.", ex);
            return false;
        }
    }

    private void LoadSavedCredentials()
    {
        try
        {
            var phone = Credentials.Retrieve(AppConfig.CredentialPhoneKey);
            if (!string.IsNullOrEmpty(phone))
            {
                _phoneBox.Text = phone;
            }

            var password = Credentials.Retrieve(AppConfig.CredentialPasswordKey);
            if (!string.IsNullOrEmpty(password))
            {
                _passwordBox.Text = password;
            }

            if (double.TryParse(
                    Credentials.Retrieve(AppConfig.CredentialThresholdMbKey),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
            {
                _thresholdBox.Value = Math.Clamp(
                    (decimal)threshold, _thresholdBox.Minimum, _thresholdBox.Maximum);
            }

            if (int.TryParse(
                    Credentials.Retrieve(AppConfig.CredentialIntervalSecondsKey),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
            {
                _intervalBox.Value = Math.Clamp(
                    interval, (int)_intervalBox.Minimum, (int)_intervalBox.Maximum);
            }
        }
        catch (Exception ex)
        {
            // Corrupted/undecryptable state -> keep the default (empty) fields.
            DiagLog.Warn("Setup", "Could not load saved credentials.", ex);
        }
    }

    // ── Control factories ──

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = System.Drawing.Color.White,
        Padding = new Padding(0, 4, 0, 0)
    };

    private static TextBox StatusBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Font = new System.Drawing.Font("Segoe UI", 9f),
        BackColor = System.Drawing.Color.FromArgb(40, 40, 40),
        ForeColor = System.Drawing.Color.FromArgb(230, 230, 230),
        BorderStyle = BorderStyle.FixedSingle
    };

    private static TextBox PhoneBox() => new()
    {
        Dock = DockStyle.Fill,
        Font = new System.Drawing.Font("Segoe UI", 9f),
        BorderStyle = BorderStyle.FixedSingle
    };

    private static TextBox PasswordBox() => new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true,
        Font = new System.Drawing.Font("Segoe UI", 9f),
        BorderStyle = BorderStyle.FixedSingle
    };

    private static NumericUpDown ThresholdBox() => new()
    {
        Dock = DockStyle.Fill,
        Minimum = 1m,
        Maximum = 99999m,
        Increment = 50m,
        DecimalPlaces = 1,
        Value = AppConfig.DefaultThresholdMb,
        Font = new System.Drawing.Font("Segoe UI", 9f)
    };

    private static NumericUpDown IntervalBox() => new()
    {
        Dock = DockStyle.Fill,
        Minimum = 1m,
        Maximum = 86400m,
        Value = AppConfig.DefaultIntervalSeconds,
        Font = new System.Drawing.Font("Segoe UI", 9f)
    };

    private static TextBox LogBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        Font = new System.Drawing.Font("Consolas", 9f),
        BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
        ForeColor = System.Drawing.Color.FromArgb(235, 235, 235),
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Button Button(string text, System.Drawing.Color back, System.Drawing.Color fore) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(3),
        BackColor = back,
        ForeColor = fore
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _monitor.Stop();
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.StatusChanged -= OnMonitorStatus;
            _monitor.LogAdded -= OnMonitorLog;
            _monitor.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
