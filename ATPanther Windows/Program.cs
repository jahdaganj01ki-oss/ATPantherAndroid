using System.Runtime.InteropServices;
using ATPanther.Ui;

namespace ATPanther;

/// <summary>
/// Einstiegspunkt. Pendant zu Android `PantherApp` + `MainActivity` mit
/// `launchMode="singleTask"` (AndroidManifest.xml:39): ein zweiter Start
/// holt die laufende Instanz nach vorn, statt eine zweite zu oeffnen.
/// </summary>
internal static class Program
{
    private const string MutexName = "Local\\ATPanther.SingleInstance";
    private const string WindowTitle = "AT Panther";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            BringExistingInstanceToFront();
            return;
        }

        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            IntPtr hwnd = FindWindow(null, WindowTitle);
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Bestehende Instanz ist da, aber das Fenster ist gerade nicht
            // auffindbar (Hochfahren/Tray). Kein Grund zu scheitern.
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
