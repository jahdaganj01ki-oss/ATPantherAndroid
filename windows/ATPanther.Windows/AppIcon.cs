using System.Drawing;

namespace ATPanther.Windows;

/// <summary>
/// Loads the window/tray icon from the running exe (the CI build embeds
/// <c>assets\app.ico</c> as <c>ApplicationIcon</c>). Never throws: when no exe
/// icon is available, the system default icon is returned.
///
/// A <see cref="NotifyIcon"/> with <c>Visible = true</c> and no icon crashes
/// the startup path, so every consumer must go through this loader.
/// Icon source: <c>assets/app-icon-source.png</c> (7 sizes, PNG-compressed ICO).
/// </summary>
internal static class AppIcon
{
    public static Icon Load()
    {
        try
        {
            string? exe = null;
            try { exe = Application.ExecutablePath; } catch { /* fall through to fallback */ }

            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                try
                {
                    Icon? associated = Icon.ExtractAssociatedIcon(exe);
                    if (associated != null) return associated;
                }
                catch (Exception ex)
                {
                    DiagLog.Warn("Icon", "ExtractAssociatedIcon failed, falling back to system icon.", ex);
                }
            }
            else
            {
                DiagLog.Warn("Icon", "ExecutablePath unusable, falling back to system icon.");
            }
        }
        catch (Exception ex)
        {
            DiagLog.Warn("Icon", "Unexpected icon failure, falling back to system icon.", ex);
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
