using System.IO;
using System.Threading;
using System.Windows;
using ATPanther.Core;

namespace ATPanther;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        FileLogger.Init();
        FileLogger.Info("Application starting...");

        _mutex = new Mutex(true, "ATPanther_SingleInstance", out var isNew);
        if (!isNew)
        {
            FileLogger.Warning("Second instance detected, shutting down.");
            MessageBox.Show("AT Panther läuft bereits.", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            base.OnStartup(e);
            FileLogger.Info("MainWindow shown successfully.");
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            MessageBox.Show($"Fehler beim Starten:\n{ex.Message}", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.Error(e.Exception);
        MessageBox.Show($"Unerwarteter Fehler:\n{e.Exception.Message}\n\nDetails wurden in die Logdatei geschrieben.",
            "AT Panther", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLogger.Info("Application exiting.");
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
