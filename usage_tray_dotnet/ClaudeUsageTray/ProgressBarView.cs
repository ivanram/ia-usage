using System.Drawing.Drawing2D;

namespace ClaudeUsageTray;

/// <summary>A small rounded, color-coded progress bar (green/amber/red by percent).</summary>
public sealed class ProgressBarView : Control
{
    private int _percent;
    private Color _trackColor = Color.FromArgb(40, 128, 128, 128);
    private int _cornerRadius = 5;

    public int Percent
    {
        get => _percent;
        set { _percent = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    public Color TrackColor
    {
        get => _trackColor;
        set { _trackColor = value; Invalidate(); }
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = value; Invalidate(); }
    }

    public ProgressBarView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 10;
    }

    private Color BarColor => _percent switch
    {
        < 60 => Color.FromArgb(255, 88, 189, 125),
        < 85 => Color.FromArgb(255, 235, 170, 60),
        _ => Color.FromArgb(255, 224, 90, 90),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var track = RoundedRect(ClientRectangle, _cornerRadius);
        using var trackBrush = new SolidBrush(_trackColor);
        g.FillPath(trackBrush, track);

        // Clip the fill to the track's own rounded outline instead of drawing a
        // second independently-rounded shape: guarantees the fill's corners
        // exactly match the track's, with no seam or mismatched border.
        var fillWidth = (int)(ClientRectangle.Width * (_percent / 100.0));
        if (fillWidth > 0)
        {
            var oldClip = g.Clip;
            g.SetClip(track, CombineMode.Intersect);
            using var fillBrush = new SolidBrush(BarColor);
            g.FillRectangle(fillBrush, new Rectangle(0, 0, fillWidth, Height));
            g.Clip = oldClip;
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) { path.AddRectangle(bounds); return path; }
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
