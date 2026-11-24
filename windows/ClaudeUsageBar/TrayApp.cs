using System.Globalization;
using System.Text.Json;
using UsageCore;

namespace ClaudeUsageBar;

/// <summary>The tray icon and its panel: the Windows counterpart of ClaudeUsageBarApp and UsageModel.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray = new();
    readonly PanelForm panel = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5 * 60 * 1000 };
    readonly System.Windows.Forms.Timer retryTimer = new();
    readonly AppState state = AppState.Load();
    readonly ToolStripMenuItem loginMenuItem = new("Open at login") { CheckOnClick = true };
    Icon? icon;
    DateTime panelHiddenAt;
    bool refreshing;
    readonly HoverCard hover = new();
    readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 100 };
    Point lastHoverPos;
    DateTime lastHoverAt, hoverStartedAt;

    PanelState View => panel.State;

    public TrayApp()
    {
        // Opt in to launch at login on first run; afterwards the toggle decides.
        if (!state.DidSetUpLoginItem)
        {
            state.DidSetUpLoginItem = true;
            state.Save();
            SetOpensAtLogin(true);
        }
        else
        {
            LoginItem.Repair();
        }
        View.OpensAtLogin = LoginItem.IsEnabled;

        panel.RefreshClicked += () => _ = RefreshAsync();
        panel.QuitClicked += Quit;
        panel.OpenAtLoginChanged += SetOpensAtLogin;
        panel.VisibleChanged += (_, _) => { if (!panel.Visible) panelHiddenAt = DateTime.UtcNow; };

        loginMenuItem.CheckedChanged += (_, _) => { if (loginMenuItem.Checked != View.OpensAtLogin) SetOpensAtLogin(loginMenuItem.Checked); };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            new ToolStripMenuItem("Refresh", null, (_, _) => _ = RefreshAsync()),
            loginMenuItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
        ]);
        menu.Opening += (_, _) => loginMenuItem.Checked = View.OpensAtLogin;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        tray.MouseMove += (_, _) => OnTrayMouseMove();
        hoverTimer.Tick += (_, _) => OnHoverTick();

        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();
        retryTimer.Tick += (_, _) => { retryTimer.Stop(); _ = RefreshAsync(); };

        UpdateTray();
        tray.Visible = true;
        _ = RefreshAsync();
    }

    void TogglePanel()
    {
        hover.Hide();
        if (panel.Visible) { panel.Hide(); return; }
        // Clicking the icon while the panel is open first deactivates (hides) it; don't reopen on that same click.
        if ((DateTime.UtcNow - panelHiddenAt).TotalMilliseconds < 300) return;
        panel.ShowNearTray();
    }

    void SetOpensAtLogin(bool on)
    {
        try
        {
            LoginItem.Set(on);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            View.Error = $"Open at login: {e.Message}";
        }
        View.OpensAtLogin = LoginItem.IsEnabled;
        panel.UpdateView();
    }

    void Quit()
    {
        tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            icon?.Dispose();
            timer.Dispose();
            retryTimer.Dispose();
            hoverTimer.Dispose();
            hover.Dispose();
            panel.Dispose();
        }
        base.Dispose(disposing);
    }

    async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var usage = await UsageApi.FetchAsync();
            View.Usage = usage;
            View.Error = null;
            View.UpdatedAt = DateTimeOffset.Now;
            Notify(Pace.Alerts(usage.Rows, DateTimeOffset.Now));
        }
        catch (UsageException e)
        {
            View.Error = e.Message;
            // The usage endpoint rate-limits per account; retry when it says so instead of waiting the full 5 min.
            if (e.RetryAfter is { } wait)
            {
                retryTimer.Interval = (int)Math.Clamp((wait + 1) * 1000, 1000, int.MaxValue);
                retryTimer.Start();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            View.Error = e is TaskCanceledException ? "Request timed out" : e.Message;
        }
        finally
        {
            refreshing = false;
            UpdateTray();
            panel.UpdateView();
        }
    }

    void UpdateTray()
    {
        // The icon shows the session (the first limit group); the hover card has every limit.
        var top = View.Usage is { } usage ? Usage.TopPerGroup(usage.Rows) : [];
        var old = icon;
        icon = TrayIcon.Render(top.Count > 0 ? top[0].Percent : null, error: View.Usage is null && View.Error is not null);
        tray.Icon = icon;
        old?.Dispose();
        // The hover card replaces the text tooltip; an empty text keeps Windows from showing both.
        tray.Text = "";
        if (hover.Visible) hover.UpdateView(View);
    }

    void OnTrayMouseMove()
    {
        if (!hoverTimer.Enabled) { hoverStartedAt = DateTime.UtcNow; hoverTimer.Start(); }
        lastHoverPos = Cursor.Position;
        lastHoverAt = DateTime.UtcNow;
    }

    /// <summary>NotifyIcon has no mouse-leave event: the pointer has left once it moved and the icon stopped
    /// reporting moves. A short delay before showing keeps the card from flashing when passing by.</summary>
    void OnHoverTick()
    {
        var now = DateTime.UtcNow;
        var over = Cursor.Position == lastHoverPos || (now - lastHoverAt).TotalMilliseconds < 200;
        if (!over || panel.Visible || tray.ContextMenuStrip!.Visible)
        {
            hover.Hide();
            hoverTimer.Stop();
            return;
        }
        if (!hover.Visible && (now - hoverStartedAt).TotalMilliseconds >= 300)
        {
            hover.UpdateView(View);
            hover.ShowNear(lastHoverPos);
        }
    }


    /// <summary>Shows each alert once per limit window; sent keys are kept in the state file.</summary>
    void Notify(IReadOnlyList<UsageAlert> alerts)
    {
        var fresh = alerts.Where(a => !state.SentAlerts.Contains(a.Key)).ToList();
        if (fresh.Count == 0) return;
        // shortcut: keys are never pruned; a few per week, fine for years.
        state.SentAlerts.UnionWith(fresh.Select(a => a.Key));
        state.Save();

        static string When(DateTimeOffset d) => d.ToLocalTime().ToString("ddd d MMM, t", CultureInfo.CurrentCulture);
        var messages = fresh.Select(alert =>
        {
            var row = alert.Row;
            var used = UsageFormat.Round(row.Percent);
            return alert.Level == AlertLevel.Pace && Pace.Projection(row, DateTimeOffset.Now) is Projection.RunsOut runsOut
                ? (Title: $"{row.Title}: heavy usage", Body: $"{used}% used. At this pace it runs out {When(runsOut.At)}.")
                : (Title: $"{row.Title}: {used}% used", Body: row.ResetsAt is { } r ? $"Resets {When(r)}." : "");
        }).ToList();
        // A tray balloon (a toast on Windows 10/11) holds one message, so several alerts are merged into one.
        var (title, body) = messages.Count == 1
            ? messages[0]
            : ("Claude usage alerts", string.Join("\n", messages.Select(m => $"{m.Title}. {m.Body}")));
        tray.ShowBalloonTip(10_000, title, string.IsNullOrEmpty(body) ? " " : body, ToolTipIcon.Warning);
    }
}

