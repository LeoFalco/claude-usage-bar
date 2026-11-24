using System.Drawing.Imaging;
using System.Globalization;
using UsageCore;

namespace ClaudeUsageBar;

/// <summary>Renders the panel (<c>--snapshot out.png</c>) or the hover card (<c>--snapshot-hover out.png</c>)
/// offscreen with sample data into a PNG, so the README screenshots never show real account data.</summary>
static class Snapshot
{
    static Usage SampleUsage()
    {
        var now = DateTimeOffset.UtcNow;
        string Iso(double hours) => now.AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Usage.Decode($$"""
        {"limits": [
          {"kind": "session", "group": "session", "percent": 34, "resets_at": "{{Iso(2.5)}}"},
          {"kind": "weekly_all", "group": "weekly", "percent": 58, "resets_at": "{{Iso(88)}}"},
          {"kind": "weekly_scoped", "group": "weekly", "percent": 12, "resets_at": "{{Iso(88)}}",
           "scope": {"model": {"display_name": "Fable"} } }
        ]}
        """);
    }

    public static bool Render(string path, bool hover = false)
    {
        // Same date format on every machine that redraws the README images.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        var state = new PanelState { Usage = SampleUsage(), UpdatedAt = DateTimeOffset.Now, OpensAtLogin = true };
        // 2x so the image is sharp on high-DPI screens.
        using Form form = hover ? new HoverCard(fixedScale: 2, dark: true) : new PanelForm(fixedScale: 2, dark: true);
        if (form is HoverCard card)
        {
            card.UpdateView(state);
        }
        else if (form is PanelForm panel)
        {
            panel.State.Usage = state.Usage;
            panel.State.UpdatedAt = state.UpdatedAt;
            panel.State.OpensAtLogin = true;
            panel.UpdateView();
        }
        form.CreateControl();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
        // The form is never shown, so its buttons are not part of its own capture.
        foreach (Control child in form.Controls) child.DrawToBitmap(bitmap, child.Bounds);
        try
        {
            bitmap.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
