using System.Threading;
using System.Windows;

namespace ATPanther;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ATPanther_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("AT Panther läuft bereits.", "AT Panther", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
