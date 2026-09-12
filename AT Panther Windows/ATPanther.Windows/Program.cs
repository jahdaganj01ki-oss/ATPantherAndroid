using System.Windows.Forms;
using ATPanther.Windows;

// Startup, background-thread and finalizer failures land in
// %LocalAppData%\ATPanther\diagnostics.log (see DiagLog); a crash dialog
// names the exact log path plus the sxstrace hint for the loader failure
// class ("side-by-side configuration is invalid"), which Windows raises
// before this code even runs.
DiagLog.StartupHeader("Main");

try
{
    Application.ThreadException += (_, e) =>
    {
        DiagLog.Error("Crash", "Application.ThreadException (UI thread).", e.Exception);
        ShowCrash("Absturz (UI-Thread)", "Im UI-Thread ist ein Fehler aufgetreten.", e.Exception);
    };
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        if (e.ExceptionObject is Exception ex)
            DiagLog.Error("Crash", "AppDomain.UnhandledException (isTerminating=" + e.IsTerminating + ").", ex);
        else
            DiagLog.Error("Crash", "AppDomain.UnhandledException with unknown object (isTerminating=" + e.IsTerminating + ").");
    };
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        DiagLog.Error("Crash", "TaskScheduler.UnobservedTaskException.", e.Exception);
        e.SetObserved();
    };
    DiagLog.Info("Start", "Crash handlers registered (UI / AppDomain / tasks).");
}
catch (Exception ex)
{
    DiagLog.Warn("Start", "Could not register crash handlers.", ex);
}

try
{
    ApplicationConfiguration.Initialize();
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    DiagLog.Info("Start", "ApplicationConfiguration initialized.");
}
catch (Exception ex)
{
    DiagLog.Error("Start", "ApplicationConfiguration.Initialize() failed.", ex);
    ShowCrash("Startfehler", "Die App-Oberfläche konnte nicht initialisiert werden.", ex);
    return;
}

// Only one instance may run: two processes would both poll and both book 1 GB.
using var mutex = new Mutex(true, @"Local\ATPanther_SingleInstance", out var createdNew);
if (!createdNew)
{
    DiagLog.Info("Start", "Second instance detected – existing window keeps running.");
    MessageBox.Show(
        "AT Panther läuft bereits. Bitte das bestehende Fenster verwenden.",
        "AT Panther",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

try
{
    DiagLog.Info("Start", "Starting main window.");
    var app = new PantherApp();
    Application.Run(app);
    DiagLog.Info("Start", "Main window exited regularly.");
}
catch (Exception ex)
{
    DiagLog.Error("Start", "Unhandled exception in UI thread.", ex);
    ShowCrash("Absturz", "AT Panther ist abgestürzt.", ex);
}

void ShowCrash(string title, string headline, Exception ex)
{
    try
    {
        var body = headline + Environment.NewLine + Environment.NewLine +
            "Fehler: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
            "Diagnose-Log:" + Environment.NewLine + DiagLog.FilePath + Environment.NewLine + Environment.NewLine +
            "Bitte diese Datei beim Melden mitsenden." + Environment.NewLine +
            "Kommt stattdessen schon beim Doppelklick „Side-by-Side-Konfiguration ungültig“, " +
            "lief die App gar nicht an – dann Diagnose-AT-Panther.bat als Administrator ausführen " +
            "(sxstrace-Ablauf) und den Ordner %LocalAppData%\\ATPanther mitsenden.";
        MessageBox.Show(body, "AT Panther – " + title,
            MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
    }
    catch
    {
        // No dialog possible (e.g. shutdown) – the log already holds the details.
    }
}
