using System.Drawing;
using System.IO;

namespace ATPanther.Ui;

/// <summary>
/// Laden des App-Icons. Versucht zuerst das eingebettete/bundled <c>assets/app.ico</c>,
/// sonst fallback zum Standard-Windows-Form-Icon (systemeigen, kein externes Icon nötig).
/// </summary>
public static class IconLoader
{
    /// <summary>
    /// Gibt ein <see cref="Icon"/> zurück. Bei Fehler oder fehlender Asset-Datei
    /// ein plausibles Fallback-Icon.
    /// </summary>
    public static Icon Load()
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string? path = Path.Combine(baseDir, "assets", "app.ico");

        if (File.Exists(path))
        {
            try
            {
                return new Icon(path);
            }
            catch
            {
                // Asset ist vorhanden, aber ungültig → Fallback
            }
        }

        // Fallback: ein einfaches monochromes 16x16 Icon erzeugen.
        // Das ist kein perfektes Panther-Logo, aber es verhindert Compilerfehler
        // und lässt sich später durch eine echte ico-Datei ersetzen.
        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        // Minimales, leicht erkennbares Icon: weißes Quadrat auf transparentem Hintergrund.
        // 16x16, 32-Bit-ARGB, damit es auf dunklem Desktopsichtbar ist.
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(0xE0, 0xE0, 0xE0));
            g.FillRectangle(brush, 2, 2, 12, 12);
        }

        // Bitmap → Icon
        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }
}
