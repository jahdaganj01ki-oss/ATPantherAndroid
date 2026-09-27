using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ATPanther;

/// <summary>
/// Schaltet die Fenster-Titelleiste auf dunkel (Windows 10 20H1+ / 11).
/// Nutzt DWMWA_USE_IMMERSIVE_DARK_MODE – keine Custom-Chrome-Bastelei,
/// Min/Max/X-Buttons bleiben nativ. Auf aelteren Builds: No-Op.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // vor 20H1
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // ab 20H1

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Enable(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                // Fenster noch ohne Handle (z.B. vor Loaded) – nach dem Laden erneut versuchen.
                window.SourceInitialized += (_, _) => Apply(window);
                return;
            }
            Apply(window);
        }
        catch (Exception ex)
        {
            Core.FileLogger.Warning($"DarkTitleBar: aktivieren fehlgeschlagen ({ex.Message})");
        }
    }

    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var useDark = 1;
            // Neues Attribut zuerst, Fallback aufs alte bei Fehlercode.
            var hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            if (hr != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            Core.FileLogger.Info("DarkTitleBar: dunkle Titelleiste aktiviert.");
        }
        catch (Exception ex)
        {
            Core.FileLogger.Warning($"DarkTitleBar: anwenden fehlgeschlagen ({ex.Message})");
        }
    }
}
