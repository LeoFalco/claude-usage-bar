using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using UsageCore;

namespace ClaudeUsageBar;

static class TrayIcon
{
    /// <summary>Claude's orange, for the ✳ mark (the same mark the macOS menu bar shows).</summary>
    static readonly Color ClaudeOrange = Color.FromArgb(0xD9, 0x77, 0x57);

    /// <summary>Draws the tray icon: a small ✳ on top and the session percentage below it, as large as fits.
    /// The number keeps the taskbar's text color below 50% and turns yellow, orange and red from 50, 70 and
    /// 90%. "…" while loading, a red "!" on error.</summary>
    public static Icon Render(double? percent, bool error, int? iconSize = null)
    {
        var size = iconSize ?? SystemInformation.SmallIconSize.Width;
        var dark = WindowsTheme.TaskbarDark;
        var theme = dark ? Theme.Dark : Theme.Light;
        var unit = size / 16f;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            // ✳ on whole pixels, without antialiasing, so it stays crisp at 16 px: a vertical, a horizontal and
            // two diagonal strokes through the center, each `thick` pixels wide.
            var radius = Math.Max(3, (int)Math.Round(3 * unit));
            var thick = Math.Max(1, (int)Math.Round(unit));
            var cx = size / 2 - (thick > 1 ? thick / 2 : 0);
            var cy = radius;
            using (var star = new SolidBrush(ClaudeOrange))
            {
                for (var d = -radius; d <= radius; d++)
                {
                    var diagonal = Math.Abs(d) <= radius - 1; // diagonals a pixel shorter, so the star looks round
                    g.FillRectangle(star, cx + d, cy, thick, thick);
                    g.FillRectangle(star, cx, cy + d, thick, thick);
                    if (diagonal)
                    {
                        g.FillRectangle(star, cx + d, cy + d, thick, thick);
                        g.FillRectangle(star, cx + d, cy - d, thick, thick);
                    }
                }
            }
            var starSize = 2 * radius + thick;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var label = percent is { } p ? UsageFormat.Round(p).ToString(CultureInfo.InvariantCulture) : error ? "!" : "…";
            var color = error && percent is null ? theme.Red
                : percent is >= 50 ? theme.UsageColor(percent.Value)
                : dark ? Color.White : Color.Black;
            var top = starSize + (int)Math.Round(unit);
            var box = new RectangleF(0, top, size, size - top);
            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            path.AddString(label, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
            using var matrix = new Matrix();
            matrix.Translate(box.X + (box.Width - bounds.Width * scale) / 2, box.Bottom - bounds.Height * scale);
            matrix.Scale(scale, scale);
            matrix.Translate(-bounds.X, -bounds.Y);
            path.Transform(matrix);
            using var text = new SolidBrush(color);
            g.FillPath(text, path);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Card shown while the pointer rests on the tray icon, in place of the text tooltip:
/// one progress bar per limit with its percentage and reset countdown. Never takes focus.</summary>
sealed class HoverCard : Form
{
    const int LogicalWidth = 260;
    readonly float? fixedScale;
    readonly bool? fixedDark;
    Usage? usage;
    string? error;
    Theme theme = Theme.Dark;
    float scale = 1;
    Font titleFont = null!, captionFont = null!;

    /// <param name="fixedScale">Fixed pixel scale and theme, for offscreen rendering.</param>
    public HoverCard(float? fixedScale = null, bool? dark = null)
    {
        this.fixedScale = fixedScale;
        fixedDark = dark;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ApplyStyle();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2; // DWMWCP_ROUND, Windows 11 only
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    int S(float v) => (int)Math.Round(v * scale);

    void ApplyStyle()
    {
        theme = fixedDark ?? WindowsTheme.AppsDark ? Theme.Dark : Theme.Light;
        var newScale = fixedScale ?? DeviceDpi / 96f;
        if (titleFont is null || newScale != scale)
        {
            scale = newScale;
            titleFont?.Dispose();
            captionFont?.Dispose();
            titleFont = new Font("Segoe UI Semibold", 12 * scale, GraphicsUnit.Pixel);
            captionFont = new Font("Segoe UI", 11 * scale, GraphicsUnit.Pixel);
        }
        BackColor = theme.Back;
    }

    public void UpdateView(PanelState state)
    {
        usage = state.Usage;
        error = state.Error;
        ApplyStyle();
        ClientSize = new Size(S(LogicalWidth), RenderContent(null));
        Invalidate();
    }

    /// <summary>Shows the card just above (or below) the tray, centered on the pointer.</summary>
    public void ShowNear(Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        var margin = S(8);
        var x = Math.Clamp(cursor.X - Width / 2, area.Left + margin, area.Right - Width - margin);
        var y = cursor.Y < area.Top ? area.Top + margin : area.Bottom - Height - margin;
        Location = new Point(x, y);
        Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RenderContent(e.Graphics);
        using var border = new Pen(theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    int RenderContent(Graphics? g)
    {
        var pad = S(12);
        var width = S(LogicalWidth) - 2 * pad;
        var y = pad;
        var now = DateTimeOffset.Now;

        var rows = usage?.Rows ?? [];
        foreach (var row in rows)
        {
            var percent = $"{UsageFormat.Round(row.Percent)}%";
            var h = titleFont.Height;
            if (g != null)
            {
                TextRenderer.DrawText(g, percent, titleFont, new Rectangle(pad, y, width, h), theme.UsageColor(row.Percent),
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);
                var percentWidth = TextRenderer.MeasureText(percent, titleFont).Width;
                TextRenderer.DrawText(g, row.Title, titleFont, new Rectangle(pad, y, width - percentWidth - S(8), h), theme.Fore,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            y += h + S(6);
            if (g != null) BarPainter.Draw(g, new Rectangle(pad, y, width, S(10)), row, now, theme, scale);
            y += S(10) + S(4);
            if (row.ResetsAt is { } resetsAt)
            {
                if (g != null)
                    TextRenderer.DrawText(g, UsageFormat.ResetDescription(resetsAt, now), captionFont,
                        new Point(pad, y), theme.Secondary, TextFormatFlags.NoPadding);
                y += captionFont.Height;
            }
            y += S(10);
        }
        if (error is not null || rows.Count == 0)
        {
            var text = error ?? "Loading…";
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            var size = TextRenderer.MeasureText(text, captionFont, new Size(width, 0), flags);
            if (g != null)
                TextRenderer.DrawText(g, text, captionFont, new Rectangle(pad, y, width, size.Height),
                    error is null ? theme.Secondary : theme.Red, flags);
            y += size.Height + S(10);
        }
        return y - S(10) + pad;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
