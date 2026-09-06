using System.Windows.Forms;
using ATPanther.Windows;

ApplicationConfiguration.Initialize();
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

// Only one instance may run: two processes would both poll and both book 1 GB.
using var mutex = new Mutex(true, @"Local\ATPanther_SingleInstance", out var createdNew);
if (!createdNew)
{
    MessageBox.Show(
        "AT Panther läuft bereits. Bitte das bestehende Fenster verwenden.",
        "AT Panther",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

var app = new PantherApp();
Application.Run(app);
