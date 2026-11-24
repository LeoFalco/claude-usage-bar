using System.Drawing.Drawing2D;
using UsageCore;

namespace ClaudeUsageBar;

sealed record Theme(Color Back, Color Fore, Color Secondary, Color Track, Color Border, Color Button,
    Color Blue, Color Yellow, Color Orange, Color Red, Color Green)
{
    public static readonly Theme Dark = new(
        Color.FromArgb(0x20, 0x20, 0x20), Color.White, Color.FromArgb(0xA0, 0xA0, 0xA0), Color.FromArgb(0x3A, 0x3A, 0x3A),
        Color.FromArgb(0x45, 0x45, 0x45), Color.FromArgb(0x2D, 0x2D, 0x2D),
        Color.FromArgb(0x0A, 0x84, 0xFF), Color.FromArgb(0xFF, 0xD6, 0x0A), Color.FromArgb(0xFF, 0x9F, 0x0A),
        Color.FromArgb(0xFF, 0x45, 0x3A), Color.FromArgb(0x32, 0xD7, 0x4B));

    public static readonly Theme Light = new(
        Color.FromArgb(0xF9, 0xF9, 0xF9), Color.Black, Color.FromArgb(0x60, 0x60, 0x60), Color.FromArgb(0xE0, 0xE0, 0xE0),
        Color.FromArgb(0xD0, 0xD0, 0xD0), Color.White,
        Color.FromArgb(0x00, 0x7A, 0xFF), Color.FromArgb(0xD4, 0xA8, 0x00), Color.FromArgb(0xE0, 0x80, 0x00),
        Color.FromArgb(0xE0, 0x30, 0x25), Color.FromArgb(0x1E, 0x8E, 0x3E));

    /// <summary>Traffic-light scale for a usage bar: green, yellow from 50%, orange from 70%, red from 90%.</summary>
    public Color UsageColor(double percent) =>
        percent >= 90 ? Red : percent >= 70 ? Orange : percent >= 50 ? Yellow : Green;
}

static class BarPainter
{
    /// <summary>The usage bar: a gradient fill colored by usage, a hatched extension up to where the current
    /// pace ends at reset, faint ticks at 50/70/90% where the color changes, and a marker for how much of the
    /// window has passed (a fill left of it means using less than an even pace).</summary>
    public static void Draw(Graphics g, Rectangle r, UsageRow row, DateTimeOffset now, Theme theme, float scale)
    {
        int S(float v) => (int)Math.Round(v * scale);
        var percent = Math.Clamp(row.Percent, 0, 100);
        int X(double p) => r.X + (int)Math.Round(r.Width * Math.Clamp(p, 0, 100) / 100);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var trackPath = Capsule(r);
        using (var track = new SolidBrush(theme.Track)) g.FillPath(track, trackPath);
        var clip = g.Clip;
        g.SetClip(trackPath);

        using (var tick = new Pen(Color.FromArgb(70, theme.Fore), Math.Max(1, S(1))))
            foreach (var threshold in new[] { 50, 70, 90 }) g.DrawLine(tick, X(threshold), r.Top, X(threshold), r.Bottom);

        var projected = Pace.Projection(row, now) switch
        {
            Projection.Lasts lasts => lasts.PercentAtReset,
            Projection.RunsOut => 100,
            _ => percent,
        };
        if (projected > percent)
        {
            var ghostColor = theme.UsageColor(projected);
            using var ghost = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(110, ghostColor), Color.FromArgb(45, ghostColor));
            g.FillRectangle(ghost, Rectangle.FromLTRB(X(percent) - r.Height, r.Top, X(projected), r.Bottom));
        }

        if (percent > 0)
        {
            var color = theme.UsageColor(percent);
            var fillRect = new Rectangle(r.X, r.Y, Math.Max(X(percent) - r.X, r.Height), r.Height);
            using var fillPath = Capsule(fillRect);
            using var fill = new LinearGradientBrush(fillRect, ControlPaint.Light(color, 0.5f), color, LinearGradientMode.Horizontal);
            g.FillPath(fill, fillPath);
            // Soft highlight on the upper half for depth.
            using var shine = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, r.Height / 2 + 1),
                Color.FromArgb(90, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical);
            g.FillRectangle(shine, fillRect.X, fillRect.Y, fillRect.Width, r.Height / 2);
        }
        g.Clip = clip;

        // Time marker: share of the window already elapsed.
        if (row.ResetsAt is { } resetsAt && Pace.WindowLength(row.Group) is { } length)
        {
            var elapsed = 100 * (1 - (resetsAt - now).TotalSeconds / length.TotalSeconds);
            if (elapsed is > 0 and < 100)
            {
                var x = X(elapsed);
                using var outline = new Pen(theme.Back, S(4));
                using var marker = new Pen(theme.Fore, S(2));
                g.DrawLine(outline, x, r.Top - S(3), x, r.Bottom + S(3));
                g.DrawLine(marker, x, r.Top - S(3), x, r.Bottom + S(3));
            }
        }
    }

    public static GraphicsPath Capsule(RectangleF r)
    {
        var path = new GraphicsPath();
        var d = Math.Min(r.Height, r.Width);
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}
