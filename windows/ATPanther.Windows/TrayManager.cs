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
        _menu.Items.Add("-");
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        _icon = new NotifyIcon
        {
            Text = "AT Panther",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _icon.DoubleClick += (_, _) => ShowForm();
        _monitor.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(string status, float remainingMb)
    {
        _icon.Text = $"AT Panther - {status}";
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
