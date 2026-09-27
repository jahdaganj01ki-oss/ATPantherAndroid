using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using ATPanther.Core;

namespace ATPanther;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private static System.Windows.Forms.NotifyIcon? _trayIcon;
    private static MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        FileLogger.Init();
        FileLogger.Info("Application starting...");

        _mutex = new Mutex(true, "ATPanther_SingleInstance", out var isNew);
        if (!isNew)
        {
            FileLogger.Warning("Second instance detected, shutting down.");
            System.Windows.MessageBox.Show("AT Panther läuft bereits.", "AT Panther", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            base.OnStartup(e);
            _mainWindow = MainWindow as MainWindow;
            InitTray();
            FileLogger.Info("MainWindow shown successfully.");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            System.Windows.MessageBox.Show($"Fehler beim Starten:\n{ex.Message}", "AT Panther", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void InitTray()
    {
        if (_mainWindow == null) return;

        Icon appIcon;
        try
        {
            // Fix 27.09.2026: Icon aus eingebetteter Resource laden (Single-File-safe).
            // Datei-Pfade (Assets\icon.ico / icon.ico) existieren im Artifact nicht,
            // da nur die .exe hochgeladen wird – das liess den Start krachen.
            var uri = new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            appIcon = stream != null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application;
        }
        catch
        {
            appIcon = System.Drawing.SystemIcons.Application;
        }

        // Fix 27.09.2026: Tray-Icon sofort sichtbar machen (Visible=true reicht nicht
        // immer beim ersten Minimieren – Icon explizit erzeugen + Tooltip setzen).
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = appIcon,
            Visible = true,
            Text = "AT Panther",
        };

        var openMenuItem = new System.Windows.Forms.ToolStripMenuItem("Öffnen");
        openMenuItem.Click += (_, _) => RestoreMainWindow();

        var exitMenuItem = new System.Windows.Forms.ToolStripMenuItem("Beenden");
        exitMenuItem.Click += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _mainWindow.Dispatcher.Invoke(() =>
            {
                _mainWindow.Close();
                Shutdown();
            });
        };

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add(openMenuItem);
        contextMenu.Items.Add(exitMenuItem);

        _trayIcon.ContextMenuStrip = contextMenu;
        // Fix 27.09.2026: Einfacher Linksklick stellt das Fenster wieder her
        // (vorher nur Doppelklick) – Rechtsklick oeffnet weiterhin das Menue.
        _trayIcon.Click += (_, e) =>
        {
            if (e is System.Windows.Forms.MouseEventArgs me && me.Button == System.Windows.Forms.MouseButtons.Left)
                RestoreMainWindow();
        };
        _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow == null) return;
        // Ensure tray icon stays visible
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
        }
        _mainWindow.Dispatcher.Invoke(() =>
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.ShowInTaskbar = true;
            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        });
    }

    public static void UpdateTrayTooltip(string status)
    {
        if (_trayIcon == null) return;
        _trayIcon.Text = string.IsNullOrWhiteSpace(status) ? "AT Panther" : $"AT Panther\n{status}";
    }

    public static void ShowTrayNotification(string title, string text)
    {
        if (_trayIcon == null) return;
        try
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = text;
            _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning;
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.Error(e.Exception);
        ShowTrayNotification("Unerwarteter Fehler", e.Exception.Message);
        System.Windows.MessageBox.Show($"Unerwarteter Fehler:\n{e.Exception.Message}\n\nDetails wurden in die Logdatei geschrieben.",
            "AT Panther", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLogger.Info("Application exiting.");
        _trayIcon?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
