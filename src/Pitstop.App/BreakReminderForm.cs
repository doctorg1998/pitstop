using System.Runtime.InteropServices;

namespace Pitstop.App;

/// <summary>
/// A small corner card instead of a Windows notification: profiles like Play mute notifications, which
/// would silently swallow the reminder. It never takes focus, so it can't interrupt typing or a game.
/// </summary>
internal sealed class BreakReminderForm : Form
{
    private static readonly Color Panel = Color.FromArgb(0x12, 0x19, 0x23), Accent = Color.FromArgb(0x4c, 0xc9, 0xf0),
        TextColor = Color.FromArgb(0xd9, 0xe2, 0xec), Muted = Color.FromArgb(0x87, 0x96, 0xa8), Faint = Color.FromArgb(0x6b, 0x7a, 0x8c);

    private readonly System.Windows.Forms.Timer _autoClose = new() { Interval = 15_000 };

    public BreakReminderForm(string profileName, int minutes)
    {
        Text = "Pitstop break reminder";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Panel;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 12, 16, 12);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Panel, WrapContents = false };
        layout.Controls.Add(MakeLabel("Time for a quick break", 11.5f, FontStyle.Bold, TextColor));
        layout.Controls.Add(MakeLabel($"{minutes} minutes in {profileName}. Look away from the screen, stretch, and grab some water.", 10f, FontStyle.Regular, Muted));
        layout.Controls.Add(MakeLabel("Click to dismiss", 8.5f, FontStyle.Regular, Faint));
        Controls.Add(layout);

        foreach (Control c in layout.Controls) c.Click += (_, _) => Close();
        layout.Click += (_, _) => Close();
        Click += (_, _) => Close();
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color,
        AutoSize = true,
        MaximumSize = new Size(320, 0),
        Margin = new Padding(0, 0, 0, 6),
    };

    // Show without stealing focus from whatever the user is doing.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        var round = 2; // DWMWCP_ROUND: Windows 11 rounded corners
        DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Accent, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _autoClose.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
