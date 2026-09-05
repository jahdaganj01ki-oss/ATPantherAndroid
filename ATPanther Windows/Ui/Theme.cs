using System.Drawing;
using System.Runtime.InteropServices;

namespace ATPanther.Ui;

/// <summary>
/// Farbwerte exakt aus <c>app/src/main/res/values/colors.xml</c> – monochrom
/// Schwarz/Grau/Weiß, keine Buntfarben.
/// </summary>
public static class Theme
{
    public static readonly Color Primary = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color PrimaryDark = Color.FromArgb(0x6E, 0x6E, 0x6E);
    public static readonly Color Accent = Color.FromArgb(0xE0, 0xE0, 0xE0);
    public static readonly Color BgBlack = Color.FromArgb(0x00, 0x00, 0x00);
    public static readonly Color CardBg = Color.FromArgb(0x14, 0x14, 0x14);
    public static readonly Color CardBgAlt = Color.FromArgb(0x1F, 0x1F, 0x1F);
    public static readonly Color TextPrimary = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color TextSecondary = Color.FromArgb(0xB0, 0xB0, 0xB0);
    public static readonly Color StatusOk = Color.FromArgb(0xB0, 0xB0, 0xB0);
    public static readonly Color StatusWarn = Color.FromArgb(0xD9, 0xD9, 0xD9);
    public static readonly Color StatusErr = Color.FromArgb(0xFF, 0xFF, 0xFF);

    public static readonly Font TitleFont = new Font("Segoe UI", 15.75F, FontStyle.Bold);
    public static readonly Font CardTitleFont = new Font("Segoe UI", 10.5F, FontStyle.Bold);
    public static readonly Font BodyFont = new Font("Segoe UI", 9F, FontStyle.Regular);
    public static readonly Font LogFont = new Font("Consolas", 9.75F, FontStyle.Regular);
}

/// <summary>Karte im Sinne von MaterialCardView: dunkle Fläche, feine Kontur.</summary>
public sealed class CardPanel : Panel
{
    public CardPanel()
    {
        BackColor = Theme.CardBg;
        Margin = new Padding(0, 0, 0, 12);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBgAlt);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Gefüllte Taste wie MaterialButton (weiß auf schwarz, schwarze Schrift).</summary>
public static class Buttons
{
    public static Button Filled(string text)
    {
        var button = new Button
        {
            Text = text,
            BackColor = Theme.Primary,
            ForeColor = Theme.BgBlack,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Dock = DockStyle.Top,
            Height = 34,
            Margin = new Padding(0, 8, 0, 0),
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = Theme.Primary;
        return button;
    }

    /// <summary>Widget.Material3.Button.OutlinedButton – Kontur statt Füllung.</summary>
    public static Button Outlined(string text)
    {
        var button = new Button
        {
            Text = text,
            BackColor = Theme.CardBg,
            ForeColor = Theme.Primary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Dock = DockStyle.Top,
            Height = 34,
            Margin = new Padding(0, 8, 0, 0),
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = Theme.PrimaryDark;
        return button;
    }
}

/// <summary>
/// Platzhaltertext im Feld – Annäherung an den <c>android:hint</c> der
/// TextInputLayouts (WinForms hat keinen eingebauten Placeholder).
/// </summary>
public static class Placeholder
{
    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public static void Set(TextBox box, string text)
    {
        if (!box.IsHandleCreated)
        {
            box.HandleCreated += (_, _) => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            return;
        }
        SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
    }
}

/// <summary>Nur Ziffern – Pendant zu android:inputType="number".</summary>
public static class NumberInput
{
    public static void Attach(TextBox box)
    {
        box.KeyPress += (_, e) =>
        {
            if (char.IsControl(e.KeyChar)) return;
            if (e.KeyChar >= '0' && e.KeyChar <= '9') return;
            e.Handled = true;
        };
    }
}
