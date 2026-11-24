using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using UsageCore;

namespace ClaudeUsageBar;

sealed class PanelState
{
    public Usage? Usage { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool OpensAtLogin { get; set; }
}

/// <summary>The panel that opens from the tray icon, the Windows counterpart of the macOS MenuBarExtra window.
/// Rows are painted by hand (like the macOS UsageBar) so colors and spacing match on every Windows version.</summary>
sealed class PanelForm : Form
{
    const int LogicalWidth = 280;

    public event Action? RefreshClicked;
    public event Action? QuitClicked;
    public event Action<bool>? OpenAtLoginChanged;

    public PanelState State { get; } = new();

    readonly float? fixedScale;
    readonly bool? fixedDark;
    readonly Button refresh = new() { Text = "Refresh", FlatStyle = FlatStyle.Flat, TabIndex = 0 };
    readonly Button gear = new() { Text = "", FlatStyle = FlatStyle.Flat, TabIndex = 1, AccessibleName = "Options" };
    readonly ContextMenuStrip gearMenu = new();
    readonly ToolStripMenuItem loginItem = new("Open at login") { CheckOnClick = true };
    Theme theme = Theme.Dark;
    Font titleFont = null!, bodyFont = null!, captionFont = null!, iconFont = null!;
    float scale = 1;

    /// <param name="fixedScale">Fixed pixel scale and theme, for the offscreen README screenshot.</param>
    public PanelForm(float? fixedScale = null, bool? dark = null)
    {
        this.fixedScale = fixedScale;
        fixedDark = dark;
        Text = "Claude Usage Bar";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;

        loginItem.CheckedChanged += (_, _) => { if (loginItem.Checked != State.OpensAtLogin) OpenAtLoginChanged?.Invoke(loginItem.Checked); };
        gearMenu.Items.AddRange([loginItem, new ToolStripSeparator(), new ToolStripMenuItem("Quit", null, (_, _) => QuitClicked?.Invoke()) { ShortcutKeyDisplayString = "Ctrl+Q" }]);
        refresh.Click += (_, _) => RefreshClicked?.Invoke();
        gear.Click += (_, _) => gearMenu.Show(gear, new Point(0, gear.Height));
        Controls.AddRange([refresh, gear]);
        ApplyStyle();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no Alt+Tab entry
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounded corners; ignored on Windows 10.
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyStyle();
        UpdateView();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.R) RefreshClicked?.Invoke();
        else if (e.Control && e.KeyCode == Keys.Q) QuitClicked?.Invoke();
        else if (e.KeyCode == Keys.Escape) Hide();
        else return;
        e.Handled = true;
    }

    int S(float v) => (int)Math.Round(v * scale);

    /// <summary>Re-reads the theme and DPI; fonts and buttons follow.</summary>
    public void ApplyStyle()
    {
        theme = fixedDark ?? WindowsTheme.AppsDark ? Theme.Dark : Theme.Light;
        var newScale = fixedScale ?? DeviceDpi / 96f;
        // Fonts are only rebuilt when the scale changes. Control.Font compares fonts by value, so assigning an
        // equal new font keeps the old object: disposing it here would leave the buttons painting with a
        // disposed font ("Parameter is not valid" on the first click on the tray icon).
        if (titleFont is null || newScale != scale)
        {
            scale = newScale;
            Font?[] old = [titleFont, bodyFont, captionFont, iconFont];
            titleFont = new Font("Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);
            bodyFont = new Font("Segoe UI", 13 * scale, GraphicsUnit.Pixel);
            captionFont = new Font("Segoe UI", 11 * scale, GraphicsUnit.Pixel);
            iconFont = new Font("Segoe MDL2 Assets", 12 * scale, GraphicsUnit.Pixel);
            refresh.Font = bodyFont;
            gear.Font = iconFont;
            foreach (var f in old) f?.Dispose();
        }

        BackColor = theme.Back;
        var hoverBack = theme == Theme.Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEC, 0xEC, 0xEC);
        foreach (var b in new[] { refresh, gear })
        {
            b.BackColor = theme.Button;
            b.ForeColor = theme.Fore;
            b.FlatAppearance.BorderColor = theme.Border;
            b.FlatAppearance.MouseOverBackColor = hoverBack;
        }
        gearMenu.Renderer ??= new ToolStripProfessionalRenderer();
    }

    /// <summary>Lays the panel out for the current <see cref="State"/> and repaints it.</summary>
    public void UpdateView()
    {
        loginItem.Checked = State.OpensAtLogin;
        var height = RenderContent(null);
        var buttonHeight = S(28);
        var footerY = height - S(16) - buttonHeight;
        gear.Bounds = new Rectangle(S(LogicalWidth) - S(16) - S(32), footerY, S(32), buttonHeight);
        var refreshWidth = TextRenderer.MeasureText("Refresh", bodyFont).Width + S(20);
        refresh.Bounds = new Rectangle(gear.Left - S(8) - refreshWidth, footerY, refreshWidth, buttonHeight);
        ClientSize = new Size(S(LogicalWidth), height);
        Invalidate();
    }

    /// <summary>Places the panel next to the tray, on the screen with the cursor, whatever side the taskbar is on.</summary>
    public void ShowNearTray()
    {
        ApplyStyle();
        UpdateView();
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        var margin = S(12);
        var x = Math.Clamp(cursor.X - Width / 2, area.Left + margin, area.Right - Width - margin);
        var y = cursor.Y < area.Top ? area.Top + margin : area.Bottom - Height - margin;
        Location = new Point(x, y);
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!gearMenu.Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RenderContent(e.Graphics);
        using var border = new Pen(theme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    static string When(DateTimeOffset d)
    {
        var local = d.ToLocalTime();
        return local.ToString("ddd d MMM, ", CultureInfo.CurrentCulture) + local.ToString("t", CultureInfo.CurrentCulture);
    }

    /// <summary>Draws the content when <paramref name="g"/> is set, else only measures it. Returns the panel height.</summary>
    int RenderContent(Graphics? g)
    {
        var pad = S(16);
        var width = S(LogicalWidth) - 2 * pad;
        var y = pad;
        var now = DateTimeOffset.Now;

        int Line(string text, Font font, Color color, int top, TextFormatFlags align = TextFormatFlags.Left)
        {
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | align;
            var size = TextRenderer.MeasureText(text, font, new Size(width, 0), flags);
            if (g != null) TextRenderer.DrawText(g, text, font, new Rectangle(pad, top, width, size.Height), color, flags);
            return size.Height;
        }

        var rows = State.Usage?.Rows ?? [];
        foreach (var row in rows)
        {
            var percent = $"{UsageFormat.Round(row.Percent)}%";
            var h = Line(percent, bodyFont, theme.Fore, y, TextFormatFlags.Right);
            if (g != null)
            {
                var percentWidth = TextRenderer.MeasureText(percent, bodyFont).Width;
                TextRenderer.DrawText(g, row.Title, titleFont, new Rectangle(pad, y, width - percentWidth - S(8), h),
                    theme.Fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            y += h + S(4);

            y += S(3); // room for the time marker, which sticks out of the bar
            if (g != null) BarPainter.Draw(g, new Rectangle(pad, y, width, S(BarHeight)), row, now, theme, scale);
            y += S(BarHeight) + S(3);

            if (row.ResetsAt is { } resetsAt)
                y += S(4) + Line($"{UsageFormat.ResetDescription(resetsAt, now)} · {When(resetsAt)}", captionFont, theme.Secondary, y + S(4));
            switch (Pace.Projection(row, now))
            {
                case Projection.Lasts lasts:
                    y += S(4) + Line($"At this pace: lasts, about {UsageFormat.Round(lasts.PercentAtReset)}% at reset", captionFont, theme.Green, y + S(4));
                    break;
                case Projection.RunsOut runsOut:
                    y += S(4) + Line($"At this pace: runs out {When(runsOut.At)}", captionFont, theme.Orange, y + S(4));
                    break;
            }
            y += S(12);
        }
        if (State.Error is { } error) y += Line(error, bodyFont, theme.Red, y) + S(12);
        else if (rows.Count == 0) y += Line("Loading…", bodyFont, theme.Secondary, y) + S(12);

        if (g != null)
        {
            using var divider = new Pen(theme.Border, Math.Max(1, S(1)));
            g.DrawLine(divider, pad, y, pad + width, y);
        }
        y += S(12);

        var buttonHeight = S(28);
        if (State.UpdatedAt is { } updated && g != null)
        {
            var text = $"Updated {updated.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";
            TextRenderer.DrawText(g, text, captionFont, new Rectangle(pad, y, width / 2, buttonHeight), theme.Secondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        return y + buttonHeight + pad;
    }

    const int BarHeight = 12;
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
